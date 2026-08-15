using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ARObjectReplacement.Detection
{
    public sealed class YoloCoreMLDetector
    {
        public bool IsAvailable
        {
            get
            {
#if UNITY_IOS && !UNITY_EDITOR
                return AROR_YoloIsAvailable();
#else
                return false;
#endif
            }
        }

        public bool TryDetectCenterObject(
            byte[] rgbaBytes,
            int imageWidth,
            int imageHeight,
            float confidenceThreshold,
            float iouThreshold,
            int targetClassId,
            double sourceTimestamp,
            bool enableGeometryTrace,
            out DetectionResult result)
        {
            result = default;
#if UNITY_IOS && !UNITY_EDITOR
            if (rgbaBytes == null || rgbaBytes.Length < imageWidth * imageHeight * 4)
            {
                return false;
            }

            var success = AROR_YoloDetectCenterObject(
                rgbaBytes,
                rgbaBytes.Length,
                imageWidth,
                imageHeight,
                Screen.width,
                Screen.height,
                confidenceThreshold,
                iouThreshold,
                targetClassId,
                sourceTimestamp,
                enableGeometryTrace ? 1 : 0,
                out var x,
                out var y,
                out var width,
                out var height,
                out var rawNormalizedX,
                out var rawNormalizedY,
                out var rawNormalizedWidth,
                out var rawNormalizedHeight,
                out var classId,
                out var confidence,
                out var hasMaskBottomCenter,
                out var maskBottomCenterX,
                out var maskBottomCenterY,
                out var hasMaskCenter,
                out var maskCenterX,
                out var maskCenterY);

            if (!success)
            {
                return false;
            }

            result = new DetectionResult
            {
                IsValid = true,
                PixelRect = new Rect(x, y, width, height),
                RawCameraNormalizedTopLeft = new Rect(rawNormalizedX, rawNormalizedY, rawNormalizedWidth, rawNormalizedHeight),
                ClassId = classId,
                Confidence = confidence,
                Timestamp = Time.timeAsDouble,
                SourceTimestamp = sourceTimestamp,
                HasMaskBottomCenter = hasMaskBottomCenter != 0,
                MaskBottomCenter = new Vector2(maskBottomCenterX, maskBottomCenterY),
                HasMaskCenter = hasMaskCenter != 0,
                MaskCenter = new Vector2(maskCenterX, maskCenterY)
            };
            if (enableGeometryTrace)
            {
                Debug.Log(
                    "[FP-GEO][CS-DETECT] " +
                    $"trace={sourceTimestamp:F9} " +
                    $"bbox_screen_top_left_px={RectToString(result.PixelRect)} " +
                    $"corners=({result.PixelRect.xMin:F1},{result.PixelRect.yMin:F1})-({result.PixelRect.xMax:F1},{result.PixelRect.yMax:F1}) " +
                    $"screen={Screen.width}x{Screen.height} " +
                    $"yolo_input={imageWidth}x{imageHeight} " +
                    $"raw_camera_norm_top_left={RectToString(result.RawCameraNormalizedTopLeft)} " +
                    $"class={classId} conf={confidence:F4} " +
                    "coordinate_space=iOSScreenTopLeftPixels representation=x_y_w_h");
                Debug.Log(
                    "[FP-GEO][RAW-BBOX] " +
                    $"trace={sourceTimestamp:F9} " +
                    $"raw_norm_top_left={RectToString(result.RawCameraNormalizedTopLeft)} " +
                    $"corners=({result.RawCameraNormalizedTopLeft.xMin:F6},{result.RawCameraNormalizedTopLeft.yMin:F6})-({result.RawCameraNormalizedTopLeft.xMax:F6},{result.RawCameraNormalizedTopLeft.yMax:F6}) " +
                    "coordinate_space=RawCameraNormalizedTopLeft representation=x_y_w_h source=CSharpNativeReturn");
            }
            return true;
#else
            return false;
#endif
        }

#if UNITY_IOS && !UNITY_EDITOR
        [DllImport("__Internal")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool AROR_YoloIsAvailable();

        [DllImport("__Internal")]
        [return: MarshalAs(UnmanagedType.I1)]
        private static extern bool AROR_YoloDetectCenterObject(
            byte[] rgbaBytes,
            int byteCount,
            int imageWidth,
            int imageHeight,
            int screenWidth,
            int screenHeight,
            float confidenceThreshold,
            float iouThreshold,
            int targetClassId,
            double sourceTimestamp,
            int enableGeometryTrace,
            out float x,
            out float y,
            out float width,
            out float height,
            out float rawNormalizedX,
            out float rawNormalizedY,
            out float rawNormalizedWidth,
            out float rawNormalizedHeight,
            out int classId,
            out float confidence,
            out int hasMaskBottomCenter,
            out float maskBottomCenterX,
            out float maskBottomCenterY,
            out int hasMaskCenter,
            out float maskCenterX,
            out float maskCenterY);
#endif

        static string RectToString(Rect rect)
        {
            return $"({rect.x:F1},{rect.y:F1},{rect.width:F1},{rect.height:F1})";
        }
    }
}
