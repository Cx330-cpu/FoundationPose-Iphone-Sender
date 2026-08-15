using ARObjectReplacement.Detection;
using Unity.Collections;
using UnityEngine;
using UnityEngine.XR.ARFoundation;
using UnityEngine.XR.ARSubsystems;

namespace FoundationPoseStreaming
{
    [DefaultExecutionOrder(-100)]
    public sealed class FoundationPoseCenterYoloAdapter : MonoBehaviour
    {
        [Header("References")]
        public ARCameraManager cameraManager;
        public YoloBBoxToMaskAdapter maskAdapter;

        [Header("YOLO")]
        public int yoloInputWidth = 640;
        public int yoloInputHeight = 480;
        [Range(0.01f, 1.0f)]
        public float confidenceThreshold = 0.25f;
        [Range(0.01f, 1.0f)]
        public float iouThreshold = 0.45f;
        public bool restrictToTargetClass = true;
        public int targetClassId = 41;
        public float targetFps = 5.0f;
        public int foundationPoseFinalWidthForDebug = 256;
        public int foundationPoseFinalHeightForDebug = 192;

        [Header("Overlay")]
        public bool drawOverlay = true;
        [Range(0.5f, 6.0f)]
        public float overlayScale = 3.0f;
        public int boxThickness = 2;
        public int crosshairSize = 96;
        public int crosshairGap = 18;
        public int statusFontSize = 42;
        public bool editorPreviewBox = true;

        [Header("Logging")]
        public bool verboseLogging = true;
        public bool enableGeometryTrace = true;
        [Range(0.1f, 5.0f)]
        public float geometryTraceIntervalSeconds = 0.5f;

        readonly YoloCoreMLDetector detector = new YoloCoreMLDetector();
        DetectionResult latestDetection;
        bool hasLatestDetection;
        double lastCaptureRealtime;
        double lastLogRealtime;
        double lastGeometryTraceRealtime;
        Texture2D lineTexture;
        GUIStyle statusStyle;

        void Reset()
        {
            cameraManager = FindObjectOfType<ARCameraManager>();
            maskAdapter = FindObjectOfType<YoloBBoxToMaskAdapter>();
        }

        void OnEnable()
        {
            if (cameraManager == null)
            {
                Debug.LogError("[FoundationPoseCenterYoloAdapter] Missing ARCameraManager.");
                enabled = false;
                return;
            }

            if (maskAdapter == null)
            {
                Debug.LogError("[FoundationPoseCenterYoloAdapter] Missing YoloBBoxToMaskAdapter.");
                enabled = false;
                return;
            }

            cameraManager.frameReceived += OnCameraFrameReceived;
            maskAdapter.enableGeometryTrace = enableGeometryTrace;
        }

        void OnDisable()
        {
            if (cameraManager != null)
            {
                cameraManager.frameReceived -= OnCameraFrameReceived;
            }
        }

        void OnCameraFrameReceived(ARCameraFrameEventArgs args)
        {
            if (!ShouldCaptureNow())
            {
                return;
            }

            if (!detector.IsAvailable)
            {
                hasLatestDetection = false;
                maskAdapter.Clear();
                LogThrottled("YOLO unavailable");
                return;
            }

            if (!cameraManager.TryAcquireLatestCpuImage(out XRCpuImage cameraImage))
            {
                hasLatestDetection = false;
                maskAdapter.Clear();
                LogThrottled("waiting for camera CPU image");
                return;
            }

            using (cameraImage)
            {
                TryDetect(cameraImage);
            }
        }

        void TryDetect(XRCpuImage cameraImage)
        {
            int outputWidth = Mathf.Clamp(yoloInputWidth, 64, cameraImage.width);
            int outputHeight = Mathf.Clamp(yoloInputHeight, 64, cameraImage.height);
            bool traceThisDetection = ShouldLogGeometryTrace();
            if (traceThisDetection)
            {
                Debug.Log(
                    "[FP-GEO][AR] " +
                    $"trace={cameraImage.timestamp:F9} " +
                    $"raw_camera={cameraImage.width}x{cameraImage.height} " +
                    $"yolo_input={outputWidth}x{outputHeight} " +
                    $"screen={Screen.width}x{Screen.height} " +
                    $"screen_orientation={Screen.orientation} " +
                    "cpu_image_origin=ARKitNative conversion_input=full_frame " +
                    "conversion_transform=None output_format=RGBA32 resize=full_frame_to_yolo_input");
            }

            XRCpuImage.ConversionParams conversionParams = new XRCpuImage.ConversionParams
            {
                inputRect = new RectInt(0, 0, cameraImage.width, cameraImage.height),
                outputDimensions = new Vector2Int(outputWidth, outputHeight),
                outputFormat = TextureFormat.RGBA32,
                transformation = XRCpuImage.Transformation.None
            };

            NativeArray<byte> rgbaBytes = default;
            try
            {
                int byteCount = cameraImage.GetConvertedDataSize(conversionParams);
                rgbaBytes = new NativeArray<byte>(byteCount, Allocator.Temp);
                cameraImage.Convert(conversionParams, rgbaBytes);

                bool success = detector.TryDetectCenterObject(
                    rgbaBytes.ToArray(),
                    outputWidth,
                    outputHeight,
                    confidenceThreshold,
                    iouThreshold,
                    restrictToTargetClass ? targetClassId : -1,
                    cameraImage.timestamp,
                    traceThisDetection,
                    out DetectionResult detection);

                if (!success)
                {
                    hasLatestDetection = false;
                    maskAdapter.Clear();
                    LogThrottled("YOLO scanning");
                    return;
                }

                Rect normalizedTopLeft = detection.RawCameraNormalizedTopLeft;

                latestDetection = detection;
                hasLatestDetection = true;
                maskAdapter.enableGeometryTrace = enableGeometryTrace;
                if (traceThisDetection)
                {
                    Debug.Log(
                        "[FP-GEO][BRIDGE] " +
                        $"trace={cameraImage.timestamp:F9} " +
                        $"bbox_norm_top_left={RectToString(normalizedTopLeft)} " +
                        $"corners=({normalizedTopLeft.xMin:F6},{normalizedTopLeft.yMin:F6})-({normalizedTopLeft.xMax:F6},{normalizedTopLeft.yMax:F6}) " +
                        "source=RawCameraNormalizedTopLeft " +
                        $"coordinate_space={FPBBoxCoordinateSpace.NormalizedTopLeft} " +
                        $"raw_camera={cameraImage.width}x{cameraImage.height} " +
                        $"yolo_input={outputWidth}x{outputHeight} " +
                        $"foundationpose_final_debug={foundationPoseFinalWidthForDebug}x{foundationPoseFinalHeightForDebug}");
                }
                maskAdapter.SetBoundingBox(normalizedTopLeft, cameraImage.timestamp, FPBBoxCoordinateSpace.NormalizedTopLeft);

                if (verboseLogging)
                {
                    Debug.Log(
                        "[FoundationPoseCenterYoloAdapter] bbox_trace " +
                        $"class={detection.ClassId} conf={detection.Confidence:F3} " +
                        $"raw_camera={cameraImage.width}x{cameraImage.height} yolo_input={outputWidth}x{outputHeight} screen={Screen.width}x{Screen.height} " +
                        $"detection_result_screen_top_left_px={RectToString(detection.PixelRect)} " +
                        $"raw_camera_norm_top_left={RectToString(detection.RawCameraNormalizedTopLeft)} " +
                        $"set_bbox_norm_top_left={RectToString(normalizedTopLeft)} coordinate_space={FPBBoxCoordinateSpace.NormalizedTopLeft} source=RawCameraNormalizedTopLeft " +
                        $"foundationpose_final_debug={foundationPoseFinalWidthForDebug}x{foundationPoseFinalHeightForDebug} " +
                        $"ts={cameraImage.timestamp:F6}");
                }
            }
            catch (System.Exception ex)
            {
                hasLatestDetection = false;
                maskAdapter.Clear();
                Debug.LogWarning($"[FoundationPoseCenterYoloAdapter] detection failed: {ex.Message}");
            }
            finally
            {
                if (rgbaBytes.IsCreated)
                {
                    rgbaBytes.Dispose();
                }
            }
        }

        bool ShouldLogGeometryTrace()
        {
            if (!enableGeometryTrace)
            {
                return false;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (now - lastGeometryTraceRealtime < Mathf.Max(0.1f, geometryTraceIntervalSeconds))
            {
                return false;
            }

            lastGeometryTraceRealtime = now;
            return true;
        }

        bool ShouldCaptureNow()
        {
            if (targetFps <= 0.0f)
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

        void LogThrottled(string message)
        {
            if (!verboseLogging)
            {
                return;
            }

            double now = Time.realtimeSinceStartupAsDouble;
            if (now - lastLogRealtime < 1.0)
            {
                return;
            }

            lastLogRealtime = now;
            Debug.Log($"[FoundationPoseCenterYoloAdapter] {message}");
        }

        void OnGUI()
        {
            if (!drawOverlay)
            {
                return;
            }

            EnsureLineTexture();
            DrawCrosshair();

            if (!hasLatestDetection)
            {
                DrawEditorPreviewBox();
                DrawStatus("YOLO scanning");
                return;
            }

            Rect rect = latestDetection.PixelRect;
            DrawBox(rect, Mathf.Max(1, Mathf.RoundToInt(boxThickness * overlayScale)), Color.green);
            DrawStatus($"YOLO {CocoClassNames.GetName(latestDetection.ClassId)} {latestDetection.Confidence:F2}");
        }

        void EnsureLineTexture()
        {
            if (lineTexture != null)
            {
                return;
            }

            lineTexture = new Texture2D(1, 1);
            lineTexture.SetPixel(0, 0, Color.white);
            lineTexture.Apply();
        }

        void DrawCrosshair()
        {
            float centerX = Screen.width * 0.5f;
            float centerY = Screen.height * 0.5f;
            float size = crosshairSize * overlayScale;
            float gap = crosshairGap * overlayScale;
            int thickness = Mathf.Max(1, Mathf.RoundToInt(3 * overlayScale));
            DrawFilledRect(new Rect(centerX - size, centerY - thickness * 0.5f, size - gap, thickness), Color.white);
            DrawFilledRect(new Rect(centerX + gap, centerY - thickness * 0.5f, size - gap, thickness), Color.white);
            DrawFilledRect(new Rect(centerX - thickness * 0.5f, centerY - size, thickness, size - gap), Color.white);
            DrawFilledRect(new Rect(centerX - thickness * 0.5f, centerY + gap, thickness, size - gap), Color.white);
        }

        void DrawBox(Rect rect, int thickness, Color color)
        {
            DrawFilledRect(new Rect(rect.xMin, rect.yMin, rect.width, thickness), color);
            DrawFilledRect(new Rect(rect.xMin, rect.yMax - thickness, rect.width, thickness), color);
            DrawFilledRect(new Rect(rect.xMin, rect.yMin, thickness, rect.height), color);
            DrawFilledRect(new Rect(rect.xMax - thickness, rect.yMin, thickness, rect.height), color);
        }

        void DrawStatus(string text)
        {
            EnsureStatusStyle();
            Rect backgroundRect = new Rect(18, 18, 760, Mathf.Max(68, statusFontSize + 30));
            DrawFilledRect(backgroundRect, new Color(0f, 0f, 0f, 0.55f));
            GUI.color = Color.white;
            GUI.Label(new Rect(32, 24, 730, backgroundRect.height), text, statusStyle);
        }

        void DrawFilledRect(Rect rect, Color color)
        {
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, lineTexture);
            GUI.color = previous;
        }

        void EnsureStatusStyle()
        {
            if (statusStyle != null && statusStyle.fontSize == statusFontSize)
            {
                return;
            }

            statusStyle = new GUIStyle(GUI.skin.label)
            {
                fontSize = Mathf.Max(12, statusFontSize),
                fontStyle = FontStyle.Bold,
                normal = { textColor = Color.white },
                alignment = TextAnchor.MiddleLeft
            };
        }

        void DrawEditorPreviewBox()
        {
#if UNITY_EDITOR
            if (!editorPreviewBox)
            {
                return;
            }

            float width = Mathf.Min(Screen.width * 0.46f, 620f);
            float height = Mathf.Min(Screen.height * 0.36f, 520f);
            Rect previewRect = new Rect(
                Screen.width * 0.5f - width * 0.5f,
                Screen.height * 0.5f - height * 0.5f,
                width,
                height);
            DrawBox(previewRect, Mathf.Max(1, Mathf.RoundToInt(boxThickness * overlayScale)), new Color(0.15f, 1f, 0.25f, 0.9f));
#endif
        }

        static string RectToString(Rect rect)
        {
            return $"(x:{rect.x:F4}, y:{rect.y:F4}, w:{rect.width:F4}, h:{rect.height:F4})";
        }
    }
}
