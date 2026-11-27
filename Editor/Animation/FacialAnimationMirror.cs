using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    /// <summary>Mirrors blendshape channels without resampling or modifying the source clip.</summary>
    public static class FacialAnimationMirror
    {
        private static readonly Regex Side = new Regex(
            @"Left|Right|(?<![A-Z])LEFT(?![A-Z])|(?<![A-Z])RIGHT(?![A-Z])|(?<![a-z])left(?![a-z])|(?<![a-z])right(?![a-z])|(?<![A-Za-z0-9])[LR](?![a-z])|(?<=[a-z0-9])[LR]$",
            RegexOptions.CultureInvariant);

        public static string FlipName(string name)
        {
            return Side.Replace(name, m =>
            {
                switch (m.Value)
                {
                    case "Left": return "Right";
                    case "Right": return "Left";
                    case "LEFT": return "RIGHT";
                    case "RIGHT": return "LEFT";
                    case "left": return "right";
                    case "right": return "left";
                    case "L": return "R";
                    default: return "L";
                }
            });
        }

        public static AnimationClip CreateFlippedClip(AnimationClip source, IEnumerable<string> paths, out int changedCurves)
        {
            if (source == null) throw new ArgumentNullException(nameof(source));
            var allowed = new HashSet<string>(paths ?? throw new ArgumentNullException(nameof(paths)), StringComparer.Ordinal);
            var bindings = AnimationUtility.GetCurveBindings(source)
                .Where(b => allowed.Contains(b.path ?? "") && b.type == typeof(SkinnedMeshRenderer)
                    && b.propertyName.StartsWith("blendShape.", StringComparison.Ordinal))
                .ToArray();
            var changes = new List<KeyValuePair<EditorCurveBinding, AnimationCurve>>();
            var originals = new List<EditorCurveBinding>();
            var targets = new HashSet<EditorCurveBinding>();
            foreach (var binding in bindings)
            {
                var target = binding;
                target.propertyName = "blendShape." + FlipName(binding.propertyName.Substring("blendShape.".Length));
                if (target.propertyName == binding.propertyName) continue;
                if (!targets.Add(target)) throw new InvalidOperationException("L/R 이름 변환 충돌: " + target.propertyName);
                originals.Add(binding);
                changes.Add(new KeyValuePair<EditorCurveBinding, AnimationCurve>(target, AnimationUtility.GetEditorCurve(source, binding)));
            }
            var result = UnityEngine.Object.Instantiate(source);
            result.name = source.name + "_LRFlipped";
            // Read everything from the source, then clear before writing: paired and one-sided keys both work.
            foreach (var binding in originals) AnimationUtility.SetEditorCurve(result, binding, null);
            foreach (var change in changes) AnimationUtility.SetEditorCurve(result, change.Key, change.Value);
            changedCurves = changes.Count;
            return result;
        }
    }
}
