// ForearmHingeBaker.cs
// Humanoid 애니메이션 클립에서 Forearm 힌지 보정 후 Generic 클립을 생성합니다.
//
// 베이크 모드 두 가지:
//   Edit Mode 베이크  - 기존 방식. AnimationMode.SampleAnimationClip 사용.
//                      클립에 저장된 foot IK goal은 반영되나,
//                      런타임 foot stabilization은 반영 안 됨.
//
//   Play Mode 베이크  - 실제 Play Mode에서 Animator를 구동하여 샘플링.
//                      animator.Update(dt)로 Humanoid 전체 파이프라인(foot IK,
//                      stabilization 포함)이 적용된 상태로 기록.
//                      팔뚝 힌지 보정은 ForearmHingeRecorder가 실시간으로 적용.

using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using System.Collections.Generic;
using System.IO;

namespace YAMO.UnityTools.Editor
{
    // ================================================================
    // [InitializeOnLoad] 브릿지: 도메인 리로드 후에도 콜백 유지
    // ================================================================
    [InitializeOnLoad]
    static class ForearmHingeBakerBridge
    {
        const string K_PENDING  = "YAMO.ForearmHinge.PendingBake";
        const string K_NEWPATH  = "YAMO.ForearmHinge.NewClipPath";
        const string K_RATE     = "YAMO.ForearmHinge.SampleRate";
        const string K_GOID     = "YAMO.ForearmHinge.AnimGOInstanceID";
        const string K_TMPCTRL  = "YAMO.ForearmHinge.TempCtrlPath";
        const string K_ORIGCTRL = "YAMO.ForearmHinge.OrigCtrlPath";

        static ForearmHingeBakerBridge()
        {
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        static void OnPlayModeChanged(PlayModeStateChange state)
        {
            if (state != PlayModeStateChange.EnteredEditMode) return;
            if (!SessionState.GetBool(K_PENDING, false)) return;

            string resultsPath = YAMO.UnityTools.ForearmHingeRecorder.ResultsFilePath;
            string newClipPath = SessionState.GetString(K_NEWPATH, "");
            int    sampleRate  = SessionState.GetInt(K_RATE, 30);
            string tmpCtrlPath = SessionState.GetString(K_TMPCTRL, "");
            string origCtrlPath= SessionState.GetString(K_ORIGCTRL, "");
            int    goInstanceID= SessionState.GetInt(K_GOID, 0);

            // SessionState 초기화
            SessionState.EraseBool(K_PENDING);
            SessionState.EraseString(K_NEWPATH);
            SessionState.EraseInt(K_RATE);
            SessionState.EraseInt(K_GOID);
            SessionState.EraseString(K_TMPCTRL);
            SessionState.EraseString(K_ORIGCTRL);

            // Recorder 컴포넌트 제거 + 원본 컨트롤러 복원
            var go = EditorUtility.InstanceIDToObject(goInstanceID) as GameObject;
            if (go != null)
            {
                var recorder = go.GetComponent<YAMO.UnityTools.ForearmHingeRecorder>();
                if (recorder != null) Object.DestroyImmediate(recorder);

                var anim = go.GetComponent<Animator>();
                if (anim != null && !string.IsNullOrEmpty(origCtrlPath))
                {
                    var origCtrl = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(origCtrlPath);
                    anim.runtimeAnimatorController = origCtrl;
                }
            }

            // 임시 컨트롤러 삭제
            if (!string.IsNullOrEmpty(tmpCtrlPath) && !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(tmpCtrlPath)))
                AssetDatabase.DeleteAsset(tmpCtrlPath);

            // 결과 읽기 & 베이크
            if (!File.Exists(resultsPath))
            {
                Debug.LogWarning("[ForearmHingeBaker] 녹화 결과 파일 없음 - Play Mode 베이크 취소");
                return;
            }

            ForearmHingeBaker.BakeFromResultFile(resultsPath, newClipPath, sampleRate);

            try { File.Delete(resultsPath); } catch { /* 이미 삭제됐으면 무시 */ }
        }
    }

    // ================================================================
    // EditorWindow
    // ================================================================
    public class ForearmHingeBaker : EditorWindow
    {
        private static ForearmHingeBaker _instance;

        Animator      animator;
        AnimationClip sourceClip;
        int           sampleRate = 30;
        bool          enableHingeCorrection = true;
        float         handRotationCompensation = 1f;

        enum HingeAxis { X, Y, Z }
        HingeAxis hingeAxis = HingeAxis.Z;

        const string TempCtrlPath = "Assets/__YAMO_ForearmHingeTempCtrl__.controller";

        [MenuItem("Tools/YAMO/Animation/Forearm Hinge Baker")]
        static void Open()
        {
            if (_instance != null) { _instance.Close(); return; }
            var win = GetWindow<ForearmHingeBaker>("Forearm Hinge Baker");
            win.minSize = new Vector2(350, 310);
        }

        private void OnEnable()  => _instance = this;
        private void OnDisable() => _instance = null;

        void OnGUI()
        {
            EditorGUILayout.Space(4);
            animator   = EditorGUILayout.ObjectField("Animator (씬)", animator,   typeof(Animator),      true)  as Animator;
            sourceClip = EditorGUILayout.ObjectField("소스 클립",     sourceClip, typeof(AnimationClip), false) as AnimationClip;

            EditorGUILayout.Space(4);
            sampleRate = EditorGUILayout.IntSlider("샘플레이트 (fps)", sampleRate, 1, 120);
            enableHingeCorrection = GUILayout.Toggle(
                enableHingeCorrection,
                enableHingeCorrection ? "Forearm Hinge 보정: 활성화" : "Forearm Hinge 보정: 비활성화",
                GUI.skin.button,
                GUILayout.Height(26f));
            using (new EditorGUI.DisabledScope(!enableHingeCorrection))
            {
                hingeAxis = (HingeAxis)EditorGUILayout.EnumPopup("Forearm 힌지축 (로컬)", hingeAxis);
                handRotationCompensation = EditorGUILayout.Slider(
                    "손목 과회전 제거량",
                    handRotationCompensation,
                    0f,
                    1f);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "Edit Mode 베이크: AnimationMode 샘플링 (빠름, foot IK goal 반영)\n" +
                "Play Mode 베이크: 실제 런타임 실행 (느림, foot stabilization까지 반영)\n\n" +
                "Play Mode 베이크는 임시 AnimatorController를 생성하고\n" +
                "캐릭터에 ForearmHingeRecorder를 붙여 Play Mode를 자동 진행합니다.\n" +
                "완료 후 자동으로 Edit Mode로 복귀하며 클립이 저장됩니다.",
                MessageType.Info);

            EditorGUILayout.Space(4);
            bool ready = animator != null && sourceClip != null;
            GUI.enabled = ready;

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Edit Mode 베이크",  GUILayout.Height(30))) BakeEditMode();
            if (GUILayout.Button("Play Mode 베이크",  GUILayout.Height(30))) BakePlayMode();
            EditorGUILayout.EndHorizontal();

            GUI.enabled = true;
        }

        // ============================================================
        // Edit Mode 베이크 (기존 방식)
        // ============================================================
        void BakeEditMode()
        {
            try
            {
                var result = ForearmHingeBakeService.BakeEditMode(
                    animator,
                    sourceClip,
                    new ForearmHingeBakeSettings
                    {
                        SampleRate = sampleRate,
                        EnableHingeCorrection = enableHingeCorrection,
                        HingeAxis = (ForearmHingeAxis)(int)hingeAxis,
                        HandRotationCompensation = handRotationCompensation
                    },
                    (message, progress) =>
                    {
                        EditorUtility.DisplayProgressBar("Forearm Hinge Baker (Edit Mode)", message, progress);
                        return false;
                    });

                SaveClip(result.Clip, sourceClip, result.FrameCount, result.BoneCount, "");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        void LegacyBakeEditMode()
        {
            var go = animator.gameObject;

            var armTriplets = new (HumanBodyBones upper, HumanBodyBones lower, HumanBodyBones hand)[]
            {
                (HumanBodyBones.LeftUpperArm,  HumanBodyBones.LeftLowerArm,  HumanBodyBones.LeftHand),
                (HumanBodyBones.RightUpperArm, HumanBodyBones.RightLowerArm, HumanBodyBones.RightHand),
            };

            var allBones  = new List<Transform>();
            var bonePaths = new Dictionary<Transform, string>();
            CollectHumanoidBones(animator, allBones, bonePaths);

            if (allBones.Count == 0)
            {
                EditorUtility.DisplayDialog("오류", "Humanoid 매핑 본을 찾을 수 없습니다.\nAvatar가 올바르게 설정되어 있는지 확인하세요.", "OK");
                return;
            }

            int frameCount = Mathf.CeilToInt(sourceClip.length * sampleRate) + 1;

            var rotations = new Dictionary<Transform, Quaternion[]>();
            var positions = new Dictionary<Transform, Vector3[]>();
            foreach (var bone in allBones)
            {
                rotations[bone] = new Quaternion[frameCount];
                positions[bone] = new Vector3[frameCount];
            }

            Vector3 axisVec = HingeAxisVector(hingeAxis);

            AnimationMode.StartAnimationMode();
            try
            {
                for (int i = 0; i < frameCount; i++)
                {
                    float t = Mathf.Min((float)i / sampleRate, sourceClip.length);
                    AnimationMode.SampleAnimationClip(go, sourceClip, t);

                    foreach (var bone in allBones)
                    {
                        rotations[bone][i] = bone.localRotation;
                        positions[bone][i] = bone.localPosition;
                    }

                    foreach (var (upperBone, lowerBone, handBone) in armTriplets)
                    {
                        var upper = animator.GetBoneTransform(upperBone);
                        var lower = animator.GetBoneTransform(lowerBone);
                        var hand  = animator.GetBoneTransform(handBone);
                        if (upper == null || lower == null || hand == null) continue;

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

                        rotations[upper][i] = upper.localRotation;
                        rotations[lower][i] = lower.localRotation;
                        rotations[hand][i]  = hand.localRotation;
                    }

                    if (i % 100 == 0)
                        EditorUtility.DisplayProgressBar("Forearm Hinge Baker (Edit Mode)",
                            $"샘플링 {i}/{frameCount}", (float)i / frameCount);
                }
            }
            finally
            {
                AnimationMode.StopAnimationMode();
                EditorUtility.ClearProgressBar();
            }

            var newClip = BuildClip(allBones, bonePaths, rotations, positions, frameCount, sampleRate);
            SaveClip(newClip, sourceClip, frameCount, allBones.Count, "");
        }

        // ============================================================
        // Play Mode 베이크 진입
        // ============================================================
        void BakePlayMode()
        {
            // 1. 임시 AnimatorController 생성 (소스 클립만 재생)
            if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(TempCtrlPath)))
                AssetDatabase.DeleteAsset(TempCtrlPath);

            var tmpCtrl = AnimatorController.CreateAnimatorControllerAtPath(TempCtrlPath);
            var state   = tmpCtrl.layers[0].stateMachine.AddState("Record");
            state.motion    = sourceClip;
            state.iKOnFeet  = true;   // Foot IK (foot stabilization) 활성화
            tmpCtrl.layers[0].stateMachine.defaultState = state;
            AssetDatabase.SaveAssets();

            // 2. 원본 컨트롤러 경로 저장 후 교체
            string origCtrlPath = AssetDatabase.GetAssetPath(animator.runtimeAnimatorController);
            animator.runtimeAnimatorController = tmpCtrl;

            // 3. Recorder 컴포넌트 추가
            var recorder             = animator.gameObject.AddComponent<YAMO.UnityTools.ForearmHingeRecorder>();
            recorder.sampleRate      = sampleRate;
            recorder.enableHingeCorrection = enableHingeCorrection;
            recorder.hingeAxisIndex  = (int)hingeAxis;
            recorder.handRotationCompensation = handRotationCompensation;

            // 4. 복귀 후 처리에 필요한 정보를 SessionState에 저장
            SessionState.SetBool  ("YAMO.ForearmHinge.PendingBake",     true);
            SessionState.SetString("YAMO.ForearmHinge.NewClipPath",     DetermineNewClipPath(sourceClip));
            SessionState.SetInt   ("YAMO.ForearmHinge.SampleRate",      sampleRate);
            SessionState.SetInt   ("YAMO.ForearmHinge.AnimGOInstanceID",animator.gameObject.GetInstanceID());
            SessionState.SetString("YAMO.ForearmHinge.TempCtrlPath",   TempCtrlPath);
            SessionState.SetString("YAMO.ForearmHinge.OrigCtrlPath",   origCtrlPath);

            Debug.Log("[ForearmHingeBaker] Play Mode 진입 → ForearmHingeRecorder 시작");

            // 5. Play Mode 진입
            EditorApplication.isPlaying = true;
        }

        // ============================================================
        // Play Mode 결과 파일로부터 베이크 (브릿지에서 호출)
        // ============================================================
        public static void BakeFromResultFile(string resultsPath, string newClipPath, int sampleRate)
        {
            EditorUtility.DisplayProgressBar("Forearm Hinge Baker (Play Mode)", "결과 읽는 중...", 0f);
            try
            {
                using var reader     = new BinaryReader(File.Open(resultsPath, FileMode.Open));
                int frameCount = reader.ReadInt32();
                int boneCount  = reader.ReadInt32();

                var paths = new string[boneCount];
                var rots  = new Quaternion[boneCount][];
                var poss  = new Vector3[boneCount][];

                for (int b = 0; b < boneCount; b++)
                {
                    paths[b] = reader.ReadString();
                    rots[b]  = new Quaternion[frameCount];
                    poss[b]  = new Vector3[frameCount];
                    for (int f = 0; f < frameCount; f++)
                    {
                        rots[b][f] = new Quaternion(
                            reader.ReadSingle(), reader.ReadSingle(),
                            reader.ReadSingle(), reader.ReadSingle());
                        poss[b][f] = new Vector3(
                            reader.ReadSingle(), reader.ReadSingle(), reader.ReadSingle());
                    }

                    if (b % 50 == 0)
                        EditorUtility.DisplayProgressBar("Forearm Hinge Baker (Play Mode)",
                            $"결과 읽는 중... {b}/{boneCount}", (float)b / boneCount * 0.5f);
                }

                // 커브 빌드
                var newClip = new AnimationClip { frameRate = sampleRate };

                for (int b = 0; b < boneCount; b++)
                {
                    string path = paths[b];

                    var cx = new AnimationCurve(); var cy = new AnimationCurve();
                    var cz = new AnimationCurve(); var cw = new AnimationCurve();

                    for (int f = 0; f < frameCount; f++)
                    {
                        float time = (float)f / sampleRate;
                        cx.AddKey(time, rots[b][f].x); cy.AddKey(time, rots[b][f].y);
                        cz.AddKey(time, rots[b][f].z); cw.AddKey(time, rots[b][f].w);
                    }

                    newClip.SetCurve(path, typeof(Transform), "localRotation.x", cx);
                    newClip.SetCurve(path, typeof(Transform), "localRotation.y", cy);
                    newClip.SetCurve(path, typeof(Transform), "localRotation.z", cz);
                    newClip.SetCurve(path, typeof(Transform), "localRotation.w", cw);

                    bool posAnimated = false;
                    for (int f = 1; f < frameCount; f++)
                        if ((poss[b][f] - poss[b][0]).sqrMagnitude > 1e-6f) { posAnimated = true; break; }

                    if (posAnimated)
                    {
                        var px = new AnimationCurve(); var py = new AnimationCurve(); var pz = new AnimationCurve();
                        for (int f = 0; f < frameCount; f++)
                        {
                            float time = (float)f / sampleRate;
                            px.AddKey(time, poss[b][f].x);
                            py.AddKey(time, poss[b][f].y);
                            pz.AddKey(time, poss[b][f].z);
                        }
                        newClip.SetCurve(path, typeof(Transform), "localPosition.x", px);
                        newClip.SetCurve(path, typeof(Transform), "localPosition.y", py);
                        newClip.SetCurve(path, typeof(Transform), "localPosition.z", pz);
                    }

                    if (b % 50 == 0)
                        EditorUtility.DisplayProgressBar("Forearm Hinge Baker (Play Mode)",
                            $"커브 빌드 중... {b}/{boneCount}", 0.5f + (float)b / boneCount * 0.5f);
                }

                newClip.EnsureQuaternionContinuity();

                newClipPath = AssetDatabase.GenerateUniqueAssetPath(newClipPath);
                AssetDatabase.CreateAsset(newClip, newClipPath);
                AssetDatabase.SaveAssets();

                Debug.Log($"[ForearmHingeBaker] Play Mode 베이크 완료: {newClipPath} ({frameCount}프레임, {boneCount}본)");
                EditorUtility.DisplayDialog("완료 (Play Mode 베이크)",
                    $"저장: {newClipPath}\n프레임: {frameCount}\n본: {boneCount}", "OK");

                Selection.activeObject = newClip;
                EditorGUIUtility.PingObject(newClip);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
        }

        // ============================================================
        // 공유 유틸리티
        // ============================================================
        static Vector3 HingeAxisVector(HingeAxis a) => a switch
        {
            HingeAxis.X => Vector3.right,
            HingeAxis.Y => Vector3.up,
            _            => Vector3.forward,
        };

        static string DetermineNewClipPath(AnimationClip clip)
        {
            string srcPath = AssetDatabase.GetAssetPath(clip);
            if (!string.IsNullOrEmpty(srcPath))
            {
                string dir  = System.IO.Path.GetDirectoryName(srcPath);
                string name = System.IO.Path.GetFileNameWithoutExtension(srcPath);
                return $"{dir}/{name}_hinged.anim";
            }
            return "Assets/hinged_clip.anim";
        }

        static AnimationClip BuildClip(
            List<Transform> allBones,
            Dictionary<Transform, string> bonePaths,
            Dictionary<Transform, Quaternion[]> rotations,
            Dictionary<Transform, Vector3[]> positions,
            int frameCount, int sampleRate)
        {
            var clip = new AnimationClip { frameRate = sampleRate };

            foreach (var bone in allBones)
            {
                string path = bonePaths[bone];
                var rots = rotations[bone];
                var poss = positions[bone];

                var cx = new AnimationCurve(); var cy = new AnimationCurve();
                var cz = new AnimationCurve(); var cw = new AnimationCurve();

                for (int j = 0; j < frameCount; j++)
                {
                    float time = (float)j / sampleRate;
                    cx.AddKey(time, rots[j].x); cy.AddKey(time, rots[j].y);
                    cz.AddKey(time, rots[j].z); cw.AddKey(time, rots[j].w);
                }

                clip.SetCurve(path, typeof(Transform), "localRotation.x", cx);
                clip.SetCurve(path, typeof(Transform), "localRotation.y", cy);
                clip.SetCurve(path, typeof(Transform), "localRotation.z", cz);
                clip.SetCurve(path, typeof(Transform), "localRotation.w", cw);

                bool posAnimated = false;
                for (int j = 1; j < frameCount; j++)
                    if ((poss[j] - poss[0]).sqrMagnitude > 1e-6f) { posAnimated = true; break; }

                if (posAnimated)
                {
                    var px = new AnimationCurve(); var py = new AnimationCurve(); var pz = new AnimationCurve();
                    for (int j = 0; j < frameCount; j++)
                    {
                        float time = (float)j / sampleRate;
                        px.AddKey(time, poss[j].x);
                        py.AddKey(time, poss[j].y);
                        pz.AddKey(time, poss[j].z);
                    }
                    clip.SetCurve(path, typeof(Transform), "localPosition.x", px);
                    clip.SetCurve(path, typeof(Transform), "localPosition.y", py);
                    clip.SetCurve(path, typeof(Transform), "localPosition.z", pz);
                }
            }

            clip.EnsureQuaternionContinuity();
            return clip;
        }

        static void SaveClip(AnimationClip clip, AnimationClip sourceClip,
            int frameCount, int boneCount, string extraNote)
        {
            string newPath = DetermineNewClipPath(sourceClip);
            newPath = AssetDatabase.GenerateUniqueAssetPath(newPath);
            AssetDatabase.CreateAsset(clip, newPath);
            AssetDatabase.SaveAssets();

            Debug.Log($"[ForearmHingeBaker] 저장 완료: {newPath} ({frameCount}프레임, {boneCount}본{extraNote})");
            EditorUtility.DisplayDialog("완료",
                $"저장: {newPath}\n프레임: {frameCount}\n본: {boneCount}", "OK");

            Selection.activeObject = clip;
            EditorGUIUtility.PingObject(clip);
        }

        // Avatar에 매핑된 Humanoid 본만 수집 (물리·악세서리 등 잡다한 본 제외)
        static void CollectHumanoidBones(Animator anim,
            List<Transform> bones, Dictionary<Transform, string> paths)
        {
            var root = anim.transform;
            for (int i = 0; i < (int)HumanBodyBones.LastBone; i++)
            {
                var t = anim.GetBoneTransform((HumanBodyBones)i);
                if (t == null || bones.Contains(t)) continue;

                string path = AnimationUtility.CalculateTransformPath(t, root);
                bones.Add(t);
                paths[t] = path;
            }
        }
    }
}
