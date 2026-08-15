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
        public float targetFps = 5.0f;

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

        readonly YoloCoreMLDetector detector = new YoloCoreMLDetector();
        DetectionResult latestDetection;
        bool hasLatestDetection;
        double lastCaptureRealtime;
        double lastLogRealtime;
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
                    out DetectionResult detection);

                if (!success)
                {
                    hasLatestDetection = false;
                    maskAdapter.Clear();
                    LogThrottled("YOLO scanning");
                    return;
                }

                RectInt imageRoi = BoundingBoxMapper.ScreenRectToImageRoi(
                    detection.PixelRect,
                    new Vector2Int(Mathf.Max(1, Screen.width), Mathf.Max(1, Screen.height)),
                    new Vector2Int(outputWidth, outputHeight));

                Rect normalizedTopLeft = new Rect(
                    imageRoi.xMin / (float)outputWidth,
                    imageRoi.yMin / (float)outputHeight,
                    imageRoi.width / (float)outputWidth,
                    imageRoi.height / (float)outputHeight);

                latestDetection = detection;
                hasLatestDetection = true;
                maskAdapter.SetBoundingBox(normalizedTopLeft, cameraImage.timestamp, FPBBoxCoordinateSpace.NormalizedTopLeft);

                if (verboseLogging)
                {
                    Debug.Log($"[FoundationPoseCenterYoloAdapter] center_object class={detection.ClassId} conf={detection.Confidence:F3} screen_bbox={detection.PixelRect} mask_bbox_norm={normalizedTopLeft} ts={cameraImage.timestamp:F6}");
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
    }
}
