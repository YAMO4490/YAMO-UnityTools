// ForearmHingeRecorder.cs
// Play Mode에서 실행되는 힌지 베이크 레코더.
// ForearmHingeBaker EditorWindow가 Play Mode 진입 직전에
// 캐릭터 GameObject에 이 컴포넌트를 자동으로 추가합니다.
//
// 동작:
// 1. animator.Update(1/sampleRate) 로 매 샘플 스텝씩 수동 진행
//    → Humanoid 전체 파이프라인(foot IK, stabilization 포함)이 적용된 상태로 본 위치 획득
// 2. Forearm 힌지 보정 알고리즘 적용 (팔뚝 비-힌지 성분 제거)
// 3. 전체 본의 localRotation / localPosition 기록
// 4. 기록 완료 → 결과를 바이너리 파일로 저장 → Play Mode 종료

using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace YAMO.UnityTools
{
    [DefaultExecutionOrder(200)]
    public class ForearmHingeRecorder : MonoBehaviour
    {
        // ForearmHingeBaker가 Play Mode 진입 전에 설정하는 파라미터
        [HideInInspector] public int   sampleRate     = 30;
        [HideInInspector] public int   hingeAxisIndex = 2; // 0=X 1=Y 2=Z

        // 결과 파일 경로 (Edit·Play Mode 양쪽에서 동일하게 접근)
        public static string ResultsFilePath =>
            Path.Combine(Application.dataPath, "..", "Temp", "ForearmHinge_results.bin");

        Animator          animator;
        List<Transform>   allBones  = new();
        List<string>      bonePaths = new();

        void Start()
        {
            animator = GetComponent<Animator>();
            if (animator == null)
            {
                Debug.LogError("[ForearmHingeRecorder] Animator를 찾을 수 없습니다.");
                return;
            }

            CollectHumanoidBones();
            StartCoroutine(Record());
        }

        // Avatar에 매핑된 Humanoid 본만 수집 (물리·악세서리 등 잡다한 본 제외)
        void CollectHumanoidBones()
        {
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var t = animator.GetBoneTransform((HumanBodyBones)i);
                if (t == null || allBones.Contains(t)) continue;

                allBones.Add(t);
                bonePaths.Add(BuildBonePath(t, transform));
            }
            Debug.Log($"[ForearmHingeRecorder] 기록 대상 본: {allBones.Count}개 (Humanoid 매핑 본만)");
        }

        // 루트(Animator GameObject) 기준 상대 경로 계산
        static string BuildBonePath(Transform bone, Transform root)
        {
            var parts = new List<string>();
            var t = bone;
            while (t != null && t != root)
            {
                parts.Insert(0, t.name);
                t = t.parent;
            }
            return string.Join("/", parts);
        }

        IEnumerator Record()
        {
            // Animator가 초기화되도록 1 프레임 대기
            yield return null;

            float clipLength  = animator.GetCurrentAnimatorStateInfo(0).length;
            int   totalFrames = Mathf.CeilToInt(clipLength * sampleRate) + 1;
            float dt          = 1f / sampleRate;
            int   boneCount   = allBones.Count;

            // 결과 배열 사전 할당
            var rotData = new Quaternion[boneCount][];
            var posData = new Vector3[boneCount][];
            for (int b = 0; b < boneCount; b++)
            {
                rotData[b] = new Quaternion[totalFrames];
                posData[b] = new Vector3[totalFrames];
            }

            Vector3 axisVec = hingeAxisIndex == 0 ? Vector3.right :
                               hingeAxisIndex == 1 ? Vector3.up   : Vector3.forward;

            var armTriplets = new (HumanBodyBones u, HumanBodyBones l, HumanBodyBones h)[]
            {
                (HumanBodyBones.LeftUpperArm,  HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand),
                (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand),
            };

            // Real-time 자동 진행을 막고 수동으로만 진행
            Time.timeScale = 0f;

            for (int i = 0; i < totalFrames; i++)
            {
                // ① Humanoid 파이프라인 전체 실행 (foot IK, stabilization 포함)
                animator.Update(dt);

                // ② Forearm 힌지 보정
                foreach (var (u, l, h) in armTriplets)
                    ApplyHingeCorrection(u, l, h, axisVec);

                // ③ 전체 본 기록
                for (int b = 0; b < boneCount; b++)
                {
                    rotData[b][i] = allBones[b].localRotation;
                    posData[b][i] = allBones[b].localPosition;
                }

                if (i % 100 == 0)
                    Debug.Log($"[ForearmHingeRecorder] {i} / {totalFrames} 프레임 녹화 중...");

                yield return null;
            }

            // 시간 복원
            Time.timeScale = 1f;

            // 결과 파일 저장
            Directory.CreateDirectory(Path.GetDirectoryName(ResultsFilePath)!);
            WriteResults(totalFrames, boneCount, rotData, posData);
            Debug.Log($"[ForearmHingeRecorder] 녹화 완료 → {ResultsFilePath}");

            // Play Mode 종료 요청
#if UNITY_EDITOR
            UnityEditor.EditorApplication.isPlaying = false;
#endif
        }

        // ============================================================
        // Forearm 힌지 보정 (ForearmHingeBaker의 알고리즘과 동일)
        // ============================================================
        void ApplyHingeCorrection(
            HumanBodyBones upperBone, HumanBodyBones lowerBone, HumanBodyBones handBone,
            Vector3 axisVec)
        {
            var upper = animator.GetBoneTransform(upperBone);
            var lower = animator.GetBoneTransform(lowerBone);
            var hand  = animator.GetBoneTransform(handBone);
            if (upper == null || lower == null || hand == null) return;

            Vector3    origHandPos = hand.position;
            Quaternion origHandRot = hand.rotation;
            Vector3    shoulderPos = upper.position;
            Vector3    elbowPos    = lower.position;

            lower.localRotation = Quaternion.identity;
            Vector3 h0 = hand.position - elbowPos;

            lower.localRotation = Quaternion.AngleAxis(90f, axisVec);
            Vector3 h90 = hand.position - elbowPos;

            Quaternion parentRot = lower.parent != null ? lower.parent.rotation : Quaternion.identity;
            Vector3 worldAxis = (parentRot * axisVec).normalized;

            Vector3 centerOffset  = Vector3.Dot(h0, worldAxis) * worldAxis;
            Vector3 r0            = h0  - centerOffset;
            Vector3 r90           = h90 - centerOffset;
            Vector3 targetOffset  = origHandPos - elbowPos - centerOffset;
            Vector3 targetInPlane = targetOffset - Vector3.Dot(targetOffset, worldAxis) * worldAxis;

            float theta = 0f;
            if (targetInPlane.sqrMagnitude > 1e-10f && r0.sqrMagnitude > 1e-10f)
            {
                theta = Mathf.Atan2(
                    Vector3.Dot(targetInPlane.normalized, r90.normalized),
                    Vector3.Dot(targetInPlane.normalized, r0.normalized)
                ) * Mathf.Rad2Deg;
            }

            lower.localRotation = Quaternion.AngleAxis(theta, axisVec);

            Vector3 curDir = hand.position - shoulderPos;
            Vector3 tgtDir = origHandPos   - shoulderPos;
            if (curDir.sqrMagnitude > 1e-8f && tgtDir.sqrMagnitude > 1e-8f)
                upper.rotation = Quaternion.FromToRotation(curDir.normalized, tgtDir.normalized) * upper.rotation;

            hand.rotation = origHandRot;
        }

        // ============================================================
        // 결과 바이너리 직렬화
        // 형식: frameCount(int) boneCount(int)
        //       [본마다] path(string) [프레임마다] quat(4f) pos(3f)
        // ============================================================
        void WriteResults(int frames, int bones,
            Quaternion[][] rotData, Vector3[][] posData)
        {
            using var w = new BinaryWriter(File.Open(ResultsFilePath, FileMode.Create));
            w.Write(frames);
            w.Write(bones);
            for (int b = 0; b < bones; b++)
            {
                w.Write(bonePaths[b]);
                for (int f = 0; f < frames; f++)
                {
                    var q = rotData[b][f];
                    w.Write(q.x); w.Write(q.y); w.Write(q.z); w.Write(q.w);
                    var v = posData[b][f];
                    w.Write(v.x); w.Write(v.y); w.Write(v.z);
                }
            }
        }
    }
}
