using System;
using System.Threading;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace FoundationPoseStreaming
{
    public sealed class FoundationPoseFrameStreamer : MonoBehaviour
    {
        [Header("AR Foundation")]
        public ARCameraManager cameraManager;
        public AROcclusionManager occlusionManager;

        [Header("FoundationPose")]
        public FoundationPoseTcpSender tcpSender;
        public YoloBBoxToMaskAdapter maskSource;

        [Header("Capture")]
        public double maxRgbDepthDeltaMs = 50.0;
        [Tooltip("Maximum absolute RGB/depth aspect-ratio difference allowed before dropping a frame.")]
        public double maxAspectRatioDelta = 0.01;
        public float targetFps = 5.0f;
        public FPRgbCodec rgbCodec = FPRgbCodec.Png;
        [Range(1, 100)]
        public int jpegQuality = 95;
        public bool verboseLogging = true;

        readonly object encoderGate = new object();
        readonly AutoResetEvent sampleAvailable = new AutoResetEvent(false);

        Thread encoderThread;
        volatile bool stopRequested;

        RawFrameSample pendingRegistrationSample;
        RawFrameSample latestTrackingSample;

        double lastCaptureRealtime;
        int nextIndex;
        long droppedCaptureCount;
        long droppedTrackingEncodeCount;

        sealed class RawFrameSample
        {
            public byte[] rgb24;
            public ushort[] depthMillimeters;
            public byte[] maskU8;
            public int width;
            public int height;
            public FPCameraIntrinsics intrinsics;
            public bool isRegistration;
            public double rgbTimestamp;
            public double depthTimestamp;
            public double deltaMs;
            public int invalidDepthCount;
            public RectInt maskBBox;
        }

        void Reset()
        {
            cameraManager = FindObjectOfType<ARCameraManager>();
            occlusionManager = FindObjectOfType<AROcclusionManager>();
            tcpSender = FindObjectOfType<FoundationPoseTcpSender>();
            maskSource = FindObjectOfType<YoloBBoxToMaskAdapter>();
        }

        void OnEnable()
        {
            if (cameraManager == null)
            {
                Debug.LogError("[FoundationPoseFrameStreamer] Missing ARCameraManager.");
                enabled = false;
                return;
            }

            if (occlusionManager == null)
            {
                Debug.LogError("[FoundationPoseFrameStreamer] Missing AROcclusionManager.");
                enabled = false;
                return;
            }

            if (tcpSender == null)
            {
                Debug.LogError("[FoundationPoseFrameStreamer] Missing FoundationPoseTcpSender.");
                enabled = false;
                return;
            }

            stopRequested = false;
            encoderThread = new Thread(EncoderLoop)
            {
                IsBackground = true,
                Name = "FoundationPose Frame Encoder"
            };
            encoderThread.Start();

            cameraManager.frameReceived += OnCameraFrameReceived;
        }

        void OnDisable()
        {
            if (cameraManager != null)
            {
                cameraManager.frameReceived -= OnCameraFrameReceived;
            }

            stopRequested = true;
            sampleAvailable.Set();
            if (encoderThread != null && encoderThread.IsAlive)
            {
                encoderThread.Join(1000);
            }

            encoderThread = null;
        }

        void OnCameraFrameReceived(ARCameraFrameEventArgs args)
        {
            if (!ShouldCaptureNow())
            {
                return;
            }

            if (!tcpSender.NeedsRegistrationFrame && !tcpSender.IsTrackingStream)
            {
                LogDrop("sender_not_ready");
                return;
            }

            if (!cameraManager.TryGetIntrinsics(out XRCameraIntrinsics intrinsics))
            {
                LogDrop("missing_intrinsics");
                return;
            }

            if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage rgbImage))
            {
                LogDrop("missing_rgb_cpu_image");
                return;
            }

            try
            {
                if (!occlusionManager.TryAcquireRawEnvironmentDepthCpuImage(out XRCpuImage depthImage))
                {
                    LogDrop("missing_raw_depth_cpu_image");
                    return;
                }

                try
                {
                    RawFrameSample sample = BuildRawFrameSample(rgbImage, depthImage, intrinsics);
                    if (sample == null)
                    {
                        return;
                    }

                    if (sample.isRegistration)
                    {
                        EnqueueRegistrationSample(sample);
                    }
                    else
                    {
                        EnqueueTrackingSample(sample);
                    }
                }
                finally
                {
                    depthImage.Dispose();
                }
            }
            finally
            {
                rgbImage.Dispose();
            }
        }

        RawFrameSample BuildRawFrameSample(XRCpuImage rgbImage, XRCpuImage depthImage, XRCameraIntrinsics intrinsics)
        {
            double rgbTimestamp = rgbImage.timestamp;
            double depthTimestamp = depthImage.timestamp;
            double deltaMs = Math.Abs(rgbTimestamp - depthTimestamp) * 1000.0;
            if (deltaMs > maxRgbDepthDeltaMs)
            {
                LogDrop($"rgb_depth_timestamp_delta_too_large rgb_ts={rgbTimestamp:F6} depth_ts={depthTimestamp:F6} delta_ms={deltaMs:F2}");
                return null;
            }

            int finalWidth = depthImage.width;
            int finalHeight = depthImage.height;
            if (finalWidth <= 0 || finalHeight <= 0)
            {
                LogDrop($"invalid_depth_dimensions {finalWidth}x{finalHeight}");
                return null;
            }

            if (finalWidth > rgbImage.width || finalHeight > rgbImage.height)
            {
                LogDrop($"depth_larger_than_rgb rgb={rgbImage.width}x{rgbImage.height} depth={finalWidth}x{finalHeight}");
                return null;
            }

            double rgbAspect = rgbImage.width / (double)rgbImage.height;
            double depthAspect = finalWidth / (double)finalHeight;
            double aspectDelta = Math.Abs(rgbAspect - depthAspect);
            if (aspectDelta > maxAspectRatioDelta)
            {
                LogDrop($"rgb_depth_aspect_mismatch rgb={rgbImage.width}x{rgbImage.height} depth={finalWidth}x{finalHeight} rgb_aspect={rgbAspect:F6} depth_aspect={depthAspect:F6} delta={aspectDelta:F6}");
                return null;
            }

            if (!TryConvertRgbToFinalResolution(rgbImage, finalWidth, finalHeight, out byte[] rgb24, out string rgbReason))
            {
                LogDrop(rgbReason);
                return null;
            }

            if (!TryReadDepthMillimeters(depthImage, out ushort[] depthMillimeters, out int invalidDepthCount, out string depthReason))
            {
                LogDrop(depthReason);
                return null;
            }

            FPCameraIntrinsics scaledIntrinsics = ScaleIntrinsics(intrinsics, finalWidth, finalHeight);
            bool isRegistration = tcpSender.NeedsRegistrationFrame;

            byte[] mask = null;
            RectInt maskBBox = default;
            if (isRegistration)
            {
                if (maskSource == null)
                {
                    LogDrop("registration_requires_mask_source");
                    return null;
                }

                if (!maskSource.TryBuildMask(finalWidth, finalHeight, rgbTimestamp, out mask, out maskBBox, out double maskTimestamp, out string maskReason))
                {
                    LogDrop($"registration_mask_unavailable {maskReason}");
                    return null;
                }

                Log($"Using registration mask bbox={maskBBox}, mask_ts={maskTimestamp:F6}, rgb_ts={rgbTimestamp:F6}");
            }

            Log($"captured register={isRegistration} rgb_ts={rgbTimestamp:F6} depth_ts={depthTimestamp:F6} delta_ms={deltaMs:F2} final={finalWidth}x{finalHeight} K=[{scaledIntrinsics.fx:F3},{scaledIntrinsics.fy:F3},{scaledIntrinsics.cx:F3},{scaledIntrinsics.cy:F3}] invalid_depth={invalidDepthCount}");

            return new RawFrameSample
            {
                rgb24 = rgb24,
                depthMillimeters = depthMillimeters,
                maskU8 = mask,
                width = finalWidth,
                height = finalHeight,
                intrinsics = scaledIntrinsics,
                isRegistration = isRegistration,
                rgbTimestamp = rgbTimestamp,
                depthTimestamp = depthTimestamp,
                deltaMs = deltaMs,
                invalidDepthCount = invalidDepthCount,
                maskBBox = maskBBox
            };
        }

        bool ShouldCaptureNow()
        {
            if (targetFps <= 0)
            {
                return true;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            double minInterval = 1.0 / targetFps;
            if (now - lastCaptureRealtime < minInterval)
            {
                return false;
            }

            lastCaptureRealtime = now;
            return true;
        }

        bool TryConvertRgbToFinalResolution(XRCpuImage rgbImage, int finalWidth, int finalHeight, out byte[] rgb24, out string reason)
        {
            rgb24 = null;
            reason = null;

            XRCpuImage.ConversionParams conversionParams = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, rgbImage.width, rgbImage.height),
                outputDimensions = new Vector2Int(finalWidth, finalHeight),
                outputFormat = TextureFormat.RGB24,
                transformation = XRCpuImage.Transformation.None
            };

            int dataSize;
            try
            {
                dataSize = rgbImage.GetConvertedDataSize(conversionParams);
            }
            catch (Exception ex)
            {
                reason = $"rgb_convert_size_failed {ex.Message}";
                return false;
            }

            NativeArray<byte> buffer = new NativeArray<byte>(dataSize, Allocator.Temp);
            try
            {
                rgbImage.Convert(conversionParams, new NativeSlice<byte>(buffer));
                rgb24 = buffer.ToArray();
                return true;
            }
            catch (Exception ex)
            {
                reason = $"rgb_convert_failed {ex.Message}";
                return false;
            }
            finally
            {
                buffer.Dispose();
            }
        }

        bool TryReadDepthMillimeters(XRCpuImage depthImage, out ushort[] depthMillimeters, out int invalidDepthCount, out string reason)
        {
            depthMillimeters = null;
            invalidDepthCount = 0;
            reason = null;

            int width = depthImage.width;
            int height = depthImage.height;
            int pixels = width * height;
            depthMillimeters = new ushort[pixels];

            try
            {
                XRCpuImage.Plane plane = depthImage.GetPlane(0);
                byte[] rawBytes = plane.data.ToArray();

                switch (depthImage.format)
                {
                    case XRCpuImage.Format.DepthFloat32:
                        FillDepthFromFloat32(rawBytes, plane.rowStride, plane.pixelStride, width, height, depthMillimeters, out invalidDepthCount);
                        return true;

                    case XRCpuImage.Format.DepthUint16:
                        FillDepthFromUInt16(rawBytes, plane.rowStride, plane.pixelStride, width, height, depthMillimeters, out invalidDepthCount);
                        return true;

                    default:
                        reason = $"unsupported_raw_depth_format {depthImage.format}";
                        return false;
                }
            }
            catch (Exception ex)
            {
                reason = $"read_raw_depth_failed {ex.Message}";
                return false;
            }
        }

        static void FillDepthFromFloat32(byte[] raw, int rowStride, int pixelStride, int width, int height, ushort[] output, out int invalidCount)
        {
            invalidCount = 0;
            int stride = pixelStride > 0 ? pixelStride : sizeof(float);
            for (int y = 0; y < height; ++y)
            {
                int rowOffset = y * rowStride;
                for (int x = 0; x < width; ++x)
                {
                    int offset = rowOffset + x * stride;
                    float meters = BitConverter.ToSingle(raw, offset);
                    ushort mm = MetersToMillimeters(meters);
                    if (mm == 0)
                    {
                        invalidCount++;
                    }
                    output[y * width + x] = mm;
                }
            }
        }

        static void FillDepthFromUInt16(byte[] raw, int rowStride, int pixelStride, int width, int height, ushort[] output, out int invalidCount)
        {
            invalidCount = 0;
            int stride = pixelStride > 0 ? pixelStride : sizeof(ushort);
            for (int y = 0; y < height; ++y)
            {
                int rowOffset = y * rowStride;
                for (int x = 0; x < width; ++x)
                {
                    int offset = rowOffset + x * stride;
                    ushort mm = BitConverter.ToUInt16(raw, offset);
                    if (mm == 0)
                    {
                        invalidCount++;
                    }
                    output[y * width + x] = mm;
                }
            }
        }

        static ushort MetersToMillimeters(float meters)
        {
            if (float.IsNaN(meters) || float.IsInfinity(meters) || meters <= 0.0f)
            {
                return 0;
            }

            double millimeters = Math.Round(meters * 1000.0);
            if (millimeters <= 0.0 || millimeters > ushort.MaxValue)
            {
                return 0;
            }

            return (ushort)millimeters;
        }

        static FPCameraIntrinsics ScaleIntrinsics(XRCameraIntrinsics intrinsics, int finalWidth, int finalHeight)
        {
            Vector2Int sourceResolution = intrinsics.resolution;
            double scaleX = finalWidth / (double)sourceResolution.x;
            double scaleY = finalHeight / (double)sourceResolution.y;
            return new FPCameraIntrinsics(
                intrinsics.focalLength.x * scaleX,
                intrinsics.focalLength.y * scaleY,
                intrinsics.principalPoint.x * scaleX,
                intrinsics.principalPoint.y * scaleY);
        }

        void EnqueueRegistrationSample(RawFrameSample sample)
        {
            lock (encoderGate)
            {
                if (pendingRegistrationSample != null)
                {
                    LogDrop("registration_sample_already_pending");
                    return;
                }

                pendingRegistrationSample = sample;
                sampleAvailable.Set();
            }
        }

        void EnqueueTrackingSample(RawFrameSample sample)
        {
            lock (encoderGate)
            {
                if (latestTrackingSample != null)
                {
                    droppedTrackingEncodeCount++;
                }

                latestTrackingSample = sample;
                sampleAvailable.Set();
            }
        }

        void EncoderLoop()
        {
            while (!stopRequested)
            {
                RawFrameSample sample = null;
                lock (encoderGate)
                {
                    if (pendingRegistrationSample != null)
                    {
                        sample = pendingRegistrationSample;
                        pendingRegistrationSample = null;
                    }
                    else if (latestTrackingSample != null)
                    {
                        sample = latestTrackingSample;
                        latestTrackingSample = null;
                    }
                }

                if (sample == null)
                {
                    sampleAvailable.WaitOne(20);
                    continue;
                }

                try
                {
                    DateTime start = DateTime.UtcNow;
                    int frameIndex = nextIndex;
                    string frameId = FPFrameProtocol.FrameIdFromTimestamp(sample.rgbTimestamp, frameIndex);
                    FPEncodedFrame encoded = FPFrameProtocol.BuildFrameMessage(
                        sample.rgb24,
                        sample.depthMillimeters,
                        sample.maskU8,
                        sample.width,
                        sample.height,
                        sample.intrinsics,
                        frameId,
                        frameIndex,
                        sample.rgbTimestamp,
                        rgbCodec,
                        jpegQuality);

                    bool accepted = sample.isRegistration
                        ? tcpSender.EnqueueRegistrationFrame(encoded)
                        : tcpSender.EnqueueTrackingFrame(encoded);

                    double encodeMs = (DateTime.UtcNow - start).TotalMilliseconds;
                    if (accepted)
                    {
                        nextIndex++;
                        Log($"encoded frame_id={frameId} index={frameIndex} register={sample.isRegistration} encode_ms={encodeMs:F2} message_bytes={encoded.message.Length} dropped_tracking_encode={droppedTrackingEncodeCount}");
                    }
                    else
                    {
                        LogDrop($"sender_rejected_encoded_frame frame_id={frameId} index={frameIndex} register={sample.isRegistration}");
                    }
                }
                catch (Exception ex)
                {
                    Debug.LogError($"[FoundationPoseFrameStreamer] encode failed: {ex}");
                }
            }
        }

        void LogDrop(string reason)
        {
            droppedCaptureCount++;
            if (verboseLogging)
            {
                Debug.Log($"[FoundationPoseFrameStreamer] dropped frame reason={reason} dropped_capture={droppedCaptureCount}");
            }
        }

        void Log(string message)
        {
            if (verboseLogging)
            {
                Debug.Log($"[FoundationPoseFrameStreamer] {message}");
            }
        }
    }
}
