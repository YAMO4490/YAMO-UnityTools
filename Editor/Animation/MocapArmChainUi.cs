using System;
using System.IO;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    internal static class MocapArmChainUi
    {
        internal static void Draw(MocapArmChainSettings settings)
        {
            EditorGUILayout.HelpBox(
                "OptiTrack·MMRP 공통: Raw 손 위치·회전을 우선 보존하고, 거의 펴진 팔꿈치의 평면 전환을 안정화합니다. " +
                "Forearm Hinge 보정은 끕니다. 기준 MAX를 매번 새로 열어 모션별 MAX·FBX를 저장하고 검증합니다.", MessageType.Info);
            var previous = settings.BaseMaxPath;
            settings.BaseMaxPath = FileField("기준 MAX", settings.BaseMaxPath, "max");
            if (previous != settings.BaseMaxPath && string.IsNullOrWhiteSpace(settings.MaxOutputDirectory))
                settings.MaxOutputDirectory = Path.GetDirectoryName(settings.BaseMaxPath);
            settings.MaxBatchExecutable = FileField("3ds Max 2025 배치", settings.MaxBatchExecutable, "exe");
            using (new EditorGUILayout.HorizontalScope())
            {
                settings.MaxOutputDirectory = EditorGUILayout.TextField("MAX 출력 폴더", settings.MaxOutputDirectory);
                if (GUILayout.Button("찾기", GUILayout.Width(48)))
                {
                    var value = EditorUtility.OpenFolderPanel("모션별 MAX 출력 폴더", settings.MaxOutputDirectory, string.Empty);
                    if (!string.IsNullOrEmpty(value)) settings.MaxOutputDirectory = value;
                }
            }
            EditorGUILayout.LabelField("FBX와 MAX는 각각 출력 폴더 안에 모션별 하위 폴더로 저장합니다.", EditorStyles.wordWrappedMiniLabel);
            settings.ApplyAvatarTwistProfile = EditorGUILayout.Toggle(
                new GUIContent("Avatar 팔 Twist 자동 적용", "선택한 대상 Avatar의 Upper Arm Twist를 0.5, Lower Arm Twist를 0으로 설정합니다. 나머지 muscle 범위는 유지합니다."),
                settings.ApplyAvatarTwistProfile);
            EditorGUILayout.LabelField("팔 Twist 프리셋: Upper 1 / Lower 0 · 각도 제한값이 아닌 회전 분배 설정", EditorStyles.wordWrappedMiniLabel);
            settings.RawTPoseFrame = Mathf.Max(0, EditorGUILayout.IntField("원본 T포즈 프레임", settings.RawTPoseFrame));
            settings.BipedRoot = EditorGUILayout.TextField("Biped 이름", settings.BipedRoot);
            settings.ExportSelectionSet = EditorGUILayout.TextField("추출 Selection Set", settings.ExportSelectionSet);
            EditorGUILayout.HelpBox(
                "기준 MAX와 Unity Biped는 같은 골격·비율이어야 합니다. 원본 T포즈가 잘못된 경우 처리를 중단합니다. " +
                "이 모드는 Edit Mode에서 선택 구간을 오프라인 베이크합니다. FBX 출력이 Assets 안에 있으면 지정 Avatar까지 적용하며, MAX 출력은 Assets 밖을 사용하세요.", MessageType.None);
        }

        private static string FileField(string label, string value, string extension)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                value = EditorGUILayout.TextField(label, value);
                var rect = GUILayoutUtility.GetLastRect();
                var e = Event.current;
                if (rect.Contains(e.mousePosition) && (e.type == EventType.DragUpdated || e.type == EventType.DragPerform) &&
                    DragAndDrop.paths.Length == 1 && DragAndDrop.paths[0].EndsWith("." + extension, StringComparison.OrdinalIgnoreCase))
                {
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    if (e.type == EventType.DragPerform) { DragAndDrop.AcceptDrag(); value = Path.GetFullPath(DragAndDrop.paths[0]); }
                    e.Use();
                }
                if (GUILayout.Button("찾기", GUILayout.Width(48)))
                {
                    var selected = EditorUtility.OpenFilePanel(label, string.IsNullOrEmpty(value) ? string.Empty : Path.GetDirectoryName(value), extension);
                    if (!string.IsNullOrEmpty(selected)) value = selected;
                }
            }
            return value;
        }
    }
}
