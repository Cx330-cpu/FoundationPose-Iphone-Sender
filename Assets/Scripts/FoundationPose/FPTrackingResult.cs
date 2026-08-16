using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text.RegularExpressions;
using UnityEngine;

namespace FoundationPoseStreaming
{
    public sealed class FPLine2D
    {
        public Vector2 from;
        public Vector2 to;
        public Color32 color;
        public string name;
    }

    public sealed class FPTrackingResult
    {
        public long sequence;
        public DateTime receivedUtc;
        public string frameId;
        public int index = -1;
        public bool hasTimestamp;
        public double timestamp;
        public string state;
        public string operation;
        public double processingFps;
        public bool hasPcReceivedTime;
        public double pcReceivedTime;
        public bool hasPcResultTime;
        public double pcResultTime;
        public bool hasPcQueueLatencyMs;
        public double pcQueueLatencyMs;
        public bool hasSmoothingAlpha;
        public double smoothingAlpha;
        public int imageWidth;
        public int imageHeight;
        public FPLine2D[] bboxLines = Array.Empty<FPLine2D>();
        public FPLine2D[] axisLines = Array.Empty<FPLine2D>();
        public bool hasLatencyEstimate;
        public double latencyMs;
    }

    public static class FPResultJsonParser
    {
        const string NumberPattern = @"[-+]?(?:\d+\.?\d*|\.\d+)(?:[eE][-+]?\d+)?";

        public static bool TryGetString(string json, string fieldName, out string value)
        {
            value = null;
            if (string.IsNullOrEmpty(json) || string.IsNullOrEmpty(fieldName))
            {
                return false;
            }

            Match match = Regex.Match(
                json,
                "\"" + Regex.Escape(fieldName) + "\"\\s*:\\s*\"(?<value>(?:\\\\.|[^\"])*)\"");
            if (!match.Success)
            {
                return false;
            }

            value = Regex.Unescape(match.Groups["value"].Value);
            return true;
        }

        public static bool TryParseTrackingResult(string json, out FPTrackingResult result, out string reason)
        {
            result = null;
            reason = null;

            if (!TryGetString(json, "magic", out string magic) || magic != "FPRESULT")
            {
                reason = "not_fpresult";
                return false;
            }

            if (!TryGetInt(json, "version", out int version) || version != 1)
            {
                reason = "unsupported_version";
                return false;
            }

            if (!TryGetString(json, "type", out string type) || type != "tracking_result")
            {
                reason = "unsupported_type";
                return false;
            }

            if (!TryGetInt(json, "image_width", out int imageWidth) ||
                !TryGetInt(json, "image_height", out int imageHeight) ||
                imageWidth <= 0 ||
                imageHeight <= 0)
            {
                reason = "invalid_image_size";
                return false;
            }

            TryGetString(json, "frame_id", out string frameId);
            bool hasIndex = TryGetInt(json, "index", out int index);
            bool hasTimestamp = TryGetDouble(json, "timestamp", out double timestamp);
            TryGetString(json, "state", out string state);
            TryGetString(json, "operation", out string operation);
            TryGetDouble(json, "processing_fps", out double processingFps);
            bool hasPcReceivedTime = TryGetDouble(json, "pc_received_time", out double pcReceivedTime);
            bool hasPcResultTime = TryGetDouble(json, "pc_result_time", out double pcResultTime);
            bool hasPcQueueLatencyMs = TryGetDouble(json, "pc_queue_latency_ms", out double pcQueueLatencyMs);
            bool hasSmoothingAlpha = TryGetDouble(json, "smoothing_alpha", out double smoothingAlpha);

            result = new FPTrackingResult
            {
                frameId = frameId,
                index = hasIndex ? index : -1,
                hasTimestamp = hasTimestamp,
                timestamp = timestamp,
                state = state,
                operation = operation,
                processingFps = processingFps,
                hasPcReceivedTime = hasPcReceivedTime,
                pcReceivedTime = pcReceivedTime,
                hasPcResultTime = hasPcResultTime,
                pcResultTime = pcResultTime,
                hasPcQueueLatencyMs = hasPcQueueLatencyMs,
                pcQueueLatencyMs = pcQueueLatencyMs,
                hasSmoothingAlpha = hasSmoothingAlpha,
                smoothingAlpha = smoothingAlpha,
                imageWidth = imageWidth,
                imageHeight = imageHeight,
                bboxLines = ParseBBoxLines(json),
                axisLines = ParseAxisLines(json)
            };
            return true;
        }

        static bool TryGetInt(string json, string fieldName, out int value)
        {
            value = 0;
            if (!TryGetDouble(json, fieldName, out double doubleValue))
            {
                return false;
            }

            value = Mathf.RoundToInt((float)doubleValue);
            return true;
        }

        static bool TryGetDouble(string json, string fieldName, out double value)
        {
            value = 0.0;
            Match match = Regex.Match(
                json,
                "\"" + Regex.Escape(fieldName) + "\"\\s*:\\s*(?<value>" + NumberPattern + ")");
            if (!match.Success)
            {
                return false;
            }

            return double.TryParse(
                match.Groups["value"].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out value);
        }

        static FPLine2D[] ParseBBoxLines(string json)
        {
            if (!TryGetArraySection(json, "bbox_lines_2d", out string section))
            {
                return Array.Empty<FPLine2D>();
            }

            List<double> numbers = ExtractNumbers(section);
            int lineCount = numbers.Count / 4;
            FPLine2D[] lines = new FPLine2D[lineCount];
            for (int i = 0; i < lineCount; ++i)
            {
                int offset = i * 4;
                lines[i] = new FPLine2D
                {
                    from = new Vector2((float)numbers[offset], (float)numbers[offset + 1]),
                    to = new Vector2((float)numbers[offset + 2], (float)numbers[offset + 3]),
                    color = new Color32(255, 255, 0, 255),
                    name = "bbox"
                };
            }

            return lines;
        }

        static FPLine2D[] ParseAxisLines(string json)
        {
            if (!TryGetArraySection(json, "axis_lines_2d", out string section))
            {
                return Array.Empty<FPLine2D>();
            }

            List<string> objects = ExtractObjectSections(section);
            List<FPLine2D> lines = new List<FPLine2D>(objects.Count);
            foreach (string axisObject in objects)
            {
                if (!TryGetNumberArray(axisObject, "from", out List<double> from) ||
                    !TryGetNumberArray(axisObject, "to", out List<double> to) ||
                    from.Count < 2 ||
                    to.Count < 2)
                {
                    continue;
                }

                Color32 color = new Color32(255, 255, 255, 255);
                if (TryGetNumberArray(axisObject, "color", out List<double> colorValues) && colorValues.Count >= 3)
                {
                    color = new Color32(
                        ClampColor(colorValues[0]),
                        ClampColor(colorValues[1]),
                        ClampColor(colorValues[2]),
                        255);
                }

                TryGetString(axisObject, "name", out string name);
                lines.Add(new FPLine2D
                {
                    from = new Vector2((float)from[0], (float)from[1]),
                    to = new Vector2((float)to[0], (float)to[1]),
                    color = color,
                    name = name
                });
            }

            return lines.ToArray();
        }

        static bool TryGetArraySection(string json, string fieldName, out string section)
        {
            section = null;
            Match fieldMatch = Regex.Match(json, "\"" + Regex.Escape(fieldName) + "\"\\s*:");
            if (!fieldMatch.Success)
            {
                return false;
            }

            int start = json.IndexOf('[', fieldMatch.Index + fieldMatch.Length);
            if (start < 0)
            {
                return false;
            }

            int depth = 0;
            for (int i = start; i < json.Length; ++i)
            {
                char c = json[i];
                if (c == '[')
                {
                    depth++;
                }
                else if (c == ']')
                {
                    depth--;
                    if (depth == 0)
                    {
                        section = json.Substring(start, i - start + 1);
                        return true;
                    }
                }
            }

            return false;
        }

        static bool TryGetNumberArray(string json, string fieldName, out List<double> numbers)
        {
            numbers = null;
            if (!TryGetArraySection(json, fieldName, out string section))
            {
                return false;
            }

            numbers = ExtractNumbers(section);
            return true;
        }

        static List<double> ExtractNumbers(string text)
        {
            MatchCollection matches = Regex.Matches(text, NumberPattern);
            List<double> numbers = new List<double>(matches.Count);
            foreach (Match match in matches)
            {
                if (double.TryParse(match.Value, NumberStyles.Float, CultureInfo.InvariantCulture, out double value))
                {
                    numbers.Add(value);
                }
            }

            return numbers;
        }

        static List<string> ExtractObjectSections(string text)
        {
            List<string> objects = new List<string>();
            int depth = 0;
            int start = -1;
            for (int i = 0; i < text.Length; ++i)
            {
                char c = text[i];
                if (c == '{')
                {
                    if (depth == 0)
                    {
                        start = i;
                    }

                    depth++;
                }
                else if (c == '}')
                {
                    depth--;
                    if (depth == 0 && start >= 0)
                    {
                        objects.Add(text.Substring(start, i - start + 1));
                        start = -1;
                    }
                }
            }

            return objects;
        }

        static byte ClampColor(double value)
        {
            return (byte)Mathf.Clamp(Mathf.RoundToInt((float)value), 0, 255);
        }
    }
}
