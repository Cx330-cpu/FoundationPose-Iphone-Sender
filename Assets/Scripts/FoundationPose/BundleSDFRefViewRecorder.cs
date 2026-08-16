using System;
using System.Globalization;
using System.IO;
using System.Text;
using Unity.Collections;
using UnityEngine;
using UnityEngine.Experimental.Rendering;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace FoundationPoseStreaming
{
    public sealed class BundleSDFRefViewRecorder : MonoBehaviour
    {
        [Header("AR Foundation")]
        public ARCameraManager cameraManager;
        public AROcclusionManager occlusionManager;
        public YoloBBoxToMaskAdapter maskSource;
        public Transform objectAnchor;

        [Header("Output")]
        public string sessionName = "glass_cup_ref_views";
        public int objectId = 1;
        public bool clearExistingObjectDirectory;
        public bool useDepthResolution = true;
        public int fallbackOutputWidth = 256;
        public int fallbackOutputHeight = 192;

        [Header("Capture")]
        public bool recordOnEnable;
        public int captureCountTarget = 24;
        public float minCaptureIntervalSeconds = 0.4f;
        public float minCameraTranslationMeters = 0.03f;
        public float minCameraRotationDegrees = 8.0f;
        public double maxRgbDepthDeltaMs = 80.0;
        public double maxAspectRatioDelta = 0.01;

        [Header("UI")]
        public bool drawControls = true;
        public int controlWidth = 560;
        public int controlHeight = 100;
        public int controlSpacing = 18;
        public int controlBottomMargin = 32;
        public int controlFontSize = 34;

        [Header("Logging")]
        public bool verboseLogging = true;

        bool recording;
        bool captureOnceRequested;
        bool sessionPrepared;
        bool hasWorldFromObject;
        bool hasLastCapturePose;
        int nextFrameIndex;
        double lastCaptureRealtime;
        string activeSessionName;
        string objectDirectory;
        Matrix4x4 worldFromObject;
        Vector3 lastCapturePosition;
        Quaternion lastCaptureRotation;
        GUIStyle controlStyle;

        void Reset()
        {
            cameraManager = FindObjectOfType<ARCameraManager>();
            occlusionManager = FindObjectOfType<AROcclusionManager>();
            maskSource = FindObjectOfType<YoloBBoxToMaskAdapter>();
        }

        void OnEnable()
        {
            if (cameraManager == null)
            {
                Debug.LogError("[BundleSDFRefViewRecorder] Missing ARCameraManager.");
                enabled = false;
                return;
            }

            if (occlusionManager == null)
            {
                Debug.LogError("[BundleSDFRefViewRecorder] Missing AROcclusionManager.");
                enabled = false;
                return;
            }

            cameraManager.frameReceived += OnCameraFrameReceived;
            if (recordOnEnable)
            {
                StartRecording();
            }
        }

        void OnDisable()
        {
            if (cameraManager != null)
            {
                cameraManager.frameReceived -= OnCameraFrameReceived;
            }
        }

        [ContextMenu("BundleSDF/Start Recording")]
        public void StartRecording()
        {
            if (sessionPrepared && !recording && nextFrameIndex > 0)
            {
                ResetSessionState();
            }

            recording = true;
            captureOnceRequested = false;
            PrepareSessionIfNeeded();
            Log($"manual_recording_started captured={nextFrameIndex} target={captureCountTarget} path={objectDirectory}");
        }

        [ContextMenu("BundleSDF/Stop Recording")]
        public void StopRecording()
        {
            recording = false;
            WriteSelectFrames();
            Log($"recording_stopped frames={nextFrameIndex} path={objectDirectory}");
        }

        [ContextMenu("BundleSDF/Capture One Frame")]
        public void CaptureOneFrame()
        {
            if (!recording)
            {
                Log("capture_ignored_start_recording_first");
                return;
            }

            if (captureCountTarget > 0 && nextFrameIndex >= captureCountTarget)
            {
                Log($"capture_ignored_target_reached captured={nextFrameIndex} target={captureCountTarget}");
                return;
            }

            captureOnceRequested = true;
            Log($"capture_one_requested next_frame={nextFrameIndex:D6}");
        }

        [ContextMenu("BundleSDF/Log Output Directory")]
        public void LogOutputDirectory()
        {
            PrepareSessionIfNeeded();
            GUIUtility.systemCopyBuffer = objectDirectory;
            Log($"output_directory={objectDirectory} copied_to_clipboard=1");
        }

        void OnCameraFrameReceived(ARCameraFrameEventArgs args)
        {
            if (!recording || !captureOnceRequested)
            {
                return;
            }

            if (recording && captureCountTarget > 0 && nextFrameIndex >= captureCountTarget)
            {
                StopRecording();
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
                    TryCaptureFrame(rgbImage, depthImage, intrinsics);
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

        void TryCaptureFrame(XRCpuImage rgbImage, XRCpuImage depthImage, XRCameraIntrinsics intrinsics)
        {
            PrepareSessionIfNeeded();

            double deltaMs = Math.Abs(rgbImage.timestamp - depthImage.timestamp) * 1000.0;
            if (deltaMs > maxRgbDepthDeltaMs)
            {
                LogDrop($"rgb_depth_timestamp_delta_too_large rgb_ts={rgbImage.timestamp:F6} depth_ts={depthImage.timestamp:F6} delta_ms={deltaMs:F2}");
                return;
            }

            int outputWidth = useDepthResolution ? depthImage.width : Mathf.Clamp(fallbackOutputWidth, 16, rgbImage.width);
            int outputHeight = useDepthResolution ? depthImage.height : Mathf.Clamp(fallbackOutputHeight, 16, rgbImage.height);
            if (outputWidth <= 0 || outputHeight <= 0)
            {
                LogDrop($"invalid_output_dimensions {outputWidth}x{outputHeight}");
                return;
            }

            if (outputWidth > rgbImage.width || outputHeight > rgbImage.height)
            {
                LogDrop($"output_larger_than_rgb rgb={rgbImage.width}x{rgbImage.height} output={outputWidth}x{outputHeight}");
                return;
            }

            double rgbAspect = rgbImage.width / (double)rgbImage.height;
            double outputAspect = outputWidth / (double)outputHeight;
            double aspectDelta = Math.Abs(rgbAspect - outputAspect);
            if (aspectDelta > maxAspectRatioDelta)
            {
                LogDrop($"rgb_output_aspect_mismatch rgb={rgbImage.width}x{rgbImage.height} output={outputWidth}x{outputHeight} rgb_aspect={rgbAspect:F6} output_aspect={outputAspect:F6} delta={aspectDelta:F6}");
                return;
            }

            if (!TryConvertRgb(rgbImage, outputWidth, outputHeight, out byte[] rgb24, out string rgbReason))
            {
                LogDrop(rgbReason);
                return;
            }

            if (!TryReadDepthMillimeters(depthImage, outputWidth, outputHeight, out ushort[] depthMillimeters, out int invalidDepthCount, out string depthReason))
            {
                LogDrop(depthReason);
                return;
            }

            if (maskSource == null)
            {
                LogDrop("missing_mask_source");
                return;
            }

            if (!maskSource.TryBuildMask(outputWidth, outputHeight, rgbImage.timestamp, out byte[] maskU8, out RectInt maskBBox, out double maskTimestamp, out string maskReason))
            {
                LogDrop($"mask_unavailable {maskReason}");
                return;
            }

            FPCameraIntrinsics scaledIntrinsics = ScaleIntrinsics(intrinsics, outputWidth, outputHeight);
            Matrix4x4 unityCamInOb = ComputeUnityCameraFromObject();
            Matrix4x4 camInOb = UnityPoseToOpenCvPose(unityCamInOb);
            string frameName = nextFrameIndex.ToString("D6", CultureInfo.InvariantCulture);

            WriteFrameFiles(frameName, rgb24, depthMillimeters, maskU8, outputWidth, outputHeight, camInOb);
            WriteK(scaledIntrinsics);

            int depthValidCount = depthMillimeters.Length - invalidDepthCount;
            Log(
                "[BUNDLE-REC][CAPTURE] " +
                $"frame={frameName} size={outputWidth}x{outputHeight} " +
                $"rgb_ts={rgbImage.timestamp:F9} depth_ts={depthImage.timestamp:F9} delta_ms={deltaMs:F2} " +
                $"K=({scaledIntrinsics.fx:F3},{scaledIntrinsics.fy:F3},{scaledIntrinsics.cx:F3},{scaledIntrinsics.cy:F3}) " +
                $"mask_bbox=({maskBBox.xMin},{maskBBox.yMin},{maskBBox.xMax},{maskBBox.yMax}) mask_ts={maskTimestamp:F9} " +
                $"depth_valid_count={depthValidCount} path={objectDirectory}");
            Log(
                "[BUNDLE-REC][POSE] " +
                $"frame={frameName} " +
                $"unity_world_from_camera={MatrixToCompactString(cameraManager.transform.localToWorldMatrix)} " +
                $"unity_camera_from_world={MatrixToCompactString(cameraManager.transform.worldToLocalMatrix)} " +
                $"unity_camera_from_object={MatrixToCompactString(unityCamInOb)} " +
                $"opencv_camera_from_object={MatrixToCompactString(camInOb)}");
            Log(
                "[BUNDLE-REC][POSE-CHECK] " +
                $"frame={frameName} " +
                $"detR={RotationDeterminant(camInOb):F6} " +
                $"handedness_dot={HandednessDot(camInOb):F6} " +
                "expected_detR=+1 expected_handedness_dot=+1");

            Transform cameraTransform = cameraManager.transform;
            lastCapturePosition = cameraTransform.position;
            lastCaptureRotation = cameraTransform.rotation;
            hasLastCapturePose = true;
            lastCaptureRealtime = Time.realtimeSinceStartupAsDouble;
            captureOnceRequested = false;
            nextFrameIndex++;
            WriteSelectFrames();

            if (captureCountTarget > 0 && nextFrameIndex >= captureCountTarget)
            {
                StopRecording();
            }
        }

        void PrepareSessionIfNeeded()
        {
            if (sessionPrepared)
            {
                return;
            }

            string baseDirectory = Path.Combine(Application.persistentDataPath, "BundleSDFRefViews");
            activeSessionName = string.IsNullOrWhiteSpace(sessionName) ? "glass_cup_ref_views" : SanitizePathSegment(sessionName);
            objectDirectory = Path.Combine(baseDirectory, activeSessionName, $"ob_{Mathf.Max(0, objectId):D7}");

            if (Directory.Exists(objectDirectory))
            {
                if (clearExistingObjectDirectory)
                {
                    Directory.Delete(objectDirectory, true);
                }
                else
                {
                    activeSessionName = $"{activeSessionName}_{DateTime.Now:yyyyMMdd_HHmmss}";
                    objectDirectory = Path.Combine(baseDirectory, activeSessionName, $"ob_{Mathf.Max(0, objectId):D7}");
                }
            }

            Directory.CreateDirectory(Path.Combine(objectDirectory, "rgb"));
            Directory.CreateDirectory(Path.Combine(objectDirectory, "depth_enhanced"));
            Directory.CreateDirectory(Path.Combine(objectDirectory, "mask"));
            Directory.CreateDirectory(Path.Combine(objectDirectory, "cam_in_ob"));
            WriteSelectFrames();

            if (objectAnchor != null)
            {
                worldFromObject = objectAnchor.localToWorldMatrix;
                hasWorldFromObject = true;
            }
            else
            {
                hasWorldFromObject = false;
                Log("object_anchor_missing will_use_first_captured_camera_pose_as_world_from_object");
            }

            sessionPrepared = true;
        }

        void ResetSessionState()
        {
            sessionPrepared = false;
            captureOnceRequested = false;
            hasWorldFromObject = false;
            hasLastCapturePose = false;
            nextFrameIndex = 0;
            lastCaptureRealtime = 0.0;
            activeSessionName = null;
            objectDirectory = null;
        }

        Matrix4x4 ComputeUnityCameraFromObject()
        {
            if (!hasWorldFromObject)
            {
                worldFromObject = objectAnchor != null ? objectAnchor.localToWorldMatrix : cameraManager.transform.localToWorldMatrix;
                hasWorldFromObject = true;
            }

            Matrix4x4 worldInObject = worldFromObject.inverse;
            return cameraManager.transform.worldToLocalMatrix * worldInObject;
        }

        static Matrix4x4 UnityPoseToOpenCvPose(Matrix4x4 unityCameraFromObject)
        {
            Matrix4x4 unityToOpenCv = Matrix4x4.Scale(new Vector3(1.0f, -1.0f, 1.0f));
            return unityToOpenCv * unityCameraFromObject * unityToOpenCv;
        }

        void WriteFrameFiles(
            string frameName,
            byte[] rgb24,
            ushort[] depthMillimeters,
            byte[] maskU8,
            int width,
            int height,
            Matrix4x4 camInOb)
        {
            File.WriteAllBytes(Path.Combine(objectDirectory, "rgb", $"{frameName}.png"), EncodeRgbPng(rgb24, width, height));
            File.WriteAllBytes(Path.Combine(objectDirectory, "depth_enhanced", $"{frameName}.png"), EncodeDepthPng(depthMillimeters, width, height));
            File.WriteAllBytes(Path.Combine(objectDirectory, "mask", $"{frameName}.png"), EncodeMaskPng(maskU8, width, height));
            File.WriteAllText(Path.Combine(objectDirectory, "cam_in_ob", $"{frameName}.txt"), MatrixToText(camInOb));
        }

        static byte[] EncodeRgbPng(byte[] rgb24, int width, int height)
        {
            return ImageConversion.EncodeArrayToPNG(
                rgb24,
                GraphicsFormat.R8G8B8_UNorm,
                (uint)width,
                (uint)height,
                0);
        }

        static byte[] EncodeDepthPng(ushort[] depthMillimeters, int width, int height)
        {
            byte[] depthBytes = new byte[depthMillimeters.Length * sizeof(ushort)];
            Buffer.BlockCopy(depthMillimeters, 0, depthBytes, 0, depthBytes.Length);
            return ImageConversion.EncodeArrayToPNG(
                depthBytes,
                GraphicsFormat.R16_UNorm,
                (uint)width,
                (uint)height,
                0);
        }

        static byte[] EncodeMaskPng(byte[] maskU8, int width, int height)
        {
            return ImageConversion.EncodeArrayToPNG(
                maskU8,
                GraphicsFormat.R8_UNorm,
                (uint)width,
                (uint)height,
                0);
        }

        void WriteK(FPCameraIntrinsics intrinsics)
        {
            string text =
                $"{Format(intrinsics.fx)} 0 {Format(intrinsics.cx)}\n" +
                $"0 {Format(intrinsics.fy)} {Format(intrinsics.cy)}\n" +
                "0 0 1\n";
            File.WriteAllText(Path.Combine(objectDirectory, "K.txt"), text);
        }

        void WriteSelectFrames()
        {
            if (string.IsNullOrEmpty(objectDirectory))
            {
                return;
            }

            StringBuilder builder = new StringBuilder();
            builder.AppendLine("frames:");
            for (int i = 0; i < nextFrameIndex; ++i)
            {
                builder.Append("  - ");
                builder.AppendLine(i.ToString(CultureInfo.InvariantCulture));
            }

            File.WriteAllText(Path.Combine(objectDirectory, "select_frames.yml"), builder.ToString());
        }

        static bool TryConvertRgb(XRCpuImage rgbImage, int width, int height, out byte[] rgb24, out string reason)
        {
            rgb24 = null;
            reason = null;
            XRCpuImage.ConversionParams conversionParams = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, rgbImage.width, rgbImage.height),
                outputDimensions = new Vector2Int(width, height),
                outputFormat = TextureFormat.RGB24,
                transformation = XRCpuImage.Transformation.None
            };

            NativeArray<byte> buffer = default;
            try
            {
                buffer = new NativeArray<byte>(rgbImage.GetConvertedDataSize(conversionParams), Allocator.Temp);
                rgbImage.Convert(conversionParams, buffer);
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
                if (buffer.IsCreated)
                {
                    buffer.Dispose();
                }
            }
        }

        static bool TryReadDepthMillimeters(XRCpuImage depthImage, int width, int height, out ushort[] depthMillimeters, out int invalidDepthCount, out string reason)
        {
            depthMillimeters = null;
            invalidDepthCount = 0;
            reason = null;

            if (depthImage.width != width || depthImage.height != height)
            {
                reason = $"depth_size_mismatch depth={depthImage.width}x{depthImage.height} output={width}x{height}";
                return false;
            }

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

        static FPCameraIntrinsics ScaleIntrinsics(XRCameraIntrinsics intrinsics, int width, int height)
        {
            Vector2Int sourceResolution = intrinsics.resolution;
            double scaleX = width / (double)sourceResolution.x;
            double scaleY = height / (double)sourceResolution.y;
            return new FPCameraIntrinsics(
                intrinsics.focalLength.x * scaleX,
                intrinsics.focalLength.y * scaleY,
                intrinsics.principalPoint.x * scaleX,
                intrinsics.principalPoint.y * scaleY);
        }

        void OnGUI()
        {
            if (!drawControls)
            {
                return;
            }

            if (controlStyle == null)
            {
                controlStyle = new GUIStyle(GUI.skin.button)
                {
                    fontSize = Mathf.Max(18, controlFontSize)
                };
            }
            else
            {
                controlStyle.fontSize = Mathf.Max(18, controlFontSize);
            }

            Rect safeArea = Screen.safeArea;
            float width = Mathf.Min(Mathf.Max(220, controlWidth), Mathf.Max(220, safeArea.width - 32));
            float height = Mathf.Max(56, controlHeight);
            float spacing = Mathf.Max(0, controlSpacing);
            float totalHeight = height * 3 + spacing * 2;
            float x = safeArea.x + (safeArea.width - width) * 0.5f;
            float safeBottomGuiY = Screen.height - safeArea.yMin;
            float y = safeBottomGuiY - totalHeight - Mathf.Max(0, controlBottomMargin);
            y = Mathf.Max(16, y);

            if (!recording)
            {
                if (GUI.Button(new Rect(x, y, width, height), "Start BundleSDF Rec", controlStyle))
                {
                    StartRecording();
                }
            }
            else if (GUI.Button(new Rect(x, y, width, height), $"Stop Rec ({nextFrameIndex}/{captureCountTarget})", controlStyle))
            {
                StopRecording();
            }

            bool canCapture = recording && (captureCountTarget <= 0 || nextFrameIndex < captureCountTarget);
            GUI.enabled = canCapture;
            string captureLabel = recording
                ? $"Take Ref View ({nextFrameIndex}/{captureCountTarget})"
                : "Press Start First";
            if (GUI.Button(new Rect(x, y + height + spacing, width, height), captureLabel, controlStyle))
            {
                CaptureOneFrame();
            }

            GUI.enabled = true;
            if (GUI.Button(new Rect(x, y + (height + spacing) * 2, width, height), "Log Output Path", controlStyle))
            {
                LogOutputDirectory();
            }
        }

        static string MatrixToText(Matrix4x4 matrix)
        {
            StringBuilder builder = new StringBuilder();
            for (int row = 0; row < 4; ++row)
            {
                for (int col = 0; col < 4; ++col)
                {
                    if (col > 0)
                    {
                        builder.Append(' ');
                    }

                    builder.Append(Format(matrix[row, col]));
                }

                builder.AppendLine();
            }

            return builder.ToString();
        }

        static string MatrixToCompactString(Matrix4x4 matrix)
        {
            return MatrixToText(matrix).Replace('\n', ';');
        }

        static float RotationDeterminant(Matrix4x4 matrix)
        {
            Vector3 c0 = RotationColumn(matrix, 0);
            Vector3 c1 = RotationColumn(matrix, 1);
            Vector3 c2 = RotationColumn(matrix, 2);
            return Vector3.Dot(Vector3.Cross(c0, c1), c2);
        }

        static float HandednessDot(Matrix4x4 matrix)
        {
            Vector3 c0 = RotationColumn(matrix, 0);
            Vector3 c1 = RotationColumn(matrix, 1);
            Vector3 c2 = RotationColumn(matrix, 2);
            return Vector3.Dot(Vector3.Cross(c0, c1).normalized, c2.normalized);
        }

        static Vector3 RotationColumn(Matrix4x4 matrix, int column)
        {
            return new Vector3(matrix[0, column], matrix[1, column], matrix[2, column]);
        }

        static string Format(double value)
        {
            return value.ToString("G9", CultureInfo.InvariantCulture);
        }

        static string SanitizePathSegment(string value)
        {
            StringBuilder builder = new StringBuilder(value.Length);
            foreach (char c in value)
            {
                builder.Append(Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c);
            }

            return builder.ToString();
        }

        void LogDrop(string reason)
        {
            if (verboseLogging)
            {
                Debug.Log($"[BundleSDFRefViewRecorder] dropped reason={reason}");
            }
        }

        void Log(string message)
        {
            if (verboseLogging)
            {
                Debug.Log($"[BundleSDFRefViewRecorder] {message}");
            }
        }
    }
}
