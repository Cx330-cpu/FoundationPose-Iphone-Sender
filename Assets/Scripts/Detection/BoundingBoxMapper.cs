using UnityEngine;

namespace ARObjectReplacement.Detection
{
    public static class BoundingBoxMapper
    {
        public static RectInt ScreenRectToImageRoi(
            Rect screenRect,
            Vector2Int screenResolution,
            Vector2Int imageResolution,
            bool enableGeometryTrace = false,
            double traceTimestamp = 0.0)
        {
            var screenWidth = Mathf.Max(1, screenResolution.x);
            var screenHeight = Mathf.Max(1, screenResolution.y);
            var imageWidth = Mathf.Max(1, imageResolution.x);
            var imageHeight = Mathf.Max(1, imageResolution.y);

            if (enableGeometryTrace)
            {
                Debug.Log(
                    "[FP-GEO][MAP-IN] " +
                    $"trace={traceTimestamp:F9} " +
                    $"bbox_screen_top_left_px={RectToString(screenRect)} " +
                    $"corners=({screenRect.xMin:F1},{screenRect.yMin:F1})-({screenRect.xMax:F1},{screenRect.yMax:F1}) " +
                    $"screen={screenWidth}x{screenHeight} image={imageWidth}x{imageHeight} " +
                    "from=iOSScreenTopLeftPixels to=YoloInputTopLeftPixels operation=scale_xy_only representation=x_y_w_h");
            }

            var xMin = Mathf.RoundToInt(screenRect.xMin * imageWidth / screenWidth);
            var xMax = Mathf.RoundToInt(screenRect.xMax * imageWidth / screenWidth);
            var yMin = Mathf.RoundToInt(screenRect.yMin * imageHeight / screenHeight);
            var yMax = Mathf.RoundToInt(screenRect.yMax * imageHeight / screenHeight);

            xMin = Mathf.Clamp(xMin, 0, imageWidth - 1);
            xMax = Mathf.Clamp(xMax, xMin + 1, imageWidth);
            yMin = Mathf.Clamp(yMin, 0, imageHeight - 1);
            yMax = Mathf.Clamp(yMax, yMin + 1, imageHeight);

            var mapped = new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
            if (enableGeometryTrace)
            {
                Debug.Log(
                    "[FP-GEO][MAP-OUT] " +
                    $"trace={traceTimestamp:F9} " +
                    $"bbox_yolo_input_top_left_px={mapped} " +
                    $"corners=({mapped.xMin},{mapped.yMin})-({mapped.xMax},{mapped.yMax}) " +
                    $"screen={screenWidth}x{screenHeight} image={imageWidth}x{imageHeight} " +
                    "coordinate_space=YoloInputTopLeftPixels representation=x_y_w_h");
            }

            return mapped;
        }

        public static RectInt ExpandAndClip(RectInt roi, int width, int height, float expandRatio)
        {
            var expandX = Mathf.RoundToInt(roi.width * Mathf.Max(0f, expandRatio) * 0.5f);
            var expandY = Mathf.RoundToInt(roi.height * Mathf.Max(0f, expandRatio) * 0.5f);
            var xMin = Mathf.Clamp(roi.xMin - expandX, 0, width);
            var yMin = Mathf.Clamp(roi.yMin - expandY, 0, height);
            var xMax = Mathf.Clamp(roi.xMax + expandX, xMin + 1, width);
            var yMax = Mathf.Clamp(roi.yMax + expandY, yMin + 1, height);
            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        static string RectToString(Rect rect)
        {
            return $"({rect.x:F1},{rect.y:F1},{rect.width:F1},{rect.height:F1})";
        }
    }
}
