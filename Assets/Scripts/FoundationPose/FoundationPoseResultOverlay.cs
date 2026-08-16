using System;
using UnityEngine;

namespace FoundationPoseStreaming
{
    public enum FPOverlayFitMode
    {
        AspectFill,
        AspectFit,
        Stretch
    }

    public enum FPOverlayRotation
    {
        None,
        Clockwise90,
        CounterClockwise90,
        Rotate180
    }

    public sealed class FoundationPoseResultOverlay : MonoBehaviour
    {
        [Header("References")]
        public FoundationPoseTcpSender tcpSender;

        [Header("Display Mapping")]
        public FPOverlayFitMode fitMode = FPOverlayFitMode.AspectFill;
        public FPOverlayRotation coordinateRotation = FPOverlayRotation.Clockwise90;
        public bool horizontalMirror;
        [Tooltip("Drop old tracking results instead of drawing stale overlays.")]
        public double maxResultAgeSeconds = 0.15;
        public double maxEstimatedLatencyMs = 150.0;
        public int maxFrameLag = 5;

        [Header("Drawing")]
        public bool drawOverlay = true;
        public int bboxThickness = 3;
        public int axisThickness = 5;
        public int statusFontSize = 36;
        public bool drawStatus = true;

        [Header("Logging")]
        public bool verboseLogging = true;

        Texture2D lineTexture;
        GUIStyle statusStyle;
        long lastLoggedSequence = -1;

        void Reset()
        {
            tcpSender = FindObjectOfType<FoundationPoseTcpSender>();
        }

        void OnGUI()
        {
            if (!drawOverlay)
            {
                return;
            }

            if (tcpSender == null)
            {
                tcpSender = FindObjectOfType<FoundationPoseTcpSender>();
                if (tcpSender == null)
                {
                    return;
                }
            }

            if (!tcpSender.TryGetLatestTrackingResult(out FPTrackingResult result))
            {
                return;
            }

            double resultAgeSeconds = (DateTime.UtcNow - result.receivedUtc).TotalSeconds;
            if (result.imageWidth <= 0 || result.imageHeight <= 0)
            {
                LogResultDecision(result, false, "invalid_image_size", resultAgeSeconds);
                DrawStatus(result, resultAgeSeconds, false, "invalid_image_size");
                return;
            }

            if (TryGetStaleReason(result, resultAgeSeconds, out string staleReason))
            {
                LogResultDecision(result, false, staleReason, resultAgeSeconds);
                DrawStatus(result, resultAgeSeconds, false, staleReason);
                return;
            }

            EnsureLineTexture();
            GetTransformedImageSize(result.imageWidth, result.imageHeight, out int displayImageWidth, out int displayImageHeight);
            Rect imageRect = ComputeImageRect(displayImageWidth, displayImageHeight);
            foreach (FPLine2D line in result.bboxLines)
            {
                DrawImageLine(line, result.imageWidth, result.imageHeight, imageRect, Color.yellow, bboxThickness);
            }

            foreach (FPLine2D line in result.axisLines)
            {
                DrawImageLine(line, result.imageWidth, result.imageHeight, imageRect, line.color, axisThickness);
            }

            if (drawStatus)
            {
                DrawStatus(result, resultAgeSeconds, true, "drawn");
            }

            LogResultDecision(result, true, "drawn", resultAgeSeconds);
        }

        Rect ComputeImageRect(int imageWidth, int imageHeight)
        {
            float screenWidth = Screen.width;
            float screenHeight = Screen.height;
            if (fitMode == FPOverlayFitMode.Stretch)
            {
                return new Rect(0f, 0f, screenWidth, screenHeight);
            }

            float scaleX = screenWidth / imageWidth;
            float scaleY = screenHeight / imageHeight;
            float scale = fitMode == FPOverlayFitMode.AspectFit
                ? Mathf.Min(scaleX, scaleY)
                : Mathf.Max(scaleX, scaleY);
            float width = imageWidth * scale;
            float height = imageHeight * scale;
            return new Rect(
                (screenWidth - width) * 0.5f,
                (screenHeight - height) * 0.5f,
                width,
                height);
        }

        void GetTransformedImageSize(int imageWidth, int imageHeight, out int transformedWidth, out int transformedHeight)
        {
            if (coordinateRotation == FPOverlayRotation.Clockwise90 ||
                coordinateRotation == FPOverlayRotation.CounterClockwise90)
            {
                transformedWidth = imageHeight;
                transformedHeight = imageWidth;
                return;
            }

            transformedWidth = imageWidth;
            transformedHeight = imageHeight;
        }

        Vector2 TransformPcPoint(Vector2 point, int imageWidth, int imageHeight, out int transformedWidth, out int transformedHeight)
        {
            GetTransformedImageSize(imageWidth, imageHeight, out transformedWidth, out transformedHeight);

            Vector2 transformed;
            switch (coordinateRotation)
            {
                case FPOverlayRotation.Clockwise90:
                    transformed = new Vector2(imageHeight - point.y, point.x);
                    break;

                case FPOverlayRotation.CounterClockwise90:
                    transformed = new Vector2(point.y, imageWidth - point.x);
                    break;

                case FPOverlayRotation.Rotate180:
                    transformed = new Vector2(imageWidth - point.x, imageHeight - point.y);
                    break;

                default:
                    transformed = point;
                    break;
            }

            if (horizontalMirror)
            {
                transformed.x = transformedWidth - transformed.x;
            }

            return transformed;
        }

        Vector2 MapImagePoint(Vector2 point, int imageWidth, int imageHeight, Rect imageRect)
        {
            Vector2 transformed = TransformPcPoint(point, imageWidth, imageHeight, out int transformedWidth, out int transformedHeight);
            float xScale = imageRect.width / transformedWidth;
            float yScale = imageRect.height / transformedHeight;
            return new Vector2(
                imageRect.xMin + transformed.x * xScale,
                imageRect.yMin + transformed.y * yScale);
        }

        void DrawImageLine(FPLine2D line, int imageWidth, int imageHeight, Rect imageRect, Color fallbackColor, int thickness)
        {
            Vector2 from = MapImagePoint(line.from, imageWidth, imageHeight, imageRect);
            Vector2 to = MapImagePoint(line.to, imageWidth, imageHeight, imageRect);
            Color color = line.color.a == 0 ? fallbackColor : (Color)line.color;
            DrawLine(from, to, color, Mathf.Max(1, thickness));
        }

        void DrawLine(Vector2 from, Vector2 to, Color color, int thickness)
        {
            Vector2 delta = to - from;
            float length = delta.magnitude;
            if (length <= 0.5f)
            {
                return;
            }

            Matrix4x4 previousMatrix = GUI.matrix;
            Color previousColor = GUI.color;
            GUI.color = color;
            float angle = Mathf.Atan2(delta.y, delta.x) * Mathf.Rad2Deg;
            GUIUtility.RotateAroundPivot(angle, from);
            GUI.DrawTexture(new Rect(from.x, from.y - thickness * 0.5f, length, thickness), lineTexture);
            GUI.matrix = previousMatrix;
            GUI.color = previousColor;
        }

        bool TryGetStaleReason(FPTrackingResult result, double resultAgeSeconds, out string reason)
        {
            double ageMs = resultAgeSeconds * 1000.0;
            if (ageMs > maxResultAgeSeconds * 1000.0)
            {
                reason = $"stale_local_age_ms={ageMs:F1}";
                return true;
            }

            if (result.hasLatencyEstimate && result.latencyMs > maxEstimatedLatencyMs)
            {
                reason = $"stale_latency_ms={result.latencyMs:F1}";
                return true;
            }

            int currentFrameIndex = tcpSender.LastSentFrameIndex;
            if (result.index >= 0 && currentFrameIndex >= 0 && currentFrameIndex - result.index > maxFrameLag)
            {
                reason = $"stale_frame_lag={currentFrameIndex - result.index}";
                return true;
            }

            reason = null;
            return false;
        }

        void DrawStatus(FPTrackingResult result, double resultAgeSeconds, bool drawn, string drawReason)
        {
            if (!drawStatus)
            {
                return;
            }

            EnsureStatusStyle();
            string latency = result.hasLatencyEstimate ? $"{result.latencyMs:F1}ms" : "unknown";
            string pcQueue = result.hasPcQueueLatencyMs ? $"{result.pcQueueLatencyMs:F1}ms" : "unknown";
            string smoothing = result.hasSmoothingAlpha ? result.smoothingAlpha.ToString("F2") : "unknown";
            string text =
                $"FP {result.state ?? "UNKNOWN"} {result.operation ?? "none"} " +
                $"pc_fps={result.processingFps:F1} frame={result.frameId ?? "none"} index={result.index} " +
                $"pc_queue={pcQueue} age={resultAgeSeconds * 1000.0:F0}ms latency={latency} " +
                $"smooth={smoothing} drawn={drawn} reason={drawReason} rot={coordinateRotation} mirror_x={horizontalMirror}";
            Rect backgroundRect = new Rect(18, 88, Screen.width - 36, Mathf.Max(58, statusFontSize + 22));
            DrawFilledRect(backgroundRect, new Color(0f, 0f, 0f, 0.55f));
            GUI.color = Color.white;
            GUI.Label(new Rect(backgroundRect.x + 14, backgroundRect.y + 4, backgroundRect.width - 28, backgroundRect.height), text, statusStyle);
        }

        void DrawFilledRect(Rect rect, Color color)
        {
            EnsureLineTexture();
            Color previous = GUI.color;
            GUI.color = color;
            GUI.DrawTexture(rect, lineTexture);
            GUI.color = previous;
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

        void LogResultDecision(FPTrackingResult result, bool drawn, string reason, double resultAgeSeconds)
        {
            if (!verboseLogging || result.sequence == lastLoggedSequence)
            {
                return;
            }

            lastLoggedSequence = result.sequence;
            Debug.Log(
                "[FoundationPoseResultOverlay] FPRESULT_DRAW_DECISION " +
                $"frame_id={result.frameId ?? "none"} current_unity_frame_index={tcpSender.LastSentFrameIndex} " +
                $"result_index={result.index} " +
                $"latency_ms={(result.hasLatencyEstimate ? result.latencyMs.ToString("F2") : "unknown")} " +
                $"pc_queue_latency_ms={(result.hasPcQueueLatencyMs ? result.pcQueueLatencyMs.ToString("F2") : "unknown")} " +
                $"age_ms={resultAgeSeconds * 1000.0:F2} bbox_lines={result.bboxLines.Length} " +
                $"axis_lines={result.axisLines.Length} drawn={drawn} reason={reason} " +
                $"rotation={coordinateRotation} horizontal_mirror={horizontalMirror}");
        }
    }
}
