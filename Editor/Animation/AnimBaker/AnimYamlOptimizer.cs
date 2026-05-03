using System.Globalization;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;

namespace YAMO.UnityTools.Editor
{
    /*
        Post-processes an .anim YAML file produced by Unity to shrink it further:
        - Rounds value/inSlope/outSlope to fewer significant digits (Unity writes full float32 precision).
        - Snaps near-zero slopes to "0".
        - (optionally) Strips m_EditorCurves block as a belt-and-suspenders against Unity re-populating it.

        After running, AssetDatabase.ImportAsset should be called to make Unity re-read the file.
    */
    public static class AnimYamlOptimizer
    {
        public struct Options
        {
            public int ValueSignificantDigits;
            public int SlopeSignificantDigits;
            public float SlopeZeroSnapThreshold;
            public bool StripEditorCurves;
            public bool StripDefaultKeyframeFields;

            public static Options Default => new Options
            {
                ValueSignificantDigits = 5,
                SlopeSignificantDigits = 4,
                SlopeZeroSnapThreshold = 1e-5f,
                StripEditorCurves = true,
                StripDefaultKeyframeFields = false,
            };
        }

        public struct Stats
        {
            public long BeforeBytes;
            public long AfterBytes;
            public int RoundedValues;
            public int RoundedSlopes;
            public int SlopesSnappedToZero;
            public bool EditorCurvesStripped;
            public int StrippedDefaultFields;
        }

        private static readonly Regex NumericFieldRegex = new Regex(
            @"^(\s+)(value|inSlope|outSlope):\s+(-?[\d.eE+-]+)\s*$",
            RegexOptions.Compiled);

        // weightedMode=0 (None) means inWeight/outWeight are unused. inWeight=0 / outWeight=0 are
        // overridden to 0 by our writer (instead of Unity's default 0.333) so they're "default-like"
        // for the unweighted case. Stripping these lines is safe IF Unity's YAML parser tolerates
        // missing fields (it usually does, but this hasn't been formally verified across all versions).
        private static readonly Regex DefaultFieldRegex = new Regex(
            @"^\s+(weightedMode:\s+0|inWeight:\s+0|outWeight:\s+0)\s*$",
            RegexOptions.Compiled);

        public static Stats Optimize(string assetPath, Options opts)
        {
            Stats stats = default;
            stats.BeforeBytes = new FileInfo(assetPath).Length;

            string content = File.ReadAllText(assetPath);
            var lines = content.Split('\n');
            var output = new StringBuilder(content.Length);

            bool inEditorCurves = false;
            int editorCurvesIndent = -1;

            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i];

                if (inEditorCurves)
                {
                    // Skip lines belonging to the m_EditorCurves block until a sibling top-level field appears.
                    int indent = LeadingSpaces(line);
                    if (line.Length > 0 && indent <= editorCurvesIndent && line[indent] != '-')
                    {
                        inEditorCurves = false;
                        // fall through to write the current line
                    }
                    else
                    {
                        continue;
                    }
                }

                if (opts.StripEditorCurves && IsEditorCurvesHeader(line))
                {
                    int indent = LeadingSpaces(line);
                    output.Append(' ', indent);
                    output.Append("m_EditorCurves: []\n");
                    inEditorCurves = true;
                    editorCurvesIndent = indent;
                    stats.EditorCurvesStripped = true;
                    continue;
                }

                if (opts.StripDefaultKeyframeFields && DefaultFieldRegex.IsMatch(line))
                {
                    stats.StrippedDefaultFields++;
                    continue;
                }

                var match = NumericFieldRegex.Match(line);
                if (match.Success)
                {
                    string indentStr = match.Groups[1].Value;
                    string field = match.Groups[2].Value;
                    string numStr = match.Groups[3].Value;
                    if (float.TryParse(numStr, NumberStyles.Float, CultureInfo.InvariantCulture, out float val))
                    {
                        bool isSlope = field == "inSlope" || field == "outSlope";
                        int digits = isSlope ? opts.SlopeSignificantDigits : opts.ValueSignificantDigits;
                        string formatted;
                        if (isSlope && System.Math.Abs(val) < opts.SlopeZeroSnapThreshold)
                        {
                            formatted = "0";
                            stats.SlopesSnappedToZero++;
                        }
                        else
                        {
                            formatted = FormatFloat(val, digits);
                            if (isSlope) stats.RoundedSlopes++;
                            else stats.RoundedValues++;
                        }
                        output.Append(indentStr);
                        output.Append(field);
                        output.Append(": ");
                        output.Append(formatted);
                        output.Append('\n');
                        continue;
                    }
                }

                output.Append(line);
                if (i < lines.Length - 1) output.Append('\n');
                else if (content.EndsWith("\n")) output.Append('\n');
            }

            File.WriteAllText(assetPath, output.ToString());
            stats.AfterBytes = new FileInfo(assetPath).Length;
            return stats;
        }

        private static bool IsEditorCurvesHeader(string line)
        {
            // matches "  m_EditorCurves:" (with any indent) and nothing after the colon
            int n = line.Length;
            int i = 0;
            while (i < n && line[i] == ' ') i++;
            string remainder = line.Substring(i);
            return remainder == "m_EditorCurves:";
        }

        private static int LeadingSpaces(string s)
        {
            int n = s.Length;
            int i = 0;
            while (i < n && s[i] == ' ') i++;
            return i;
        }

        private static string FormatFloat(float val, int sigDigits)
        {
            if (val == 0f) return "0";
            // "G<n>" uses up to n significant digits with shortest representation.
            string s = val.ToString("G" + sigDigits, CultureInfo.InvariantCulture);
            // Avoid leaving trailing decimal point: e.g., "1." → "1"
            if (s.EndsWith(".")) s = s.Substring(0, s.Length - 1);
            return s;
        }
    }
}
