using System;
using System.Collections.Generic;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

namespace FoundationPoseStreaming
{
    public enum FPSenderState
    {
        Stopped,
        Connecting,
        WaitingForRegistrationFrame,
        SendingRegistrationFrame,
        TrackingStream,
        Error
    }

    public sealed class FoundationPoseTcpSender : MonoBehaviour
    {
        [Header("PC Server")]
        public string host = "127.0.0.1";
        public int port = 5000;
        public bool connectOnStart = true;
        public int connectTimeoutMs = 3000;
        public int sendTimeoutMs = 3000;
        public int initialConnectDelayMs = 1000;
        public int reconnectDelayMs = 1000;

        [Header("Logging")]
        public bool verboseLogging = true;

        readonly object gate = new object();
        readonly AutoResetEvent frameAvailable = new AutoResetEvent(false);
        readonly Dictionary<string, DateTime> sentFrameUtcById = new Dictionary<string, DateTime>();
        readonly Queue<string> sentFrameIdOrder = new Queue<string>();

        Thread workerThread;
        Thread controlThread;
        TcpClient client;
        NetworkStream stream;
        volatile bool stopRequested;

        FPEncodedFrame pendingRegistrationFrame;
        FPEncodedFrame latestTrackingFrame;

        long sentFrameCount;
        long droppedTrackingFrameCount;
        FPSenderState state = FPSenderState.Stopped;
        string lastError;
        int maskBurstFramesRemaining;
        string maskBurstReason;
        string maskBurstRequestFrameId;
        DateTime lastSendUtc;
        FPTrackingResult latestTrackingResult;
        long trackingResultSequence;
        int lastSentFrameIndex = -1;

        [Serializable]
        sealed class FPControlMessage
        {
            public string magic;
            public int version;
            public string type;
            public string frame_id;
            public string reason;
            public int request_frames;
        }

        public FPSenderState State
        {
            get
            {
                lock (gate)
                {
                    return state;
                }
            }
        }

        public bool NeedsRegistrationFrame => State == FPSenderState.WaitingForRegistrationFrame;
        public bool IsTrackingStream => State == FPSenderState.TrackingStream;
        public long SentFrameCount => Interlocked.Read(ref sentFrameCount);
        public long DroppedTrackingFrameCount => Interlocked.Read(ref droppedTrackingFrameCount);
        public string LastError => lastError;
        public int LastSentFrameIndex => Volatile.Read(ref lastSentFrameIndex);

        public bool TryGetLatestTrackingResult(out FPTrackingResult result)
        {
            lock (gate)
            {
                result = latestTrackingResult;
                return result != null;
            }
        }

        public bool TryConsumeMaskBurstFrame(out int remainingAfterConsume, out string reason, out string requestFrameId)
        {
            lock (gate)
            {
                if (maskBurstFramesRemaining <= 0)
                {
                    remainingAfterConsume = 0;
                    reason = null;
                    requestFrameId = null;
                    return false;
                }

                maskBurstFramesRemaining--;
                remainingAfterConsume = maskBurstFramesRemaining;
                reason = maskBurstReason;
                requestFrameId = maskBurstRequestFrameId;

                Debug.Log($"[FoundationPoseTcpSender] MASK_BURST_REMAINING remaining={remainingAfterConsume} reason={reason ?? "unspecified"} request_frame_id={requestFrameId ?? "none"}");
                if (maskBurstFramesRemaining == 0)
                {
                    Debug.Log($"[FoundationPoseTcpSender] MASK_BURST_END reason={reason ?? "unspecified"} request_frame_id={requestFrameId ?? "none"}");
                }

                return true;
            }
        }

        void Start()
        {
            Debug.Log($"[FoundationPoseTcpSender] Startup host={host} port={port} connectOnStart={connectOnStart} " +
                      $"connectTimeoutMs={connectTimeoutMs} sendTimeoutMs={sendTimeoutMs} " +
                      $"internetReachability={Application.internetReachability} platform={Application.platform}");

            if (connectOnStart)
            {
                StartSender();
            }
        }

        void OnDestroy()
        {
            StopSender();
        }

        public void StartSender()
        {
            lock (gate)
            {
                if (workerThread != null && workerThread.IsAlive)
                {
                    return;
                }

                stopRequested = false;
                state = FPSenderState.Connecting;
                workerThread = new Thread(SendLoop)
                {
                    IsBackground = true,
                    Name = "FoundationPose TCP Sender"
                };
                workerThread.Start();
            }
        }

        public void StopSender()
        {
            stopRequested = true;
            CloseSocket();
            frameAvailable.Set();

            Thread threadToJoin;
            lock (gate)
            {
                threadToJoin = workerThread;
            }

            if (threadToJoin != null && threadToJoin.IsAlive)
            {
                threadToJoin.Join(1000);
            }

            lock (gate)
            {
                if (threadToJoin == null || !threadToJoin.IsAlive)
                {
                    workerThread = null;
                }
                else
                {
                    Debug.LogWarning("[FoundationPoseTcpSender] Sender thread did not stop within 1000 ms; keeping thread reference to prevent duplicate sender threads.");
                }

                pendingRegistrationFrame = null;
                latestTrackingFrame = null;
                maskBurstFramesRemaining = 0;
                maskBurstReason = null;
                maskBurstRequestFrameId = null;
                latestTrackingResult = null;
                sentFrameUtcById.Clear();
                sentFrameIdOrder.Clear();
                Volatile.Write(ref lastSentFrameIndex, -1);
                if (state != FPSenderState.Error)
                {
                    state = FPSenderState.Stopped;
                }
            }
        }

        public bool EnqueueRegistrationFrame(FPEncodedFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            lock (gate)
            {
                if (state != FPSenderState.WaitingForRegistrationFrame)
                {
                    return false;
                }

                if (pendingRegistrationFrame != null)
                {
                    return false;
                }

                pendingRegistrationFrame = frame;
                frameAvailable.Set();
                return true;
            }
        }

        public bool EnqueueTrackingFrame(FPEncodedFrame frame)
        {
            if (frame == null)
            {
                throw new ArgumentNullException(nameof(frame));
            }

            lock (gate)
            {
                if (state != FPSenderState.TrackingStream)
                {
                    return false;
                }

                if (latestTrackingFrame != null)
                {
                    Interlocked.Increment(ref droppedTrackingFrameCount);
                }

                latestTrackingFrame = frame;
                frameAvailable.Set();
                return true;
            }
        }

        void SendLoop()
        {
            try
            {
                if (WaitForStop(initialConnectDelayMs))
                {
                    return;
                }

                int connectAttempt = 0;
                while (!stopRequested)
                {
                    connectAttempt++;
                    lock (gate)
                    {
                        state = FPSenderState.Connecting;
                    }

                    try
                    {
                        CloseSocket();
                        client = new TcpClient();
                        client.NoDelay = true;
                        client.SendTimeout = sendTimeoutMs;

                        LogResolvedAddresses(host);
                        Debug.Log($"[FoundationPoseTcpSender] Connecting host={host} port={port} attempt={connectAttempt} " +
                                  $"timeoutMs={connectTimeoutMs} addressFamily={client.Client.AddressFamily} " +
                                  $"noDelay={client.NoDelay} sendTimeoutMs={client.SendTimeout}");
                        ConnectWithTimeout(client, host, port, connectTimeoutMs);
                        stream = client.GetStream();
                        StartControlReader(stream);
                        lastSendUtc = default(DateTime);
                        lastError = null;

                        lock (gate)
                        {
                            state = FPSenderState.WaitingForRegistrationFrame;
                        }

                        Debug.Log($"[FoundationPoseTcpSender] Connected host={host} port={port} attempt={connectAttempt} " +
                                  $"connected={client.Connected} local={client.Client.LocalEndPoint} " +
                                  $"remote={client.Client.RemoteEndPoint} addressFamily={client.Client.AddressFamily}");
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (stopRequested)
                        {
                            return;
                        }

                        lastError = ex.ToString();
                        LogConnectFailure(connectAttempt, ex);
                        CloseSocket();

                        if (WaitForStop(reconnectDelayMs))
                        {
                            return;
                        }
                    }
                }

                if (stopRequested)
                {
                    return;
                }

                while (!stopRequested)
                {
                    FPEncodedFrame frame = null;
                    bool registrationFrame = false;

                    lock (gate)
                    {
                        if (pendingRegistrationFrame != null)
                        {
                            state = FPSenderState.SendingRegistrationFrame;
                            frame = pendingRegistrationFrame;
                            pendingRegistrationFrame = null;
                            registrationFrame = true;
                        }
                        else if (state == FPSenderState.TrackingStream && latestTrackingFrame != null)
                        {
                            frame = latestTrackingFrame;
                            latestTrackingFrame = null;
                        }
                    }

                    if (frame == null)
                    {
                        frameAvailable.WaitOne(20);
                        continue;
                    }

                    DateTime start = DateTime.UtcNow;
                    stream.Write(frame.message, 0, frame.message.Length);
                    stream.Flush();
                    Interlocked.Increment(ref sentFrameCount);
                    RecordSentFrame(frame.header.frame_id, frame.header.index, start);
                    double sendMs = (DateTime.UtcNow - start).TotalMilliseconds;
                    double frameIntervalMs = lastSendUtc == default(DateTime) ? 0.0 : (start - lastSendUtc).TotalMilliseconds;
                    double effectiveSendFps = frameIntervalMs > 0.0 ? 1000.0 / frameIntervalMs : 0.0;
                    lastSendUtc = start;

                    if (registrationFrame)
                    {
                        lock (gate)
                        {
                            if (!stopRequested)
                            {
                                state = FPSenderState.TrackingStream;
                            }
                        }
                        LogSend("REGISTER", frame, sendMs, frameIntervalMs, effectiveSendFps);
                    }
                    else if (verboseLogging)
                    {
                        LogSend("TRACK", frame, sendMs, frameIntervalMs, effectiveSendFps);
                    }
                }
            }
            catch (Exception ex)
            {
                lastError = ex.ToString();
                lock (gate)
                {
                    state = FPSenderState.Error;
                }
                LogException("Sender loop failed (connection may have been closed while sending)", ex);
            }
            finally
            {
                CloseSocket();
            }
        }

        void StartControlReader(NetworkStream connectedStream)
        {
            controlThread = new Thread(() => ControlReadLoop(connectedStream))
            {
                IsBackground = true,
                Name = "FoundationPose TCP Control Reader"
            };
            controlThread.Start();
        }

        void ControlReadLoop(NetworkStream controlStream)
        {
            try
            {
                byte[] lengthBuffer = new byte[4];
                while (!stopRequested)
                {
                    if (!ReadExact(controlStream, lengthBuffer, 0, lengthBuffer.Length))
                    {
                        return;
                    }

                    uint jsonLength = ReadUInt32BigEndian(lengthBuffer, 0);
                    if (jsonLength == 0 || jsonLength > 1024 * 1024)
                    {
                        Debug.LogWarning($"[FoundationPoseTcpSender] Ignoring invalid control json_len={jsonLength}");
                        return;
                    }

                    byte[] jsonBuffer = new byte[jsonLength];
                    if (!ReadExact(controlStream, jsonBuffer, 0, jsonBuffer.Length))
                    {
                        return;
                    }

                    string json = Encoding.UTF8.GetString(jsonBuffer);
                    HandleControlMessage(json);
                }
            }
            catch (Exception ex)
            {
                if (!stopRequested)
                {
                    Debug.LogWarning($"[FoundationPoseTcpSender] Control reader stopped: {DescribeException(ex)}");
                }
            }
        }

        void HandleControlMessage(string json)
        {
            if (!FPResultJsonParser.TryGetString(json, "magic", out string magic))
            {
                Debug.LogWarning($"[FoundationPoseTcpSender] Ignoring JSON without magic json={json}");
                return;
            }

            if (magic == "FPRESULT")
            {
                HandleTrackingResult(json);
                return;
            }

            if (magic != "FPCONTROL")
            {
                Debug.LogWarning($"[FoundationPoseTcpSender] Ignoring unknown PC message magic={magic}");
                return;
            }

            FPControlMessage control;
            try
            {
                control = JsonUtility.FromJson<FPControlMessage>(json);
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FoundationPoseTcpSender] Failed to parse control json={json}\n{DescribeException(ex)}");
                return;
            }

            if (control == null || control.version != 1)
            {
                Debug.LogWarning($"[FoundationPoseTcpSender] Ignoring unknown control json={json}");
                return;
            }

            if (control.type != "mask_request")
            {
                Log($"Ignoring unsupported control type={control.type}");
                return;
            }

            int requestedFrames = Math.Min(30, Math.Max(1, control.request_frames <= 0 ? 5 : control.request_frames));
            lock (gate)
            {
                maskBurstFramesRemaining = Math.Max(maskBurstFramesRemaining, requestedFrames);
                maskBurstReason = string.IsNullOrEmpty(control.reason) ? "mask_request" : control.reason;
                maskBurstRequestFrameId = control.frame_id;
            }

            Debug.Log(
                "[FoundationPoseTcpSender] MASK_BURST_START " +
                $"request_frames={requestedFrames} reason={maskBurstReason} request_frame_id={maskBurstRequestFrameId ?? "none"}");
        }

        void HandleTrackingResult(string json)
        {
            if (!FPResultJsonParser.TryParseTrackingResult(json, out FPTrackingResult result, out string reason))
            {
                Debug.LogWarning($"[FoundationPoseTcpSender] Dropped FPRESULT reason={reason} json={json}");
                return;
            }

            result.sequence = Interlocked.Increment(ref trackingResultSequence);
            result.receivedUtc = DateTime.UtcNow;
            int currentFrameIndex = LastSentFrameIndex;

            lock (gate)
            {
                if (latestTrackingResult != null &&
                    result.index >= 0 &&
                    latestTrackingResult.index >= 0 &&
                    result.index <= latestTrackingResult.index)
                {
                    Debug.Log(
                        "[FoundationPoseTcpSender] FPRESULT_DROPPED_OLD " +
                        $"frame_id={result.frameId ?? "none"} index={result.index} " +
                        $"latest_index={latestTrackingResult.index} current_unity_frame_index={currentFrameIndex}");
                    return;
                }

                if (!string.IsNullOrEmpty(result.frameId) &&
                    sentFrameUtcById.TryGetValue(result.frameId, out DateTime sentUtc))
                {
                    result.hasLatencyEstimate = true;
                    result.latencyMs = (result.receivedUtc - sentUtc).TotalMilliseconds;
                }

                latestTrackingResult = result;
            }

            Debug.Log(
                "[FoundationPoseTcpSender] FPRESULT_RECEIVED " +
                $"frame_id={result.frameId ?? "none"} index={result.index} timestamp={(result.hasTimestamp ? result.timestamp.ToString("F6") : "unknown")} " +
                $"current_unity_frame_index={currentFrameIndex} " +
                $"latency_ms={(result.hasLatencyEstimate ? result.latencyMs.ToString("F2") : "unknown")} " +
                $"pc_queue_latency_ms={(result.hasPcQueueLatencyMs ? result.pcQueueLatencyMs.ToString("F2") : "unknown")} " +
                $"smoothing_alpha={(result.hasSmoothingAlpha ? result.smoothingAlpha.ToString("F3") : "unknown")} " +
                $"bbox_lines={result.bboxLines.Length} axis_lines={result.axisLines.Length} drawn=pending");
        }

        void RecordSentFrame(string frameId, int frameIndex, DateTime sentUtc)
        {
            Volatile.Write(ref lastSentFrameIndex, frameIndex);
            if (string.IsNullOrEmpty(frameId))
            {
                return;
            }

            lock (gate)
            {
                sentFrameUtcById[frameId] = sentUtc;
                sentFrameIdOrder.Enqueue(frameId);
                while (sentFrameIdOrder.Count > 120)
                {
                    string oldFrameId = sentFrameIdOrder.Dequeue();
                    sentFrameUtcById.Remove(oldFrameId);
                }
            }
        }

        static bool ReadExact(NetworkStream input, byte[] buffer, int offset, int count)
        {
            int totalRead = 0;
            while (totalRead < count)
            {
                int read = input.Read(buffer, offset + totalRead, count - totalRead);
                if (read <= 0)
                {
                    return false;
                }

                totalRead += read;
            }

            return true;
        }

        static uint ReadUInt32BigEndian(byte[] buffer, int offset)
        {
            return ((uint)buffer[offset] << 24) |
                   ((uint)buffer[offset + 1] << 16) |
                   ((uint)buffer[offset + 2] << 8) |
                   buffer[offset + 3];
        }

        void LogSend(string operation, FPEncodedFrame frame, double sendMs, double frameIntervalMs, double effectiveSendFps)
        {
            FPFrameHeader header = frame.header;
            Log(
                $"Sent {operation} frame {header.frame_id} index={header.index} bytes={frame.message.Length} " +
                $"rgb_size={header.width}x{header.height} depth_size={header.width}x{header.height} " +
                $"rgb_format={header.rgb_format} rgb_len={header.rgb_len} depth_len={header.depth_len} " +
                $"mask_format={header.mask_format} mask_len={header.mask_len} mask_pixels={frame.maskPixelCount} " +
                $"mask_source={frame.maskSourceKind ?? "unknown"} mask_requested={frame.maskRequested} " +
                $"mask_burst_remaining={frame.maskBurstRemaining} mask_reason={frame.maskReason ?? "ok"} " +
                $"encode_rgb_ms={frame.encodeRgbMs:F2} encode_depth_ms={frame.encodeDepthMs:F2} " +
                $"encode_mask_ms={frame.encodeMaskMs:F2} send_ms={sendMs:F2} " +
                $"frame_interval_ms={frameIntervalMs:F2} effective_send_fps={effectiveSendFps:F2} " +
                $"dropped_tracking={DroppedTrackingFrameCount}");
        }

        void CloseSocket()
        {
            try
            {
                stream?.Close();
            }
            catch
            {
                // Ignore close failures during teardown.
            }

            try
            {
                client?.Close();
            }
            catch
            {
                // Ignore close failures during teardown.
            }

            stream = null;
            client = null;
        }

        static void ConnectWithTimeout(TcpClient tcpClient, string targetHost, int targetPort, int timeoutMs)
        {
            IAsyncResult result = tcpClient.BeginConnect(targetHost, targetPort, null, null);
            bool connected = result.AsyncWaitHandle.WaitOne(Math.Max(1, timeoutMs));
            if (!connected)
            {
                try
                {
                    tcpClient.Close();
                }
                catch
                {
                    // Ignore close failures on timeout.
                }

                throw new TimeoutException($"Timed out connecting to {targetHost}:{targetPort} after {timeoutMs} ms");
            }

            tcpClient.EndConnect(result);
        }

        bool WaitForStop(int delayMs)
        {
            if (delayMs <= 0)
            {
                return stopRequested;
            }

            return frameAvailable.WaitOne(delayMs) && stopRequested;
        }

        void LogConnectFailure(int attempt, Exception ex)
        {
            Debug.LogWarning($"[FoundationPoseTcpSender] Connect failed host={host} port={port} attempt={attempt} " +
                             $"timeoutMs={connectTimeoutMs} socket={DescribeSocket()}\n{DescribeException(ex)}");
        }

        void LogException(string context, Exception ex)
        {
            Debug.LogError($"[FoundationPoseTcpSender] {context} host={host} port={port} " +
                           $"state={State} socket={DescribeSocket()}\n{DescribeException(ex)}");
        }

        string DescribeSocket()
        {
            try
            {
                if (client == null)
                {
                    return "null";
                }

                Socket socket = client.Client;
                return $"connected={client.Connected}, local={socket.LocalEndPoint}, remote={socket.RemoteEndPoint}, " +
                       $"addressFamily={socket.AddressFamily}, socketType={socket.SocketType}, protocol={socket.ProtocolType}";
            }
            catch (Exception ex)
            {
                return $"unavailable ({ex.GetType().FullName}: {ex.Message})";
            }
        }

        static string DescribeException(Exception exception)
        {
            StringBuilder details = new StringBuilder();
            int depth = 0;
            for (Exception current = exception; current != null; current = current.InnerException)
            {
                if (depth > 0)
                {
                    details.AppendLine();
                }

                details.Append($"exception[{depth}] type={current.GetType().FullName} message={current.Message} hResult=0x{current.HResult:X8}");
                if (current is SocketException socketException)
                {
                    details.Append($" errorCode={socketException.ErrorCode} socketErrorCode={socketException.SocketErrorCode} nativeErrorCode={socketException.NativeErrorCode}");
                }

                if (!string.IsNullOrEmpty(current.StackTrace))
                {
                    details.AppendLine();
                    details.Append(current.StackTrace);
                }

                depth++;
            }

            return details.ToString();
        }

        static void LogResolvedAddresses(string targetHost)
        {
            try
            {
                IPAddress[] addresses = Dns.GetHostAddresses(targetHost);
                string resolved = addresses.Length == 0 ? "<none>" : string.Join(", ", Array.ConvertAll(addresses, address => $"{address} ({address.AddressFamily})"));
                Debug.Log($"[FoundationPoseTcpSender] DNS host={targetHost} addresses={resolved}");
            }
            catch (Exception ex)
            {
                Debug.LogWarning($"[FoundationPoseTcpSender] DNS resolution failed host={targetHost}\n{DescribeException(ex)}");
            }
        }

        void Log(string message)
        {
            if (verboseLogging)
            {
                Debug.Log($"[FoundationPoseTcpSender] {message}");
            }
        }
    }
}
