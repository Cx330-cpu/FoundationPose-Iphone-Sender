using System;
using UnityEngine;

namespace FoundationPoseStreaming
{
    public enum FPBBoxCoordinateSpace
    {
        FinalImagePixelsTopLeft,
        NormalizedTopLeft,
        NormalizedBottomLeft
    }

    public sealed class YoloBBoxToMaskAdapter : MonoBehaviour
    {
        [Tooltip("Maximum timestamp gap between the RGB frame and the YOLO bbox used for registration.")]
        public double maxMaskAgeMs = 50.0;

        [Tooltip("Inflates the bbox before rasterizing the rectangle mask. 1.0 keeps the bbox unchanged.")]
        [Range(1.0f, 2.0f)]
        public float bboxInflation = 1.0f;

        [Tooltip("Prints FP-GEO bbox and mask geometry traces for iPhone/FoundationPose alignment debugging.")]
        public bool enableGeometryTrace = true;

        readonly object gate = new object();
        bool hasBBox;
        Rect bbox;
        FPBBoxCoordinateSpace coordinateSpace;
        double bboxTimestamp;

        public bool HasBBox
        {
            get
            {
                lock (gate)
                {
                    return hasBBox;
                }
            }
        }

        public void SetBoundingBox(Rect newBBox, double timestamp, FPBBoxCoordinateSpace space)
        {
            lock (gate)
            {
                bbox = newBBox;
                bboxTimestamp = timestamp;
                coordinateSpace = space;
                hasBBox = true;
            }

            if (enableGeometryTrace)
            {
                Debug.Log(
                    "[FP-GEO][MASK-SET] " +
                    $"trace={timestamp:F9} " +
                    $"bbox={RectToString(newBBox)} " +
                    $"corners=({newBBox.xMin:F6},{newBBox.yMin:F6})-({newBBox.xMax:F6},{newBBox.yMax:F6}) " +
                    $"coordinate_space={space} representation=x_y_w_h " +
                    $"bbox_inflation={bboxInflation:F3} max_age_ms={maxMaskAgeMs:F1}");
            }
        }

        public void Clear()
        {
            lock (gate)
            {
                hasBBox = false;
            }
        }

        public bool TryBuildMask(
            int width,
            int height,
            double rgbTimestamp,
            out byte[] mask,
            out RectInt pixelBBox,
            out double maskTimestamp,
            out string reason)
        {
            mask = null;
            pixelBBox = default;
            maskTimestamp = 0.0;
            reason = null;

            Rect localBBox;
            FPBBoxCoordinateSpace localSpace;
            lock (gate)
            {
                if (!hasBBox)
                {
                    reason = "no_yolo_bbox";
                    return false;
                }

                localBBox = bbox;
                localSpace = coordinateSpace;
                maskTimestamp = bboxTimestamp;
            }

            double ageMs = Math.Abs(rgbTimestamp - maskTimestamp) * 1000.0;
            if (ageMs > maxMaskAgeMs)
            {
                reason = $"stale_yolo_bbox age_ms={ageMs:F2}";
                return false;
            }

            pixelBBox = ToPixelBBox(localBBox, localSpace, width, height);
            pixelBBox = InflateAndClamp(pixelBBox, width, height, bboxInflation);
            if (pixelBBox.width <= 0 || pixelBBox.height <= 0)
            {
                reason = $"empty_yolo_bbox {pixelBBox}";
                return false;
            }

            mask = new byte[width * height];
            int xMax = pixelBBox.x + pixelBBox.width;
            int yMax = pixelBBox.y + pixelBBox.height;
            for (int y = pixelBBox.y; y < yMax; ++y)
            {
                int rowOffset = y * width;
                for (int x = pixelBBox.x; x < xMax; ++x)
                {
                    mask[rowOffset + x] = 255;
                }
            }

            if (enableGeometryTrace)
            {
                RectInt actualBBox = GetActualMaskBounds(mask, width, height);
                Debug.Log(
                    "[FP-GEO][MASK-BUILD] " +
                    $"trace={maskTimestamp:F9} rgb_ts={rgbTimestamp:F9} age_ms={ageMs:F2} " +
                    $"final_size={width}x{height} input_bbox={RectToString(localBBox)} input_space={localSpace} " +
                    $"pixel_bbox={pixelBBox} corners=({pixelBBox.xMin},{pixelBBox.yMin})-({pixelBBox.xMax},{pixelBBox.yMax}) " +
                    $"center=({pixelBBox.x + pixelBBox.width * 0.5f:F1},{pixelBBox.y + pixelBBox.height * 0.5f:F1})");
                Debug.Log(
                    "[FP-GEO][MASK-ACTUAL] " +
                    $"trace={maskTimestamp:F9} final_size={width}x{height} " +
                    $"actual_bbox={actualBBox} corners=({actualBBox.xMin},{actualBBox.yMin})-({actualBBox.xMax},{actualBBox.yMax}) " +
                    "mask_value=255 coordinate_space=FoundationPoseFinalTopLeftPixels");
            }

            return true;
        }

        static RectInt ToPixelBBox(Rect source, FPBBoxCoordinateSpace space, int width, int height)
        {
            switch (space)
            {
                case FPBBoxCoordinateSpace.FinalImagePixelsTopLeft:
                    return RectIntFromMinMax(
                        Mathf.RoundToInt(source.xMin),
                        Mathf.RoundToInt(source.yMin),
                        Mathf.RoundToInt(source.xMax),
                        Mathf.RoundToInt(source.yMax),
                        width,
                        height);

                case FPBBoxCoordinateSpace.NormalizedTopLeft:
                    return RectIntFromMinMax(
                        Mathf.RoundToInt(source.xMin * width),
                        Mathf.RoundToInt(source.yMin * height),
                        Mathf.RoundToInt(source.xMax * width),
                        Mathf.RoundToInt(source.yMax * height),
                        width,
                        height);

                case FPBBoxCoordinateSpace.NormalizedBottomLeft:
                    return RectIntFromMinMax(
                        Mathf.RoundToInt(source.xMin * width),
                        Mathf.RoundToInt((1.0f - source.yMax) * height),
                        Mathf.RoundToInt(source.xMax * width),
                        Mathf.RoundToInt((1.0f - source.yMin) * height),
                        width,
                        height);

                default:
                    throw new ArgumentOutOfRangeException(nameof(space), space, null);
            }
        }

        static RectInt InflateAndClamp(RectInt rect, int width, int height, float inflation)
        {
            float centerX = rect.x + rect.width * 0.5f;
            float centerY = rect.y + rect.height * 0.5f;
            float halfW = rect.width * inflation * 0.5f;
            float halfH = rect.height * inflation * 0.5f;
            return RectIntFromMinMax(
                Mathf.FloorToInt(centerX - halfW),
                Mathf.FloorToInt(centerY - halfH),
                Mathf.CeilToInt(centerX + halfW),
                Mathf.CeilToInt(centerY + halfH),
                width,
                height);
        }

        static RectInt RectIntFromMinMax(int xMin, int yMin, int xMax, int yMax, int width, int height)
        {
            int clampedXMin = Mathf.Clamp(Mathf.Min(xMin, xMax), 0, width);
            int clampedYMin = Mathf.Clamp(Mathf.Min(yMin, yMax), 0, height);
            int clampedXMax = Mathf.Clamp(Mathf.Max(xMin, xMax), 0, width);
            int clampedYMax = Mathf.Clamp(Mathf.Max(yMin, yMax), 0, height);
            return new RectInt(clampedXMin, clampedYMin, clampedXMax - clampedXMin, clampedYMax - clampedYMin);
        }

        static RectInt GetActualMaskBounds(byte[] mask, int width, int height)
        {
            int xMin = width;
            int yMin = height;
            int xMax = -1;
            int yMax = -1;
            for (int y = 0; y < height; ++y)
            {
                int rowOffset = y * width;
                for (int x = 0; x < width; ++x)
                {
                    if (mask[rowOffset + x] == 0)
                    {
                        continue;
                    }

                    xMin = Mathf.Min(xMin, x);
                    yMin = Mathf.Min(yMin, y);
                    xMax = Mathf.Max(xMax, x + 1);
                    yMax = Mathf.Max(yMax, y + 1);
                }
            }

            if (xMax < xMin || yMax < yMin)
            {
                return new RectInt(0, 0, 0, 0);
            }

            return new RectInt(xMin, yMin, xMax - xMin, yMax - yMin);
        }

        static string RectToString(Rect rect)
        {
            return $"({rect.x:F6},{rect.y:F6},{rect.width:F6},{rect.height:F6})";
        }
    }
}
