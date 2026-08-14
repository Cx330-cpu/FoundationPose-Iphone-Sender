using UnityEngine;

namespace FoundationPoseStreaming
{
    public sealed class FoundationPoseSyntheticSender : MonoBehaviour
    {
        public FoundationPoseTcpSender tcpSender;
        public int width = 256;
        public int height = 192;
        public float fps = 2.0f;
        public FPRgbCodec rgbCodec = FPRgbCodec.Png;
        [Range(1, 100)]
        public int jpegQuality = 95;

        int nextIndex;
        double lastSendTime;

        void Reset()
        {
            tcpSender = FindObjectOfType<FoundationPoseTcpSender>();
        }

        void Update()
        {
            if (tcpSender == null)
            {
                return;
            }

            if (fps > 0.0f)
            {
                double now = Time.realtimeSinceStartupAsDouble;
                if (now - lastSendTime < 1.0 / fps)
                {
                    return;
                }
                lastSendTime = now;
            }

            bool isRegistration = tcpSender.NeedsRegistrationFrame;
            if (!isRegistration && !tcpSender.IsTrackingStream)
            {
                return;
            }

            int index = nextIndex;
            double timestamp = Time.realtimeSinceStartupAsDouble;
            string frameId = FPFrameProtocol.FrameIdFromTimestamp(timestamp, index);

            byte[] rgb24 = BuildSyntheticRgb(width, height, index);
            ushort[] depthMm = BuildSyntheticDepth(width, height);
            byte[] mask = isRegistration ? BuildSyntheticMask(width, height) : null;
            FPCameraIntrinsics intrinsics = new FPCameraIntrinsics(
                width * 0.9,
                width * 0.9,
                width * 0.5,
                height * 0.5);

            FPEncodedFrame encoded = FPFrameProtocol.BuildFrameMessage(
                rgb24,
                depthMm,
                mask,
                width,
                height,
                intrinsics,
                frameId,
                index,
                timestamp,
                rgbCodec,
                jpegQuality);

            bool accepted = isRegistration
                ? tcpSender.EnqueueRegistrationFrame(encoded)
                : tcpSender.EnqueueTrackingFrame(encoded);

            if (accepted)
            {
                nextIndex++;
                Debug.Log($"[FoundationPoseSyntheticSender] queued frame_id={frameId} register={isRegistration} size={width}x{height}");
            }
        }

        static byte[] BuildSyntheticRgb(int width, int height, int index)
        {
            byte[] rgb = new byte[width * height * 3];
            for (int y = 0; y < height; ++y)
            {
                for (int x = 0; x < width; ++x)
                {
                    int offset = (y * width + x) * 3;
                    rgb[offset] = (byte)((x + index * 3) % 256);
                    rgb[offset + 1] = (byte)((y + index * 5) % 256);
                    rgb[offset + 2] = (byte)(((x + y) / 2 + index * 7) % 256);
                }
            }
            return rgb;
        }

        static ushort[] BuildSyntheticDepth(int width, int height)
        {
            ushort[] depth = new ushort[width * height];
            for (int y = 0; y < height; ++y)
            {
                for (int x = 0; x < width; ++x)
                {
                    depth[y * width + x] = (ushort)(
                        700
                        + (x * 100 / Mathf.Max(1, width - 1))
                        + (y * 50 / Mathf.Max(1, height - 1)));
                }
            }
            return depth;
        }

        static byte[] BuildSyntheticMask(int width, int height)
        {
            byte[] mask = new byte[width * height];
            int xMin = width / 8;
            int xMax = width / 2;
            int yMin = height / 6;
            int yMax = height / 2;
            for (int y = yMin; y < yMax; ++y)
            {
                for (int x = xMin; x < xMax; ++x)
                {
                    mask[y * width + x] = 255;
                }
            }

            int notchXMin = width / 8;
            int notchXMax = width / 4;
            int notchYMin = height / 6;
            int notchYMax = height / 3;
            for (int y = notchYMin; y < notchYMax; ++y)
            {
                for (int x = notchXMin; x < notchXMax; ++x)
                {
                    mask[y * width + x] = 0;
                }
            }

            int tabXMin = width * 5 / 8;
            int tabXMax = width * 3 / 4;
            int tabYMin = height * 2 / 3;
            int tabYMax = height * 5 / 6;
            for (int y = tabYMin; y < tabYMax; ++y)
            {
                for (int x = tabXMin; x < tabXMax; ++x)
                {
                    mask[y * width + x] = 255;
                }
            }
            return mask;
        }
    }
}
