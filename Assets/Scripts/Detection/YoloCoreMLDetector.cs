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
                out var x,
                out var y,
                out var width,
                out var height,
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
                ClassId = classId,
                Confidence = confidence,
                Timestamp = Time.timeAsDouble,
                HasMaskBottomCenter = hasMaskBottomCenter != 0,
                MaskBottomCenter = new Vector2(maskBottomCenterX, maskBottomCenterY),
                HasMaskCenter = hasMaskCenter != 0,
                MaskCenter = new Vector2(maskCenterX, maskCenterY)
            };
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
            out float x,
            out float y,
            out float width,
            out float height,
            out int classId,
            out float confidence,
            out int hasMaskBottomCenter,
            out float maskBottomCenterX,
            out float maskBottomCenterY,
            out int hasMaskCenter,
            out float maskCenterX,
            out float maskCenterY);
#endif
    }
}
