using System;
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

        Thread workerThread;
        TcpClient client;
        NetworkStream stream;
        volatile bool stopRequested;

        byte[] pendingRegistrationMessage;
        string pendingRegistrationFrameId;
        byte[] latestTrackingMessage;
        string latestTrackingFrameId;

        long sentFrameCount;
        long droppedTrackingFrameCount;
        FPSenderState state = FPSenderState.Stopped;
        string lastError;

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

                pendingRegistrationMessage = null;
                pendingRegistrationFrameId = null;
                latestTrackingMessage = null;
                latestTrackingFrameId = null;
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

                if (pendingRegistrationMessage != null)
                {
                    return false;
                }

                pendingRegistrationMessage = frame.message;
                pendingRegistrationFrameId = frame.header.frame_id;
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

                if (latestTrackingMessage != null)
                {
                    Interlocked.Increment(ref droppedTrackingFrameCount);
                }

                latestTrackingMessage = frame.message;
                latestTrackingFrameId = frame.header.frame_id;
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
                    byte[] message = null;
                    string frameId = null;
                    bool registrationFrame = false;

                    lock (gate)
                    {
                        if (pendingRegistrationMessage != null)
                        {
                            state = FPSenderState.SendingRegistrationFrame;
                            message = pendingRegistrationMessage;
                            frameId = pendingRegistrationFrameId;
                            pendingRegistrationMessage = null;
                            pendingRegistrationFrameId = null;
                            registrationFrame = true;
                        }
                        else if (state == FPSenderState.TrackingStream && latestTrackingMessage != null)
                        {
                            message = latestTrackingMessage;
                            frameId = latestTrackingFrameId;
                            latestTrackingMessage = null;
                            latestTrackingFrameId = null;
                        }
                    }

                    if (message == null)
                    {
                        frameAvailable.WaitOne(20);
                        continue;
                    }

                    DateTime start = DateTime.UtcNow;
                    stream.Write(message, 0, message.Length);
                    stream.Flush();
                    Interlocked.Increment(ref sentFrameCount);
                    double sendMs = (DateTime.UtcNow - start).TotalMilliseconds;

                    if (registrationFrame)
                    {
                        lock (gate)
                        {
                            if (!stopRequested)
                            {
                                state = FPSenderState.TrackingStream;
                            }
                        }
                        Log($"Sent REGISTER frame {frameId}, bytes={message.Length}, send_ms={sendMs:F2}");
                    }
                    else if (verboseLogging)
                    {
                        Log($"Sent TRACK frame {frameId}, bytes={message.Length}, send_ms={sendMs:F2}, dropped_tracking={DroppedTrackingFrameCount}");
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
