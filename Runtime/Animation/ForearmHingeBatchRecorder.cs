using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace YAMO.UnityTools
{
    /// <summary>
    /// Records multiple Humanoid motions during one Play Mode session so runtime
    /// foot IK/stabilization is included before the forearm hinge correction.
    /// The editor pipeline supplies one Animator state and result path per item.
    /// </summary>
    [DefaultExecutionOrder(200)]
    public sealed class ForearmHingeBatchRecorder : MonoBehaviour
    {
        [HideInInspector] public int sampleRate = 60;
        [HideInInspector] public bool enableHingeCorrection = true;
        [HideInInspector] public int hingeAxisIndex = 2;
        [HideInInspector, Range(0f, 1f)] public float handRotationCompensation = 1f;
        [HideInInspector] public string[] stateNames;
        [HideInInspector] public string[] resultPaths;

        private Animator animator;
        private readonly List<Transform> bones = new List<Transform>();
        private readonly List<string> bonePaths = new List<string>();

        private void Start()
        {
            animator = GetComponent<Animator>();
            if (!ValidateConfiguration())
            {
                ExitPlayMode();
                return;
            }

            CollectHumanoidBones();
            if (bones.Count == 0)
            {
                Debug.LogError("[Mocap Pipeline] Play Mode에서 기록할 Humanoid 본을 찾을 수 없습니다.");
                ExitPlayMode();
                return;
            }

            StartCoroutine(RecordAll());
        }

        private bool ValidateConfiguration()
        {
            if (animator == null || animator.avatar == null ||
                !animator.avatar.isValid || !animator.avatar.isHuman)
            {
                Debug.LogError("[Mocap Pipeline] Play Mode Recorder에 유효한 Humanoid Animator가 필요합니다.");
                return false;
            }
            if (sampleRate <= 0)
            {
                Debug.LogError("[Mocap Pipeline] Play Mode Sample Rate가 올바르지 않습니다.");
                return false;
            }
            if (stateNames == null || resultPaths == null ||
                stateNames.Length == 0 || stateNames.Length != resultPaths.Length)
            {
                Debug.LogError("[Mocap Pipeline] Play Mode 배치 항목 구성이 올바르지 않습니다.");
                return false;
            }
            return true;
        }

        private void CollectHumanoidBones()
        {
            for (var index = 0; index < (int)HumanBodyBones.LastBone; index++)
            {
                var bone = animator.GetBoneTransform((HumanBodyBones)index);
                if (bone == null || bones.Contains(bone))
                    continue;
                bones.Add(bone);
                bonePaths.Add(BuildBonePath(bone, transform));
            }
        }

        private static string BuildBonePath(Transform bone, Transform root)
        {
            var parts = new List<string>();
            for (var current = bone; current != null && current != root; current = current.parent)
                parts.Insert(0, current.name);
            return string.Join("/", parts);
        }

        private IEnumerator RecordAll()
        {
            yield return null;
            var previousTimeScale = Time.timeScale;
            Time.timeScale = 0f;
            try
            {
                for (var index = 0; index < stateNames.Length; index++)
                    yield return RecordState(index);
            }
            finally
            {
                Time.timeScale = previousTimeScale;
            }

            Debug.Log($"[Mocap Pipeline] Play Mode Hinge 배치 녹화 완료: {stateNames.Length}개");
            ExitPlayMode();
        }

        private IEnumerator RecordState(int itemIndex)
        {
            animator.Play(stateNames[itemIndex], 0, 0f);
            animator.Update(0f);
            yield return null;
            animator.Update(0f);

            var clipLength = animator.GetCurrentAnimatorStateInfo(0).length;
            if (clipLength <= 0f || float.IsNaN(clipLength) || float.IsInfinity(clipLength))
            {
                Debug.LogError($"[Mocap Pipeline] {stateNames[itemIndex]} 길이를 확인할 수 없습니다.");
                yield break;
            }

            var frameCount = Mathf.CeilToInt(clipLength * sampleRate) + 1;
            var boneCount = bones.Count;
            var rotations = new Quaternion[boneCount][];
            var positions = new Vector3[boneCount][];
            for (var bone = 0; bone < boneCount; bone++)
            {
                rotations[bone] = new Quaternion[frameCount];
                positions[bone] = new Vector3[frameCount];
            }

            var axis = hingeAxisIndex == 0 ? Vector3.right :
                       hingeAxisIndex == 1 ? Vector3.up : Vector3.forward;
            var sampledTime = 0f;
            for (var frame = 0; frame < frameCount; frame++)
            {
                if (frame > 0)
                {
                    var targetTime = Mathf.Min(frame / (float)sampleRate, clipLength);
                    animator.Update(Mathf.Max(0f, targetTime - sampledTime));
                    sampledTime = targetTime;
                }

                if (enableHingeCorrection)
                {
                    ForearmHingeCorrection.Apply(
                        animator,
                        HumanBodyBones.LeftUpperArm,
                        HumanBodyBones.LeftLowerArm,
                        HumanBodyBones.LeftHand,
                        axis,
                        handRotationCompensation);
                    ForearmHingeCorrection.Apply(
                        animator,
                        HumanBodyBones.RightUpperArm,
                        HumanBodyBones.RightLowerArm,
                        HumanBodyBones.RightHand,
                        axis,
                        handRotationCompensation);
                }

                for (var bone = 0; bone < boneCount; bone++)
                {
                    rotations[bone][frame] = bones[bone].localRotation;
                    positions[bone][frame] = bones[bone].localPosition;
                }

                if (frame % 100 == 0)
                {
                    Debug.Log(
                        $"[Mocap Pipeline] Play Mode {itemIndex + 1}/{stateNames.Length}, " +
                        $"{frame}/{frameCount} frames");
                }
                yield return null;
            }

            WriteResults(resultPaths[itemIndex], frameCount, rotations, positions);
        }

        private void WriteResults(
            string path,
            int frameCount,
            IReadOnlyList<Quaternion[]> rotations,
            IReadOnlyList<Vector3[]> positions)
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            using (var writer = new BinaryWriter(File.Open(path, FileMode.Create, FileAccess.Write, FileShare.None)))
            {
                writer.Write(frameCount);
                writer.Write(bones.Count);
                for (var bone = 0; bone < bones.Count; bone++)
                {
                    writer.Write(bonePaths[bone]);
                    for (var frame = 0; frame < frameCount; frame++)
                    {
                        var rotation = rotations[bone][frame];
                        writer.Write(rotation.x);
                        writer.Write(rotation.y);
                        writer.Write(rotation.z);
                        writer.Write(rotation.w);
                        var position = positions[bone][frame];
                        writer.Write(position.x);
                        writer.Write(position.y);
                        writer.Write(position.z);
                    }
                }
            }
        }

        private static void ExitPlayMode()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#endif
        }
    }
}
