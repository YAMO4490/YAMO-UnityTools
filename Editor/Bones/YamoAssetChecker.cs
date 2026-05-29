// YamoAssetChecker — 아바타/씬 자산을 점검·정리하는 통합 EditorWindow.
//
// 섹션 구성 (각 섹션은 폴드아웃):
//   1) Object Name Tools        — 일괄 이름 변경, 자식 정렬, 휴머노이드 스케일 점검
//   2) Duplicate Names          — 중복 이름 검출 + 자동 리네임
//   3) Unused Bones             — 사용되지 않는 본 검출 (선택 객체 하위)
//   4) Missing / Disabled Scripts — 미싱·비활성 MonoBehaviour 정리
//   5) Missing Bones            — SkinnedMeshRenderer.bones 의 null 검사
//   6) Humanoid Bone Extractor  — 아바타 복제 후 휴머노이드 본만 남겨 스켈레톤 추출
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
        // ---- 윈도우 전체 스크롤 ----
        private Vector2 _windowScroll;

        // ---- 섹션 폴드아웃 상태 ----
        private bool _foldNameTools         = true;
        private bool _foldDuplicates        = true;
        private bool _foldUnusedBones       = true;
        private bool _foldMissingScripts    = true;
        private bool _foldMissingBones      = true;
        private bool _foldHumanoidExtractor = true;

        // ---- 섹션 1: Object Name Tools ----
        private string _prefixText = "";
        private string _suffixText = "";
        private List<Transform> _invalidScaleBones = new List<Transform>();
        private Vector2 _invalidScaleScroll;
        private bool _invalidScaleSearched = false;

        // ---- 섹션 2: Duplicate Names ----
        private GameObject _duplicateRoot;
        private Dictionary<string, List<Transform>> _duplicateGroups = new Dictionary<string, List<Transform>>();
        private Vector2 _duplicateScroll;
        private bool _duplicateSearched = false;

        // ---- 섹션 3: Unused Bones ----
        private List<string> _excludeStrings = new List<string>();
        private bool _excludeMagicaColliders = true;
        private bool _excludeVRMSpringBones  = true;

        // ---- 섹션 4: Missing / Disabled Scripts ----
        private GameObject _scriptTargetObject;

        // ---- 섹션 5: Missing Bones ----
        private List<YamoAssetCheckerCore.MissingBoneResult> _missingBoneResults = new List<YamoAssetCheckerCore.MissingBoneResult>();
        private Vector2 _missingBoneScroll;
        private bool _missingBoneSearched = false;

        // ---- 섹션 6: Humanoid Bone Extractor ----
        // EditorWindow 인스턴스를 임베드해 자체 상태(소스/매핑)를 보유.
        private HumanoidBoneExtractorWindow _humanoidExtractorInstance;

        // ---- 섹션 7: Smart Empty Object Cleaner ----
        private bool _foldEmptyCleaner = true;

        // ---- 섹션 8: Inactive Object Finder ----
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
            _windowScroll = EditorGUILayout.BeginScrollView(_windowScroll);

            EditorGUILayout.LabelField("YAMO Asset Checker", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "아바타/씬의 이름·본·스크립트 상태를 점검하고 정리합니다.\n각 섹션을 펼쳐서 사용하세요.",
                MessageType.Info);

            DrawNameToolsSection();
            DrawDuplicatesSection();
            DrawUnusedBonesSection();
            DrawMissingScriptsSection();
            DrawMissingBonesSection();
            DrawHumanoidExtractorSection();
            DrawSmartEmptyCleanerSection();
            DrawInactiveObjectsSection();

            EditorGUILayout.EndScrollView();
        }

        // ============================================================
        // 섹션 1: Object Name Tools
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
        // 섹션 2: Duplicate Names
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
        // 섹션 3: Unused Bones
        // ============================================================
        private void DrawUnusedBonesSection()
        {
            DrawSeparator();
            _foldUnusedBones = EditorGUILayout.Foldout(_foldUnusedBones, "3. Unused Bones  (Selection 기반)", true, EditorStyles.foldoutHeader);
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
        // 섹션 4: Missing / Disabled Scripts
        // ============================================================
        private void DrawMissingScriptsSection()
        {
            DrawSeparator();
            _foldMissingScripts = EditorGUILayout.Foldout(_foldMissingScripts, "4. Missing / Disabled Scripts", true, EditorStyles.foldoutHeader);
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
        // 섹션 5: Missing Bones (SkinnedMeshRenderer)
        // ============================================================
        private void DrawMissingBonesSection()
        {
            DrawSeparator();
            _foldMissingBones = EditorGUILayout.Foldout(_foldMissingBones, "5. Missing Bones in SkinnedMeshRenderer", true, EditorStyles.foldoutHeader);
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
        // 섹션 6: Humanoid Bone Extractor
        // ============================================================
        private void DrawHumanoidExtractorSection()
        {
            DrawSeparator();
            _foldHumanoidExtractor = EditorGUILayout.Foldout(_foldHumanoidExtractor, "6. Humanoid Bone Extractor", true, EditorStyles.foldoutHeader);
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
        // 섹션 7: Smart Empty Object Cleaner
        // ============================================================
        private void DrawSmartEmptyCleanerSection()
        {
            DrawSeparator();
            _foldEmptyCleaner = EditorGUILayout.Foldout(_foldEmptyCleaner, "7. Smart Empty Object Cleaner", true, EditorStyles.foldoutHeader);
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
        // 섹션 8: Inactive Object Finder
        // ============================================================
        private void DrawInactiveObjectsSection()
        {
            DrawSeparator();
            _foldInactiveObjects = EditorGUILayout.Foldout(_foldInactiveObjects, "8. Inactive Object Finder", true, EditorStyles.foldoutHeader);
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
