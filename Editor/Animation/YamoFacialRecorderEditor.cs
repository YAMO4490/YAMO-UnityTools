using System;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    [CustomEditor(typeof(YamoFacialRecorder))]
    public sealed class YamoFacialRecorderEditor : UnityEditor.Editor
    {
        [MenuItem("Tools/YAMO/Animation/Create Live Facial Recorder")]
        public static void CreateRecorder()
        {
            var template = AssetDatabase.LoadAssetAtPath<GameObject>("Packages/com.yamo.unitytools/Runtime/Animation/YamoFacialRecorder.prefab");
            var go = template != null ? (GameObject)PrefabUtility.InstantiatePrefab(template) : new GameObject("YAMO Facial Recorder");
            Undo.RegisterCreatedObjectUndo(go, "Create YAMO Facial Recorder");
            var recorder = go.GetComponent<YamoFacialRecorder>() ?? go.AddComponent<YamoFacialRecorder>();
            foreach (var selected in Selection.gameObjects)
            {
                var animator = selected.GetComponent<Animator>() ?? selected.GetComponentInChildren<Animator>(true);
                var root = animator != null ? animator.transform : selected.transform;
                var slot = new YamoFacialTarget { Label = root.name, Root = root };
                Populate(slot);
                if (slot.FaceMeshes.Length > 0 && !recorder.Targets.Any(t => t.Root == root)) recorder.Targets.Add(slot);
            }
            Selection.activeGameObject = go;
        }

        public static void Populate(YamoFacialTarget slot)
        {
            if (slot.Root == null) return;
            slot.FaceMeshes = slot.Root.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .Where(r => r.sharedMesh != null && r.sharedMesh.blendShapeCount > 0).ToArray();
            var animator = slot.Root.GetComponent<Animator>();
            if (animator == null || !animator.isHuman || animator.avatar == null || !animator.avatar.isValid) return;
            slot.Head = animator.GetBoneTransform(HumanBodyBones.Head);
            slot.LeftEye = animator.GetBoneTransform(HumanBodyBones.LeftEye);
            slot.RightEye = animator.GetBoneTransform(HumanBodyBones.RightEye);
        }

        public override void OnInspectorGUI()
        {
            var recorder = (YamoFacialRecorder)target;
            EditorGUILayout.HelpBox("캐릭터 루트 = 클립을 재생할 Animator 루트입니다. FaceOnly는 Avatar 없이 BlendShape만 녹화합니다.\n" +
                "슬롯 순서: Num 1~9, Num 0. Play 중에도 루트·메시를 수정하고 슬롯 버튼을 누르면 바로 적용됩니다.", MessageType.Info);
            DrawDefaultInspector();
            serializedObject.ApplyModifiedProperties();
            using (new EditorGUI.DisabledScope(recorder.IsRecording))
            {
                if (GUILayout.Button("각 루트에서 얼굴 메시 / Humanoid 머리·눈 본 찾기"))
                {
                    Undo.RecordObject(recorder, "Find Facial Targets");
                    foreach (var slot in recorder.Targets.Where(t => t != null)) Populate(slot);
                    EditorUtility.SetDirty(recorder);
                    if (Application.isPlaying && recorder.ActiveTargetIndex >= 0) recorder.SelectTarget(recorder.ActiveTargetIndex);
                }
            }
            for (var i = 0; i < recorder.Targets.Count; i++)
            {
                var valid = YamoFacialRecorder.ValidateTarget(recorder.Targets[i], out var error);
                if (!valid) EditorGUILayout.HelpBox($"슬롯 {i + 1}: {error}", MessageType.Warning);
            }
            EditorGUILayout.HelpBox("Play 진입 시 자동으로 수신하고 종료 시 연결을 해제합니다. 기존 FacialRecorder 없이 이 도구만 사용하세요.", MessageType.Info);
            using (new EditorGUI.DisabledScope(!Application.isPlaying))
            {
                EditorGUILayout.Space();
                for (var i = 0; i < recorder.Targets.Count; i++)
                    if (GUILayout.Button((i == recorder.ActiveTargetIndex ? "▶ " : "") +
                        (i < 10 ? "Num " + (i == 9 ? 0 : i + 1) + " · " : "") + recorder.TargetLabel(i))) recorder.SelectTarget(i);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button(recorder.IsRecording ? "녹화 종료 / 저장" : "녹화 시작"))
                    { if (recorder.IsRecording) recorder.StopRecording(); else recorder.StartRecording(); }
                }
                if (recorder.HasUnsavedTake && GUILayout.Button("저장 재시도")) recorder.SaveTake();
            }
            EditorGUILayout.HelpBox(recorder.Status, recorder.IsRecording ? MessageType.Warning : MessageType.None);
            if (Application.isPlaying)
            {
                EditorGUILayout.LabelField("수신 패킷 / 해석", recorder.ReceivedPackets + " / " + recorder.ParsedPackets);
                if (!string.IsNullOrEmpty(recorder.LastSender)) EditorGUILayout.LabelField("송신 기기", recorder.LastSender);
                if (!string.IsNullOrEmpty(recorder.LastPacketPreview)) EditorGUILayout.TextArea(recorder.LastPacketPreview);
            }
            if (!string.IsNullOrEmpty(recorder.LastSavedPath) && GUILayout.Button("마지막 저장 클립 선택"))
                Selection.activeObject = AssetDatabase.LoadAssetAtPath<AnimationClip>(recorder.LastSavedPath);
            if (Application.isPlaying) Repaint();
        }
    }
}
