using System;
using System.Collections.Generic;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    /*
        Cubic Hermite curve fitter (Schneider 1990, simplified for 1D time-series).

        Each segment [start, end] is approximated by a cubic Hermite curve with:
            P0 = keys[start].value
            P1 = keys[end].value
            M0 = outTangent at start (slope)
            M1 = inTangent at end (slope)

        M0 and M1 are solved via least-squares against the source samples between start and end.
        If the worst residual exceeds tolerance, split at the worst point and recurse.

        Output: list of (index, inSlope, outSlope) for each kept key. inSlope is taken from
        the segment ending at the key, outSlope from the segment starting at the key, so
        keys can have C0 corners at sharp transitions.
    */
    public static class AnimClipCubicFitter
    {
        public struct Slot
        {
            public int Index;
            public float InSlope;
            public float OutSlope;
        }

        private struct Segment
        {
            public int Start;
            public int End;
            public float Slope0;  // outSlope at Start
            public float Slope1;  // inSlope at End
        }

        public static List<Slot> Fit(Keyframe[] keys, float tolerance, out float maxOverallError)
        {
            maxOverallError = 0f;
            int n = keys.Length;
            var result = new List<Slot>();
            if (n == 0) return result;
            if (n == 1)
            {
                result.Add(new Slot { Index = 0, InSlope = 0f, OutSlope = 0f });
                return result;
            }

            // Recursive fit using an explicit stack to avoid C# stack overflow on long curves.
            var segments = new List<Segment>();
            var stack = new Stack<(int start, int end)>();
            stack.Push((0, n - 1));

            while (stack.Count > 0)
            {
                var (start, end) = stack.Pop();
                if (end - start < 1) continue;

                if (end - start == 1)
                {
                    // Atomic 1-frame segment: always linear (chord slope on both ends → pure linear interp).
                    float chord = (keys[end].value - keys[start].value)
                                  / Mathf.Max(1e-6f, keys[end].time - keys[start].time);
                    segments.Add(new Segment { Start = start, End = end, Slope0 = chord, Slope1 = chord });
                    continue;
                }

                FitOne(keys, start, end, out float s0, out float s1, out float maxErr, out int maxIdx);

                if (maxErr > tolerance)
                {
                    int splitIdx = maxIdx;
                    if (splitIdx <= start || splitIdx >= end)
                    {
                        // Bisection fallback when natural max-error point lands on boundary
                        // (e.g., overshoot in the first/last adjacent-pair sub-sample).
                        splitIdx = start + (end - start) / 2;
                        if (splitIdx <= start) splitIdx = start + 1;
                        if (splitIdx >= end) splitIdx = end - 1;
                    }
                    if (splitIdx > start && splitIdx < end)
                    {
                        stack.Push((start, splitIdx));
                        stack.Push((splitIdx, end));
                        continue;
                    }
                    // Truly indivisible (impossible for end > start+1, but defensive): force linear.
                    float chord = (keys[end].value - keys[start].value) / Mathf.Max(1e-6f, keys[end].time - keys[start].time);
                    s0 = chord;
                    s1 = chord;
                    maxErr = 0f;
                }

                segments.Add(new Segment { Start = start, End = end, Slope0 = s0, Slope1 = s1 });
                if (maxErr > maxOverallError) maxOverallError = maxErr;
            }

            segments.Sort((a, b) => a.Start.CompareTo(b.Start));

            // Build per-key slots: inSlope from segment ending here, outSlope from segment starting here.
            var slotMap = new Dictionary<int, Slot>(segments.Count + 1);
            foreach (var seg in segments)
            {
                if (!slotMap.TryGetValue(seg.Start, out var startSlot))
                    startSlot = new Slot { Index = seg.Start };
                startSlot.OutSlope = seg.Slope0;
                slotMap[seg.Start] = startSlot;

                if (!slotMap.TryGetValue(seg.End, out var endSlot))
                    endSlot = new Slot { Index = seg.End };
                endSlot.InSlope = seg.Slope1;
                slotMap[seg.End] = endSlot;
            }

            var sortedKeys = new List<int>(slotMap.Keys);
            sortedKeys.Sort();
            foreach (var idx in sortedKeys) result.Add(slotMap[idx]);
            return result;
        }

        private const int OvershootSubSamples = 8;

        // Slope clamp: tangent magnitude limit relative to chord slope. 1.5 = monotone-preserving,
        // 3.0 = allows real curvature while still preventing wild oscillation.
        private const float SlopeClampFactor = 3.0f;

        // Overshoot relax: cubic is allowed to extend beyond the local source-pair box by up to
        // (tolerance × this factor). 1.0 = strict box (linear-like), 1.5+ = visible curvature.
        // Internally we DIVIDE measured overshoot by this factor so the caller's tolerance check
        // effectively becomes "split if overshoot > tolerance × OvershootRelaxFactor".
        private const float OvershootRelaxFactor = 1.5f;

        private static void FitOne(Keyframe[] keys, int start, int end, out float slope0, out float slope1, out float maxErr, out int maxIdx)
        {
            float t0 = keys[start].time;
            float t1 = keys[end].time;
            float dt = t1 - t0;
            float p0 = keys[start].value;
            float p1 = keys[end].value;

            slope0 = 0f; slope1 = 0f; maxErr = 0f; maxIdx = -1;
            if (dt <= 0f)
            {
                return;
            }

            double a11 = 0, a12 = 0, a22 = 0;
            double b1 = 0, b2 = 0;

            for (int k = start + 1; k < end; k++)
            {
                float t = (keys[k].time - t0) / dt;
                float t2 = t * t;
                float t3 = t2 * t;
                float h00 = 2f * t3 - 3f * t2 + 1f;
                float h01 = -2f * t3 + 3f * t2;
                float h10 = (t3 - 2f * t2 + t) * dt;
                float h11 = (t3 - t2) * dt;

                float r = keys[k].value - h00 * p0 - h01 * p1;

                a11 += (double)h10 * h10;
                a12 += (double)h10 * h11;
                a22 += (double)h11 * h11;
                b1 += (double)h10 * r;
                b2 += (double)h11 * r;
            }

            float chord = (p1 - p0) / dt;
            double det = a11 * a22 - a12 * a12;
            if (Math.Abs(det) < 1e-12)
            {
                slope0 = chord;
                slope1 = chord;
            }
            else
            {
                slope0 = (float)((a22 * b1 - a12 * b2) / det);
                slope1 = (float)((a11 * b2 - a12 * b1) / det);
            }

            // Slope clamping (Fritsch-Carlson monotone-preserving threshold).
            // Limits how strongly tangents can "pull" the curve, preventing exotic overshoot.
            float maxAbsSlope = Mathf.Max(Mathf.Abs(chord) * SlopeClampFactor, 1e-4f);
            slope0 = Mathf.Clamp(slope0, -maxAbsSlope, maxAbsSlope);
            slope1 = Mathf.Clamp(slope1, -maxAbsSlope, maxAbsSlope);

            // Pass 1: fit error at source samples.
            for (int k = start; k <= end; k++)
            {
                float t = (keys[k].time - t0) / dt;
                float t2 = t * t;
                float t3 = t2 * t;
                float v = (2f * t3 - 3f * t2 + 1f) * p0
                        + (-2f * t3 + 3f * t2) * p1
                        + (t3 - 2f * t2 + t) * slope0 * dt
                        + (t3 - t2) * slope1 * dt;
                float err = Mathf.Abs(v - keys[k].value);
                if (err > maxErr) { maxErr = err; maxIdx = k; }
            }

            // Pass 2: between adjacent source keys, sub-sample the cubic and check that it stays
            // within the value range of those two source keys. Anything outside that range is a
            // genuine overshoot (motion that didn't exist in the source data) and is penalized
            // even when source-sample error is small.
            for (int k = start; k < end; k++)
            {
                float kt0 = keys[k].time;
                float kt1 = keys[k + 1].time;
                float kdt = kt1 - kt0;
                if (kdt <= 0f) continue;
                float kvLo = Mathf.Min(keys[k].value, keys[k + 1].value);
                float kvHi = Mathf.Max(keys[k].value, keys[k + 1].value);

                for (int s = 1; s <= OvershootSubSamples; s++)
                {
                    float st = (float)s / (OvershootSubSamples + 1);
                    float tAbs = kt0 + kdt * st;
                    float t = (tAbs - t0) / dt;
                    float t2 = t * t;
                    float t3 = t2 * t;
                    float v = (2f * t3 - 3f * t2 + 1f) * p0
                            + (-2f * t3 + 3f * t2) * p1
                            + (t3 - 2f * t2 + t) * slope0 * dt
                            + (t3 - t2) * slope1 * dt;

                    float overshoot = 0f;
                    if (v > kvHi) overshoot = v - kvHi;
                    else if (v < kvLo) overshoot = kvLo - v;

                    // Scale overshoot down so caller's tolerance check effectively allows
                    // (OvershootRelaxFactor × tolerance) of curvature beyond local box.
                    float effective = overshoot / OvershootRelaxFactor;
                    if (effective > maxErr)
                    {
                        maxErr = effective;
                        maxIdx = (st < 0.5f) ? k : k + 1;
                    }
                }
            }
        }
    }
}
