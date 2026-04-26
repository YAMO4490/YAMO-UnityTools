// ForearmHingeBaker.cs
// Humanoid 애니메이션 클립에서 Forearm의 비-힌지 회전 성분을 제거하고,
// UpperArm 회전을 보정하여 Hand가 원래 방향을 유지하도록 합니다.
// Biped의 단축(힌지) Forearm과 호환되는 Generic 클립을 생성합니다.
//
// 알고리즘:
// 1. 원본 포즈에서 Hand 월드 위치/회전을 기록
// 2. Forearm 힌지각을 해석적으로 풀이 — Hand가 그리는 원 위에서 원본 Hand에 가장 가까운 점
// 3. UpperArm을 최소한으로 보정하여 Hand가 원래 방향을 가리키도록
// 4. Hand 월드 회전을 원본으로 복원
//
// 사용법:
// 1. 씬에 Animator + Avatar가 설정된 캐릭터 배치
// 2. Tools > YAMO > Animation > Forearm Hinge Baker
// 3. Animator와 소스 클립 지정
// 4. 힌지축 설정 (Forearm이 구부러지는 로컬 축)
// 5. Bake 실행 → _hinged.anim 파일 생성

using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace YAMO.UnityTools.Editor
{
    public class ForearmHingeBaker : EditorWindow
    {
        Animator animator;
        AnimationClip sourceClip;
        int sampleRate = 30;

        enum HingeAxis { X, Y, Z }
        HingeAxis hingeAxis = HingeAxis.Z;

        [MenuItem("Tools/YAMO/Animation/Forearm Hinge Baker")]
        static void Open()
        {
            var win = GetWindow<ForearmHingeBaker>("Forearm Hinge Baker");
            win.minSize = new Vector2(350, 280);
        }

        void OnGUI()
        {
            EditorGUILayout.Space(4);
            animator = EditorGUILayout.ObjectField("Animator (씬)", animator, typeof(Animator), true) as Animator;
            sourceClip = EditorGUILayout.ObjectField("소스 클립", sourceClip, typeof(AnimationClip), false) as AnimationClip;

            EditorGUILayout.Space(4);
            sampleRate = EditorGUILayout.IntSlider("샘플레이트 (fps)", sampleRate, 1, 120);
            hingeAxis = (HingeAxis)EditorGUILayout.EnumPopup("Forearm 힌지축 (로컬)", hingeAxis);

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Forearm 힌지각을 해석적으로 풀이합니다.\n" +
                "Hand가 힌지 회전으로 그리는 원 위에서 원본 위치에 가장 가까운 점을 찾고,\n" +
                "UpperArm을 최소한으로 보정합니다.\n\n" +
                "출력은 Generic 클립 (bone localRotation 기반)입니다.",
                MessageType.Info);

            EditorGUILayout.Space(4);
            GUI.enabled = animator != null && sourceClip != null;
            if (GUILayout.Button("Bake", GUILayout.Height(30)))
                Bake();
            GUI.enabled = true;
        }

        // ============================================================
        // Core
        // ============================================================
        void Bake()
        {
            var go = animator.gameObject;

            // 양쪽 팔 트리플렛: upper, lower, hand
            var armTriplets = new (HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones hand)[]
            {
                (HumanBodyBones.LeftUpperArm,  HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand),
                (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand),
            };

            // 본 계층 수집 (루트 자식부터, path = 상대경로)
            var allBones = new List<Transform>();
            var bonePaths = new Dictionary<Transform, string>();
            foreach (Transform child in go.transform)
                CollectHierarchy(child, child.name, allBones, bonePaths);

            if (allBones.Count == 0)
            {
                EditorUtility.DisplayDialog("오류", "Animator 하위에 본이 없습니다.", "OK");
                return;
            }

            int frameCount = Mathf.CeilToInt(sourceClip.length * sampleRate) + 1;

            // 프레임별 저장소
            var rotations = new Dictionary<Transform, Quaternion[]>();
            var positions = new Dictionary<Transform, Vector3[]>();
            foreach (var bone in allBones)
            {
                rotations[bone] = new Quaternion[frameCount];
                positions[bone] = new Vector3[frameCount];
            }

            // 힌지축 벡터
            Vector3 axisVec = hingeAxis switch
            {
                HingeAxis.X => Vector3.right,
                HingeAxis.Y => Vector3.up,
                HingeAxis.Z => Vector3.forward,
                _ => Vector3.forward
            };

            // 수정 대상 본 세트 (arm triplet에 포함된 본들)
            var modifiedBones = new HashSet<Transform>();

            // 샘플링 + 힌지 제약 적용
            AnimationMode.StartAnimationMode();
            try
            {
                for (int i = 0; i < frameCount; i++)
                {
                    float t = Mathf.Min((float)i / sampleRate, sourceClip.length);
                    AnimationMode.SampleAnimationClip(go, sourceClip, t);

                    // 모든 본의 원본 로컬 트랜스폼 기록
                    foreach (var bone in allBones)
                    {
                        rotations[bone][i] = bone.localRotation;
                        positions[bone][i] = bone.localPosition;
                    }

                    // 각 팔에 힌지 제약 적용
                    foreach (var (upperBone, lowerBone, handBone) in armTriplets)
                    {
                        var upper = animator.GetBoneTransform(upperBone);
                        var lower = animator.GetBoneTransform(lowerBone);
                        var hand  = animator.GetBoneTransform(handBone);
                        if (upper == null || lower == null || hand == null) continue;

                        // 첫 프레임에서 수정 대상 등록
                        if (i == 0)
                        {
                            modifiedBones.Add(upper);
                            modifiedBones.Add(lower);
                            modifiedBones.Add(hand);
                        }

                        // --- 0. 원본 월드 트랜스폼 기록 ---
                        Vector3 origHandPos = hand.position;
                        Quaternion origHandRot = hand.rotation;
                        Vector3 shoulderPos = upper.position;
                        Vector3 elbowPos = lower.position;

                        // --- 1. 최적 힌지 각도 해석적 풀이 ---
                        // Forearm이 힌지축으로만 회전하면 Hand는 팔꿈치 중심 원 위를 이동.
                        // 이 원 위에서 원본 Hand 위치에 가장 가까운 점의 각도를 구함.

                        // θ=0, θ=90 에서 Hand 위치를 샘플링하여 원의 기하 정의
                        lower.localRotation = Quaternion.identity;
                        Vector3 h0 = hand.position - elbowPos;

                        lower.localRotation = Quaternion.AngleAxis(90f, axisVec);
                        Vector3 h90 = hand.position - elbowPos;

                        // 월드 힌지축
                        Quaternion parentRot = lower.parent != null ? lower.parent.rotation : Quaternion.identity;
                        Vector3 worldAxis = (parentRot * axisVec).normalized;

                        // 원의 중심 (elbow 기준 오프셋)
                        Vector3 centerOffset = Vector3.Dot(h0, worldAxis) * worldAxis;

                        // 원 위의 기준 방향
                        Vector3 r0 = h0 - centerOffset;
                        Vector3 r90 = h90 - centerOffset;

                        // 타겟을 원 평면에 투영
                        Vector3 targetOffset = origHandPos - elbowPos - centerOffset;
                        Vector3 targetInPlane = targetOffset - Vector3.Dot(targetOffset, worldAxis) * worldAxis;

                        float theta = 0f;
                        if (targetInPlane.sqrMagnitude > 1e-10f && r0.sqrMagnitude > 1e-10f)
                        {
                            theta = Mathf.Atan2(
                                Vector3.Dot(targetInPlane.normalized, r90.normalized),
                                Vector3.Dot(targetInPlane.normalized, r0.normalized)
                            ) * Mathf.Rad2Deg;
                        }

                        // --- 2. 최적 힌지 각도 적용 ---
                        lower.localRotation = Quaternion.AngleAxis(theta, axisVec);

                        // --- 3. UpperArm 최소 보정 ---
                        // 최적 힌지라도 원본 Hand가 원 밖이면 오차 존재.
                        // FromToRotation으로 어깨→손 방향을 일치시켜 잔여 오차 보정.
                        Vector3 currentHandPos = hand.position;
                        Vector3 curDir = (currentHandPos - shoulderPos);
                        Vector3 tgtDir = (origHandPos - shoulderPos);

                        if (curDir.sqrMagnitude > 1e-8f && tgtDir.sqrMagnitude > 1e-8f)
                        {
                            Quaternion correction = Quaternion.FromToRotation(curDir.normalized, tgtDir.normalized);
                            upper.rotation = correction * upper.rotation;
                        }

                        // --- 4. Hand 월드 회전 복원 ---
                        hand.rotation = origHandRot;

                        // 수정된 로컬 회전 저장
                        rotations[upper][i] = upper.localRotation;
                        rotations[lower][i] = lower.localRotation;
                        rotations[hand][i] = hand.localRotation;
                    }

                    // 진행률
                    if (i % 100 == 0)
                        EditorUtility.DisplayProgressBar("Forearm Hinge Baker",
                            $"샘플링 {i}/{frameCount}", (float)i / frameCount);
                }
            }
            finally
            {
                AnimationMode.StopAnimationMode();
                EditorUtility.ClearProgressBar();
            }

            // 새 클립 생성
            var newClip = new AnimationClip();
            newClip.frameRate = sampleRate;

            foreach (var bone in allBones)
            {
                string path = bonePaths[bone];
                var rots = rotations[bone];
                var poss = positions[bone];

                // 회전 커브 (Quaternion)
                var cx = new AnimationCurve();
                var cy = new AnimationCurve();
                var cz = new AnimationCurve();
                var cw = new AnimationCurve();

                for (int j = 0; j < frameCount; j++)
                {
                    float time = (float)j / sampleRate;
                    cx.AddKey(time, rots[j].x);
                    cy.AddKey(time, rots[j].y);
                    cz.AddKey(time, rots[j].z);
                    cw.AddKey(time, rots[j].w);
                }

                newClip.SetCurve(path, typeof(Transform), "localRotation.x", cx);
                newClip.SetCurve(path, typeof(Transform), "localRotation.y", cy);
                newClip.SetCurve(path, typeof(Transform), "localRotation.z", cz);
                newClip.SetCurve(path, typeof(Transform), "localRotation.w", cw);

                // 위치 커브 (변화가 있는 본만)
                bool posAnimated = false;
                for (int j = 1; j < frameCount; j++)
                {
                    if ((poss[j] - poss[0]).sqrMagnitude > 1e-6f)
                    {
                        posAnimated = true;
                        break;
                    }
                }

                if (posAnimated)
                {
                    var px = new AnimationCurve();
                    var py = new AnimationCurve();
                    var pz = new AnimationCurve();

                    for (int j = 0; j < frameCount; j++)
                    {
                        float time = (float)j / sampleRate;
                        px.AddKey(time, poss[j].x);
                        py.AddKey(time, poss[j].y);
                        pz.AddKey(time, poss[j].z);
                    }

                    newClip.SetCurve(path, typeof(Transform), "localPosition.x", px);
                    newClip.SetCurve(path, typeof(Transform), "localPosition.y", py);
                    newClip.SetCurve(path, typeof(Transform), "localPosition.z", pz);
                }
            }

            newClip.EnsureQuaternionContinuity();

            // 저장
            string srcPath = AssetDatabase.GetAssetPath(sourceClip);
            string newPath;
            if (!string.IsNullOrEmpty(srcPath))
            {
                string dir = System.IO.Path.GetDirectoryName(srcPath);
                string name = System.IO.Path.GetFileNameWithoutExtension(srcPath);
                newPath = $"{dir}/{name}_hinged.anim";
            }
            else
            {
                newPath = "Assets/hinged_clip.anim";
            }

            newPath = AssetDatabase.GenerateUniqueAssetPath(newPath);
            AssetDatabase.CreateAsset(newClip, newPath);
            AssetDatabase.SaveAssets();

            Debug.Log($"[ForearmHingeBaker] 저장 완료: {newPath} ({frameCount}프레임, {allBones.Count}본)");
            EditorUtility.DisplayDialog("완료",
                $"저장: {newPath}\n프레임: {frameCount}\n본: {allBones.Count}", "OK");

            Selection.activeObject = newClip;
            EditorGUIUtility.PingObject(newClip);
        }

        // ============================================================
        // Swing-Twist 분해
        // q = swing * twist
        // twist = 지정 축 주위 회전 (힌지 성분, forearm에 유지)
        // swing = 나머지 회전 (제거 대상)
        // ============================================================
        static void SwingTwist(Quaternion q, Vector3 twistAxis,
            out Quaternion swing, out Quaternion twist)
        {
            Vector3 r = new Vector3(q.x, q.y, q.z);
            Vector3 proj = Vector3.Dot(r, twistAxis) * twistAxis;

            twist = new Quaternion(proj.x, proj.y, proj.z, q.w);
            float mag = Mathf.Sqrt(twist.x * twist.x + twist.y * twist.y
                                  + twist.z * twist.z + twist.w * twist.w);

            if (mag < 1e-6f)
            {
                twist = Quaternion.identity;
                swing = q;
            }
            else
            {
                twist.x /= mag;
                twist.y /= mag;
                twist.z /= mag;
                twist.w /= mag;
                swing = q * Quaternion.Inverse(twist);
            }
        }

        // ============================================================
        // 본 계층 수집
        // ============================================================
        void CollectHierarchy(Transform t, string path,
            List<Transform> bones, Dictionary<Transform, string> paths)
        {
            bones.Add(t);
            paths[t] = path;

            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                CollectHierarchy(child, path + "/" + child.name, bones, paths);
            }
        }
    }
}
