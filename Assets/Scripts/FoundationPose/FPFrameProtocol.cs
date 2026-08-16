using System;
using System.Globalization;
using System.Text;
using UnityEngine;
using UnityEngine.Experimental.Rendering;

namespace FoundationPoseStreaming
{
    public enum FPRgbCodec
    {
        Png,
        Jpeg
    }

    [Serializable]
    public sealed class FPFrameHeader
    {
        public string magic = FPFrameProtocol.Magic;
        public int version = FPFrameProtocol.Version;
        public string frame_id;
        public int index;
        public double timestamp;
        public int width;
        public int height;
        public double fx;
        public double fy;
        public double cx;
        public double cy;
        public string rgb_format;
        public string depth_format = "uint16_png_mm";
        public string mask_format;
        public int rgb_len;
        public int depth_len;
        public int mask_len;
    }

    public readonly struct FPCameraIntrinsics
    {
        public readonly double fx;
        public readonly double fy;
        public readonly double cx;
        public readonly double cy;

        public FPCameraIntrinsics(double fx, double fy, double cx, double cy)
        {
            this.fx = fx;
            this.fy = fy;
            this.cx = cx;
            this.cy = cy;
        }
    }

    public sealed class FPEncodedFrame
    {
        public readonly byte[] message;
        public readonly FPFrameHeader header;
        public readonly double encodeRgbMs;
        public readonly double encodeDepthMs;
        public readonly double encodeMaskMs;
        public int maskPixelCount;
        public string maskSourceKind;
        public string maskReason;
        public bool maskRequested;
        public int maskBurstRemaining;

        public FPEncodedFrame(byte[] message, FPFrameHeader header, double encodeRgbMs, double encodeDepthMs, double encodeMaskMs)
        {
            this.message = message;
            this.header = header;
            this.encodeRgbMs = encodeRgbMs;
            this.encodeDepthMs = encodeDepthMs;
            this.encodeMaskMs = encodeMaskMs;
        }
    }

    public static class FPFrameProtocol
    {
        public const string Magic = "FPFRAME";
        public const int Version = 1;

        public static FPEncodedFrame BuildFrameMessage(
            byte[] rgb24,
            ushort[] depthMillimeters,
            byte[] maskU8,
            int width,
            int height,
            FPCameraIntrinsics intrinsics,
            string frameId,
            int index,
            double timestamp,
            FPRgbCodec rgbCodec,
            int jpegQuality)
        {
            ValidateImageInputs(rgb24, depthMillimeters, maskU8, width, height, index, intrinsics, timestamp);

            DateTime encodeStart = DateTime.UtcNow;
            byte[] rgbBytes = EncodeRgb(rgb24, width, height, rgbCodec, jpegQuality);
            double encodeRgbMs = (DateTime.UtcNow - encodeStart).TotalMilliseconds;

            encodeStart = DateTime.UtcNow;
            byte[] depthBytes = EncodeDepthPng(depthMillimeters, width, height);
            double encodeDepthMs = (DateTime.UtcNow - encodeStart).TotalMilliseconds;

            byte[] maskBytes = Array.Empty<byte>();
            double encodeMaskMs = 0.0;
            if (maskU8 != null)
            {
                encodeStart = DateTime.UtcNow;
                maskBytes = EncodeMaskPng(maskU8, width, height);
                encodeMaskMs = (DateTime.UtcNow - encodeStart).TotalMilliseconds;
            }

            FPFrameHeader header = new FPFrameHeader
            {
                frame_id = frameId,
                index = index,
                timestamp = timestamp,
                width = width,
                height = height,
                fx = intrinsics.fx,
                fy = intrinsics.fy,
                cx = intrinsics.cx,
                cy = intrinsics.cy,
                rgb_format = rgbCodec == FPRgbCodec.Jpeg ? "jpeg" : "png",
                mask_format = maskBytes.Length == 0 ? "none" : "uint8_png",
                rgb_len = rgbBytes.Length,
                depth_len = depthBytes.Length,
                mask_len = maskBytes.Length
            };

            string json = JsonUtility.ToJson(header);
            byte[] headerBytes = Encoding.UTF8.GetBytes(json);
            byte[] message = new byte[4 + headerBytes.Length + rgbBytes.Length + depthBytes.Length + maskBytes.Length];

            WriteUInt32BigEndian(message, 0, (uint)headerBytes.Length);
            Buffer.BlockCopy(headerBytes, 0, message, 4, headerBytes.Length);
            int offset = 4 + headerBytes.Length;
            Buffer.BlockCopy(rgbBytes, 0, message, offset, rgbBytes.Length);
            offset += rgbBytes.Length;
            Buffer.BlockCopy(depthBytes, 0, message, offset, depthBytes.Length);
            offset += depthBytes.Length;
            if (maskBytes.Length > 0)
            {
                Buffer.BlockCopy(maskBytes, 0, message, offset, maskBytes.Length);
            }

            return new FPEncodedFrame(message, header, encodeRgbMs, encodeDepthMs, encodeMaskMs);
        }

        public static string FrameIdFromTimestamp(double timestampSeconds, int fallbackIndex)
        {
            if (double.IsNaN(timestampSeconds) || double.IsInfinity(timestampSeconds) || timestampSeconds < 0)
            {
                return fallbackIndex.ToString(CultureInfo.InvariantCulture);
            }

            double timestampNs = Math.Round(timestampSeconds * 1e9);
            return timestampNs.ToString("F0", CultureInfo.InvariantCulture);
        }

        public static byte[] EncodeRgb(byte[] rgb24, int width, int height, FPRgbCodec codec, int jpegQuality)
        {
            byte[] encoderRgb = FlipRows(rgb24, width * 3, height);
            if (codec == FPRgbCodec.Jpeg)
            {
                int quality = Mathf.Clamp(jpegQuality, 1, 100);
                return ImageConversion.EncodeArrayToJPG(
                    encoderRgb,
                    GraphicsFormat.R8G8B8_UNorm,
                    (uint)width,
                    (uint)height,
                    0,
                    quality);
            }

            return ImageConversion.EncodeArrayToPNG(
                encoderRgb,
                GraphicsFormat.R8G8B8_UNorm,
                (uint)width,
                (uint)height,
                0);
        }

        public static byte[] EncodeDepthPng(ushort[] depthMillimeters, int width, int height)
        {
            byte[] depthBytes = new byte[depthMillimeters.Length * sizeof(ushort)];
            Buffer.BlockCopy(depthMillimeters, 0, depthBytes, 0, depthBytes.Length);
            byte[] encoderDepth = FlipRows(depthBytes, width * sizeof(ushort), height);
            return ImageConversion.EncodeArrayToPNG(
                encoderDepth,
                GraphicsFormat.R16_UNorm,
                (uint)width,
                (uint)height,
                0);
        }

        public static byte[] EncodeMaskPng(byte[] maskU8, int width, int height)
        {
            byte[] encoderMask = FlipRows(maskU8, width, height);
            return ImageConversion.EncodeArrayToPNG(
                encoderMask,
                GraphicsFormat.R8_UNorm,
                (uint)width,
                (uint)height,
                0);
        }

        static byte[] FlipRows(byte[] source, int rowBytes, int height)
        {
            byte[] result = new byte[source.Length];
            for (int y = 0; y < height; ++y)
            {
                int srcOffset = y * rowBytes;
                int dstOffset = (height - 1 - y) * rowBytes;
                Buffer.BlockCopy(source, srcOffset, result, dstOffset, rowBytes);
            }
            return result;
        }

        static void ValidateImageInputs(
            byte[] rgb24,
            ushort[] depthMillimeters,
            byte[] maskU8,
            int width,
            int height,
            int index,
            FPCameraIntrinsics intrinsics,
            double timestamp)
        {
            if (width <= 0 || height <= 0)
            {
                throw new ArgumentOutOfRangeException(nameof(width), $"Invalid FPFRAME dimensions: {width}x{height}");
            }

            int pixels = checked(width * height);
            if (rgb24 == null || rgb24.Length != pixels * 3)
            {
                throw new ArgumentException($"RGB24 length must be {pixels * 3}, got {rgb24?.Length ?? 0}");
            }

            if (depthMillimeters == null || depthMillimeters.Length != pixels)
            {
                throw new ArgumentException($"Depth length must be {pixels}, got {depthMillimeters?.Length ?? 0}");
            }

            if (maskU8 != null && maskU8.Length != pixels)
            {
                throw new ArgumentException($"Mask length must be {pixels}, got {maskU8.Length}");
            }

            if (index < 0)
            {
                throw new ArgumentOutOfRangeException(nameof(index), "Frame index must be non-negative.");
            }

            if (!IsFinitePositive(intrinsics.fx) || !IsFinitePositive(intrinsics.fy))
            {
                throw new ArgumentException($"Invalid focal length: fx={intrinsics.fx}, fy={intrinsics.fy}");
            }

            if (!IsFinite(intrinsics.cx) || !IsFinite(intrinsics.cy))
            {
                throw new ArgumentException($"Invalid principal point: cx={intrinsics.cx}, cy={intrinsics.cy}");
            }

            if (!IsFinite(timestamp))
            {
                throw new ArgumentException($"Invalid timestamp: {timestamp}");
            }
        }

        static bool IsFinite(double value)
        {
            return !double.IsNaN(value) && !double.IsInfinity(value);
        }

        static bool IsFinitePositive(double value)
        {
            return IsFinite(value) && value > 0.0;
        }

        static void WriteUInt32BigEndian(byte[] buffer, int offset, uint value)
        {
            buffer[offset] = (byte)((value >> 24) & 0xff);
            buffer[offset + 1] = (byte)((value >> 16) & 0xff);
            buffer[offset + 2] = (byte)((value >> 8) & 0xff);
            buffer[offset + 3] = (byte)(value & 0xff);
        }
    }
}
