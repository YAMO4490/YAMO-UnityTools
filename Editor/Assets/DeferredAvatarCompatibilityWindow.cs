#if UNITY_6000_0_OR_NEWER
using System;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    /// <summary>Version-gated UI host. The project's Nilo adapter stays outside the portable package.</summary>
    public sealed class DeferredAvatarCompatibilityWindow : EditorWindow
    {
        const string Backend = "Yamo.Rendering.Editor.NiloToonDeferredAvatarSetup";
        [SerializeField] GameObject target;
        [SerializeField] string reportJson;
        Vector2 scroll;
        Report report;

        [Serializable] public sealed class Report
        {
            public bool canApply, applied;
            public string summary, rendererAsset;
            public int rendererCount, stencilGroups, stencilRenderers, hairDepthRenderers, extraSlots;
            public string[] errors, warnings, details;
        }

        [MenuItem("Tools/YAMO/Rendering/Avatar Deferred Compatibility")]
        public static void Open()
        {
            var window = GetWindow<DeferredAvatarCompatibilityWindow>("Deferred 호환");
            window.minSize = new Vector2(600, 400);
        }

        void OnEnable()
        {
            if (target == null && IsSceneObject(Selection.activeGameObject)) target = Selection.activeGameObject;
            if (!string.IsNullOrEmpty(reportJson)) report = JsonUtility.FromJson<Report>(reportJson);
            EditorApplication.playModeStateChanged += ModeChanged;
        }
        void OnDisable() => EditorApplication.playModeStateChanged -= ModeChanged;
        void ModeChanged(PlayModeStateChange _) => Repaint();
        static bool IsSceneObject(GameObject go) => go && !EditorUtility.IsPersistent(go) && go.scene.IsValid() && go.scene.isLoaded;

        void OnGUI() => DrawGUI();
        public void DrawGUI()
        {
            scroll = EditorGUILayout.BeginScrollView(scroll);
            EditorGUILayout.LabelField("아바타 Deferred 호환", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("아바타의 원본 스텐실·추가 머티리얼 슬롯·반투명 설정을 읽고, 현재 Deferred/Deferred+ 렌더러에 맞는 보강을 적용합니다.", MessageType.Info);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                target = (GameObject)YamoSelectionTargetGUI.Draw("아바타 루트", target, typeof(GameObject), true);
                if (EditorGUI.EndChangeCheck()) { report = null; reportJson = null; }

            }
            bool busy = EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling;
            if (busy) EditorGUILayout.HelpBox("편집모드에서 적용할 수 있습니다.", MessageType.Info);
            using (new EditorGUI.DisabledScope(!IsSceneObject(target) || busy))
            using (new EditorGUILayout.HorizontalScope())
            {
                if (GUILayout.Button("검사", GUILayout.Height(30))) Run(false);
                if (GUILayout.Button("호환 적용", GUILayout.Height(30))) Run(true);
            }
            EditorGUILayout.LabelField("검사 범위: 지정한 아바타 하위와 현재 사용 렌더러", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("원본 머티리얼과 렌더링 방식은 유지합니다. 적용 후 씬을 저장하세요. Undo를 지원합니다.", EditorStyles.wordWrappedMiniLabel);
            EditorGUILayout.LabelField("Undo는 아바타 설정을 되돌립니다. 렌더러에 설치한 공유 보강 기능은 유지됩니다.", EditorStyles.wordWrappedMiniLabel);
            if (report != null)
            {
                EditorGUILayout.Space(8);
                EditorGUILayout.HelpBox(report.summary ?? "", report.applied ? MessageType.Info : report.canApply ? MessageType.Info : MessageType.Warning);
                EditorGUILayout.LabelField("사용 렌더러", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(report.rendererAsset ?? "확인 불가", EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.LabelField("렌더러 / 스텐실 그룹 / 추가 슬롯", $"{report.rendererCount} / {report.stencilGroups} / {report.extraSlots}");
                EditorGUILayout.LabelField("스텐실 보강 / 앞머리 깊이 보강", $"{report.stencilRenderers} / {report.hairDepthRenderers}");
                foreach (var s in report.errors ?? Array.Empty<string>()) EditorGUILayout.HelpBox(s, MessageType.Error);
                foreach (var s in report.warnings ?? Array.Empty<string>()) EditorGUILayout.HelpBox(s, MessageType.Warning);
                foreach (var s in report.details ?? Array.Empty<string>()) EditorGUILayout.LabelField(s, EditorStyles.wordWrappedMiniLabel);
            }
            EditorGUILayout.EndScrollView();
        }

        void Run(bool apply)
        {
            try
            {
                var type = AppDomain.CurrentDomain.GetAssemblies().Select(a => a.GetType(Backend, false)).FirstOrDefault(t => t != null);
                var method = type?.GetMethod(apply ? "Apply" : "Analyze", BindingFlags.Public | BindingFlags.Static, null, new[] { typeof(GameObject) }, null);
                if (method == null) throw new InvalidOperationException("YAMO_Custom의 Deferred 보강 구현을 찾지 못했습니다. NiloToon 보강 스크립트가 설치·컴파일됐는지 확인하세요.");
                reportJson = (string)method.Invoke(null, new object[] { target });
                report = JsonUtility.FromJson<Report>(reportJson);
            }
            catch (Exception ex)
            {
                var e = ex is TargetInvocationException tie && tie.InnerException != null ? tie.InnerException : ex;
                report = new Report { summary = "적용하지 못했습니다.", errors = new[] { e.Message } };
                reportJson = JsonUtility.ToJson(report);
            }
        }
    }
}
#endif
