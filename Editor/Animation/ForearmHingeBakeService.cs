using System;
using System.Collections.Generic;
using System.IO;
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
        public bool EnableHingeCorrection { get; set; } = true;
        public ForearmHingeAxis HingeAxis { get; set; } = ForearmHingeAxis.Z;
        public float HandRotationCompensation { get; set; } = 1f;
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

            if (settings.EnableHingeCorrection)
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

                    if (settings.EnableHingeCorrection)
                    {
                        YAMO.UnityTools.ForearmHingeCorrection.Apply(
                            animator,
                            HumanBodyBones.LeftUpperArm,
                            HumanBodyBones.LeftLowerArm,
                            HumanBodyBones.LeftHand,
                            axis,
                            settings.HandRotationCompensation);
                        YAMO.UnityTools.ForearmHingeCorrection.Apply(
                            animator,
                            HumanBodyBones.RightUpperArm,
                            HumanBodyBones.RightLowerArm,
                            HumanBodyBones.RightHand,
                            axis,
                            settings.HandRotationCompensation);

                        StoreCorrectedArm(animator, rotations, frame);
                    }

                    if (progressCallback?.Invoke(
                            settings.EnableHingeCorrection
                                ? $"{sourceClip.name}: Forearm Hinge {frame + 1}/{frameCount}"
                                : $"{sourceClip.name}: Transform Bake {frame + 1}/{frameCount}",
                            (frame + 1f) / frameCount) == true)
                        throw new OperationCanceledException("Forearm Hinge 베이크가 취소되었습니다.");
                }
            }
            finally
            {
                AnimationMode.StopAnimationMode();
            }

            var clip = BuildClip(
                sourceClip.name + (settings.EnableHingeCorrection ? "_hinged" : "_baked"),
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

        /// <summary>
        /// Rebuilds an in-memory Generic Transform clip from a Play Mode recorder
        /// result. The returned clip is not saved as an asset and must be destroyed
        /// by the caller after the final FBX export.
        /// </summary>
        public static ForearmHingeBakeResult LoadPlayModeResult(
            string resultsPath,
            int sampleRate,
            string clipName)
        {
            if (string.IsNullOrWhiteSpace(resultsPath) || !File.Exists(resultsPath))
                throw new FileNotFoundException("Play Mode Hinge 결과 파일을 찾을 수 없습니다.", resultsPath);
            if (sampleRate <= 0)
                throw new ArgumentOutOfRangeException(nameof(sampleRate));

            using (var reader = new BinaryReader(File.Open(resultsPath, FileMode.Open, FileAccess.Read, FileShare.Read)))
            {
                var frameCount = reader.ReadInt32();
                var boneCount = reader.ReadInt32();
                if (frameCount <= 0 || boneCount <= 0)
                    throw new InvalidDataException(
                        $"Play Mode Hinge 결과가 비어 있습니다: {frameCount} frames, {boneCount} bones.");

                var paths = new string[boneCount];
                var rotations = new Quaternion[boneCount][];
                var positions = new Vector3[boneCount][];
                for (var bone = 0; bone < boneCount; bone++)
                {
                    paths[bone] = reader.ReadString();
                    rotations[bone] = new Quaternion[frameCount];
                    positions[bone] = new Vector3[frameCount];
                    for (var frame = 0; frame < frameCount; frame++)
                    {
                        rotations[bone][frame] = new Quaternion(
                            reader.ReadSingle(), reader.ReadSingle(),
                            reader.ReadSingle(), reader.ReadSingle());
                        positions[bone][frame] = new Vector3(
                            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    }
                }

                if (reader.BaseStream.Position != reader.BaseStream.Length)
                    throw new InvalidDataException("Play Mode Hinge 결과 파일에 예상하지 못한 데이터가 남아 있습니다.");

                var clip = BuildClipFromRecordedPaths(
                    string.IsNullOrWhiteSpace(clipName) ? "PlayMode_hinged" : clipName,
                    sampleRate,
                    paths,
                    rotations,
                    positions,
                    frameCount);
                return new ForearmHingeBakeResult
                {
                    Clip = clip,
                    FrameCount = frameCount,
                    BoneCount = boneCount
                };
            }
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

        private static AnimationClip BuildClipFromRecordedPaths(
            string name,
            int sampleRate,
            IReadOnlyList<string> paths,
            IReadOnlyList<Quaternion[]> rotations,
            IReadOnlyList<Vector3[]> positions,
            int frameCount)
        {
            var clip = new AnimationClip { name = name, frameRate = sampleRate };
            for (var bone = 0; bone < paths.Count; bone++)
            {
                var rotationX = new AnimationCurve();
                var rotationY = new AnimationCurve();
                var rotationZ = new AnimationCurve();
                var rotationW = new AnimationCurve();
                for (var frame = 0; frame < frameCount; frame++)
                {
                    var time = frame / (float)sampleRate;
                    var rotation = rotations[bone][frame];
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
                for (var frame = 1; frame < frameCount; frame++)
                {
                    if ((positions[bone][frame] - positions[bone][0]).sqrMagnitude > 1e-6f)
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
                for (var frame = 0; frame < frameCount; frame++)
                {
                    var time = frame / (float)sampleRate;
                    var position = positions[bone][frame];
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
