using System.Collections.Generic;
using System.IO;
using System.Reflection;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace YAMO.UnityTools.Editor
{
    public class FbxAnimSetupWindow : EditorWindow
    {
        private static FbxAnimSetupWindow _instance;

        private readonly List<Object> _targets = new List<Object>();
        private VisualElement _listContainer;
        private Label _statusLabel;

        // ── Section 2: OptiTrack motion binding ──
        private readonly List<Object> _optiTargets = new List<Object>();
        private VisualElement _optiListContainer;
        private Label _optiStatusLabel;

        // Reflection cache for UnityEditor.AvatarSetupTool.SetupHumanSkeleton (internal).
        private static MethodInfo _setupHumanSkeleton;
        private static bool _reflectionResolved;

        // Reflection cache for the Rig tab "Update" logic
        // (UnityEditor.ModelImporterRigEditor.CopyHumanDescriptionToDestination).
        private static MethodInfo _copyHumanDescription;
        private static bool _copyReflectionResolved;

        // OptiTrack spine template (prefix varies per actor, suffixes are fixed).
        private const string SpineBone      = "_Spine1";
        private const string ChestBone      = "_Spine3";
        private const string UpperChestBone = "_Spine4";

        [MenuItem("Tools/YAMO/Animation/FBX 애니메이션 설정 _9")]
        public static void ToggleWindow()
        {
            if (_instance != null)
            {
                _instance.Close();
                return;
            }
            var w = GetWindow<FbxAnimSetupWindow>("FBX 애니메이션 설정");
            w.minSize = new Vector2(400, 480);
        }

        private void OnEnable()  => _instance = this;
        private void OnDisable() => _instance = null;

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.style.paddingTop = 0;
            root.style.paddingBottom = 0;
            root.style.paddingLeft = 0;
            root.style.paddingRight = 0;

            // Header
            var header = new VisualElement();
            header.style.paddingTop = 8;
            header.style.paddingLeft = 10;
            header.style.paddingRight = 10;
            header.style.borderBottomWidth = 1;
            header.style.borderBottomColor = new StyleColor(new Color(0, 0, 0, 0.2f));
            header.style.marginBottom = 4;
            var title = new Label("FBX 애니메이션 설정");
            title.style.fontSize = 14;
            title.style.unityFontStyleAndWeight = FontStyle.Bold;
            title.style.marginBottom = 2;
            header.Add(title);
            var sub = new Label("압축 Off · 클립명 → 파일명 · Root Bake Into Pose + Based Upon = Original 일괄 적용");
            sub.style.fontSize = 11;
            sub.style.color = new StyleColor(new Color(0.65f, 0.65f, 0.65f));
            sub.style.marginBottom = 6;
            sub.style.whiteSpace = WhiteSpace.Normal;
            header.Add(sub);
            root.Add(header);

            var scroll = new ScrollView { mode = ScrollViewMode.Vertical };
            scroll.style.flexGrow = 1;
            scroll.style.paddingLeft = 10;
            scroll.style.paddingRight = 10;
            root.Add(scroll);

            BuildListSection(scroll);
            BuildRunSection(scroll);
            BuildOptiTrackSection(scroll);
        }

        // ────────────────────────────── Sections

        private void BuildListSection(VisualElement parent)
        {
            var section = MakeSection("대상 FBX 목록");

            var btnRow = new VisualElement();
            btnRow.style.flexDirection = FlexDirection.Row;
            btnRow.style.marginBottom = 6;

            var addBtn = new Button(AddFromSelection) { text = "선택 항목 추가" };
            addBtn.style.flexGrow = 1;
            addBtn.tooltip = "Project 창에서 선택된 FBX 에셋을 목록에 추가합니다.";
            btnRow.Add(addBtn);

            var addFolderBtn = new Button(() => AddFromFolders(_targets, RefreshList, SetStatus))
            { text = "폴더 추가" };
            addFolderBtn.style.marginLeft = 4;
            addFolderBtn.tooltip = "선택(또는 지정)한 폴더 하위의 모든 FBX를 목록에 추가합니다.";
            btnRow.Add(addFolderBtn);

            var clearBtn = new Button(ClearList) { text = "목록 비우기" };
            clearBtn.style.marginLeft = 4;
            btnRow.Add(clearBtn);
            section.Add(btnRow);

            // Manual add row
            var manualRow = new VisualElement();
            manualRow.style.flexDirection = FlexDirection.Row;
            manualRow.style.marginBottom = 6;
            manualRow.style.alignItems = Align.Center;
            var manualField = new ObjectField { objectType = typeof(Object), allowSceneObjects = false };
            manualField.style.flexGrow = 1;
            var manualAddBtn = new Button(() =>
            {
                if (manualField.value != null)
                {
                    bool added = AddAsset(manualField.value);
                    if (added) manualField.value = null;
                    else SetStatus("FBX 파일만 추가할 수 있습니다 (이미 추가됐거나 FBX가 아님).");
                }
            }) { text = "추가" };
            manualAddBtn.style.marginLeft = 4;
            manualRow.Add(manualField);
            manualRow.Add(manualAddBtn);
            section.Add(manualRow);

            _listContainer = new VisualElement();
            _listContainer.style.minHeight = 60;
            section.Add(_listContainer);
            RefreshList();

            parent.Add(section);
        }

        private void BuildRunSection(VisualElement parent)
        {
            var section = MakeSection("작업 내용 및 실행");

            section.Add(MakeReadmeButton());

            var runBtn = new Button(Apply) { text = "설정 적용" };
            runBtn.style.height = 36;
            runBtn.style.fontSize = 13;
            section.Add(runBtn);

            _statusLabel = new Label();
            _statusLabel.style.whiteSpace = WhiteSpace.Normal;
            _statusLabel.style.marginTop = 6;
            _statusLabel.style.fontSize = 11;
            section.Add(_statusLabel);

            parent.Add(section);
        }

        // ────────────────────────────── List management

        private void AddFromSelection()
        {
            int added = 0;
            foreach (var obj in Selection.objects)
            {
                if (AddAsset(obj)) added++;
            }
            SetStatus(added > 0 ? $"{added}개 추가됨." : "선택된 항목 중 추가 가능한 FBX가 없습니다.");
        }

        private bool AddAsset(Object obj)
        {
            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) return false;
            string ext = Path.GetExtension(path).ToLowerInvariant();
            if (ext != ".fbx") return false;
            if (_targets.Contains(obj)) return false;
            _targets.Add(obj);
            RefreshList();
            return true;
        }

        private void ClearList()
        {
            _targets.Clear();
            RefreshList();
            SetStatus("");
        }

        private void RefreshList()
        {
            if (_listContainer == null) return;
            _listContainer.Clear();

            if (_targets.Count == 0)
            {
                var empty = new Label("(목록이 비어 있습니다)");
                empty.style.color = new StyleColor(new Color(0.5f, 0.5f, 0.5f));
                empty.style.unityTextAlign = TextAnchor.MiddleCenter;
                empty.style.paddingTop = 10;
                empty.style.paddingBottom = 10;
                _listContainer.Add(empty);
                return;
            }

            for (int i = 0; i < _targets.Count; i++)
            {
                var obj = _targets[i];
                var capturedObj = obj;

                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.marginBottom = 2;
                row.style.paddingLeft = 6;
                row.style.paddingRight = 6;
                row.style.paddingTop = 3;
                row.style.paddingBottom = 3;
                row.style.backgroundColor = new StyleColor(new Color(0f, 0f, 0f, 0.1f));

                var nameLabel = new Label(obj != null ? obj.name : "(없음)");
                nameLabel.style.flexGrow = 1;
                row.Add(nameLabel);

                string assetPath = obj != null ? AssetDatabase.GetAssetPath(obj) : "";
                var pathLabel = new Label(assetPath);
                pathLabel.style.fontSize = 9;
                pathLabel.style.color = new StyleColor(new Color(0.5f, 0.5f, 0.5f));
                pathLabel.style.flexShrink = 1;
                pathLabel.style.overflow = Overflow.Hidden;
                row.Add(pathLabel);

                var removeBtn = new Button(() =>
                {
                    _targets.Remove(capturedObj);
                    RefreshList();
                }) { text = "✕" };
                removeBtn.style.marginLeft = 6;
                removeBtn.style.paddingLeft = 5;
                removeBtn.style.paddingRight = 5;
                row.Add(removeBtn);

                _listContainer.Add(row);
            }
        }

        // ────────────────────────────── Apply

        private void Apply()
        {
            if (_targets.Count == 0)
            {
                SetStatus("목록이 비어 있습니다.");
                return;
            }

            int success = 0, fail = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                for (int i = 0; i < _targets.Count; i++)
                {
                    var obj = _targets[i];
                    if (obj == null) { fail++; continue; }
                    string path = AssetDatabase.GetAssetPath(obj);
                    EditorUtility.DisplayProgressBar("FBX 애니메이션 설정 적용", $"{obj.name} 처리 중...", (float)i / _targets.Count);
                    if (ProcessFbx(path)) success++;
                    else fail++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
            }

            SetStatus(fail == 0
                ? $"완료: {success}개 처리됨."
                : $"완료: {success}개 성공, {fail}개 실패.");
        }

        private static bool ProcessFbx(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null) return false;

            string fileName = Path.GetFileNameWithoutExtension(assetPath);

            importer.animationCompression = ModelImporterAnimationCompression.Off;

            // If no custom clips are defined yet, seed from the auto-generated defaults.
            var clips = importer.clipAnimations;
            if (clips == null || clips.Length == 0)
                clips = importer.defaultClipAnimations;

            if (clips != null && clips.Length > 0)
            {
                // Rename single-clip FBX to file name; multi-clip keeps existing names.
                if (clips.Length == 1)
                    clips[0].name = fileName;

                foreach (var clip in clips)
                {
                    clip.lockRootRotation = true;
                    clip.keepOriginalOrientation = true;

                    clip.lockRootHeightY = true;
                    clip.keepOriginalPositionY = true;

                    clip.lockRootPositionXZ = true;
                    clip.keepOriginalPositionXZ = true;
                }

                importer.clipAnimations = clips;
            }

            importer.SaveAndReimport();
            return true;
        }

        // ══════════════════════════════ Section 2 : OptiTrack motion binding

        private void BuildOptiTrackSection(VisualElement parent)
        {
            var section = MakeSection("옵티트랙 모션 바인딩 도구");

            section.Add(MakeReadmeButton());

            var btnRow = new VisualElement();
            btnRow.style.flexDirection = FlexDirection.Row;
            btnRow.style.marginBottom = 6;
            var addBtn = new Button(OptiAddFromSelection) { text = "선택 항목 추가" };
            addBtn.style.flexGrow = 1;
            addBtn.tooltip = "Project 창에서 선택된 FBX 에셋을 목록에 추가합니다.";
            btnRow.Add(addBtn);
            var addFolderBtn = new Button(() => AddFromFolders(_optiTargets, OptiRefreshList, OptiSetStatus))
            { text = "폴더 추가" };
            addFolderBtn.style.marginLeft = 4;
            addFolderBtn.tooltip = "선택(또는 지정)한 폴더 하위의 모든 FBX를 목록에 추가합니다.";
            btnRow.Add(addFolderBtn);
            var clearBtn = new Button(OptiClearList) { text = "목록 비우기" };
            clearBtn.style.marginLeft = 4;
            btnRow.Add(clearBtn);
            section.Add(btnRow);

            _optiListContainer = new VisualElement();
            _optiListContainer.style.minHeight = 60;
            section.Add(_optiListContainer);
            OptiRefreshList();

            var runBtn = new Button(OptiApply) { text = "바인딩 실행" };
            runBtn.style.height = 36;
            runBtn.style.fontSize = 13;
            runBtn.style.marginTop = 6;
            section.Add(runBtn);

            _optiStatusLabel = new Label();
            _optiStatusLabel.style.whiteSpace = WhiteSpace.Normal;
            _optiStatusLabel.style.marginTop = 6;
            _optiStatusLabel.style.fontSize = 11;
            section.Add(_optiStatusLabel);

            parent.Add(section);
        }

        // ────────────────────────────── Section 2 list management

        private void OptiAddFromSelection()
        {
            int added = 0;
            foreach (var obj in Selection.objects)
                if (TryAddFbx(_optiTargets, obj)) added++;
            OptiRefreshList();
            OptiSetStatus(added > 0 ? $"{added}개 추가됨." : "선택된 항목 중 추가 가능한 FBX가 없습니다.");
        }

        private void OptiClearList()
        {
            _optiTargets.Clear();
            OptiRefreshList();
            OptiSetStatus("");
        }

        private void OptiRefreshList() => RenderTargetList(_optiListContainer, _optiTargets, OptiRefreshList);

        private void OptiSetStatus(string msg)
        {
            if (_optiStatusLabel != null) _optiStatusLabel.text = msg;
        }

        // ────────────────────────────── Section 2 run

        private void OptiApply()
        {
            if (_optiTargets.Count == 0)
            {
                OptiSetStatus("목록이 비어 있습니다.");
                return;
            }

            // NOTE: this pipeline relies on synchronous re-imports mid-process
            // (Import Animation off → capture T-pose → on), so it must NOT run
            // inside AssetDatabase.StartAssetEditing (which defers imports).
            int ok = 0, fail = 0;
            var notes = new List<string>();
            try
            {
                for (int i = 0; i < _optiTargets.Count; i++)
                {
                    var obj = _optiTargets[i];
                    if (obj == null) { fail++; continue; }
                    string path = AssetDatabase.GetAssetPath(obj);
                    EditorUtility.DisplayProgressBar("옵티트랙 모션 바인딩",
                        $"{obj.name} 처리 중...", (float)i / _optiTargets.Count);
                    try
                    {
                        if (ProcessOptiTrack(path, out string note)) ok++;
                        else fail++;
                        if (!string.IsNullOrEmpty(note)) notes.Add(note);
                    }
                    catch (System.Exception e)
                    {
                        fail++;
                        notes.Add($"{obj.name}: 예외 - {e.Message}");
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            string summary = fail == 0 ? $"완료: {ok}개 처리됨." : $"완료: {ok}개 성공, {fail}개 실패.";
            if (notes.Count > 0) summary += "\n" + string.Join("\n", notes);
            OptiSetStatus(summary);
        }

        /// <summary>
        /// Full OptiTrack binding pipeline for a single motion FBX (steps 1–8).
        /// </summary>
        private static bool ProcessOptiTrack(string path, out string note)
        {
            note = "";
            var imp = AssetImporter.GetAtPath(path) as ModelImporter;
            if (imp == null) { note = $"{path}: ModelImporter 아님"; return false; }

            // Step 1 — resolve animation name (clip name minus fixed "_FBX" suffix).
            string animName = SanitizeFileName(ResolveAnimName(imp));
            if (string.IsNullOrEmpty(animName)) { note = $"{path}: 애니메이션 이름을 찾을 수 없음"; return false; }

            string dir = Path.GetDirectoryName(path).Replace('\\', '/');

            // Step 1 — rename the motion asset to the animation name.
            string motionPath = $"{dir}/{animName}.fbx";
            if (!PathsEqual(path, motionPath))
            {
                if (AssetDatabase.LoadMainAssetAtPath(motionPath) != null)
                    AssetDatabase.DeleteAsset(motionPath);          // overwrite
                string err = AssetDatabase.RenameAsset(path, animName);
                if (!string.IsNullOrEmpty(err)) { note = $"{path}: 리네임 실패 - {err}"; return false; }
                path = motionPath;
                imp = AssetImporter.GetAtPath(path) as ModelImporter;
            }

            // Seed the clip list from the raw take BEFORE switching avatar mode.
            var clips = imp.clipAnimations;
            if (clips == null || clips.Length == 0) clips = imp.defaultClipAnimations;

            // Step 2 — copy to "{name}_T.fbx" (overwrite if present).
            string tPath = $"{dir}/{animName}_T.fbx";
            if (AssetDatabase.LoadMainAssetAtPath(tPath) != null)
                AssetDatabase.DeleteAsset(tPath);
            if (!AssetDatabase.CopyAsset(path, tPath)) { note = $"{path}: _T 복사 실패"; return false; }

            // Steps 3–6 — build the T-pose humanoid avatar on the copy.
            if (!BuildTPoseAvatar(tPath, out Avatar tAvatar, out note)) return false;

            // Step 7 — motion copies the T-pose avatar; Step 8 — clip settings.
            imp.animationType = ModelImporterAnimationType.Human;
            imp.importAnimation = true;
            imp.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            imp.sourceAvatar = tAvatar;

            imp.animationCompression = ModelImporterAnimationCompression.Off;
            if (clips != null && clips.Length > 0)
            {
                if (clips.Length == 1) clips[0].name = animName;
                foreach (var clip in clips)
                {
                    clip.lockRootRotation = true;
                    clip.keepOriginalOrientation = true;
                    clip.lockRootHeightY = true;
                    clip.keepOriginalPositionY = true;
                    clip.lockRootPositionXZ = true;
                    clip.keepOriginalPositionXZ = true;
                }
                imp.clipAnimations = clips;
            }

            imp.SaveAndReimport();

            // Replicate the Rig tab "Update" button. Assigning sourceAvatar alone
            // leaves the copied-avatar rig "out of date" (humanDescription mismatch),
            // which surfaces as "Error(s) found while importing rig in this animation
            // file". Copying the source's human description makes them match.
            var tImp = AssetImporter.GetAtPath(tPath) as ModelImporter;
            if (tImp != null && TryCopyHumanDescription(tImp, imp))
                imp.SaveAndReimport();
            else
                note = $"{path}: T 아바타 자동 동기화 실패 - Rig 탭에서 Update를 눌러주세요.";

            return true;
        }

        /// <summary>
        /// Copies the source model's human description into the destination model
        /// via UnityEditor.ModelImporterRigEditor.CopyHumanDescriptionToDestination
        /// (internal, reached by reflection) — the same operation the Rig tab's
        /// "Update" button performs for Copy-From-Other-Avatar rigs.
        /// </summary>
        private static bool TryCopyHumanDescription(ModelImporter source, ModelImporter dest)
        {
            if (!_copyReflectionResolved)
            {
                _copyReflectionResolved = true;
                var rigEditor = typeof(UnityEditor.Editor).Assembly
                    .GetType("UnityEditor.ModelImporterRigEditor");
                if (rigEditor != null)
                    _copyHumanDescription = rigEditor.GetMethod("CopyHumanDescriptionToDestination",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }
            if (_copyHumanDescription == null) return false;

            var srcSO = new SerializedObject(source);
            var dstSO = new SerializedObject(dest);
            _copyHumanDescription.Invoke(null, new object[] { srcSO, dstSO });
            dstSO.ApplyModifiedProperties();
            return true;
        }

        /// <summary>
        /// Configures the "_T" copy as a Humanoid avatar built from the true
        /// bind/T-pose (obtained by importing without animation), applies the
        /// OptiTrack spine re-mapping, and strips eye/jaw bones. Returns the
        /// generated Avatar sub-asset.
        /// </summary>
        private static bool BuildTPoseAvatar(string tPath, out Avatar avatar, out string note)
        {
            avatar = null;
            note = "";
            var tImp = AssetImporter.GetAtPath(tPath) as ModelImporter;
            if (tImp == null) { note = $"{tPath}: T ModelImporter 아님"; return false; }

            // Steps 3–4 — Humanoid, Create From This Model, no animation (pure T-pose).
            tImp.animationType = ModelImporterAnimationType.Human;
            tImp.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            tImp.importAnimation = false;
            tImp.SaveAndReimport();

            var tModel = AssetDatabase.LoadAssetAtPath<GameObject>(tPath);
            if (tModel == null) { note = $"{tPath}: T 모델 로드 실패"; return false; }

            if (!TryCaptureHumanoid(tModel, out var human, out var skeleton, out bool dof))
            { note = $"{tPath}: 휴머노이드 매핑 캡처 실패 (AvatarSetupTool 접근 불가)"; return false; }

            // Detect the per-actor prefix from the Hips bone (e.g. "001_Hips" → "001").
            string prefix = null;
            foreach (var h in human)
                if (h.humanName == "Hips" && !string.IsNullOrEmpty(h.boneName) && h.boneName.EndsWith("_Hips"))
                { prefix = h.boneName.Substring(0, h.boneName.Length - "_Hips".Length); break; }
            if (string.IsNullOrEmpty(prefix)) { note = $"{tPath}: Hips 본에서 접두사 탐지 실패"; return false; }

            // Steps 5–6 — override spine chain, strip eyes/jaw.
            var remapped = new List<HumanBone>(human.Length);
            foreach (var h in human)
            {
                if (h.humanName == "LeftEye" || h.humanName == "RightEye" || h.humanName == "Jaw")
                    continue;                                   // Step 6: must stay unmapped
                var hb = h;
                if (h.humanName == "Spine") hb.boneName = prefix + SpineBone;
                else if (h.humanName == "Chest") hb.boneName = prefix + ChestBone;
                else if (h.humanName == "UpperChest") hb.boneName = prefix + UpperChestBone;
                remapped.Add(hb);
            }

            var hd = tImp.humanDescription;
            hd.human = remapped.ToArray();
            hd.skeleton = skeleton;
            hd.hasTranslationDoF = dof;
            tImp.humanDescription = hd;
            tImp.SaveAndReimport();

            foreach (var o in AssetDatabase.LoadAllAssetsAtPath(tPath))
                if (o is Avatar a) { avatar = a; break; }
            if (avatar == null || !avatar.isValid) { note = $"{tPath}: T 아바타 생성 실패/무효"; return false; }
            return true;
        }

        /// <summary>
        /// Captures the auto-generated humanoid mapping + skeleton pose from a
        /// model via UnityEditor.AvatarSetupTool.SetupHumanSkeleton (internal,
        /// reached by reflection). The model must currently be in its T-pose.
        /// </summary>
        private static bool TryCaptureHumanoid(GameObject model, out HumanBone[] human,
                                               out SkeletonBone[] skeleton, out bool hasTranslationDoF)
        {
            human = null; skeleton = null; hasTranslationDoF = false;

            if (!_reflectionResolved)
            {
                _reflectionResolved = true;
                var astType = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.AvatarSetupTool");
                if (astType != null)
                    _setupHumanSkeleton = astType.GetMethod("SetupHumanSkeleton",
                        BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }
            if (_setupHumanSkeleton == null) return false;

            var args = new object[] { model, null, null, false };
            _setupHumanSkeleton.Invoke(null, args);
            human = args[1] as HumanBone[];
            skeleton = args[2] as SkeletonBone[];
            hasTranslationDoF = args[3] is bool b && b;
            return human != null && human.Length > 0 && skeleton != null && skeleton.Length > 0;
        }

        // ────────────────────────────── Section 2 helpers

        private static string ResolveAnimName(ModelImporter imp)
        {
            string raw = null;
            var ca = imp.clipAnimations;
            if (ca != null && ca.Length > 0) raw = ca[0].name;
            if (string.IsNullOrEmpty(raw))
            {
                var dca = imp.defaultClipAnimations;
                if (dca != null && dca.Length > 0) raw = dca[0].name;
            }
            if (string.IsNullOrEmpty(raw)) return null;

            // Strip the fixed trailing "_FBX" suffix (case-insensitive).
            if (raw.Length >= 4 && raw.Substring(raw.Length - 4).Equals("_FBX", System.StringComparison.OrdinalIgnoreCase))
                raw = raw.Substring(0, raw.Length - 4);
            return raw.Trim();
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name.Trim();
        }

        private static bool PathsEqual(string a, string b) =>
            string.Equals(a.Replace('\\', '/'), b.Replace('\\', '/'), System.StringComparison.OrdinalIgnoreCase);

        // ────────────────────────────── Helpers

        // Collapsible section: header click folds/unfolds the content.
        private static Foldout MakeSection(string sectionTitle)
        {
            var fold = new Foldout { text = sectionTitle, value = true };
            fold.style.marginTop = 8;
            fold.style.paddingTop = 4;
            fold.style.paddingBottom = 6;
            fold.style.paddingRight = 8;

            // Bold the header label.
            var headerLabel = fold.Q<Toggle>()?.Q<Label>();
            if (headerLabel != null) headerLabel.style.unityFontStyleAndWeight = FontStyle.Bold;

            return fold;
        }

        // Small grey link-style button that opens the README doc.
        private Button MakeReadmeButton()
        {
            var btn = new Button(OpenReadme) { text = "📖 사용법 (README 열기)" };
            btn.style.marginBottom = 8;
            btn.style.alignSelf = Align.FlexStart;
            btn.tooltip = "이 도구의 상세 설명 문서를 엽니다.";
            return btn;
        }

        private void OpenReadme()
        {
            const string readmePath =
                "Packages/com.yamo.unitytools/Editor/Animation/FbxAnimSetup_README.md";
            var asset = AssetDatabase.LoadAssetAtPath<Object>(readmePath);
            if (asset != null) { AssetDatabase.OpenAsset(asset); return; }

            string abs = Path.GetFullPath(readmePath);
            if (File.Exists(abs)) Application.OpenURL("file:///" + abs.Replace('\\', '/'));
            else Debug.LogWarning($"[FbxAnimSetup] README를 찾을 수 없습니다: {readmePath}");
        }

        private void SetStatus(string msg)
        {
            if (_statusLabel != null)
                _statusLabel.text = msg;
        }

        // Shared list helpers (used by section 2; also generic enough for reuse).

        private static bool TryAddFbx(List<Object> targets, Object obj)
        {
            string path = AssetDatabase.GetAssetPath(obj);
            if (string.IsNullOrEmpty(path)) return false;
            if (Path.GetExtension(path).ToLowerInvariant() != ".fbx") return false;
            if (targets.Contains(obj)) return false;
            targets.Add(obj);
            return true;
        }

        // Adds every FBX under the chosen folder(s). Uses folders selected in the
        // Project window; if none are selected, opens a folder-picker dialog.
        private static void AddFromFolders(List<Object> targets, System.Action onChanged,
                                           System.Action<string> setStatus)
        {
            var folders = new List<string>();
            foreach (var o in Selection.objects)
            {
                string p = AssetDatabase.GetAssetPath(o);
                if (!string.IsNullOrEmpty(p) && AssetDatabase.IsValidFolder(p)) folders.Add(p);
            }

            if (folders.Count == 0)
            {
                string abs = EditorUtility.OpenFolderPanel("FBX를 추가할 폴더 선택", Application.dataPath, "");
                if (string.IsNullOrEmpty(abs)) return;
                string rel = AbsoluteToProjectPath(abs);
                if (string.IsNullOrEmpty(rel))
                {
                    setStatus?.Invoke("프로젝트(Assets/Packages) 내부 폴더만 추가할 수 있습니다.");
                    return;
                }
                folders.Add(rel);
            }

            int added = 0;
            foreach (var guid in AssetDatabase.FindAssets("t:GameObject", folders.ToArray()))
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (Path.GetExtension(path).ToLowerInvariant() != ".fbx") continue;
                var obj = AssetDatabase.LoadMainAssetAtPath(path);
                if (obj != null && TryAddFbx(targets, obj)) added++;
            }

            onChanged?.Invoke();
            setStatus?.Invoke(added > 0
                ? $"폴더에서 {added}개 추가됨."
                : "폴더 하위에서 추가할 FBX를 찾지 못했습니다.");
        }

        // Converts an absolute filesystem path to a project-relative (Assets/… or
        // Packages/…) path, or null if it lies outside the project.
        private static string AbsoluteToProjectPath(string abs)
        {
            abs = abs.Replace('\\', '/');
            string dataPath = Application.dataPath.Replace('\\', '/');   // <project>/Assets
            if (abs == dataPath) return "Assets";
            if (abs.StartsWith(dataPath + "/")) return "Assets" + abs.Substring(dataPath.Length);

            string projRoot = dataPath.Substring(0, dataPath.Length - "/Assets".Length);
            string packages = projRoot + "/Packages";
            if (abs == packages) return "Packages";
            if (abs.StartsWith(packages + "/")) return "Packages" + abs.Substring(packages.Length);

            return null;
        }

        private static void RenderTargetList(VisualElement container, List<Object> targets, System.Action onChanged)
        {
            if (container == null) return;
            container.Clear();

            if (targets.Count == 0)
            {
                var empty = new Label("(목록이 비어 있습니다)");
                empty.style.color = new StyleColor(new Color(0.5f, 0.5f, 0.5f));
                empty.style.unityTextAlign = TextAnchor.MiddleCenter;
                empty.style.paddingTop = 10;
                empty.style.paddingBottom = 10;
                container.Add(empty);
                return;
            }

            foreach (var obj in targets)
            {
                var capturedObj = obj;

                var row = new VisualElement();
                row.style.flexDirection = FlexDirection.Row;
                row.style.alignItems = Align.Center;
                row.style.marginBottom = 2;
                row.style.paddingLeft = 6;
                row.style.paddingRight = 6;
                row.style.paddingTop = 3;
                row.style.paddingBottom = 3;
                row.style.backgroundColor = new StyleColor(new Color(0f, 0f, 0f, 0.1f));

                var nameLabel = new Label(obj != null ? obj.name : "(없음)");
                nameLabel.style.flexGrow = 1;
                row.Add(nameLabel);

                var pathLabel = new Label(obj != null ? AssetDatabase.GetAssetPath(obj) : "");
                pathLabel.style.fontSize = 9;
                pathLabel.style.color = new StyleColor(new Color(0.5f, 0.5f, 0.5f));
                pathLabel.style.flexShrink = 1;
                pathLabel.style.overflow = Overflow.Hidden;
                row.Add(pathLabel);

                var removeBtn = new Button(() =>
                {
                    targets.Remove(capturedObj);
                    onChanged?.Invoke();
                }) { text = "✕" };
                removeBtn.style.marginLeft = 6;
                removeBtn.style.paddingLeft = 5;
                removeBtn.style.paddingRight = 5;
                row.Add(removeBtn);

                container.Add(row);
            }
        }
    }
}
