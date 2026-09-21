// YamoAssetChecker — 아바타/씬 자산을 점검·정리하는 통합 EditorWindow.
//
// 하위 탭: 네이밍 도구 (1–2) / 검사도구 (1–4) / 부가 도구 (1–4).
// 각 탭에서 번호를 1부터 시작하며, 각 섹션의 폴드아웃을 유지한다.
//   네이밍 도구 1) Object Name Tools        — 일괄 이름 변경, 자식 정렬, 휴머노이드 스케일 점검
//   네이밍 도구 2) Duplicate Names          — 중복 이름 검출 + 자동 리네임
//   부가 도구 1) Unused Bones             — 사용되지 않는 본 검출 (선택 객체 하위)
//   검사도구 1) Missing / Disabled Scripts — 미싱·비활성 MonoBehaviour 정리
//   부가 도구 2) Missing Bones            — SkinnedMeshRenderer.bones 의 null 검사
//   부가 도구 3) Humanoid Bone Extractor  — 아바타 복제 후 휴머노이드 본만 남겨 스켈레톤 추출
//   검사도구 2) Smart Empty Object Cleaner — SMR 미참조 빈 오브젝트 정리
//   검사도구 3) Inactive Object Finder   — 비활성 오브젝트 탐색
//   부가 도구 4) Magica Collider Symmetry Fixer — Biped 아바타의 빈 Symmetry Target 을 반대편 Primary 본으로 지정
//   검사도구 4) Boneless SMR Fixer       — 본이 없는 SMR(익스포트 시 소실) 검출 + 본 생성·100% 스키닝
//
// 코어 로직은 YamoAssetCheckerCore.cs 의 정적 메서드를 호출.
// 이 파일은 UI / 상태 관리 / 결과 표시만 담당.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    public class YamoAssetChecker : EditorWindow
    {
        private enum ToolCategory
        {
            Naming,
            Inspection,
            Additional,
        }

        private static readonly string[] CategoryLabels =
        {
            "네이밍 도구",
            "검사도구",
            "부가 도구",
        };

        [SerializeField] private ToolCategory _activeCategory = ToolCategory.Naming;
        // 탭 버튼은 고정하고, 각 분류의 스크롤 위치는 독립적으로 유지한다.
        private readonly Vector2[] _categoryScroll = new Vector2[3];

        // ---- 섹션 폴드아웃 상태 ----
        private bool _foldNameTools         = true;
        private bool _foldDuplicates        = true;
        private bool _foldUnusedBones       = true;
        private bool _foldMissingScripts    = true;
        private bool _foldMissingBones      = true;
        private bool _foldHumanoidExtractor = true;

        // ---- 네이밍 도구 1: Object Name Tools ----
        private string _prefixText = "";
        private string _suffixText = "";
        private List<Transform> _invalidScaleBones = new List<Transform>();
        private Vector2 _invalidScaleScroll;
        private bool _invalidScaleSearched = false;

        // ---- 네이밍 도구 2: Duplicate Names ----
        private GameObject _duplicateRoot;
        private Dictionary<string, List<Transform>> _duplicateGroups = new Dictionary<string, List<Transform>>();
        private Vector2 _duplicateScroll;
        private bool _duplicateSearched = false;

        // ---- 부가 도구 1: Unused Bones ----
        private List<string> _excludeStrings = new List<string>();
        private bool _excludeMagicaColliders = true;
        private bool _excludeVRMSpringBones  = true;

        // ---- 검사도구 1: Missing / Disabled Scripts ----
        private GameObject _scriptTargetObject;

        // ---- 부가 도구 2: Missing Bones ----
        private List<YamoAssetCheckerCore.MissingBoneResult> _missingBoneResults = new List<YamoAssetCheckerCore.MissingBoneResult>();
        private Vector2 _missingBoneScroll;
        private bool _missingBoneSearched = false;

        // ---- 부가 도구 3: Humanoid Bone Extractor ----
        // EditorWindow 인스턴스를 임베드해 자체 상태(소스/매핑)를 보유.
        private HumanoidBoneExtractorWindow _humanoidExtractorInstance;

        // ---- 검사도구 2: Smart Empty Object Cleaner ----
        private bool _foldEmptyCleaner = true;

        // ---- 검사도구 3: Inactive Object Finder ----
        private bool _foldInactiveObjects = true;
        private GameObject _inactiveFinderRoot;
        private List<GameObject> _inactiveObjects = new List<GameObject>();
        private bool _inactiveSearched = false;
        private Vector2 _inactiveScroll;
        private GameObject _emptyCleanerRoot;
        private YamoAssetCheckerCore.EmptyObjectScanResult _emptyCleanerResult;
        private bool _emptyCleanerScanned = false;
        private List<bool> _stage2Selected = new List<bool>();
        private Vector2 _stage1Scroll;
        private Vector2 _stage2Scroll;

        // ---- 부가 도구 4: Magica Collider Symmetry Fixer ----
        private bool _foldMagicaSymmetry = true;
        private GameObject _magicaSymmetryRoot;
        private List<YamoAssetCheckerCore.MagicaSymmetryEntry> _magicaSymmetryEntries = new List<YamoAssetCheckerCore.MagicaSymmetryEntry>();
        private bool _magicaSymmetryScanned = false;
        private Vector2 _magicaSymmetryScroll;

        // ---- 검사도구 4: Boneless SMR Fixer ----
        private bool _foldBonelessSmr = true;
        private GameObject _bonelessSmrScanSelection;   // null = 씬 전체 스캔
        private List<YamoAssetCheckerCore.BonelessSmrEntry> _bonelessSmrEntries = new List<YamoAssetCheckerCore.BonelessSmrEntry>();
        private bool _bonelessSmrScanned = false;
        private Vector2 _bonelessSmrScroll;

        [MenuItem("Tools/YAMO/Bones/YAMO Asset Checker")]
        public static void ShowWindow()
        {
            if (HasOpenInstances<YamoAssetChecker>())
                GetWindow<YamoAssetChecker>().Close();
            else
                GetWindow<YamoAssetChecker>("YAMO Asset Checker");
        }

        private void OnEnable()
        {
            if (_humanoidExtractorInstance == null)
                _humanoidExtractorInstance = ScriptableObject.CreateInstance<HumanoidBoneExtractorWindow>();
        }

        private void OnDisable()
        {
            if (_humanoidExtractorInstance != null) DestroyImmediate(_humanoidExtractorInstance);
        }

        private void OnGUI() => DrawGUI();

        /// <summary>
        /// 외부(예: Tool Hub) 에서 호출해 임베드할 수 있는 GUI 본체.
        /// </summary>
        public void DrawGUI()
        {
            EditorGUILayout.LabelField("YAMO Asset Checker", EditorStyles.boldLabel);
            int newIndex = GUILayout.Toolbar(
                (int)_activeCategory, CategoryLabels, GUILayout.Height(26));
            if (newIndex != (int)_activeCategory)
            {
                _activeCategory = (ToolCategory)newIndex;
                GUI.FocusControl(null);
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.Space(4);
            int categoryIndex = (int)_activeCategory;
            _categoryScroll[categoryIndex] = EditorGUILayout.BeginScrollView(_categoryScroll[categoryIndex]);

            EditorGUILayout.HelpBox(
                "아바타/씬의 이름·본·스크립트 상태를 점검하고 정리합니다.\n선택한 분류의 섹션을 펼쳐서 사용하세요.",
                MessageType.Info);

            switch (_activeCategory)
            {
                case ToolCategory.Naming:
                    DrawNameToolsSection();            // 1
                    DrawDuplicatesSection();           // 2
                    break;
                case ToolCategory.Inspection:
                    DrawMissingScriptsSection();       // 1
                    DrawSmartEmptyCleanerSection();    // 2
                    DrawInactiveObjectsSection();      // 3
                    DrawBonelessSmrSection();           // 4
                    break;
                case ToolCategory.Additional:
                    DrawUnusedBonesSection();          // 1
                    DrawMissingBonesSection();         // 2
                    DrawHumanoidExtractorSection();    // 3
                    DrawMagicaSymmetrySection();        // 4
                    break;
            }

            EditorGUILayout.EndScrollView();
        }

        // ============================================================
        // 네이밍 도구 1: Object Name Tools
        // ============================================================
        private void DrawNameToolsSection()
        {
            DrawSeparator();
            _foldNameTools = EditorGUILayout.Foldout(_foldNameTools, "1. Object Name Tools  (Selection 기반)", true, EditorStyles.foldoutHeader);
            if (!_foldNameTools) return;

            EditorGUI.indentLevel++;
            _prefixText = EditorGUILayout.TextField("Prefix", _prefixText);
            _suffixText = EditorGUILayout.TextField("Suffix", _suffixText);

            if (GUILayout.Button("Apply Prefix and Suffix"))
            {
                int n = YamoAssetCheckerCore.ApplyPrefixAndSuffix(Selection.gameObjects, _prefixText, _suffixText);
                Debug.Log($"[AssetChecker] Renamed {n} object(s).");
            }

            GUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Remove First Char"))
                YamoAssetCheckerCore.RemoveFirstCharacter(Selection.gameObjects);
            if (GUILayout.Button("Remove Last Char"))
                YamoAssetCheckerCore.RemoveLastCharacter(Selection.gameObjects);
            if (GUILayout.Button("Spaces → Underscore"))
                YamoAssetCheckerCore.ReplaceSpacesWithUnderscores(Selection.gameObjects);
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(4);
            using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
            {
                if (GUILayout.Button("Sort Children by Name (selected parent)"))
                {
                    int n = YamoAssetCheckerCore.SortChildrenByName(Selection.activeGameObject);
                    Debug.Log($"[AssetChecker] Sorted {n} children of '{Selection.activeGameObject.name}'.");
                }
            }

            // Humanoid scale check
            GUILayout.Space(8);
            EditorGUILayout.LabelField("Humanoid Bone Scale Check (selected avatar)", EditorStyles.miniBoldLabel);
            using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
            {
                if (GUILayout.Button("Check Bone Scales"))
                {
                    var found = YamoAssetCheckerCore.FindHumanoidBonesWithNonOneScale(Selection.activeGameObject);
                    _invalidScaleSearched = true;
                    if (found == null)
                    {
                        _invalidScaleBones.Clear();
                        EditorUtility.DisplayDialog("YAMO Asset Checker",
                            "Selection 이 Humanoid Animator 를 가진 GameObject 가 아닙니다.", "OK");
                    }
                    else
                    {
                        _invalidScaleBones = found;
                        if (_invalidScaleBones.Count == 0)
                            Debug.Log("[AssetChecker] All humanoid bones have scale (1,1,1).");
                    }
                }
            }
            if (_invalidScaleSearched && _invalidScaleBones.Count > 0)
            {
                EditorGUILayout.LabelField($"Non-(1,1,1) scale bones: {_invalidScaleBones.Count}");
                _invalidScaleScroll = EditorGUILayout.BeginScrollView(_invalidScaleScroll, GUILayout.Height(120));
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
            EditorGUI.indentLevel--;
        }

        // ============================================================
        // 네이밍 도구 2: Duplicate Names
        // ============================================================
        private void DrawDuplicatesSection()
        {
            DrawSeparator();
            _foldDuplicates = EditorGUILayout.Foldout(_foldDuplicates, "2. Duplicate Names", true, EditorStyles.foldoutHeader);
            if (!_foldDuplicates) return;

            EditorGUI.indentLevel++;
            _duplicateRoot = (GameObject)EditorGUILayout.ObjectField("Target Root", _duplicateRoot, typeof(GameObject), true);

            EditorGUILayout.BeginHorizontal();
            using (new EditorGUI.DisabledScope(_duplicateRoot == null))
            {
                if (GUILayout.Button("Find Duplicate Names"))
                {
                    _duplicateGroups = YamoAssetCheckerCore.FindDuplicateNames(_duplicateRoot);
                    _duplicateSearched = true;
                    Debug.Log($"[AssetChecker] Found {_duplicateGroups.Count} duplicate name group(s).");
                }
            }
            using (new EditorGUI.DisabledScope(_duplicateGroups.Count == 0))
            {
                if (GUILayout.Button("Auto-Rename Duplicates"))
                {
                    int n = YamoAssetCheckerCore.AutoRenameDuplicates(_duplicateGroups);
                    Debug.Log($"[AssetChecker] Auto-renamed {n} transform(s).");
                    _duplicateGroups = YamoAssetCheckerCore.FindDuplicateNames(_duplicateRoot);
                }
            }
            EditorGUILayout.EndHorizontal();

            if (_duplicateSearched && _duplicateGroups.Count == 0)
            {
                EditorGUILayout.HelpBox("No duplicate names found.", MessageType.Info);
            }
            else if (_duplicateGroups.Count > 0)
            {
                _duplicateScroll = EditorGUILayout.BeginScrollView(_duplicateScroll, GUILayout.Height(140));
                foreach (var kv in _duplicateGroups)
                {
                    EditorGUILayout.LabelField($"{kv.Key}  ({kv.Value.Count})");
                    foreach (var t in kv.Value)
                        EditorGUILayout.ObjectField("  ↳", t, typeof(Transform), true);
                }
                EditorGUILayout.EndScrollView();
            }
            EditorGUI.indentLevel--;
        }

        // ============================================================
        // 부가 도구 1: Unused Bones
        // ============================================================
        private void DrawUnusedBonesSection()
        {
            DrawSeparator();
            _foldUnusedBones = EditorGUILayout.Foldout(_foldUnusedBones, "1. Unused Bones  (Selection 기반)", true, EditorStyles.foldoutHeader);
            if (!_foldUnusedBones) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.LabelField("Exclude Strings (substring match)", EditorStyles.miniBoldLabel);
            for (int i = 0; i < _excludeStrings.Count; i++)
            {
                EditorGUILayout.BeginHorizontal();
                _excludeStrings[i] = EditorGUILayout.TextField($"Exclude {i + 1}", _excludeStrings[i]);
                if (GUILayout.Button("-", GUILayout.Width(22)))
                {
                    _excludeStrings.RemoveAt(i);
                    GUIUtility.ExitGUI();
                }
                EditorGUILayout.EndHorizontal();
            }
            if (GUILayout.Button("+", GUILayout.Width(22)))
            {
                _excludeStrings.Add("");
            }

            GUILayout.Space(4);
            _excludeMagicaColliders = EditorGUILayout.Toggle("Exclude Magica Colliders", _excludeMagicaColliders);
            _excludeVRMSpringBones  = EditorGUILayout.Toggle("Exclude VRM Spring Bones", _excludeVRMSpringBones);

            GUILayout.Space(4);
            using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
            {
                if (GUILayout.Button("Find and Select Unused Bones"))
                {
                    var opts = new YamoAssetCheckerCore.UnusedBoneOptions
                    {
                        ExcludeStrings = _excludeStrings,
                        ExcludeMagicaColliders = _excludeMagicaColliders,
                        ExcludeVRMSpringBones = _excludeVRMSpringBones,
                    };
                    var unused = YamoAssetCheckerCore.FindUnusedBones(Selection.activeGameObject, opts);
                    if (unused.Count == 0)
                    {
                        Debug.Log("[AssetChecker] No unused bones found.");
                    }
                    else
                    {
                        var arr = new Object[unused.Count];
                        for (int i = 0; i < unused.Count; i++) arr[i] = unused[i].gameObject;
                        Selection.objects = arr;
                        Debug.Log($"[AssetChecker] Found and selected {unused.Count} unused bone(s).");
                    }
                }
            }
            EditorGUI.indentLevel--;
        }

        // ============================================================
        // 검사도구 1: Missing / Disabled Scripts
        // ============================================================
        private void DrawMissingScriptsSection()
        {
            DrawSeparator();
            _foldMissingScripts = EditorGUILayout.Foldout(_foldMissingScripts, "1. Missing / Disabled Scripts", true, EditorStyles.foldoutHeader);
            if (!_foldMissingScripts) return;

            EditorGUI.indentLevel++;
            _scriptTargetObject = (GameObject)EditorGUILayout.ObjectField("Target", _scriptTargetObject, typeof(GameObject), true);

            using (new EditorGUI.DisabledScope(_scriptTargetObject == null))
            {
                if (GUILayout.Button("Search (missing + disabled count)"))
                {
                    var (missing, disabled) = YamoAssetCheckerCore.CountScripts(_scriptTargetObject);
                    if (missing == 0 && disabled == 0)
                        Debug.Log($"[AssetChecker] [{_scriptTargetObject.name}] 미싱/비활성 스크립트 없음.");
                    else
                        Debug.Log($"[AssetChecker] [{_scriptTargetObject.name}] 미싱: {missing}개, 비활성: {disabled}개.");
                }

                EditorGUILayout.BeginHorizontal();
                if (GUILayout.Button("Remove Missing"))
                {
                    int n = YamoAssetCheckerCore.RemoveMissingScripts(_scriptTargetObject);
                    Debug.Log($"[AssetChecker] [{_scriptTargetObject.name}] 미싱 스크립트 {n}개 제거.");
                }
                if (GUILayout.Button("Remove Disabled"))
                {
                    int n = YamoAssetCheckerCore.RemoveDisabledScripts(_scriptTargetObject);
                    Debug.Log($"[AssetChecker] [{_scriptTargetObject.name}] 비활성 스크립트 {n}개 제거.");
                }
                EditorGUILayout.EndHorizontal();

                if (GUILayout.Button("Remove Missing + Disabled"))
                {
                    var (m, d) = YamoAssetCheckerCore.RemoveAllScripts(_scriptTargetObject);
                    Debug.Log($"[AssetChecker] [{_scriptTargetObject.name}] 미싱 {m}개, 비활성 {d}개 제거 완료.");
                }
            }
            EditorGUI.indentLevel--;
        }

        // ============================================================
        // 부가 도구 2: Missing Bones (SkinnedMeshRenderer)
        // ============================================================
        private void DrawMissingBonesSection()
        {
            DrawSeparator();
            _foldMissingBones = EditorGUILayout.Foldout(_foldMissingBones, "2. Missing Bones in SkinnedMeshRenderer", true, EditorStyles.foldoutHeader);
            if (!_foldMissingBones) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox(
                "SkinnedMeshRenderer 의 bones[] 배열에 null 항목 또는 누락된 rootBone 이 있는지 검사합니다.",
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Scan Whole Scene"))
            {
                _missingBoneResults = YamoAssetCheckerCore.CheckMissingBonesInScene();
                _missingBoneSearched = true;
                LogMissingBones("Scene");
            }
            using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
            {
                if (GUILayout.Button("Scan Selection (children)"))
                {
                    _missingBoneResults = YamoAssetCheckerCore.CheckMissingBonesInChildren(Selection.activeGameObject);
                    _missingBoneSearched = true;
                    LogMissingBones(Selection.activeGameObject.name);
                }
            }
            EditorGUILayout.EndHorizontal();

            if (_missingBoneSearched)
            {
                if (_missingBoneResults.Count == 0)
                {
                    EditorGUILayout.HelpBox("누락된 본이 없습니다.", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.LabelField($"문제 발견: {_missingBoneResults.Count}개의 SkinnedMeshRenderer", EditorStyles.boldLabel);
                    _missingBoneScroll = EditorGUILayout.BeginScrollView(_missingBoneScroll, GUILayout.Height(180));
                    foreach (var r in _missingBoneResults)
                    {
                        if (r.Renderer == null) continue;
                        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                        EditorGUILayout.BeginHorizontal();
                        EditorGUILayout.ObjectField(r.Renderer, typeof(SkinnedMeshRenderer), true);
                        if (GUILayout.Button("Select", GUILayout.Width(60)))
                        {
                            Selection.activeGameObject = r.Renderer.gameObject;
                            EditorGUIUtility.PingObject(r.Renderer.gameObject);
                        }
                        EditorGUILayout.EndHorizontal();
                        if (r.RootBoneMissing)
                            EditorGUILayout.LabelField("  ⚠ Root Bone 누락", EditorStyles.miniLabel);
                        if (r.MissingBoneIndices.Count > 0)
                        {
                            string indices = string.Join(", ", r.MissingBoneIndices);
                            EditorGUILayout.LabelField($"  ⚠ 누락된 본 인덱스: [{indices}] (총 {r.MissingBoneIndices.Count}개)", EditorStyles.miniLabel);
                        }
                        EditorGUILayout.EndVertical();
                    }
                    EditorGUILayout.EndScrollView();
                }
            }
            EditorGUI.indentLevel--;
        }

        // ============================================================
        // 부가 도구 3: Humanoid Bone Extractor
        // ============================================================
        private void DrawHumanoidExtractorSection()
        {
            DrawSeparator();
            _foldHumanoidExtractor = EditorGUILayout.Foldout(_foldHumanoidExtractor, "3. Humanoid Bone Extractor", true, EditorStyles.foldoutHeader);
            if (!_foldHumanoidExtractor) return;

            EditorGUI.indentLevel++;
            if (_humanoidExtractorInstance != null)
                _humanoidExtractorInstance.DrawGUI();
            EditorGUI.indentLevel--;
        }

        private void LogMissingBones(string scope)
        {
            if (_missingBoneResults.Count == 0)
            {
                Debug.Log($"[AssetChecker] '{scope}' 검사 완료: 누락된 본 없음.");
                return;
            }
            int total = 0;
            foreach (var r in _missingBoneResults)
            {
                total += r.MissingBoneIndices.Count;
                if (r.RootBoneMissing) total++;
            }
            Debug.LogWarning($"[AssetChecker] '{scope}' 검사 완료: {_missingBoneResults.Count}개 SMR / 총 {total}개 누락 본.");
        }

        // ============================================================
        // 공통
        // ============================================================
        // ============================================================
        // 검사도구 2: Smart Empty Object Cleaner
        // ============================================================
        private void DrawSmartEmptyCleanerSection()
        {
            DrawSeparator();
            _foldEmptyCleaner = EditorGUILayout.Foldout(_foldEmptyCleaner, "2. Smart Empty Object Cleaner", true, EditorStyles.foldoutHeader);
            if (!_foldEmptyCleaner) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox(
                "SkinnedMeshRenderer 에 참조되지 않는 빈 오브젝트를 탐지하고 제거합니다.\n" +
                "비활성 SMR 의 본 참조도 모두 추적합니다.\n" +
                "실행 시 원본은 유지되고 사본을 생성해 해당 사본에서 작업합니다.",
                MessageType.None);

            _emptyCleanerRoot = (GameObject)EditorGUILayout.ObjectField("Target Root", _emptyCleanerRoot, typeof(GameObject), true);

            using (new EditorGUI.DisabledScope(_emptyCleanerRoot == null))
            {
                if (GUILayout.Button("Scan"))
                {
                    _emptyCleanerResult  = YamoAssetCheckerCore.ScanEmptyObjects(_emptyCleanerRoot);
                    _emptyCleanerScanned = true;
                    RebuildStage2Selection();
                }
            }

            if (!_emptyCleanerScanned || _emptyCleanerResult == null)
            {
                EditorGUI.indentLevel--;
                return;
            }

            // ── 1단계 ──────────────────────────────────────────────────
            EditorGUILayout.Space(6);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("1단계: 완전 미참조 오브젝트 삭제", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "자신과 모든 하위 오브젝트가 어떤 SMR 에도 참조되지 않는 빈 오브젝트.",
                    EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.Space(2);

                var s1 = _emptyCleanerResult.Stage1;
                if (s1.Count == 0)
                {
                    EditorGUILayout.HelpBox("삭제 가능한 완전 미참조 오브젝트가 없습니다.", MessageType.Info);
                }
                else
                {
                    int totalObjs = EmptyCleanerSumTotal(s1);
                    EditorGUILayout.LabelField($"발견: {s1.Count}개 루트  (하위 포함 총 {totalObjs}개 삭제)");

                    _stage1Scroll = EditorGUILayout.BeginScrollView(_stage1Scroll, GUILayout.Height(110));
                    foreach (var entry in s1)
                    {
                        if (entry.Object == null) continue;
                        EditorGUILayout.BeginHorizontal();
                        using (new EditorGUI.DisabledScope(true))
                            EditorGUILayout.ObjectField(entry.Object, typeof(GameObject), true);
                        if (entry.TotalCount > 1)
                            EditorGUILayout.LabelField($"(+{entry.TotalCount - 1})", GUILayout.Width(44));
                        if (GUILayout.Button("▶", GUILayout.Width(24)))
                        {
                            Selection.activeGameObject = entry.Object;
                            EditorGUIUtility.PingObject(entry.Object);
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUILayout.EndScrollView();

                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("Console Preview"))
                    {
                        foreach (var e in s1)
                            if (e.Object != null)
                                Debug.Log($"[EmptyCleaner] Stage1 삭제 대상: {EmptyCleanerBuildPath(e.Object.transform)}  (총 {e.TotalCount}개)", e.Object);
                    }

                    var prevBg = GUI.backgroundColor;
                    GUI.backgroundColor = new Color(1f, 0.45f, 0.45f);
                    bool clickedStage1 = GUILayout.Button($"1단계 삭제  ({s1.Count}개 루트)");
                    GUI.backgroundColor = prevBg;
                    EditorGUILayout.EndHorizontal();

                    if (clickedStage1 && EditorUtility.DisplayDialog("Smart Empty Object Cleaner",
                        $"원본을 유지하고 사본을 생성한 뒤, 사본에서 1단계를 실행합니다.\n" +
                        $"삭제 대상: {s1.Count}개 루트 (총 {totalObjs}개)\nUndo 가능합니다.",
                        "사본으로 실행", "취소"))
                    {
                        var copy       = DuplicateForCleaner(_emptyCleanerRoot);
                        var copyResult = YamoAssetCheckerCore.ScanEmptyObjects(copy);
                        int n          = YamoAssetCheckerCore.ExecuteStage1(copyResult.Stage1);
                        Debug.Log($"[EmptyCleaner] 1단계 완료: 사본 '{copy.name}' 에서 {n}개 삭제. 원본은 유지됩니다.", copy);
                    }
                }
            }

            // ── 2단계 ──────────────────────────────────────────────────
            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField("2단계: 하위에 본이 있는 빈 컨테이너", EditorStyles.boldLabel);
                EditorGUILayout.LabelField(
                    "자신은 미참조이지만 하위에 참조 본을 가진 오브젝트. 삭제 시 직속 자식들이 부모로 리패런팅됩니다.",
                    EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.Space(2);

                var s2 = _emptyCleanerResult.Stage2;
                if (s2.Count == 0)
                {
                    EditorGUILayout.HelpBox("해당 오브젝트가 없습니다.", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.HelpBox("삭제 시 직속 자식들이 부모 위치로 올라갑니다. 신중하게 선택하세요.", MessageType.Warning);
                    EditorGUILayout.LabelField($"발견: {s2.Count}개");

                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("전체 선택", GUILayout.Width(80)))
                        for (int i = 0; i < _stage2Selected.Count; i++) _stage2Selected[i] = true;
                    if (GUILayout.Button("전체 해제", GUILayout.Width(80)))
                        for (int i = 0; i < _stage2Selected.Count; i++) _stage2Selected[i] = false;
                    EditorGUILayout.EndHorizontal();

                    _stage2Scroll = EditorGUILayout.BeginScrollView(_stage2Scroll, GUILayout.Height(150));
                    for (int i = 0; i < s2.Count; i++)
                    {
                        if (i >= _stage2Selected.Count) break;
                        var entry = s2[i];
                        if (entry.Object == null) continue;

                        EditorGUILayout.BeginHorizontal();
                        _stage2Selected[i] = EditorGUILayout.Toggle(_stage2Selected[i], GUILayout.Width(18));
                        using (new EditorGUI.DisabledScope(true))
                            EditorGUILayout.ObjectField(entry.Object, typeof(GameObject), true);
                        EditorGUILayout.LabelField($"자식 {entry.TotalCount}개", GUILayout.Width(60));
                        if (GUILayout.Button("▶", GUILayout.Width(24)))
                        {
                            Selection.activeGameObject = entry.Object;
                            EditorGUIUtility.PingObject(entry.Object);
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUILayout.EndScrollView();

                    int selCount = 0;
                    for (int i = 0; i < _stage2Selected.Count; i++) if (_stage2Selected[i]) selCount++;

                    var prevBg2 = GUI.backgroundColor;
                    if (selCount > 0) GUI.backgroundColor = new Color(1f, 0.65f, 0.3f);
                    using (new EditorGUI.DisabledScope(selCount == 0))
                    {
                        bool clickedStage2 = GUILayout.Button($"선택 항목 삭제  ({selCount}개)");
                        GUI.backgroundColor = prevBg2;

                        if (clickedStage2 && EditorUtility.DisplayDialog("Smart Empty Object Cleaner",
                            $"원본을 유지하고 사본을 생성한 뒤, 사본에서 2단계를 실행합니다.\n" +
                            $"선택 항목: {selCount}개 (리패런팅 후 삭제)\nUndo 가능합니다.",
                            "사본으로 실행", "취소"))
                        {
                            var copy       = DuplicateForCleaner(_emptyCleanerRoot);
                            var copyResult = YamoAssetCheckerCore.ScanEmptyObjects(copy);
                            int n          = YamoAssetCheckerCore.ExecuteStage2Selected(copyResult.Stage2, _stage2Selected);
                            Debug.Log($"[EmptyCleaner] 2단계 완료: 사본 '{copy.name}' 에서 {n}개 삭제. 원본은 유지됩니다.", copy);
                        }
                    }
                }
            }

            EditorGUI.indentLevel--;
        }

        private static GameObject DuplicateForCleaner(GameObject original)
        {
            var copy = Object.Instantiate(original, original.transform.parent);
            copy.name = original.name + " (Cleaned)";
            copy.transform.SetSiblingIndex(original.transform.GetSiblingIndex() + 1);
            // Execute 내부의 Undo.CollapseUndoOperations 가 이 등록을 함께 묶어줌
            Undo.RegisterCreatedObjectUndo(copy, "Smart Empty Cleaner: Create Copy");
            return copy;
        }

        private void RescanEmptyCleaner()
        {
            if (_emptyCleanerRoot == null) return;
            _emptyCleanerResult = YamoAssetCheckerCore.ScanEmptyObjects(_emptyCleanerRoot);
            RebuildStage2Selection();
        }

        private void RebuildStage2Selection()
        {
            _stage2Selected.Clear();
            if (_emptyCleanerResult == null) return;
            foreach (var _ in _emptyCleanerResult.Stage2) _stage2Selected.Add(false);
        }

        private static int EmptyCleanerSumTotal(List<YamoAssetCheckerCore.EmptyObjectEntry> list)
        {
            int n = 0;
            foreach (var e in list) n += e.TotalCount;
            return n;
        }

        private static string EmptyCleanerBuildPath(Transform t)
        {
            if (t.parent == null) return t.name;
            return EmptyCleanerBuildPath(t.parent) + "/" + t.name;
        }

        // ============================================================
        // 검사도구 3: Inactive Object Finder
        // ============================================================
        private void DrawInactiveObjectsSection()
        {
            DrawSeparator();
            _foldInactiveObjects = EditorGUILayout.Foldout(_foldInactiveObjects, "3. Inactive Object Finder", true, EditorStyles.foldoutHeader);
            if (!_foldInactiveObjects) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox(
                "Target Root 하위에서 비활성화(activeSelf = false)된 오브젝트를 탐색합니다.\n" +
                "비활성 부모의 자식도 모두 포함됩니다.",
                MessageType.None);

            _inactiveFinderRoot = (GameObject)EditorGUILayout.ObjectField("Target Root", _inactiveFinderRoot, typeof(GameObject), true);

            using (new EditorGUI.DisabledScope(_inactiveFinderRoot == null))
            {
                if (GUILayout.Button("Scan"))
                {
                    _inactiveObjects  = YamoAssetCheckerCore.FindInactiveObjects(_inactiveFinderRoot);
                    _inactiveSearched = true;
                }
            }

            if (_inactiveSearched)
            {
                EditorGUILayout.Space(2);
                if (_inactiveObjects.Count == 0)
                {
                    EditorGUILayout.HelpBox("비활성화된 오브젝트가 없습니다.", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.LabelField($"발견: {_inactiveObjects.Count}개");

                    EditorGUILayout.BeginHorizontal();
                    if (GUILayout.Button("에디터에서 전체 선택"))
                    {
                        var arr = new Object[_inactiveObjects.Count];
                        for (int i = 0; i < _inactiveObjects.Count; i++) arr[i] = _inactiveObjects[i];
                        Selection.objects = arr;
                    }
                    if (GUILayout.Button("Console 출력"))
                    {
                        foreach (var go in _inactiveObjects)
                            if (go != null)
                                Debug.Log($"[InactiveFinder] 비활성: {EmptyCleanerBuildPath(go.transform)}", go);
                    }
                    EditorGUILayout.EndHorizontal();

                    _inactiveScroll = EditorGUILayout.BeginScrollView(_inactiveScroll, GUILayout.Height(180));
                    foreach (var go in _inactiveObjects)
                    {
                        if (go == null) continue;
                        EditorGUILayout.BeginHorizontal();
                        using (new EditorGUI.DisabledScope(true))
                            EditorGUILayout.ObjectField(go, typeof(GameObject), true);
                        if (GUILayout.Button("▶", GUILayout.Width(24)))
                        {
                            Selection.activeGameObject = go;
                            EditorGUIUtility.PingObject(go);
                        }
                        EditorGUILayout.EndHorizontal();
                    }
                    EditorGUILayout.EndScrollView();
                }
            }

            EditorGUI.indentLevel--;
        }

        // ============================================================
        // 부가 도구 4: Magica Collider Symmetry Fixer
        // ============================================================
        private void DrawMagicaSymmetrySection()
        {
            DrawSeparator();
            _foldMagicaSymmetry = EditorGUILayout.Foldout(_foldMagicaSymmetry, "4. Magica Collider Symmetry Fixer", true, EditorStyles.foldoutHeader);
            if (!_foldMagicaSymmetry) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox(
                "Biped 변환 아바타에서 MagicaCloth2 콜라이더의 Automatic 시메트리가 반대편 본을 찾지 못하는 문제를 수정합니다.\n" +
                "팔(어깨 이하)/다리의 원본(Primary) 본 아래 콜라이더 중 Symmetry Target 이 비었거나 Biped 본인 것을 찾아,\n" +
                "X_Symmetry + 반대편 Primary 본(예: LeftHand → RightHand)으로 설정합니다. Biped 본은 타겟으로 쓰지 않습니다.",
                MessageType.None);

            _magicaSymmetryRoot = (GameObject)EditorGUILayout.ObjectField("Target Root", _magicaSymmetryRoot, typeof(GameObject), true);

            using (new EditorGUI.DisabledScope(_magicaSymmetryRoot == null))
            {
                if (GUILayout.Button("Scan"))
                {
                    _magicaSymmetryEntries  = YamoAssetCheckerCore.ScanMagicaSymmetryTargets(_magicaSymmetryRoot);
                    _magicaSymmetryScanned  = true;
                }
            }

            if (_magicaSymmetryScanned)
            {
                EditorGUILayout.Space(2);
                if (_magicaSymmetryEntries.Count == 0)
                {
                    EditorGUILayout.HelpBox("수정이 필요한 시메트리 콜라이더가 없습니다.", MessageType.Info);
                }
                else
                {
                    int fixable = 0;
                    foreach (var e in _magicaSymmetryEntries) if (e.Fixable) fixable++;
                    EditorGUILayout.LabelField($"발견: {_magicaSymmetryEntries.Count}개  (수정 가능 {fixable}개)");

                    _magicaSymmetryScroll = EditorGUILayout.BeginScrollView(_magicaSymmetryScroll, GUILayout.Height(180));
                    foreach (var e in _magicaSymmetryEntries)
                    {
                        if (e.Collider == null) continue;
                        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                        EditorGUILayout.BeginHorizontal();
                        using (new EditorGUI.DisabledScope(true))
                            EditorGUILayout.ObjectField(e.Collider.gameObject, typeof(GameObject), true);
                        if (GUILayout.Button("▶", GUILayout.Width(24)))
                        {
                            Selection.activeGameObject = e.Collider.gameObject;
                            EditorGUIUtility.PingObject(e.Collider.gameObject);
                        }
                        EditorGUILayout.EndHorizontal();

                        string cur = e.CurrentTarget != null ? e.CurrentTarget.name : "None";
                        if (e.Fixable)
                            EditorGUILayout.LabelField($"  {e.CurrentModeName} / {cur}  →  X_Symmetry / {e.MirrorBone.name}", EditorStyles.miniLabel);
                        else
                            EditorGUILayout.LabelField($"  ⚠ '{e.PrimaryBone.name}' 의 반대편 Primary 본을 찾지 못함 (수동 설정 필요)", EditorStyles.miniLabel);
                        EditorGUILayout.EndVertical();
                    }
                    EditorGUILayout.EndScrollView();

                    using (new EditorGUI.DisabledScope(fixable == 0))
                    {
                        if (GUILayout.Button($"Fix All  ({fixable}개)"))
                        {
                            int n = YamoAssetCheckerCore.FixMagicaSymmetryTargets(_magicaSymmetryEntries);
                            Debug.Log($"[AssetChecker] Magica 시메트리 타겟 {n}개 수정 완료.");
                            _magicaSymmetryEntries = YamoAssetCheckerCore.ScanMagicaSymmetryTargets(_magicaSymmetryRoot);
                        }
                    }
                }
            }

            EditorGUI.indentLevel--;
        }

        // ============================================================
        // 검사도구 4: Boneless SMR Fixer
        // ============================================================
        private void DrawBonelessSmrSection()
        {
            DrawSeparator();
            _foldBonelessSmr = EditorGUILayout.Foldout(_foldBonelessSmr, "4. Boneless SMR Fixer", true, EditorStyles.foldoutHeader);
            if (!_foldBonelessSmr) return;

            EditorGUI.indentLevel++;
            EditorGUILayout.HelpBox(
                "본/바인드포즈가 하나도 없는 SkinnedMeshRenderer 를 찾습니다 (블렌드셰이프만 있는 소품 등).\n" +
                "이런 메시는 FBX/VRM 익스포트 시 통째로 소실됩니다.\n" +
                "FIX: SMR 과 같은 부모·같은 위치에 '<이름>_Bone' 을 생성하고 100% 스키닝한 메시 사본(.asset)으로 교체합니다. 원본 메시는 유지됩니다.",
                MessageType.None);

            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Scan Whole Scene"))
            {
                _bonelessSmrScanSelection = null;
                RescanBonelessSmrs();
            }
            using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
            {
                if (GUILayout.Button("Scan Selection (children)"))
                {
                    _bonelessSmrScanSelection = Selection.activeGameObject;
                    RescanBonelessSmrs();
                }
            }
            EditorGUILayout.EndHorizontal();

            if (_bonelessSmrScanned)
            {
                EditorGUILayout.Space(2);
                if (_bonelessSmrEntries.Count == 0)
                {
                    EditorGUILayout.HelpBox("본이 없는 SkinnedMeshRenderer 가 없습니다.", MessageType.Info);
                }
                else
                {
                    EditorGUILayout.LabelField($"문제 발견: {_bonelessSmrEntries.Count}개의 SkinnedMeshRenderer", EditorStyles.boldLabel);

                    YamoAssetCheckerCore.BonelessSmrEntry fixOne = null;
                    _bonelessSmrScroll = EditorGUILayout.BeginScrollView(_bonelessSmrScroll, GUILayout.Height(150));
                    foreach (var e in _bonelessSmrEntries)
                    {
                        if (e.Renderer == null) continue;
                        EditorGUILayout.BeginVertical(EditorStyles.helpBox);
                        EditorGUILayout.BeginHorizontal();
                        using (new EditorGUI.DisabledScope(true))
                            EditorGUILayout.ObjectField(e.Renderer, typeof(SkinnedMeshRenderer), true);
                        if (GUILayout.Button("▶", GUILayout.Width(24)))
                        {
                            Selection.activeGameObject = e.Renderer.gameObject;
                            EditorGUIUtility.PingObject(e.Renderer.gameObject);
                        }
                        if (GUILayout.Button("FIX", GUILayout.Width(44))) fixOne = e;
                        EditorGUILayout.EndHorizontal();
                        string parentName = e.Renderer.transform.parent != null ? e.Renderer.transform.parent.name : "(scene root)";
                        EditorGUILayout.LabelField(
                            $"  ⚠ bones {e.BoneCount}개 / bindposes {e.BindposeCount}개  →  '{parentName}' 아래에 본 생성",
                            EditorStyles.miniLabel);
                        EditorGUILayout.EndVertical();
                    }
                    EditorGUILayout.EndScrollView();

                    bool fixAll = GUILayout.Button($"FIX All  ({_bonelessSmrEntries.Count}개)");

                    if (fixOne != null || fixAll)
                    {
                        var targets = fixAll
                            ? _bonelessSmrEntries
                            : new List<YamoAssetCheckerCore.BonelessSmrEntry> { fixOne };
                        int n = YamoAssetCheckerCore.FixBonelessSmrs(targets);
                        Debug.Log($"[AssetChecker] 본 없는 SMR {n}개 수정 완료.");
                        RescanBonelessSmrs();
                        GUIUtility.ExitGUI();
                    }
                }
            }

            EditorGUI.indentLevel--;
        }

        private void RescanBonelessSmrs()
        {
            _bonelessSmrEntries = _bonelessSmrScanSelection != null
                ? YamoAssetCheckerCore.ScanBonelessSmrsInChildren(_bonelessSmrScanSelection)
                : YamoAssetCheckerCore.ScanBonelessSmrsInScene();
            _bonelessSmrScanned = true;
        }

        // ============================================================
        // 공통
        // ============================================================
        private static void DrawSeparator()
        {
            GUILayout.Space(4);
            EditorGUILayout.LabelField(GUIContent.none, GUI.skin.horizontalSlider);
            GUILayout.Space(2);
        }
    }
}
