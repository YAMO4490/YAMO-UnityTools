using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    public static class AnimClipKeyReducer
    {
        public enum FitMode
        {
            Linear,
            Cubic,
            Auto,    // per-channel: run both, pick whichever produces fewer estimated bytes
        }

        // Estimated YAML cost per kept keyframe by tangent type.
        // Linear: slopes serialize as "0", weight as "0", short.
        // Cubic: slopes are non-zero floats, ~30 extra bytes per key.
        private const float LinearKeyByteCost = 190f;
        private const float CubicKeyByteCost = 220f;

        public struct Options
        {
            public float MuscleTolerance;
            public float SpineTolerance;
            public float RootPosTolerance;
            public float RootRotTolerance;
            public float GenericTolerance;
            public bool SetCompressedFlag;
            public bool RemoveEditorCurves;
            public FitMode Fit;
            public bool DropUnusedChannels;
            public float UnusedChannelThreshold;
            public float ResampleFrameRate;     // 0 = keep source, otherwise resample (e.g., 30, 24)
            public int SmoothingWindow;         // 0/1 = no smoothing, 3+ = moving average window

            public static Options Default => new Options
            {
                MuscleTolerance = 0.001f,
                SpineTolerance = 0.0003f,
                RootPosTolerance = 0.0002f,
                RootRotTolerance = 0.0003f,
                GenericTolerance = 0.001f,
                SetCompressedFlag = true,
                RemoveEditorCurves = true,
                Fit = FitMode.Auto,
                DropUnusedChannels = true,
                UnusedChannelThreshold = 0.005f,
                ResampleFrameRate = 0f,
                SmoothingWindow = 0,
            };
        }

        public struct Stats
        {
            public int CurveCount;
            public int OriginalKeyCount;
            public int ReducedKeyCount;
            public float MaxError;
            public double TotalErrorSum;
            public int ErrorSampleCount;
            public float ReductionRatio;   // reducedKeyCount / originalKeyCount
            public int DroppedChannels;
            public int OutputCurveCount;
            public float AvgError => ErrorSampleCount > 0 ? (float)(TotalErrorSum / ErrorSampleCount) : 0f;
        }

        private static readonly HashSet<string> _muscleNameSet = BuildMuscleNameSet();

        private static HashSet<string> BuildMuscleNameSet()
        {
            var set = new HashSet<string>();
            for (int i = 0; i < HumanTrait.MuscleCount; i++) set.Add(HumanTrait.MuscleName[i]);
            return set;
        }

        public static AnimationClip Reduce(AnimationClip src, Options opts, out Stats stats, System.Action<float, string> progress = null)
        {
            stats = default;
            if (src == null) return null;

            var newClip = new AnimationClip
            {
                name = src.name + "_reduced",
                frameRate = src.frameRate,
                wrapMode = src.wrapMode,
                legacy = src.legacy,
                localBounds = src.localBounds,
            };

            var settings = AnimationUtility.GetAnimationClipSettings(src);
            AnimationUtility.SetAnimationClipSettings(newClip, settings);
            newClip.events = src.events;

            var bindings = AnimationUtility.GetCurveBindings(src);
            var rotBindings = AnimationUtility.GetObjectReferenceCurveBindings(src);

            stats.CurveCount = bindings.Length;

            for (int b = 0; b < bindings.Length; b++)
            {
                var binding = bindings[b];
                progress?.Invoke((float)b / bindings.Length, $"채널 {b + 1}/{bindings.Length}: {binding.propertyName}");

                var srcCurve = AnimationUtility.GetEditorCurve(src, binding);
                if (srcCurve == null || srcCurve.length == 0) continue;

                float tol = ToleranceFor(binding.propertyName, opts);
                bool isRoot = IsRootChannel(binding.propertyName);
                bool isSpine = IsSpineMuscle(binding.propertyName);
                // Root channels (RootT/RootQ) drive hip world transform — never resample these,
                // since 30fps→60fps sub-sampling can introduce visible drift in body position/rotation.
                float effectiveResample = isRoot ? 0f : opts.ResampleFrameRate;
                var srcKeys = ResampleIfNeeded(srcCurve, effectiveResample, src.length);
                // Pre-smoothing removes high-frequency mocap noise that prevents key reduction.
                // Skip on Root and Spine channels to preserve body-orientation fidelity.
                if (opts.SmoothingWindow >= 3 && !isRoot && !isSpine)
                {
                    srcKeys = SmoothMovingAverage(srcKeys, opts.SmoothingWindow);
                }
                stats.OriginalKeyCount += srcKeys.Length;

                // Drop muscle channels whose values stay near zero throughout the entire clip.
                // Unity defaults missing humanoid muscles to 0, so the visual result is identical
                // and we save the entire curve+keyframes overhead in the YAML.
                if (opts.DropUnusedChannels && IsDroppableMuscle(binding.propertyName))
                {
                    float maxAbs = 0f;
                    for (int i = 0; i < srcKeys.Length; i++)
                    {
                        float a = Mathf.Abs(srcKeys[i].value);
                        if (a > maxAbs) maxAbs = a;
                    }
                    if (maxAbs <= opts.UnusedChannelThreshold)
                    {
                        stats.DroppedChannels++;
                        continue;
                    }
                }

                Keyframe[] reducedKeys;
                bool tangentsAlreadySet = false;
                bool usedCubic = false;

                // Root channels (RootT.xyz, RootQ.xyzw) drive the avatar's world transform.
                // Cubic on these can break quaternion unit-length and shift body position visibly,
                // so they're always reduced with linear-only RDP regardless of the global fit mode.
                FitMode effectiveFit = IsRootChannel(binding.propertyName) ? FitMode.Linear : opts.Fit;

                if (srcKeys.Length <= 2)
                {
                    reducedKeys = srcKeys;
                }
                else if (effectiveFit == FitMode.Cubic)
                {
                    reducedKeys = ReduceCubic(srcKeys, tol, ref stats);
                    tangentsAlreadySet = true;
                    usedCubic = true;
                }
                else if (effectiveFit == FitMode.Auto)
                {
                    // Run both, pick whichever yields smaller estimated byte size.
                    var cubicSlots = AnimClipCubicFitter.Fit(srcKeys, tol, out float cubicMaxErr);
                    var linearKeep = RDPReduce(srcKeys, tol);

                    float cubicBytes = cubicSlots.Count * CubicKeyByteCost;
                    float linearBytes = linearKeep.Count * LinearKeyByteCost;

                    if (cubicBytes < linearBytes)
                    {
                        if (cubicMaxErr > stats.MaxError) stats.MaxError = cubicMaxErr;
                        AccumulateCubicError(srcKeys, cubicSlots, ref stats);
                        reducedKeys = new Keyframe[cubicSlots.Count];
                        for (int i = 0; i < cubicSlots.Count; i++)
                        {
                            var s = cubicSlots[i];
                            var k = srcKeys[s.Index];
                            k.inTangent = s.InSlope;
                            k.outTangent = s.OutSlope;
                            k.inWeight = 0f;
                            k.outWeight = 0f;
                            k.weightedMode = WeightedMode.None;
                            reducedKeys[i] = k;
                        }
                        tangentsAlreadySet = true;
                        usedCubic = true;
                    }
                    else
                    {
                        reducedKeys = new Keyframe[linearKeep.Count];
                        for (int i = 0; i < linearKeep.Count; i++) reducedKeys[i] = srcKeys[linearKeep[i]];
                        AccumulateError(srcKeys, linearKeep, ref stats);
                    }
                }
                else
                {
                    var keepIndices = RDPReduce(srcKeys, tol);
                    reducedKeys = new Keyframe[keepIndices.Count];
                    for (int i = 0; i < keepIndices.Count; i++) reducedKeys[i] = srcKeys[keepIndices[i]];
                    AccumulateError(srcKeys, keepIndices, ref stats);
                }

                if (!tangentsAlreadySet) ApplyLinearTangents(reducedKeys);
                stats.ReducedKeyCount += reducedKeys.Length;

                var newCurve = new AnimationCurve(reducedKeys);
                var tangentMode = usedCubic
                    ? AnimationUtility.TangentMode.Free
                    : AnimationUtility.TangentMode.Linear;
                for (int i = 0; i < newCurve.length; i++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(newCurve, i, tangentMode);
                    AnimationUtility.SetKeyRightTangentMode(newCurve, i, tangentMode);
                }

                AnimationUtility.SetEditorCurve(newClip, binding, newCurve);
                stats.OutputCurveCount++;
            }

            // Object reference curves (rare for muscle clips, but copy verbatim).
            foreach (var binding in rotBindings)
            {
                var refCurve = AnimationUtility.GetObjectReferenceCurve(src, binding);
                AnimationUtility.SetObjectReferenceCurve(newClip, binding, refCurve);
            }

            stats.ReductionRatio = stats.OriginalKeyCount > 0
                ? (float)stats.ReducedKeyCount / stats.OriginalKeyCount : 0f;

            return newClip;
        }

        public static void SetCompressedFlag(AnimationClip clip, bool value)
        {
            if (clip == null) return;
            var so = new SerializedObject(clip);
            var prop = so.FindProperty("m_Compressed");
            if (prop == null) return;
            prop.boolValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        // Strips m_EditorCurves (and Euler editor curves), which duplicate m_FloatCurves on disk.
        // Runtime playback is unaffected; Animation window editing of the resulting clip will be limited.
        public static void ClearEditorCurves(AnimationClip clip)
        {
            if (clip == null) return;
            var so = new SerializedObject(clip);
            var editorProp = so.FindProperty("m_EditorCurves");
            if (editorProp != null && editorProp.isArray) editorProp.ClearArray();
            var eulerProp = so.FindProperty("m_EulerEditorCurves");
            if (eulerProp != null && eulerProp.isArray) eulerProp.ClearArray();
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        private static float ToleranceFor(string propertyName, Options opts)
        {
            switch (propertyName)
            {
                case "RootT.x":
                case "RootT.y":
                case "RootT.z":
                    return opts.RootPosTolerance;
                case "RootQ.x":
                case "RootQ.y":
                case "RootQ.z":
                case "RootQ.w":
                    return opts.RootRotTolerance;
                default:
                    if (IsSpineMuscle(propertyName)) return opts.SpineTolerance;
                    if (_muscleNameSet.Contains(propertyName)) return opts.MuscleTolerance;
                    return opts.GenericTolerance;
            }
        }

        private static bool IsSpineMuscle(string propertyName)
        {
            return propertyName != null && (
                propertyName.StartsWith("Spine ") ||
                propertyName.StartsWith("Chest ") ||
                propertyName.StartsWith("UpperChest "));
        }

        private static bool IsRootChannel(string propertyName)
        {
            return propertyName == "RootT.x" || propertyName == "RootT.y" || propertyName == "RootT.z"
                || propertyName == "RootQ.x" || propertyName == "RootQ.y" || propertyName == "RootQ.z" || propertyName == "RootQ.w";
        }

        // Only standard humanoid muscle channels can be safely dropped — Unity defaults them to 0.
        // Root channels and arbitrary generic curves must be kept (their default may not be 0).
        private static bool IsDroppableMuscle(string propertyName)
        {
            return propertyName != null
                && !IsRootChannel(propertyName)
                && _muscleNameSet.Contains(propertyName);
        }

        private static Keyframe[] ReduceCubic(Keyframe[] srcKeys, float tol, ref Stats stats)
        {
            var slots = AnimClipCubicFitter.Fit(srcKeys, tol, out float maxErr);
            if (maxErr > stats.MaxError) stats.MaxError = maxErr;
            AccumulateCubicError(srcKeys, slots, ref stats);
            var reduced = new Keyframe[slots.Count];
            for (int i = 0; i < slots.Count; i++)
            {
                var s = slots[i];
                var k = srcKeys[s.Index];
                k.inTangent = s.InSlope;
                k.outTangent = s.OutSlope;
                k.inWeight = 0f;
                k.outWeight = 0f;
                k.weightedMode = WeightedMode.None;
                reduced[i] = k;
            }
            return reduced;
        }

        private static Keyframe[] SmoothMovingAverage(Keyframe[] src, int window)
        {
            int n = src.Length;
            if (n < 2 || window < 2) return src;
            int half = window / 2;
            var result = new Keyframe[n];
            for (int i = 0; i < n; i++)
            {
                float sum = 0f;
                int count = 0;
                int start = Mathf.Max(0, i - half);
                int end = Mathf.Min(n - 1, i + half);
                for (int j = start; j <= end; j++) { sum += src[j].value; count++; }
                result[i] = new Keyframe(src[i].time, sum / count);
            }
            return result;
        }

        private static Keyframe[] ResampleIfNeeded(AnimationCurve curve, float targetRate, float clipLength)
        {
            if (targetRate <= 0f || clipLength <= 0f) return curve.keys;
            int frameCount = Mathf.Max(2, Mathf.RoundToInt(clipLength * targetRate) + 1);
            var result = new Keyframe[frameCount];
            for (int i = 0; i < frameCount; i++)
            {
                float t = i / targetRate;
                if (t > clipLength) t = clipLength;
                result[i] = new Keyframe(t, curve.Evaluate(t));
            }
            return result;
        }

        private static List<int> RDPReduce(Keyframe[] keys, float tolerance)
        {
            int n = keys.Length;
            var keep = new bool[n];
            keep[0] = true;
            keep[n - 1] = true;

            var stack = new Stack<(int start, int end)>();
            stack.Push((0, n - 1));

            while (stack.Count > 0)
            {
                var (start, end) = stack.Pop();
                if (end <= start + 1) continue;

                float t0 = keys[start].time;
                float t1 = keys[end].time;
                float v0 = keys[start].value;
                float v1 = keys[end].value;
                float dt = t1 - t0;

                if (dt <= 0f)
                {
                    for (int k = start + 1; k < end; k++) keep[k] = true;
                    continue;
                }

                float maxErr = 0f;
                int maxIdx = -1;
                for (int k = start + 1; k < end; k++)
                {
                    float t = (keys[k].time - t0) / dt;
                    float interp = v0 + (v1 - v0) * t;
                    float err = Mathf.Abs(interp - keys[k].value);
                    if (err > maxErr) { maxErr = err; maxIdx = k; }
                }

                if (maxErr > tolerance && maxIdx > 0)
                {
                    keep[maxIdx] = true;
                    stack.Push((start, maxIdx));
                    stack.Push((maxIdx, end));
                }
            }

            var result = new List<int>();
            for (int i = 0; i < n; i++) if (keep[i]) result.Add(i);
            return result;
        }

        private static void AccumulateError(Keyframe[] srcKeys, List<int> keepIndices, ref Stats stats)
        {
            for (int i = 1; i < keepIndices.Count; i++)
            {
                int s = keepIndices[i - 1];
                int e = keepIndices[i];
                float t0 = srcKeys[s].time;
                float t1 = srcKeys[e].time;
                float v0 = srcKeys[s].value;
                float v1 = srcKeys[e].value;
                float dt = t1 - t0;
                if (dt <= 0f) continue;
                for (int k = s + 1; k < e; k++)
                {
                    float t = (srcKeys[k].time - t0) / dt;
                    float interp = v0 + (v1 - v0) * t;
                    float err = Mathf.Abs(interp - srcKeys[k].value);
                    if (err > stats.MaxError) stats.MaxError = err;
                    stats.TotalErrorSum += err;
                    stats.ErrorSampleCount++;
                }
            }
        }

        private static void AccumulateCubicError(Keyframe[] srcKeys, List<AnimClipCubicFitter.Slot> slots, ref Stats stats)
        {
            for (int i = 1; i < slots.Count; i++)
            {
                int s = slots[i - 1].Index;
                int e = slots[i].Index;
                float t0 = srcKeys[s].time;
                float t1 = srcKeys[e].time;
                float dt = t1 - t0;
                if (dt <= 0f) continue;
                float p0 = srcKeys[s].value;
                float p1 = srcKeys[e].value;
                float m0 = slots[i - 1].OutSlope;
                float m1 = slots[i].InSlope;
                for (int k = s + 1; k < e; k++)
                {
                    float t = (srcKeys[k].time - t0) / dt;
                    float t2 = t * t;
                    float t3 = t2 * t;
                    float v = (2f * t3 - 3f * t2 + 1f) * p0
                            + (-2f * t3 + 3f * t2) * p1
                            + (t3 - 2f * t2 + t) * m0 * dt
                            + (t3 - t2) * m1 * dt;
                    float err = Mathf.Abs(v - srcKeys[k].value);
                    if (err > stats.MaxError) stats.MaxError = err;
                    stats.TotalErrorSum += err;
                    stats.ErrorSampleCount++;
                }
            }
        }

        private static void ApplyLinearTangents(Keyframe[] keys)
        {
            int n = keys.Length;
            for (int i = 0; i < n; i++)
            {
                var k = keys[i];
                k.inTangent = (i > 0)
                    ? (k.value - keys[i - 1].value) / Mathf.Max(1e-6f, k.time - keys[i - 1].time)
                    : 0f;
                k.outTangent = (i < n - 1)
                    ? (keys[i + 1].value - k.value) / Mathf.Max(1e-6f, keys[i + 1].time - k.time)
                    : 0f;
                k.inWeight = 0f;
                k.outWeight = 0f;
                k.weightedMode = WeightedMode.None;
                keys[i] = k;
            }
        }
    }
}
