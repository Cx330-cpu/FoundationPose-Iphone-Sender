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
        public float targetFps = 30.0f;
        public FPRgbCodec rgbCodec = FPRgbCodec.Jpeg;
        [Range(1, 100)]
        public int jpegQuality = 85;
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
        byte[] lastValidMaskU8;
        RectInt lastValidMaskBBox;
        double lastValidMaskTimestamp;
        int lastValidMaskWidth;
        int lastValidMaskHeight;

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
            public double maskTimestamp;
            public int maskPixelCount;
            public string maskSourceKind;
            public string maskReason;
            public bool maskRequested;
            public int maskBurstRemaining;
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
            bool maskRequested = false;
            int maskBurstRemaining = 0;
            string maskRequestReason = null;
            string maskRequestFrameId = null;
            if (!isRegistration)
            {
                maskRequested = tcpSender.TryConsumeMaskBurstFrame(out maskBurstRemaining, out maskRequestReason, out maskRequestFrameId);
            }

            byte[] mask;
            RectInt maskBBox;
            double maskTimestamp;
            int maskPixelCount;
            string maskSourceKind;
            string maskReason;
            if (!TryBuildFrameMask(finalWidth, finalHeight, rgbTimestamp, isRegistration, maskRequested, maskRequestReason, out mask, out maskBBox, out maskTimestamp, out maskPixelCount, out maskSourceKind, out maskReason))
            {
                return null;
            }

            Log($"captured register={isRegistration} rgb_ts={rgbTimestamp:F6} depth_ts={depthTimestamp:F6} delta_ms={deltaMs:F2} final={finalWidth}x{finalHeight} K=[{scaledIntrinsics.fx:F3},{scaledIntrinsics.fy:F3},{scaledIntrinsics.cx:F3},{scaledIntrinsics.cy:F3}] invalid_depth={invalidDepthCount} mask_requested={maskRequested} mask_burst_remaining={maskBurstRemaining} mask_request_frame_id={maskRequestFrameId ?? "none"} mask_source={maskSourceKind} mask_pixels={maskPixelCount} mask_reason={maskReason ?? "ok"}");

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
                maskBBox = maskBBox,
                maskTimestamp = maskTimestamp,
                maskPixelCount = maskPixelCount,
                maskSourceKind = maskSourceKind,
                maskReason = maskReason,
                maskRequested = maskRequested,
                maskBurstRemaining = maskBurstRemaining
            };
        }

        bool TryBuildFrameMask(
            int width,
            int height,
            double rgbTimestamp,
            bool isRegistration,
            bool maskRequested,
            string maskRequestReason,
            out byte[] mask,
            out RectInt maskBBox,
            out double maskTimestamp,
            out int maskPixelCount,
            out string maskSourceKind,
            out string maskReason)
        {
            mask = null;
            maskBBox = default;
            maskTimestamp = 0.0;
            maskPixelCount = 0;
            maskSourceKind = "missing";
            maskReason = null;

            if (!isRegistration && !maskRequested)
            {
                maskSourceKind = "none";
                return true;
            }

            if (maskSource != null &&
                maskSource.TryBuildMask(width, height, rgbTimestamp, out mask, out maskBBox, out maskTimestamp, out maskReason))
            {
                maskPixelCount = CountMaskPixels(mask);
                maskSourceKind = maskRequested ? "requested_bbox_mask" : "bbox_mask";
                RememberLastValidMask(mask, width, height, maskBBox, maskTimestamp);
                Log($"Using {maskSourceKind} bbox={maskBBox}, mask_ts={maskTimestamp:F6}, rgb_ts={rgbTimestamp:F6}, mask_pixels={maskPixelCount}");
                return true;
            }

            string unavailableReason = maskSource == null ? "no_mask_source" : maskReason;
            if (isRegistration)
            {
                LogDrop($"registration_mask_unavailable {unavailableReason}");
                return false;
            }

            if (TryGetReusableMask(width, height, out mask, out maskBBox, out maskTimestamp))
            {
                maskPixelCount = CountMaskPixels(mask);
                maskSourceKind = maskRequested ? "requested_reused_mask" : "reused_bbox_mask";
                maskReason = string.IsNullOrEmpty(maskRequestReason) ? unavailableReason : $"{maskRequestReason}; {unavailableReason}";
                Debug.LogWarning(
                    "[FoundationPoseFrameStreamer] Reused previous tracking mask " +
                    $"reason={maskReason} final={width}x{height} bbox={maskBBox} " +
                    $"mask_pixels={maskPixelCount} mask_age_ms={Math.Abs(rgbTimestamp - maskTimestamp) * 1000.0:F2}");
                return true;
            }

            maskReason = string.IsNullOrEmpty(maskRequestReason) ? unavailableReason : $"{maskRequestReason}; {unavailableReason}";
            Debug.LogWarning(
                "[FoundationPoseFrameStreamer] MASK_MISSING " +
                $"reason={maskReason} final={width}x{height} rgb_ts={rgbTimestamp:F6}");
            return true;
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

        void RememberLastValidMask(byte[] mask, int width, int height, RectInt maskBBox, double maskTimestamp)
        {
            lastValidMaskU8 = mask;
            lastValidMaskWidth = width;
            lastValidMaskHeight = height;
            lastValidMaskBBox = maskBBox;
            lastValidMaskTimestamp = maskTimestamp;
        }

        bool TryGetReusableMask(int width, int height, out byte[] mask, out RectInt maskBBox, out double maskTimestamp)
        {
            if (lastValidMaskU8 == null || lastValidMaskWidth != width || lastValidMaskHeight != height)
            {
                mask = null;
                maskBBox = default;
                maskTimestamp = 0.0;
                return false;
            }

            mask = lastValidMaskU8;
            maskBBox = lastValidMaskBBox;
            maskTimestamp = lastValidMaskTimestamp;
            return true;
        }

        static int CountMaskPixels(byte[] mask)
        {
            if (mask == null)
            {
                return 0;
            }

            int count = 0;
            for (int i = 0; i < mask.Length; ++i)
            {
                if (mask[i] != 0)
                {
                    count++;
                }
            }

            return count;
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
                    encoded.maskPixelCount = sample.maskPixelCount;
                    encoded.maskSourceKind = sample.maskSourceKind;
                    encoded.maskReason = sample.maskReason;
                    encoded.maskRequested = sample.maskRequested;
                    encoded.maskBurstRemaining = sample.maskBurstRemaining;

                    bool accepted = sample.isRegistration
                        ? tcpSender.EnqueueRegistrationFrame(encoded)
                        : tcpSender.EnqueueTrackingFrame(encoded);

                    double encodeMs = (DateTime.UtcNow - start).TotalMilliseconds;
                    if (accepted)
                    {
                        if (sample.isRegistration && sample.maskU8 != null && maskSource != null && maskSource.enableGeometryTrace)
                        {
                            Debug.Log(
                                "[FP-GEO][SEND] " +
                                $"trace={sample.maskTimestamp:F9} frame_id={frameId} frame_index={frameIndex} " +
                                $"frame_timestamp={sample.rgbTimestamp:F9} final_size={sample.width}x{sample.height} " +
                                $"mask_present=1 mask_bbox={sample.maskBBox} " +
                                $"corners=({sample.maskBBox.xMin},{sample.maskBBox.yMin})-({sample.maskBBox.xMax},{sample.maskBBox.yMax}) " +
                                $"mask_age_ms={Math.Abs(sample.rgbTimestamp - sample.maskTimestamp) * 1000.0:F2}");
                        }
                        nextIndex++;
                        Log(
                            $"encoded frame_id={frameId} index={frameIndex} register={sample.isRegistration} " +
                            $"rgb_size={sample.width}x{sample.height} depth_size={sample.width}x{sample.height} " +
                            $"mask_size={(sample.maskU8 == null ? "none" : sample.width + "x" + sample.height)} " +
                            $"mask_pixels={sample.maskPixelCount} mask_len={encoded.header.mask_len} " +
                            $"mask_format={encoded.header.mask_format} mask_source={sample.maskSourceKind} " +
                            $"mask_requested={sample.maskRequested} mask_burst_remaining={sample.maskBurstRemaining} " +
                            $"mask_reason={sample.maskReason ?? "ok"} encode_rgb_ms={encoded.encodeRgbMs:F2} " +
                            $"encode_depth_ms={encoded.encodeDepthMs:F2} encode_mask_ms={encoded.encodeMaskMs:F2} " +
                            $"encode_ms={encodeMs:F2} " +
                            $"message_bytes={encoded.message.Length} dropped_tracking_encode={droppedTrackingEncodeCount}");
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
