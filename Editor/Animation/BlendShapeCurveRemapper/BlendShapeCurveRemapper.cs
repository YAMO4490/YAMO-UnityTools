using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor
{
    [Serializable]
    public struct BlendShapeRemapSettings
    {
        public float LowerThreshold;
        public float UpperThreshold;
        public float LowerOutput;
        public float MiddleMaximumOutput;
        public float UpperOutput;

        public static BlendShapeRemapSettings Default
        {
            get
            {
                return new BlendShapeRemapSettings
                {
                    LowerThreshold = 10f,
                    UpperThreshold = 85f,
                    LowerOutput = 0f,
                    MiddleMaximumOutput = 20f,
                    UpperOutput = 100f
                };
            }
        }
    }

    public struct BlendShapeCurveTarget : IEquatable<BlendShapeCurveTarget>
    {
        public string MeshPath { get; private set; }
        public string PropertyName { get; private set; }

        public BlendShapeCurveTarget(string meshPath, string propertyName)
        {
            MeshPath = meshPath ?? string.Empty;
            PropertyName = propertyName ?? string.Empty;
        }

        public bool Equals(BlendShapeCurveTarget other)
        {
            return string.Equals(MeshPath, other.MeshPath, StringComparison.Ordinal) &&
                   string.Equals(PropertyName, other.PropertyName, StringComparison.Ordinal);
        }

        public override bool Equals(object obj)
        {
            return obj is BlendShapeCurveTarget && Equals((BlendShapeCurveTarget)obj);
        }

        public override int GetHashCode()
        {
            unchecked
            {
                return ((MeshPath != null ? StringComparer.Ordinal.GetHashCode(MeshPath) : 0) * 397) ^
                       (PropertyName != null ? StringComparer.Ordinal.GetHashCode(PropertyName) : 0);
            }
        }

        public override string ToString()
        {
            return (string.IsNullOrEmpty(MeshPath) ? "<Root>" : MeshPath) + " / " + PropertyName;
        }
    }

    public sealed class BlendShapeMeshTrack
    {
        private readonly ReadOnlyCollection<string> _propertyNames;

        public string MeshPath { get; private set; }

        public IReadOnlyList<string> PropertyNames
        {
            get { return _propertyNames; }
        }

        internal BlendShapeMeshTrack(string meshPath, IList<string> propertyNames)
        {
            MeshPath = meshPath ?? string.Empty;
            _propertyNames = new ReadOnlyCollection<string>(propertyNames);
        }
    }

    public sealed class BlendShapeCurveRemapEntry
    {
        public string MeshPath { get; private set; }
        public string PropertyName { get; private set; }
        public int KeyCount { get; private set; }
        public int ChangedKeyCount { get; private set; }
        public float OriginalMinimum { get; private set; }
        public float OriginalMaximum { get; private set; }
        public float OutputMinimum { get; private set; }
        public float OutputMaximum { get; private set; }

        internal BlendShapeCurveRemapEntry(
            string meshPath,
            string propertyName,
            int keyCount,
            int changedKeyCount,
            float originalMinimum,
            float originalMaximum,
            float outputMinimum,
            float outputMaximum)
        {
            MeshPath = meshPath;
            PropertyName = propertyName;
            KeyCount = keyCount;
            ChangedKeyCount = changedKeyCount;
            OriginalMinimum = originalMinimum;
            OriginalMaximum = originalMaximum;
            OutputMinimum = outputMinimum;
            OutputMaximum = outputMaximum;
        }
    }

    public sealed class BlendShapeRemapReport
    {
        private readonly List<BlendShapeCurveRemapEntry> _curves =
            new List<BlendShapeCurveRemapEntry>();

        public IReadOnlyList<BlendShapeCurveRemapEntry> Curves
        {
            get { return _curves; }
        }

        public int MatchedCurveCount { get { return _curves.Count; } }
        public int TotalKeyCount { get; private set; }
        public int ChangedKeyCount { get; private set; }

        internal void Add(BlendShapeCurveRemapEntry entry)
        {
            _curves.Add(entry);
            TotalKeyCount += entry.KeyCount;
            ChangedKeyCount += entry.ChangedKeyCount;
        }
    }

    /// <summary>
    /// Discovers and remaps exact SkinnedMeshRenderer blend-shape bindings.
    /// Key times are preserved and every output key uses unweighted Linear tangents.
    /// </summary>
    public static class BlendShapeCurveRemapper
    {
        public const string BlendShapePropertyPrefix = "blendShape.";

        private const float ChangeEpsilon = 0.00001f;

        public static IReadOnlyList<BlendShapeMeshTrack> DiscoverMeshTracks(AnimationClip clip)
        {
            if (clip == null)
                throw new ArgumentNullException("clip");

            var propertiesByPath = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (!IsBlendShapeBinding(binding))
                    continue;

                HashSet<string> properties;
                if (!propertiesByPath.TryGetValue(binding.path, out properties))
                {
                    properties = new HashSet<string>(StringComparer.Ordinal);
                    propertiesByPath.Add(binding.path, properties);
                }

                properties.Add(binding.propertyName);
            }

            var paths = new List<string>(propertiesByPath.Keys);
            paths.Sort(StringComparer.Ordinal);

            var tracks = new List<BlendShapeMeshTrack>(paths.Count);
            foreach (string path in paths)
            {
                var properties = new List<string>(propertiesByPath[path]);
                properties.Sort(StringComparer.Ordinal);
                tracks.Add(new BlendShapeMeshTrack(path, properties));
            }

            return new ReadOnlyCollection<BlendShapeMeshTrack>(tracks);
        }

        public static string GetSettingsValidationError(BlendShapeRemapSettings settings)
        {
            if (!IsFinite(settings.LowerThreshold) ||
                !IsFinite(settings.UpperThreshold) ||
                !IsFinite(settings.LowerOutput) ||
                !IsFinite(settings.MiddleMaximumOutput) ||
                !IsFinite(settings.UpperOutput))
            {
                return "All thresholds and output values must be finite numbers.";
            }

            if (settings.UpperThreshold <= settings.LowerThreshold)
                return "Upper Threshold must be greater than Lower Threshold.";

            return null;
        }

        public static float RemapValue(float value, BlendShapeRemapSettings settings)
        {
            string error = GetSettingsValidationError(settings);
            if (!string.IsNullOrEmpty(error))
                throw new ArgumentException(error, "settings");

            return RemapValueUnchecked(value, settings);
        }

        public static BlendShapeRemapReport AnalyzeClip(
            AnimationClip clip,
            BlendShapeRemapSettings settings,
            IEnumerable<BlendShapeCurveTarget> targets)
        {
            HashSet<BlendShapeCurveTarget> validatedTargets = ValidateInputs(clip, settings, targets);
            var report = new BlendShapeRemapReport();

            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (!IsTargetBinding(binding, validatedTargets))
                    continue;

                AnimationCurve sourceCurve = AnimationUtility.GetEditorCurve(clip, binding);
                if (sourceCurve == null)
                    continue;

                BlendShapeCurveRemapEntry entry;
                BuildLinearRemappedCurve(sourceCurve, binding, settings, out entry);
                report.Add(entry);
            }

            return report;
        }

        public static BlendShapeRemapReport ProcessClip(
            AnimationClip clip,
            BlendShapeRemapSettings settings,
            IEnumerable<BlendShapeCurveTarget> targets)
        {
            HashSet<BlendShapeCurveTarget> validatedTargets = ValidateInputs(clip, settings, targets);
            var report = new BlendShapeRemapReport();
            var outputBindings = new List<EditorCurveBinding>();
            var outputCurves = new List<AnimationCurve>();

            foreach (EditorCurveBinding binding in AnimationUtility.GetCurveBindings(clip))
            {
                if (!IsTargetBinding(binding, validatedTargets))
                    continue;

                AnimationCurve sourceCurve = AnimationUtility.GetEditorCurve(clip, binding);
                if (sourceCurve == null)
                    continue;

                BlendShapeCurveRemapEntry entry;
                AnimationCurve outputCurve = BuildLinearRemappedCurve(
                    sourceCurve,
                    binding,
                    settings,
                    out entry);

                outputBindings.Add(binding);
                outputCurves.Add(outputCurve);
                report.Add(entry);
            }

            if (outputBindings.Count > 0)
            {
                AnimationUtility.SetEditorCurves(
                    clip,
                    outputBindings.ToArray(),
                    outputCurves.ToArray());
            }

            return report;
        }

        public static AnimationClip CreateProcessedCopyAsset(
            AnimationClip source,
            string destinationAssetPath,
            BlendShapeRemapSettings settings,
            IEnumerable<BlendShapeCurveTarget> targets,
            out BlendShapeRemapReport report)
        {
            if (source == null)
                throw new ArgumentNullException("source");

            string normalizedPath = ValidateNewAssetPath(destinationAssetPath);
            AnimationClip copy = null;
            report = null;

            try
            {
                copy = Object.Instantiate(source);
                copy.name = Path.GetFileNameWithoutExtension(normalizedPath);
                copy.hideFlags = HideFlags.None;

                report = ProcessClip(copy, settings, targets);
                if (report.MatchedCurveCount == 0)
                {
                    throw new InvalidOperationException(
                        "No matching SkinnedMeshRenderer blend-shape curves were found.");
                }

                AssetDatabase.CreateAsset(copy, normalizedPath);
                EditorUtility.SetDirty(copy);
                AssetDatabase.SaveAssetIfDirty(copy);
                return copy;
            }
            catch
            {
                if (copy != null)
                {
                    string createdPath = AssetDatabase.GetAssetPath(copy);
                    if (string.Equals(createdPath, normalizedPath, StringComparison.Ordinal))
                        AssetDatabase.DeleteAsset(normalizedPath);
                    else
                        Object.DestroyImmediate(copy);
                }

                throw;
            }
        }

        public static BlendShapeRemapReport OverwriteAsset(
            AnimationClip clip,
            BlendShapeRemapSettings settings,
            IEnumerable<BlendShapeCurveTarget> targets)
        {
            string overwriteError = GetOverwriteValidationError(clip);
            if (!string.IsNullOrEmpty(overwriteError))
                throw new InvalidOperationException(overwriteError);

            BlendShapeRemapReport analysis = AnalyzeClip(clip, settings, targets);
            if (analysis.MatchedCurveCount == 0)
            {
                throw new InvalidOperationException(
                    "No matching SkinnedMeshRenderer blend-shape curves were found.");
            }

            Undo.RegisterCompleteObjectUndo(clip, "Remap BlendShape Curves");
            BlendShapeRemapReport report = ProcessClip(clip, settings, targets);
            EditorUtility.SetDirty(clip);
            AssetDatabase.SaveAssetIfDirty(clip);
            return report;
        }

        public static string GetOverwriteValidationError(AnimationClip clip)
        {
            if (clip == null)
                return "Select an AnimationClip.";

            string assetPath = AssetDatabase.GetAssetPath(clip).Replace('\\', '/');
            if (string.IsNullOrEmpty(assetPath) ||
                !assetPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                return "Only AnimationClip assets inside this project's Assets folder can be overwritten.";
            }

            if (!string.Equals(Path.GetExtension(assetPath), ".anim", StringComparison.OrdinalIgnoreCase) ||
                !AssetDatabase.IsMainAsset(clip))
            {
                return "Imported or embedded clips cannot be overwritten. Create a processed .anim copy instead.";
            }

            if (!AssetDatabase.IsOpenForEdit(clip))
                return "The selected .anim asset is read-only or not open for edit.";

            return null;
        }

        private static AnimationCurve BuildLinearRemappedCurve(
            AnimationCurve source,
            EditorCurveBinding binding,
            BlendShapeRemapSettings settings,
            out BlendShapeCurveRemapEntry entry)
        {
            Keyframe[] keys = source.keys;
            int changedKeyCount = 0;
            float originalMinimum = float.PositiveInfinity;
            float originalMaximum = float.NegativeInfinity;
            float outputMinimum = float.PositiveInfinity;
            float outputMaximum = float.NegativeInfinity;

            for (int index = 0; index < keys.Length; index++)
            {
                Keyframe key = keys[index];
                float originalValue = key.value;
                float outputValue = RemapValueUnchecked(originalValue, settings);

                if (Mathf.Abs(outputValue - originalValue) > ChangeEpsilon)
                    changedKeyCount++;

                originalMinimum = Mathf.Min(originalMinimum, originalValue);
                originalMaximum = Mathf.Max(originalMaximum, originalValue);
                outputMinimum = Mathf.Min(outputMinimum, outputValue);
                outputMaximum = Mathf.Max(outputMaximum, outputValue);

                key.value = outputValue;
                key.inWeight = 0f;
                key.outWeight = 0f;
                key.weightedMode = WeightedMode.None;
                keys[index] = key;
            }

            for (int index = 0; index < keys.Length; index++)
            {
                Keyframe key = keys[index];
                key.inTangent = index > 0
                    ? CalculateSlope(keys[index - 1], keys[index])
                    : 0f;
                key.outTangent = index < keys.Length - 1
                    ? CalculateSlope(keys[index], keys[index + 1])
                    : 0f;
                keys[index] = key;
            }

            var result = new AnimationCurve(keys)
            {
                preWrapMode = source.preWrapMode,
                postWrapMode = source.postWrapMode
            };

            for (int index = 0; index < result.length; index++)
            {
                AnimationUtility.SetKeyLeftTangentMode(
                    result,
                    index,
                    AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyRightTangentMode(
                    result,
                    index,
                    AnimationUtility.TangentMode.Linear);
                AnimationUtility.SetKeyBroken(result, index, false);
            }

            if (keys.Length == 0)
            {
                originalMinimum = 0f;
                originalMaximum = 0f;
                outputMinimum = 0f;
                outputMaximum = 0f;
            }

            entry = new BlendShapeCurveRemapEntry(
                binding.path,
                binding.propertyName,
                keys.Length,
                changedKeyCount,
                originalMinimum,
                originalMaximum,
                outputMinimum,
                outputMaximum);
            return result;
        }

        private static float CalculateSlope(Keyframe from, Keyframe to)
        {
            float deltaTime = to.time - from.time;
            return deltaTime > 0f ? (to.value - from.value) / deltaTime : 0f;
        }

        private static HashSet<BlendShapeCurveTarget> ValidateInputs(
            AnimationClip clip,
            BlendShapeRemapSettings settings,
            IEnumerable<BlendShapeCurveTarget> targets)
        {
            if (clip == null)
                throw new ArgumentNullException("clip");

            string settingsError = GetSettingsValidationError(settings);
            if (!string.IsNullOrEmpty(settingsError))
                throw new ArgumentException(settingsError, "settings");

            if (targets == null)
                throw new ArgumentNullException("targets");

            var validatedTargets = new HashSet<BlendShapeCurveTarget>();
            foreach (BlendShapeCurveTarget target in targets)
            {
                if (string.IsNullOrEmpty(target.PropertyName) ||
                    !target.PropertyName.StartsWith(BlendShapePropertyPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                validatedTargets.Add(new BlendShapeCurveTarget(
                    target.MeshPath ?? string.Empty,
                    target.PropertyName));
            }

            if (validatedTargets.Count == 0)
                throw new ArgumentException("Select at least one blend-shape property.", "targets");

            return validatedTargets;
        }

        private static bool IsBlendShapeBinding(EditorCurveBinding binding)
        {
            return binding.type == typeof(SkinnedMeshRenderer) &&
                   binding.propertyName.StartsWith(BlendShapePropertyPrefix, StringComparison.Ordinal);
        }

        private static bool IsTargetBinding(
            EditorCurveBinding binding,
            HashSet<BlendShapeCurveTarget> targets)
        {
            return IsBlendShapeBinding(binding) &&
                   targets.Contains(new BlendShapeCurveTarget(binding.path, binding.propertyName));
        }

        private static float RemapValueUnchecked(float value, BlendShapeRemapSettings settings)
        {
            if (value <= settings.LowerThreshold)
                return settings.LowerOutput;

            if (value >= settings.UpperThreshold)
                return settings.UpperOutput;

            float t = (value - settings.LowerThreshold) /
                      (settings.UpperThreshold - settings.LowerThreshold);
            return Mathf.LerpUnclamped(settings.LowerOutput, settings.MiddleMaximumOutput, t);
        }

        private static string ValidateNewAssetPath(string destinationAssetPath)
        {
            if (string.IsNullOrWhiteSpace(destinationAssetPath))
                throw new ArgumentException("Specify an output .anim path.", "destinationAssetPath");

            string normalizedPath = destinationAssetPath.Replace('\\', '/');
            if (!normalizedPath.StartsWith("Assets/", StringComparison.Ordinal))
            {
                throw new ArgumentException(
                    "The output asset must be saved inside the project's Assets folder.",
                    "destinationAssetPath");
            }

            if (!string.Equals(Path.GetExtension(normalizedPath), ".anim", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("The output asset must use the .anim extension.", "destinationAssetPath");

            string directory = Path.GetDirectoryName(normalizedPath);
            if (string.IsNullOrEmpty(directory) || !AssetDatabase.IsValidFolder(directory.Replace('\\', '/')))
                throw new ArgumentException("The output folder does not exist.", "destinationAssetPath");

            if (AssetDatabase.LoadAssetAtPath<Object>(normalizedPath) != null)
                throw new InvalidOperationException("An asset already exists at " + normalizedPath + ".");

            return normalizedPath;
        }

        private static bool IsFinite(float value)
        {
            return !float.IsNaN(value) && !float.IsInfinity(value);
        }
    }
}
