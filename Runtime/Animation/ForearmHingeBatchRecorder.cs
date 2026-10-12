using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace YAMO.UnityTools
{
    [DefaultExecutionOrder(200)]
    public sealed class ForearmHingeBatchRecorder : MonoBehaviour
    {
        [HideInInspector] public int sampleRate = 60;
        [HideInInspector] public bool enableHingeCorrection = true;
        [HideInInspector] public int hingeAxisIndex = 2;
        [HideInInspector, Range(0f, 1f)] public float handRotationCompensation = 1f;
        [HideInInspector] public string[] stateNames;
        [HideInInspector] public string[] resultPaths;
        [HideInInspector] public float startTime;
        [HideInInspector] public float duration;
        private Animator animator;
        private readonly List<Transform> bones = new List<Transform>();
        private readonly List<string> bonePaths = new List<string>();
        private MocapPoseStream stream;
        private string partialPath;
        private float previousTimeScale;
        private bool ownsTimeScale;

        private void Start()
        {
            animator = GetComponent<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isValid ||
                !animator.avatar.isHuman || sampleRate <= 0 || stateNames == null || resultPaths == null ||
                stateNames.Length != 1 || resultPaths.Length != 1)
            {
                Debug.LogError("[Mocap Pipeline] 한 항목의 유효한 Humanoid 녹화 구성이 필요합니다.");
                ExitPlayMode(); return;
            }
            for (var i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var bone = animator.GetBoneTransform((HumanBodyBones)i);
                if (bone == null || bones.Contains(bone)) continue;
                bones.Add(bone);
                var parts = new List<string>();
                for (var t = bone; t != null && t != transform; t = t.parent) parts.Insert(0, t.name);
                bonePaths.Add(string.Join("/", parts));
            }
            StartCoroutine(RecordSafely());
        }
        private IEnumerator RecordSafely()
        {
            var recording = RecordState();
            try
            {
                while (true)
                {
                    bool more;
                    try { more = recording.MoveNext(); }
                    catch (Exception exception)
                    {
                        Debug.LogException(exception);
                        try { File.WriteAllText(resultPaths[0] + ".error.txt", exception.ToString()); }
                        catch (Exception writeError) { Debug.LogException(writeError); }
                        break;
                    }
                    if (!more) break;
                    yield return recording.Current;
                }
            }
            finally { (recording as IDisposable)?.Dispose(); Release(); }
            ExitPlayMode();
        }
        private IEnumerator RecordState()
        {
            yield return null;
            previousTimeScale = Time.timeScale; ownsTimeScale = true; Time.timeScale = 0f;
            animator.Play(stateNames[0], 0, 0f); animator.Update(0f);
            yield return null; animator.Update(0f);
            var clipLength = animator.GetCurrentAnimatorStateInfo(0).length;
            var length = duration > 0f ? duration : clipLength - startTime;
            if (bones.Count == 0 || length <= 0f || float.IsNaN(length) || float.IsInfinity(length) ||
                startTime < 0f || startTime + length > clipLength + 0.0001f)
                throw new InvalidOperationException("녹화 구간 또는 본 구성이 올바르지 않습니다.");
            var frames = checked((int)Math.Ceiling((double)length * sampleRate) + 1);
            var sampledTime = 0f;
            var leadFrame = 1;
            while (sampledTime < startTime)
            {
                var time = Mathf.Min(leadFrame++ / (float)sampleRate, startTime);
                animator.Update(time - sampledTime); sampledTime = time;
                yield return null;
            }
            partialPath = resultPaths[0] + ".partial";
            stream = new MocapPoseStream(partialPath, frames, sampleRate, length, bonePaths.ToArray());
            var axis = hingeAxisIndex == 0 ? Vector3.right : hingeAxisIndex == 1 ? Vector3.up : Vector3.forward;
            for (var frame = 0; frame < frames; frame++)
            {
                var time = startTime + Mathf.Min(frame / (float)sampleRate, length);
                if (frame > 0) animator.Update(Mathf.Max(0f, time - sampledTime));
                sampledTime = time;
                if (enableHingeCorrection)
                {
                    ForearmHingeCorrection.Apply(animator, HumanBodyBones.LeftUpperArm,
                        HumanBodyBones.LeftLowerArm, HumanBodyBones.LeftHand, axis, handRotationCompensation);
                    ForearmHingeCorrection.Apply(animator, HumanBodyBones.RightUpperArm,
                        HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand, axis, handRotationCompensation);
                }
                foreach (var bone in bones) stream.Write(bone.localRotation, bone.localPosition);
                if (frame % 600 == 0) Debug.Log($"[Mocap Pipeline] 디스크 녹화 {frame}/{frames} frames");
                yield return null;
            }
            stream.Dispose(); stream = null;
            if (File.Exists(resultPaths[0])) File.Delete(resultPaths[0]);
            File.Move(partialPath, resultPaths[0]); partialPath = null;
        }
        private void OnDisable() { StopAllCoroutines(); Release(); }
        private void OnDestroy() { Release(); }
        private void Release()
        {
            stream?.Dispose(); stream = null;
            if (ownsTimeScale) { Time.timeScale = previousTimeScale; ownsTimeScale = false; }
            if (!string.IsNullOrEmpty(partialPath) && File.Exists(partialPath)) File.Delete(partialPath);
            partialPath = null;
        }
        private static void ExitPlayMode()
        {
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#endif
        }
    }
}
