using System;
using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace YAMO.Editor
{
    public sealed class FacialCsvToAnimationWindow : EditorWindow
    {
        [SerializeField] private List<string> inputs = new List<string>();
        [SerializeField] private string outputFolder = "";
        [SerializeField] private List<string> results = new List<string>();
        private Vector2 inputScroll, resultScroll;

        public static void ShowWindow()
        {
            var window = GetWindow<FacialCsvToAnimationWindow>(true, "페이셜 CSV → Anim", true);
            window.minSize = new Vector2(560, 460);
            window.Show();
        }

        private void OnGUI()
        {
            EditorGUILayout.LabelField("페이셜 CSV 일괄 변환", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("CSV 파일을 여러 개 드래그해 추가하고 출력 폴더를 선택하세요. 각 CSV 이름으로 Anim을 생성하며, 같은 이름의 파일은 덮어쓰지 않습니다.", MessageType.Info);
            var drop = GUILayoutUtility.GetRect(0, 60, GUILayout.ExpandWidth(true));
            GUI.Box(drop, "CSV 여러 개를 여기에 드래그앤드롭", EditorStyles.helpBox);
            HandleCsvDrop(drop);
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("CSV 파일 추가…"))
                {
                    string file = EditorUtility.OpenFilePanel("페이셜 CSV 추가", Application.dataPath, "csv");
                    AddFiles(new[] { file });
                }
                GUILayout.FlexibleSpace();
                EditorGUILayout.LabelField($"대기 {inputs.Count}개", GUILayout.Width(90));
                using (new EditorGUI.DisabledScope(inputs.Count == 0))
                    if (GUILayout.Button("목록 비우기", GUILayout.Width(100))) inputs.Clear();
            }
            inputScroll = EditorGUILayout.BeginScrollView(inputScroll, GUILayout.MinHeight(100), GUILayout.MaxHeight(230));
            for (int i = 0; i < inputs.Count; i++)
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    EditorGUILayout.LabelField(new GUIContent(Path.GetFileName(inputs[i]), inputs[i]));
                    if (GUILayout.Button("×", GUILayout.Width(26))) { inputs.RemoveAt(i); i--; }
                }
            }
            EditorGUILayout.EndScrollView();
            EditorGUILayout.Space();
            var folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(outputFolder);
            EditorGUI.BeginChangeCheck();
            var selected = (DefaultAsset)EditorGUILayout.ObjectField("출력 폴더", folder, typeof(DefaultAsset), false);
            if (EditorGUI.EndChangeCheck())
            {
                var path = selected ? AssetDatabase.GetAssetPath(selected) : "";
                if (selected == null || IsOutputFolder(path)) outputFolder = path;
                else ShowNotification(new GUIContent("Assets 아래의 폴더를 넣어주세요."));
            }
            var folderDrop = GUILayoutUtility.GetRect(0, 38, GUILayout.ExpandWidth(true));
            GUI.Box(folderDrop, string.IsNullOrEmpty(outputFolder) ? "저장할 프로젝트 폴더를 여기에 드래그앤드롭" : outputFolder, EditorStyles.helpBox);
            HandleFolderDrop(folderDrop);
            if (!IsOutputFolder(outputFolder))
                EditorGUILayout.HelpBox("Project 창에서 Assets 아래의 저장 폴더를 드래그해주세요.", MessageType.Warning);
            using (new EditorGUI.DisabledScope(inputs.Count == 0 || !IsOutputFolder(outputFolder)))
                if (GUILayout.Button($"{inputs.Count}개 CSV 변환", GUILayout.Height(32))) ConvertAll();
            if (results.Count > 0)
            {
                EditorGUILayout.Space();
                EditorGUILayout.LabelField("변환 결과", EditorStyles.boldLabel);
                resultScroll = EditorGUILayout.BeginScrollView(resultScroll, GUILayout.MinHeight(75));
                foreach (var result in results) EditorGUILayout.LabelField(result, EditorStyles.wordWrappedLabel);
                EditorGUILayout.EndScrollView();
            }
        }

        public void AddFiles(IEnumerable<string> paths)
        {
            foreach (var path in paths)
            {
                if (string.IsNullOrWhiteSpace(path) || !File.Exists(path) ||
                    !string.Equals(Path.GetExtension(path), ".csv", StringComparison.OrdinalIgnoreCase)) continue;
                var full = Path.GetFullPath(path);
                if (!inputs.Exists(p => string.Equals(p, full, StringComparison.OrdinalIgnoreCase))) inputs.Add(full);
            }
            Repaint();
        }

        private static bool IsOutputFolder(string path) => !string.IsNullOrEmpty(path) &&
            (path == "Assets" || path.StartsWith("Assets/", StringComparison.Ordinal)) && AssetDatabase.IsValidFolder(path);

        private void HandleCsvDrop(Rect rect)
        {
            var e = Event.current;
            if (!rect.Contains(e.mousePosition) || (e.type != EventType.DragUpdated && e.type != EventType.DragPerform)) return;
            bool valid = Array.Exists(DragAndDrop.paths, p => File.Exists(p) && string.Equals(Path.GetExtension(p), ".csv", StringComparison.OrdinalIgnoreCase));
            DragAndDrop.visualMode = valid ? DragAndDropVisualMode.Copy : DragAndDropVisualMode.Rejected;
            if (valid && e.type == EventType.DragPerform) { DragAndDrop.AcceptDrag(); AddFiles(DragAndDrop.paths); }
            e.Use();
        }

        private void HandleFolderDrop(Rect rect)
        {
            var e = Event.current;
            if (!rect.Contains(e.mousePosition) || (e.type != EventType.DragUpdated && e.type != EventType.DragPerform)) return;
            // Normalize absolute Explorer drops too; output assets must stay inside Assets.
            string candidate = null;
            if (DragAndDrop.paths.Length == 1)
            {
                var full = Path.GetFullPath(DragAndDrop.paths[0]).Replace('\\', '/');
                var assets = Path.GetFullPath(Application.dataPath).Replace('\\', '/');
                if (full.Equals(assets, StringComparison.OrdinalIgnoreCase)) candidate = "Assets";
                else if (full.StartsWith(assets + "/", StringComparison.OrdinalIgnoreCase)) candidate = "Assets" + full.Substring(assets.Length);
            }
            bool valid = IsOutputFolder(candidate);
            DragAndDrop.visualMode = valid ? DragAndDropVisualMode.Link : DragAndDropVisualMode.Rejected;
            if (valid && e.type == EventType.DragPerform) { DragAndDrop.AcceptDrag(); outputFolder = candidate; Repaint(); }
            e.Use();
        }

        private void ConvertAll()
        {
            results.Clear();
            int succeeded = 0, failed = 0;
            UnityEngine.Object lastClip = null;
            try
            {
                for (int i = 0; i < inputs.Count; i++)
                {
                    if (EditorUtility.DisplayCancelableProgressBar("페이셜 CSV 변환", Path.GetFileName(inputs[i]), (float)i / inputs.Count))
                    { results.Add($"취소: 남은 {inputs.Count - i}개는 변환하지 않았습니다."); break; }
                    try
                    {
                        string output = FacialCsvToAnimation.ConvertAndSave(inputs[i], outputFolder);
                        lastClip = AssetDatabase.LoadAssetAtPath<AnimationClip>(output);
                        results.Add("완료: " + output);
                        succeeded++;
                    }
                    catch (Exception ex) { results.Add($"실패: {Path.GetFileName(inputs[i])} — {ex.Message}"); failed++; }
                }
            }
            finally { EditorUtility.ClearProgressBar(); }
            results.Insert(0, $"성공 {succeeded}개 / 실패 {failed}개");
            if (lastClip) { Selection.activeObject = lastClip; EditorGUIUtility.PingObject(lastClip); }
            Repaint();
        }
    }
}
