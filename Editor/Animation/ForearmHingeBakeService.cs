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
        public float HandRotationCompensation { get; set; } = 0.5f;
        public float StartTime { get; set; }
        public float Duration { get; set; }
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

            var duration = settings.Duration > 0f ? settings.Duration : sourceClip.length - settings.StartTime;
            if (settings.StartTime < 0f || duration <= 0f || settings.StartTime + duration > sourceClip.length + .001f)
                throw new InvalidOperationException("베이크 구간이 원본 클립 범위를 벗어났습니다.");
            var frameCount = Mathf.CeilToInt(duration * settings.SampleRate) + 1;
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
                    var time = Mathf.Min(frame / (float)settings.SampleRate, duration);
                    sampleTimes[frame] = time;

                    AnimationMode.BeginSampling();
                    try
                    {
                        AnimationMode.SampleAnimationClip(animator.gameObject, sourceClip, Mathf.Min(settings.StartTime + time, sourceClip.length));
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
                var header = reader.ReadInt32();
                var frameMajor = header == MocapPoseStream.Magic;
                var frameCount = frameMajor ? reader.ReadInt32() : header;
                var boneCount = reader.ReadInt32();
                var duration = frameMajor ? ReadStreamDuration(reader, sampleRate) : (frameCount - 1) / (float)sampleRate;
                var stream = reader.BaseStream;
                var payloadBytes = (long)frameCount * MocapPoseStream.BytesPerPose;
                if (frameCount <= 0 || boneCount <= 0 ||
                    payloadBytes > (stream.Length - stream.Position) / boneCount ||
                    (frameMajor && (frameCount < 2 || Math.Ceiling((double)duration * sampleRate) + 1 != frameCount)))
                    throw new InvalidDataException("Play Mode Hinge 결과의 헤더 또는 샘플 크기가 잘못되었습니다.");

                var paths = new string[boneCount];
                if (frameMajor)
                {
                    for (var bone = 0; bone < boneCount; bone++)
                        paths[bone] = reader.ReadString();
                    if (stream.Length - stream.Position != payloadBytes * boneCount)
                        throw new InvalidDataException("Play Mode Hinge 결과의 샘플 크기가 일치하지 않습니다.");
                }

                var sampleStart = stream.Position;
                var rotations = new Quaternion[frameCount];
                var positions = new Vector3[frameCount];
                var clip = new AnimationClip
                {
                    name = string.IsNullOrWhiteSpace(clipName) ? "PlayMode_hinged" : clipName,
                    frameRate = sampleRate
                };
                try
                {
                    for (var bone = 0; bone < boneCount; bone++)
                    {
                        var path = frameMajor ? paths[bone] : reader.ReadString();
                        for (var frame = 0; frame < frameCount; frame++)
                        {
                            if (frameMajor)
                                stream.Position = sampleStart + ((long)frame * boneCount + bone) * MocapPoseStream.BytesPerPose;
                            rotations[frame] = new Quaternion(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                            positions[frame] = new Vector3(reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                        }
                        WriteRecordedBoneCurves(clip, path, rotations, positions, sampleRate, duration);
                    }

                    if (stream.Position != stream.Length)
                        throw new InvalidDataException("Play Mode Hinge 결과 파일에 예상하지 못한 데이터가 남아 있습니다.");

                    clip.EnsureQuaternionContinuity();
                    return new ForearmHingeBakeResult { Clip = clip, FrameCount = frameCount, BoneCount = boneCount };
                }
                catch
                {
                    UnityEngine.Object.DestroyImmediate(clip);
                    throw;
                }
            }
        }

        private static float ReadStreamDuration(BinaryReader reader, int sampleRate)
        {
            var recordedRate = reader.ReadInt32();
            var duration = reader.ReadSingle();
            if (recordedRate != sampleRate || duration <= 0f || float.IsNaN(duration) || float.IsInfinity(duration))
                throw new InvalidDataException("Play Mode Hinge 결과의 샘플 레이트 또는 길이가 잘못되었습니다.");
            return duration;
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

                var positionX = new AnimationCurve();
                var positionY = new AnimationCurve();
                var positionZ = new AnimationCurve();
                for (var frame = 0; frame < sampleTimes.Count; frame++)
                {
                    // Fixed translations still define the baked skeleton. Keep
                    // endpoints so a different FBX rest pose cannot replace them.
                    if (!positionAnimated && frame > 0 && frame < sampleTimes.Count - 1)
                        continue;
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

        private static void WriteRecordedBoneCurves(
            AnimationClip clip,
            string path,
            Quaternion[] rotations,
            Vector3[] positions,
            int sampleRate,
            float duration)
        {
            var frameCount = rotations.Length;
            var rotationX = new AnimationCurve();
            var rotationY = new AnimationCurve();
            var rotationZ = new AnimationCurve();
            var rotationW = new AnimationCurve();
            for (var frame = 0; frame < frameCount; frame++)
            {
                var time = frame == frameCount - 1 ? duration : frame / (float)sampleRate;
                var rotation = rotations[frame];
                rotationX.AddKey(time, rotation.x);
                rotationY.AddKey(time, rotation.y);
                rotationZ.AddKey(time, rotation.z);
                rotationW.AddKey(time, rotation.w);
            }

            clip.SetCurve(path, typeof(Transform), "localRotation.x", rotationX);
            clip.SetCurve(path, typeof(Transform), "localRotation.y", rotationY);
            clip.SetCurve(path, typeof(Transform), "localRotation.z", rotationZ);
            clip.SetCurve(path, typeof(Transform), "localRotation.w", rotationW);

            var positionAnimated = false;
            for (var frame = 1; frame < frameCount; frame++)
            {
                if ((positions[frame] - positions[0]).sqrMagnitude > 1e-6f)
                {
                    positionAnimated = true;
                    break;
                }
            }

            var positionX = new AnimationCurve();
            var positionY = new AnimationCurve();
            var positionZ = new AnimationCurve();
            for (var frame = 0; frame < frameCount; frame++)
            {
                if (!positionAnimated && frame > 0 && frame < frameCount - 1)
                    continue;
                var time = frame == frameCount - 1 ? duration : frame / (float)sampleRate;
                var position = positions[frame];
                positionX.AddKey(time, position.x);
                positionY.AddKey(time, position.y);
                positionZ.AddKey(time, position.z);
            }
            clip.SetCurve(path, typeof(Transform), "localPosition.x", positionX);
            clip.SetCurve(path, typeof(Transform), "localPosition.y", positionY);
            clip.SetCurve(path, typeof(Transform), "localPosition.z", positionZ);
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
