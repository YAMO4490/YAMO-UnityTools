using System;
using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    public enum ForearmHingeAxis
    {
        X,
        Y,
        Z
    }

    public sealed class ForearmHingeBakeSettings
    {
        public int SampleRate { get; set; } = 60;
        public ForearmHingeAxis HingeAxis { get; set; } = ForearmHingeAxis.Z;
    }

    public sealed class ForearmHingeBakeResult
    {
        public AnimationClip Clip { get; internal set; }
        public int FrameCount { get; internal set; }
        public int BoneCount { get; internal set; }
    }

    /// <summary>
    /// Synchronous Edit Mode forearm-hinge baker with no EditorWindow, selection,
    /// dialog, or asset-path dependency.
    /// </summary>
    public static class ForearmHingeBakeService
    {
        public static ForearmHingeBakeResult BakeEditMode(
            Animator animator,
            AnimationClip sourceClip,
            ForearmHingeBakeSettings settings = null,
            Func<string, float, bool> progressCallback = null)
        {
            if (animator == null)
                throw new ArgumentNullException(nameof(animator));
            if (sourceClip == null)
                throw new ArgumentNullException(nameof(sourceClip));

            settings ??= new ForearmHingeBakeSettings();
            if (settings.SampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(settings.SampleRate), "Sample rate must be greater than zero.");
            if (animator.avatar == null || !animator.avatar.isValid || !animator.avatar.isHuman)
                throw new InvalidOperationException("Animator에 유효한 Humanoid Avatar가 필요합니다.");
            if (AnimationMode.InAnimationMode())
                throw new InvalidOperationException("Animation Preview가 활성화되어 있습니다. Preview를 끈 뒤 다시 실행하세요.");

            var bones = new List<Transform>();
            var bonePaths = new Dictionary<Transform, string>();
            CollectHumanoidBones(animator, bones, bonePaths);
            if (bones.Count == 0)
                throw new InvalidOperationException("Humanoid 매핑 본을 찾을 수 없습니다.");

            ValidateArmMapping(animator);

            var frameCount = Mathf.CeilToInt(sourceClip.length * settings.SampleRate) + 1;
            var sampleTimes = new float[frameCount];
            var rotations = new Dictionary<Transform, Quaternion[]>(bones.Count);
            var positions = new Dictionary<Transform, Vector3[]>(bones.Count);
            foreach (var bone in bones)
            {
                rotations[bone] = new Quaternion[frameCount];
                positions[bone] = new Vector3[frameCount];
            }

            var axis = HingeAxisVector(settings.HingeAxis);
            AnimationMode.StartAnimationMode();
            try
            {
                for (var frame = 0; frame < frameCount; frame++)
                {
                    var time = Mathf.Min(frame / (float)settings.SampleRate, sourceClip.length);
                    sampleTimes[frame] = time;

                    AnimationMode.BeginSampling();
                    try
                    {
                        AnimationMode.SampleAnimationClip(animator.gameObject, sourceClip, time);
                    }
                    finally
                    {
                        AnimationMode.EndSampling();
                    }

                    foreach (var bone in bones)
                    {
                        rotations[bone][frame] = bone.localRotation;
                        positions[bone][frame] = bone.localPosition;
                    }

                    ApplyHingeCorrection(
                        animator,
                        HumanBodyBones.LeftUpperArm,
                        HumanBodyBones.LeftLowerArm,
                        HumanBodyBones.LeftHand,
                        axis);
                    ApplyHingeCorrection(
                        animator,
                        HumanBodyBones.RightUpperArm,
                        HumanBodyBones.RightLowerArm,
                        HumanBodyBones.RightHand,
                        axis);

                    StoreCorrectedArm(animator, rotations, frame);

                    if (progressCallback?.Invoke(
                            $"{sourceClip.name}: Forearm Hinge {frame + 1}/{frameCount}",
                            (frame + 1f) / frameCount) == true)
                        throw new OperationCanceledException("Forearm Hinge 베이크가 취소되었습니다.");
                }
            }
            finally
            {
                AnimationMode.StopAnimationMode();
            }

            var clip = BuildClip(
                sourceClip.name + "_hinged",
                settings.SampleRate,
                bones,
                bonePaths,
                rotations,
                positions,
                sampleTimes);

            return new ForearmHingeBakeResult
            {
                Clip = clip,
                FrameCount = frameCount,
                BoneCount = bones.Count
            };
        }

        public static string SaveAsAsset(
            ForearmHingeBakeResult result,
            string assetPath,
            bool overwrite = false)
        {
            if (result?.Clip == null)
                throw new ArgumentException("저장할 Hinge Bake 결과가 없습니다.", nameof(result));
            if (string.IsNullOrWhiteSpace(assetPath) ||
                !assetPath.StartsWith("Assets/", StringComparison.OrdinalIgnoreCase) ||
                !assetPath.EndsWith(".anim", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("AnimationClip 경로는 Assets 아래의 .anim 경로여야 합니다.", nameof(assetPath));

            var existing = AssetDatabase.LoadMainAssetAtPath(assetPath);
            if (existing != null)
            {
                if (!overwrite)
                    assetPath = AssetDatabase.GenerateUniqueAssetPath(assetPath);
                else if (!AssetDatabase.DeleteAsset(assetPath))
                    throw new InvalidOperationException($"기존 AnimationClip을 삭제하지 못했습니다: {assetPath}");
            }

            AssetDatabase.CreateAsset(result.Clip, assetPath);
            AssetDatabase.SaveAssets();
            return assetPath;
        }

        private static void ValidateArmMapping(Animator animator)
        {
            var required = new[]
            {
                HumanBodyBones.LeftUpperArm,
                HumanBodyBones.LeftLowerArm,
                HumanBodyBones.LeftHand,
                HumanBodyBones.RightUpperArm,
                HumanBodyBones.RightLowerArm,
                HumanBodyBones.RightHand
            };

            foreach (var bone in required)
            {
                if (animator.GetBoneTransform(bone) == null)
                    throw new InvalidOperationException($"Humanoid Avatar에 {bone} 매핑이 없습니다.");
            }
        }

        private static void StoreCorrectedArm(
            Animator animator,
            IReadOnlyDictionary<Transform, Quaternion[]> rotations,
            int frame)
        {
            var corrected = new[]
            {
                HumanBodyBones.LeftUpperArm,
                HumanBodyBones.LeftLowerArm,
                HumanBodyBones.LeftHand,
                HumanBodyBones.RightUpperArm,
                HumanBodyBones.RightLowerArm,
                HumanBodyBones.RightHand
            };

            foreach (var boneId in corrected)
            {
                var bone = animator.GetBoneTransform(boneId);
                if (bone != null && rotations.TryGetValue(bone, out var values))
                    values[frame] = bone.localRotation;
            }
        }

        private static void ApplyHingeCorrection(
            Animator animator,
            HumanBodyBones upperBone,
            HumanBodyBones lowerBone,
            HumanBodyBones handBone,
            Vector3 localAxis)
        {
            var upper = animator.GetBoneTransform(upperBone);
            var lower = animator.GetBoneTransform(lowerBone);
            var hand = animator.GetBoneTransform(handBone);

            var originalHandPosition = hand.position;
            var originalHandRotation = hand.rotation;
            var shoulderPosition = upper.position;
            var elbowPosition = lower.position;

            lower.localRotation = Quaternion.identity;
            var handAtZero = hand.position - elbowPosition;

            lower.localRotation = Quaternion.AngleAxis(90f, localAxis);
            var handAtNinety = hand.position - elbowPosition;

            var parentRotation = lower.parent != null ? lower.parent.rotation : Quaternion.identity;
            var worldAxis = (parentRotation * localAxis).normalized;
            var centerOffset = Vector3.Dot(handAtZero, worldAxis) * worldAxis;
            var radialZero = handAtZero - centerOffset;
            var radialNinety = handAtNinety - centerOffset;
            var targetOffset = originalHandPosition - elbowPosition - centerOffset;
            var targetInPlane = targetOffset - Vector3.Dot(targetOffset, worldAxis) * worldAxis;

            var angle = 0f;
            if (targetInPlane.sqrMagnitude > 1e-10f && radialZero.sqrMagnitude > 1e-10f)
            {
                angle = Mathf.Atan2(
                    Vector3.Dot(targetInPlane.normalized, radialNinety.normalized),
                    Vector3.Dot(targetInPlane.normalized, radialZero.normalized)) * Mathf.Rad2Deg;
            }

            lower.localRotation = Quaternion.AngleAxis(angle, localAxis);

            var currentDirection = hand.position - shoulderPosition;
            var targetDirection = originalHandPosition - shoulderPosition;
            if (currentDirection.sqrMagnitude > 1e-8f && targetDirection.sqrMagnitude > 1e-8f)
            {
                upper.rotation = Quaternion.FromToRotation(
                    currentDirection.normalized,
                    targetDirection.normalized) * upper.rotation;
            }

            hand.rotation = originalHandRotation;
        }

        private static AnimationClip BuildClip(
            string name,
            int sampleRate,
            IReadOnlyList<Transform> bones,
            IReadOnlyDictionary<Transform, string> paths,
            IReadOnlyDictionary<Transform, Quaternion[]> rotations,
            IReadOnlyDictionary<Transform, Vector3[]> positions,
            IReadOnlyList<float> sampleTimes)
        {
            var clip = new AnimationClip
            {
                name = name,
                frameRate = sampleRate
            };

            foreach (var bone in bones)
            {
                var rotationValues = rotations[bone];
                var positionValues = positions[bone];
                var rotationX = new AnimationCurve();
                var rotationY = new AnimationCurve();
                var rotationZ = new AnimationCurve();
                var rotationW = new AnimationCurve();

                for (var frame = 0; frame < sampleTimes.Count; frame++)
                {
                    var time = sampleTimes[frame];
                    var rotation = rotationValues[frame];
                    rotationX.AddKey(time, rotation.x);
                    rotationY.AddKey(time, rotation.y);
                    rotationZ.AddKey(time, rotation.z);
                    rotationW.AddKey(time, rotation.w);
                }

                var path = paths[bone];
                clip.SetCurve(path, typeof(Transform), "localRotation.x", rotationX);
                clip.SetCurve(path, typeof(Transform), "localRotation.y", rotationY);
                clip.SetCurve(path, typeof(Transform), "localRotation.z", rotationZ);
                clip.SetCurve(path, typeof(Transform), "localRotation.w", rotationW);

                var positionAnimated = false;
                for (var frame = 1; frame < sampleTimes.Count; frame++)
                {
                    if ((positionValues[frame] - positionValues[0]).sqrMagnitude > 1e-6f)
                    {
                        positionAnimated = true;
                        break;
                    }
                }

                if (!positionAnimated)
                    continue;

                var positionX = new AnimationCurve();
                var positionY = new AnimationCurve();
                var positionZ = new AnimationCurve();
                for (var frame = 0; frame < sampleTimes.Count; frame++)
                {
                    var time = sampleTimes[frame];
                    var position = positionValues[frame];
                    positionX.AddKey(time, position.x);
                    positionY.AddKey(time, position.y);
                    positionZ.AddKey(time, position.z);
                }

                clip.SetCurve(path, typeof(Transform), "localPosition.x", positionX);
                clip.SetCurve(path, typeof(Transform), "localPosition.y", positionY);
                clip.SetCurve(path, typeof(Transform), "localPosition.z", positionZ);
            }

            clip.EnsureQuaternionContinuity();
            return clip;
        }

        private static void CollectHumanoidBones(
            Animator animator,
            ICollection<Transform> bones,
            IDictionary<Transform, string> paths)
        {
            for (var index = 0; index < (int)HumanBodyBones.LastBone; index++)
            {
                var bone = animator.GetBoneTransform((HumanBodyBones)index);
                if (bone == null || bones.Contains(bone))
                    continue;

                bones.Add(bone);
                paths[bone] = AnimationUtility.CalculateTransformPath(bone, animator.transform);
            }
        }

        private static Vector3 HingeAxisVector(ForearmHingeAxis axis)
        {
            switch (axis)
            {
                case ForearmHingeAxis.X:
                    return Vector3.right;
                case ForearmHingeAxis.Y:
                    return Vector3.up;
                default:
                    return Vector3.forward;
            }
        }
    }
}
