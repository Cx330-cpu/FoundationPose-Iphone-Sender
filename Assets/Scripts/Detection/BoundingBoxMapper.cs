using UnityEngine;

namespace ARObjectReplacement.Detection
{
    public static class BoundingBoxMapper
    {
        public static RectInt ScreenRectToImageRoi(Rect screenRect, Vector2Int screenResolution, Vector2Int imageResolution)
        {
            var screenWidth = Mathf.Max(1, screenResolution.x);
            var screenHeight = Mathf.Max(1, screenResolution.y);
            var imageWidth = Mathf.Max(1, imageResolution.x);
            var imageHeight = Mathf.Max(1, imageResolution.y);

            var xMin = Mathf.RoundToInt(screenRect.xMin * imageWidth / screenWidth);
            var xMax = Mathf.RoundToInt(screenRect.xMax * imageWidth / screenWidth);
            var yMin = Mathf.RoundToInt(screenRect.yMin * imageHeight / screenHeight);
            var yMax = Mathf.RoundToInt(screenRect.yMax * imageHeight / screenHeight);

            xMin = Mathf.Clamp(xMin, 0, imageWidth - 1);
            xMax = Mathf.Clamp(xMax, xMin + 1, imageWidth);
            yMin = Mathf.Clamp(yMin, 0, imageHeight - 1);
            yMax = Mathf.Clamp(yMax, yMin + 1, imageHeight);

            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
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
    }
}
