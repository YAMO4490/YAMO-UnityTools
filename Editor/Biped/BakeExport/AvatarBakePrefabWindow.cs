// 이 파일은 MagicaCloth2 + VRM(UniVRM 0.x) 둘 다 설치된 환경에서만 컴파일됩니다.
// 상위 폴더의 YAMO.UnityTools.Biped.Editor.asmdef 가 게이팅합니다.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// 아바타 정규화 베이크 → FBX → 임포트 → 마이그레이션 → 프리팹 저장
    /// 풀 파이프라인을 한 창에서 실행하는 EditorWindow.
    /// </summary>
    public class AvatarBakePrefabWindow : EditorWindow
    {
        // ---- 입력 ----
        private GameObject _source;
        private string _fbxProjectPath = "";
        private string _prefabProjectPath = "";

        // ---- Avatar mode ----
        private AvatarMode _avatarMode = AvatarMode.Auto;
        private bool _forceTPose = false;

        // ---- 회전 보존 ----
        private bool _preserveAllRotations = true;          // 기본 ON
        private string _preserveRotationSubstring = "Bip001"; // 위 옵션 OFF 일 때만 활성

        // ---- 마이그레이션 카테고리 ----
        private bool _migActive      = true;
        private bool _migBlendShapes = true;
        private bool _migPhysics     = true;
        private bool _migConstraints = true;

        // ---- 파이프라인 옵션 ----
        private bool _validateUniqueNames        = true;
        private bool _zeroBlendShapesBeforeBake  = true;
        private bool _restoreSourceAfterBake     = true;
        private bool _updateWhenOffscreenInPrefab = true;
        private bool _materialImportNone         = false;  // 기본 OFF: 슬롯 갯수/이름 보존
        private bool _verboseDiagnostics = false;          // 메시 매핑 디버그 로그
        private string _lastLogFilePath = null;            // 가장 최근 실행 로그 파일 경로 (UI 표시용)

        // ---- 로그 ----
        private List<string> _logMessages = new List<string>();
        private Vector2 _logScroll;

        // ---- 윈도우 전체 스크롤 ----
        private Vector2 _windowScroll;

        // ---- Pre-bake utilities 상태 ----
        private Dictionary<string, List<Transform>> _duplicateGroups = new Dictionary<string, List<Transform>>();
        private Vector2 _duplicateScroll;
        private bool _duplicateScanRan = false;
        private List<Transform> _invalidScaleBones = new List<Transform>();
        private Vector2 _boneScaleScroll;
        private bool _boneScaleSearched = false;

        [MenuItem("Tools/YAMO/Biped/Avatar Bake & Prefab Generator")]
        public static void Open()
        {
            GetWindow<AvatarBakePrefabWindow>("Bake & Prefab");
        }

        // ============================================================
        // GUI
        // ============================================================

        private void OnGUI() => DrawGUI();

        /// <summary>
        /// 외부(예: Tool Hub) 에서 호출해 임베드할 수 있는 GUI 본체.
        /// </summary>
        public void DrawGUI()
        {
            _windowScroll = EditorGUILayout.BeginScrollView(_windowScroll);

            EditorGUILayout.LabelField("Avatar Bake → Prefab Pipeline", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "원본 아바타를 정규화 베이크해 FBX로 추출한 뒤, 다시 임포트하여 " +
                "원본의 On/Off · BlendShape · 물리 · Constraint 값을 입혀 프리팹으로 저장합니다.\n\n" +
                "원본 GameObject 는 임시로 모든 노드가 활성화 상태로 변경되며, " +
                "비교/복원용 snapshot 이 씬에 함께 생성됩니다.",
                MessageType.Info);

            // ---- 입력 ----
            EditorGUILayout.Space();
            _source = (GameObject)EditorGUILayout.ObjectField("Avatar Root", _source, typeof(GameObject), true);

            // 경로가 비어 있고 source 가 지정되면 기본값 자동 채우기.
            // 사용자가 직접 입력한 값은 보존; 비우면 다시 기본값으로 회복.
            AutoFillDefaultPaths();

            DrawPreBakeUtilities();

            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Output", EditorStyles.boldLabel);

            DrawPathField("FBX Path",    ref _fbxProjectPath,    "fbx",    DefaultBaseName());
            DrawPathField("Prefab Path", ref _prefabProjectPath, "prefab", DefaultBaseName());

            // ---- Avatar mode ----
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Avatar", EditorStyles.boldLabel);
            _avatarMode = (AvatarMode)EditorGUILayout.EnumPopup("Avatar Mode", _avatarMode);
            using (new EditorGUI.DisabledScope(_avatarMode == AvatarMode.Generic))
            {
                _forceTPose = EditorGUILayout.Toggle("Force T-Pose (Humanoid)", _forceTPose);
            }

            // ---- 회전 보존 ----
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Rotation Preservation", EditorStyles.boldLabel);
            _preserveAllRotations = EditorGUILayout.Toggle("Preserve All Rotations", _preserveAllRotations);
            using (new EditorGUI.DisabledScope(_preserveAllRotations))
            {
                _preserveRotationSubstring = EditorGUILayout.TextField(
                    new GUIContent("By Name Substring",
                        "위 토글이 OFF 일 때만 사용. 비워두면 모든 회전이 0 0 0 으로 초기화됩니다.\n" +
                        "예) \"Bip001\" 입력 시 이름에 그 문자열을 포함한 transform 만 회전 보존."),
                    _preserveRotationSubstring);
            }

            // ---- 마이그레이션 카테고리 ----
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Migrate (snapshot → prefab)", EditorStyles.boldLabel);
            _migActive      = EditorGUILayout.Toggle("Active States (On/Off)", _migActive);
            _migBlendShapes = EditorGUILayout.Toggle("BlendShape Weights",     _migBlendShapes);
            _migPhysics     = EditorGUILayout.Toggle("Physics (MagicaCloth2 + VRMSpringBone)", _migPhysics);
            _migConstraints = EditorGUILayout.Toggle("Constraints",            _migConstraints);

            // ---- 옵션 ----
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Pipeline Options", EditorStyles.boldLabel);
            _validateUniqueNames        = EditorGUILayout.Toggle("Pre-flight: Unique Names",     _validateUniqueNames);
            _zeroBlendShapesBeforeBake  = EditorGUILayout.Toggle("Zero BlendShapes Before Bake", _zeroBlendShapesBeforeBake);
            _restoreSourceAfterBake     = EditorGUILayout.Toggle("Restore Source After Bake",    _restoreSourceAfterBake);
            _updateWhenOffscreenInPrefab = EditorGUILayout.Toggle("Update When Offscreen (Prefab)", _updateWhenOffscreenInPrefab);
            _materialImportNone         = EditorGUILayout.Toggle("Material Import: None",        _materialImportNone);
            _verboseDiagnostics = EditorGUILayout.Toggle(
                new GUIContent("Verbose Diagnostics (debug logs)",
                    "각 단계 (source / normalized / FBX 임포트 직후 / 8.5 직후 / prefab 저장 후) 의 " +
                    "SkinnedMeshRenderer 인벤토리(GO 경로 · sharedMesh 이름 · vertex 수 · " +
                    "instance ID)를 로그로 출력. 메시 매핑 꼬임 디버깅용."),
                _verboseDiagnostics);

            // ---- 실행 ----
            EditorGUILayout.Space();
            using (new EditorGUI.DisabledScope(!CanRun()))
            {
                if (GUILayout.Button("Run Full Pipeline", GUILayout.Height(36)))
                {
                    Run();
                }
            }

            // ---- 로그 ----
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Log", EditorStyles.boldLabel);
            _logScroll = EditorGUILayout.BeginScrollView(_logScroll, GUILayout.Height(220));
            foreach (var line in _logMessages)
            {
                GUILayout.Label(line);
            }
            EditorGUILayout.EndScrollView();

            // 로그 파일 표시 / 액세스
            if (!string.IsNullOrEmpty(_lastLogFilePath))
            {
                var resolvedPath = Path.IsPathRooted(_lastLogFilePath)
                    ? _lastLogFilePath
                    : Path.Combine(Path.GetDirectoryName(Application.dataPath), _lastLogFilePath)
                        .Replace('\\', '/');
                bool fileExists = File.Exists(resolvedPath);
                EditorGUILayout.LabelField("Log file", resolvedPath, EditorStyles.miniLabel);
                EditorGUILayout.BeginHorizontal();
                using (new EditorGUI.DisabledScope(!fileExists))
                {
                    if (GUILayout.Button("Open Log File"))
                    {
                        EditorUtility.OpenWithDefaultApp(resolvedPath);
                    }
                    if (GUILayout.Button("Reveal in Explorer"))
                    {
                        EditorUtility.RevealInFinder(resolvedPath);
                    }
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Clear Log"))
            {
                _logMessages.Clear();
            }
            if (GUILayout.Button("Save Log to File…"))
            {
                SaveCurrentLogToFile();
            }
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.EndScrollView();
        }

        /// <summary>
        /// 현재 윈도우에 누적된 _logMessages 를 사용자가 지정한 파일로 저장.
        /// VerboseDiagnostics 가 꺼져 있어 자동 파일이 만들어지지 않은 경우의 수동 export.
        /// </summary>
        private void SaveCurrentLogToFile()
        {
            var defaultName = $"AvatarBake_log_{System.DateTime.Now:yyyyMMdd_HHmmss}.log";
            var path = EditorUtility.SaveFilePanel("Save Log", "", defaultName, "log");
            if (string.IsNullOrEmpty(path)) return;
            try
            {
                File.WriteAllLines(path, _logMessages, System.Text.Encoding.UTF8);
                EditorUtility.RevealInFinder(path);
            }
            catch (System.Exception e)
            {
                EditorUtility.DisplayDialog("Save Log Failed", e.Message, "OK");
            }
        }

        private void DrawPathField(string label, ref string path, string ext, string defaultName)
        {
            EditorGUILayout.BeginHorizontal();
            path = EditorGUILayout.TextField(label, path);
            if (GUILayout.Button("…", GUILayout.Width(30)))
            {
                var picked = EditorUtility.SaveFilePanelInProject(
                    $"Save {ext.ToUpper()}",
                    string.IsNullOrEmpty(defaultName) ? "avatar_baked" : defaultName,
                    ext,
                    $"Choose where to save the {ext.ToUpper()} file (must be under Assets/).");
                if (!string.IsNullOrEmpty(picked))
                {
                    path = picked;
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        private string DefaultBaseName()
        {
            return _source != null ? _source.name : "avatar";
        }

        /// <summary>
        /// 베이크 직전에 정리해두면 좋은 두 가지 작업:
        ///   1) 중복 이름 검출 + 자동 리네임 (마이그레이션의 이름 기반 매핑이 안전해짐)
        ///   2) 휴머노이드 본을 Unity 표준 이름으로 일괄 변경 (Bip001 → Hips 등)
        ///
        /// 두 기능 모두 source(Avatar Root) 를 대상으로 동작합니다.
        /// </summary>
        private void DrawPreBakeUtilities()
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Pre-Bake Utilities (operate on Source)", EditorStyles.boldLabel);

            using (new EditorGUI.DisabledScope(_source == null))
            {
                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Find Duplicate Names"))
                {
                    _duplicateGroups = AvatarBakePreUtilities.FindDuplicateNames(_source);
                    _duplicateScanRan = true;
                    Log($"Found {_duplicateGroups.Count} duplicate name group(s).");
                }
                using (new EditorGUI.DisabledScope(_duplicateGroups.Count == 0))
                {
                    if (GUILayout.Button("Auto-Rename Duplicates"))
                    {
                        int n = AvatarBakePreUtilities.AutoRenameDuplicates(_duplicateGroups);
                        Log($"Auto-renamed {n} transform(s).");
                        // 재스캔 (이름이 바뀌었으니 그룹도 갱신)
                        _duplicateGroups = AvatarBakePreUtilities.FindDuplicateNames(_source);
                    }
                }
                EditorGUILayout.EndHorizontal();

                if (_duplicateScanRan && _duplicateGroups.Count == 0)
                {
                    EditorGUILayout.HelpBox("No duplicate names found.", MessageType.Info);
                }
                else if (_duplicateGroups.Count > 0)
                {
                    _duplicateScroll = EditorGUILayout.BeginScrollView(_duplicateScroll, GUILayout.Height(120));
                    foreach (var kv in _duplicateGroups)
                    {
                        EditorGUILayout.LabelField($"{kv.Key}  ({kv.Value.Count})");
                        foreach (var t in kv.Value)
                        {
                            EditorGUILayout.ObjectField("  ↳", t, typeof(Transform), true);
                        }
                    }
                    EditorGUILayout.EndScrollView();
                }

                EditorGUILayout.Space(2);
                if (GUILayout.Button("Rename Bones to Unity Humanoid Standard"))
                {
                    var report = AvatarBakePreUtilities.RenameToUnityHumanoidNames(_source);
                    LogHumanoidRenameReport(report);
                }

                EditorGUILayout.Space(2);
                EditorGUILayout.LabelField("Humanoid Bone Scale Check", EditorStyles.miniBoldLabel);
                if (GUILayout.Button("Check Bone Scales"))
                {
                    var found = YamoAssetCheckerCore.FindHumanoidBonesWithNonOneScale(_source);
                    _boneScaleSearched = true;
                    if (found == null)
                    {
                        _invalidScaleBones.Clear();
                        Log("Bone Scale Check: Source is not a Humanoid avatar.");
                    }
                    else
                    {
                        _invalidScaleBones = found;
                        if (_invalidScaleBones.Count == 0)
                            Log("Bone Scale Check: All humanoid bones have scale (1,1,1).");
                        else
                            Log($"Bone Scale Check: {_invalidScaleBones.Count} bone(s) with non-(1,1,1) scale found.");
                    }
                }
                if (_boneScaleSearched && _invalidScaleBones.Count > 0)
                {
                    EditorGUILayout.LabelField($"Non-(1,1,1) scale bones: {_invalidScaleBones.Count}");
                    _boneScaleScroll = EditorGUILayout.BeginScrollView(_boneScaleScroll, GUILayout.Height(120));
                    foreach (var b in _invalidScaleBones)
                    {
                        if (b == null) continue;
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.ObjectField(b, typeof(Transform), true);
                        if (GUILayout.Button("Select", GUILayout.Width(60)))
                        {
                            Selection.activeGameObject = b.gameObject;
                            EditorGUIUtility.PingObject(b.gameObject);
                        }
                        EditorGUILayout.LabelField(b.localScale.ToString(), GUILayout.Width(150));
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUILayout.EndScrollView();
                }
            }
        }

        /// <summary>
        /// Source 가 지정돼 있고 경로 필드가 비어 있으면 기본값으로 채웁니다.
        /// 기본값 = "Assets/{sourceName}/{sourceName}.{ext}"
        /// </summary>
        private void AutoFillDefaultPaths()
        {
            if (_source == null) return;
            var name = _source.name;
            if (string.IsNullOrEmpty(name)) return;

            if (string.IsNullOrEmpty(_fbxProjectPath))
            {
                _fbxProjectPath = $"Assets/{name}/{name}.fbx";
            }
            if (string.IsNullOrEmpty(_prefabProjectPath))
            {
                _prefabProjectPath = $"Assets/{name}/{name}.prefab";
            }
        }

        private bool CanRun()
        {
            return _source != null
                && !string.IsNullOrEmpty(_fbxProjectPath)
                && !string.IsNullOrEmpty(_prefabProjectPath);
        }

        // ============================================================
        // Run
        // ============================================================

        private void Run()
        {
            // 덮어쓰기 경고
            if (File.Exists(ProjectRelativeToAbsolute(_fbxProjectPath))
                || File.Exists(ProjectRelativeToAbsolute(_prefabProjectPath)))
            {
                if (!EditorUtility.DisplayDialog(
                    "Overwrite Existing Files?",
                    $"기존 파일을 덮어씁니다.\n\nFBX:    {_fbxProjectPath}\nPrefab: {_prefabProjectPath}\n\n계속하시겠습니까?",
                    "Overwrite",
                    "Cancel"))
                {
                    return;
                }
            }

            _logMessages.Clear();

            // VerboseDiagnostics ON 이면 자동으로 timestamp 기반 로그 파일 경로 생성.
            // 프로젝트 루트의 Logs/YAMO/AvatarBake_yyyyMMdd_HHmmss.log
            string logFilePath = null;
            if (_verboseDiagnostics)
            {
                var sourceName = _source != null ? _source.name : "unknown";
                var safeSourceName = string.Concat(sourceName.Select(c =>
                    System.Array.IndexOf(Path.GetInvalidFileNameChars(), c) >= 0 ? '_' : c));
                logFilePath = $"Logs/YAMO/AvatarBake_{safeSourceName}_{System.DateTime.Now:yyyyMMdd_HHmmss}.log";
            }
            _lastLogFilePath = logFilePath;

            var opt = new AvatarBakeOptions
            {
                Source              = _source,
                FbxProjectPath      = _fbxProjectPath,
                PrefabProjectPath   = _prefabProjectPath,
                AvatarMode          = _avatarMode,
                ForceTPose          = _forceTPose,
                PreserveAllRotations      = _preserveAllRotations,
                PreserveRotationSubstring = _preserveRotationSubstring,
                MigrateActiveStates = _migActive,
                MigrateBlendShapes  = _migBlendShapes,
                MigratePhysics      = _migPhysics,
                MigrateConstraints  = _migConstraints,
                ValidateUniqueNames        = _validateUniqueNames,
                ZeroBlendShapesBeforeBake  = _zeroBlendShapesBeforeBake,
                RestoreSourceAfterBake     = _restoreSourceAfterBake,
                UpdateWhenOffscreenInPrefab = _updateWhenOffscreenInPrefab,
                MaterialImportNone         = _materialImportNone,
                VerboseDiagnostics         = _verboseDiagnostics,
                LogFilePath                = logFilePath,
                Log = new WindowLog(this),
            };

            bool ok;
            try
            {
                ok = AvatarBakePipeline.Run(opt);
            }
            catch (System.Exception e)
            {
                Log("[Exception] " + e.Message);
                Debug.LogException(e);
                ok = false;
            }

            if (ok)
            {
                EditorUtility.DisplayDialog("Avatar Bake & Prefab",
                    $"Pipeline completed.\n\nPrefab: {_prefabProjectPath}", "OK");
                EditorGUIUtility.PingObject(AssetDatabase.LoadMainAssetAtPath(_prefabProjectPath));
            }
            else
            {
                EditorUtility.DisplayDialog("Avatar Bake & Prefab",
                    "Pipeline failed. See log panel and console.", "OK");
            }
        }

        // ============================================================
        // 로깅
        // ============================================================

        private void Log(string message)
        {
            _logMessages.Add(message);
            Repaint();
        }

        /// <summary>
        /// "Rename Bones to Unity Humanoid Standard" 결과 로그.
        /// 변경 카운트 + Spine/Chest/UpperChest/Toes 진단을 함께 출력.
        /// </summary>
        private void LogHumanoidRenameReport(AvatarBakePreUtilities.HumanoidRenameReport r)
        {
            const string tag = "[HumanoidRename] ";
            Log("─── Humanoid Rename Report ───");

            if (!r.BonesDetected)
            {
                Log("No humanoid bones detected (Animator must be Humanoid, or biped names must match).");
                Debug.LogWarning(tag + "No humanoid bones detected (Animator must be Humanoid, or biped names must match).");
                return;
            }

            // Pre-flight 실패: 비정상 chain 거리 → 중단됨
            if (r.Aborted)
            {
                Log("⚠ ABORTED: " + r.AbortReason);
                Debug.LogError(tag + "ABORTED: " + r.AbortReason);
                if (r.SpineToNeckIntermediates >= 0)
                {
                    Log($"   • Spine → Neck intermediates: {r.SpineToNeckIntermediates}");
                    Debug.LogError(tag + $"Spine → Neck intermediates: {r.SpineToNeckIntermediates}");
                }
                if (r.ChestToHeadIntermediates >= 0)
                {
                    Log($"   • Chest → Head intermediates: {r.ChestToHeadIntermediates}");
                    Debug.LogError(tag + $"Chest → Head intermediates: {r.ChestToHeadIntermediates}");
                }
                Log("   No bones were renamed. Resolve the hierarchy first.");
                Debug.LogError(tag + "No bones were renamed. Resolve the hierarchy first.");

                // 사용자가 못 보고 지나치지 않도록 팝업 노출
                var popup = r.AbortReason + "\n\n";
                if (r.SpineToNeckIntermediates >= 0)
                    popup += $"• Spine → Neck intermediates: {r.SpineToNeckIntermediates}\n";
                if (r.ChestToHeadIntermediates >= 0)
                    popup += $"• Chest → Head intermediates: {r.ChestToHeadIntermediates}\n";
                popup += "\nNo bones were renamed. Resolve the hierarchy first, then retry.";

                EditorUtility.DisplayDialog("Humanoid Rename Aborted", popup, "OK");
                return;
            }

            if (r.RenamedCount > 0)
            {
                Log($"Renamed {r.RenamedCount} bone(s) to Unity standard names.");
                Debug.Log(tag + $"Renamed {r.RenamedCount} bone(s) to Unity standard names.");
            }
            else
            {
                Log("Nothing to rename (all bones already standard).");
                Debug.Log(tag + "Nothing to rename (all bones already standard).");
            }

            // UpperChest 우회
            if (r.HasUpperChest)
            {
                if (r.UpperChestRenamedToSecondary)
                {
                    Log($"UpperChest detected → renamed to '{AvatarBakePreUtilities.UpperChestReplacementName}' (Unity will not auto-map this slot).");
                    Debug.Log(tag + $"UpperChest detected → renamed to '{AvatarBakePreUtilities.UpperChestReplacementName}' (Unity will not auto-map this slot).");
                }
                else
                {
                    Log($"UpperChest detected; already named '{AvatarBakePreUtilities.UpperChestReplacementName}'.");
                    Debug.Log(tag + $"UpperChest detected; already named '{AvatarBakePreUtilities.UpperChestReplacementName}'.");
                }
            }

            // Spine / Chest 카운트 체크 (각 1개여야 함)
            if (r.SpineCount != 1)
            {
                Log($"⚠ Spine count = {r.SpineCount} (expected 1). Check the hierarchy.");
                Debug.LogWarning(tag + $"Spine count = {r.SpineCount} (expected 1). Check the hierarchy.");
            }
            else
            {
                Log("Spine count OK (1).");
            }

            if (r.ChestCount != 1)
            {
                Log($"⚠ Chest count = {r.ChestCount} (expected 1). Check the hierarchy.");
                Debug.LogWarning(tag + $"Chest count = {r.ChestCount} (expected 1). Check the hierarchy.");
            }
            else
            {
                Log("Chest count OK (1).");
            }

            // Toes 체크 — 자동 서치에서 누락되면 수동 변경 안내
            if (!r.LeftToesDetected)
            {
                Log("⚠ Left Toes not detected by auto-search. Manual rename required if a left-toe bone exists.");
                Debug.LogWarning(tag + "Left Toes not detected by auto-search. Manual rename required if a left-toe bone exists.");
            }
            if (!r.RightToesDetected)
            {
                Log("⚠ Right Toes not detected by auto-search. Manual rename required if a right-toe bone exists.");
                Debug.LogWarning(tag + "Right Toes not detected by auto-search. Manual rename required if a right-toe bone exists.");
            }
            if (r.LeftToesDetected && r.RightToesDetected)
            {
                Log("Toes detected on both sides.");
            }
        }

        private class WindowLog : IMigrationLog
        {
            private readonly AvatarBakePrefabWindow _w;
            public WindowLog(AvatarBakePrefabWindow w) { _w = w; }
            public void Info(string m)    { _w.Log(m);                Debug.Log("[AvatarBake] " + m); }
            public void Warning(string m) { _w.Log("Warning: " + m);  Debug.LogWarning("[AvatarBake] " + m); }
            public void Error(string m)   { _w.Log("[Error] " + m);   Debug.LogError("[AvatarBake] " + m); }
        }

        // ---- path utility (창 내 덮어쓰기 검사용) ----

        private static string ProjectRelativeToAbsolute(string projectRel)
        {
            if (string.IsNullOrEmpty(projectRel)) return null;
            var dataPath = Application.dataPath.Replace('\\', '/');
            var projectRoot = dataPath.Substring(0, dataPath.Length - "Assets".Length);
            return projectRoot + projectRel.Replace('\\', '/');
        }
    }
}
