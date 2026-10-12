using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;
using YAMO.UnityTools;

namespace YAMO.UnityTools.Editor
{
    internal static class MocapEditPoseRecorder
    {
        public static IEnumerator Record(Animator animator, AnimationClip clip, string path, float start,
            float duration, int rate, bool hinge, int axisIndex, float compensation, Func<string, float, bool> progress)
        {
            var bones = new List<Transform>(); var paths = new List<string>();
            for (var i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var bone = animator.GetBoneTransform((HumanBodyBones)i);
                if (bone == null || bones.Contains(bone)) continue;
                bones.Add(bone); paths.Add(AnimationUtility.CalculateTransformPath(bone, animator.transform));
            }
            var frames = checked((int)Math.Ceiling((double)duration * rate) + 1);
            var partial = path + ".partial";
            var axis = axisIndex == 0 ? Vector3.right : axisIndex == 1 ? Vector3.up : Vector3.forward;
            AnimationMode.StartAnimationMode();
            try
            {
                using (var stream = new MocapPoseStream(partial, frames, rate, duration, paths.ToArray()))
                {
                    var timer = System.Diagnostics.Stopwatch.StartNew();
                    for (var f = 0; f < frames; f++)
                    {
                        AnimationMode.BeginSampling();
                        try { AnimationMode.SampleAnimationClip(animator.gameObject, clip, start + Mathf.Min(f / (float)rate, duration)); }
                        finally { AnimationMode.EndSampling(); }
                        if (hinge)
                        {
                            ForearmHingeCorrection.Apply(animator, HumanBodyBones.LeftUpperArm, HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, axis, compensation);
                            ForearmHingeCorrection.Apply(animator, HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, axis, compensation);
                        }
                        foreach (var bone in bones) stream.Write(bone.localRotation, bone.localPosition);
                        if (timer.ElapsedMilliseconds >= 5 || f == frames - 1)
                        {
                            if (progress?.Invoke("Edit Mode 디스크 녹화", f / (float)frames) == true) throw new OperationCanceledException();
                            yield return null; timer.Restart();
                        }
                    }
                }
                if (File.Exists(path)) File.Delete(path);
                File.Move(partial, path);
            }
            finally
            {
                AnimationMode.StopAnimationMode();
                if (File.Exists(partial)) File.Delete(partial);
            }
        }
    }
}
