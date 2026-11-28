using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace YAMO.UnityTools
{
    /// <summary>iFacialMocap / FACEMOTION3D text packets. Weights use the sender's 0..100 scale.</summary>
    public sealed class YamoFacialPacket
    {
        public readonly Dictionary<string, float> Weights = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        public readonly Dictionary<string, Vector3> Rotations = new Dictionary<string, Vector3>(StringComparer.OrdinalIgnoreCase);

        public static string NormalizeName(string name)
        {
            if (name.EndsWith("_L", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 2) + "Left";
            else if (name.EndsWith("_R", StringComparison.OrdinalIgnoreCase)) name = name.Substring(0, name.Length - 2) + "Right";
            return name.Replace("_", "");
        }

        public static string MirrorName(string name)
        {
            if (name.EndsWith("Left", StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - 4) + "Right";
            if (name.EndsWith("Right", StringComparison.OrdinalIgnoreCase)) return name.Substring(0, name.Length - 5) + "Left";
            return name;
        }

        private static bool Number(string text, out float value) =>
            float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
            !float.IsNaN(value) && !float.IsInfinity(value);

        public static bool TryParse(string text, out YamoFacialPacket packet)
        {
            packet = new YamoFacialPacket();
            if (string.IsNullOrWhiteSpace(text)) return false;
            var parts = text.Trim('\0', '\r', '\n', ' ').Split('=');
            foreach (var token in parts[0].Split('|'))
            {
                var separator = token.IndexOf('&');
                if (separator < 0) separator = token.IndexOf('-');
                if (separator <= 0 || !Number(token.Substring(separator + 1), out var value)) continue;
                packet.Weights[NormalizeName(token.Substring(0, separator))] = Mathf.Clamp(value, 0f, 100f);
            }
            if (parts.Length > 1)
            {
                foreach (var token in parts[1].Split('|'))
                {
                    var pair = token.Split('#');
                    if (pair.Length != 2) continue;
                    var xyz = pair[1].Split(',');
                    if (xyz.Length >= 3 && Number(xyz[0], out var x) && Number(xyz[1], out var y) && Number(xyz[2], out var z))
                        packet.Rotations[pair[0]] = new Vector3(x, y, z);
                }
            }
            return packet.Weights.Count > 0 || packet.Rotations.Count > 0;
        }
    }
}
