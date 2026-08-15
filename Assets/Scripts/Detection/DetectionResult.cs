using System;
using UnityEngine;

namespace ARObjectReplacement.Detection
{
    [Serializable]
    public struct DetectionResult
    {
        public bool IsValid;
        public Rect PixelRect;
        public Rect RawCameraNormalizedTopLeft;
        public int ClassId;
        public float Confidence;
        public double Timestamp;
        public double SourceTimestamp;
        public bool HasMaskBottomCenter;
        public Vector2 MaskBottomCenter;
        public bool HasMaskCenter;
        public Vector2 MaskCenter;

        public Vector2 Center => new Vector2(PixelRect.x + PixelRect.width * 0.5f, PixelRect.y + PixelRect.height * 0.5f);
        public Vector2 BottomCenter => new Vector2(PixelRect.x + PixelRect.width * 0.5f, PixelRect.yMax);
    }
}
