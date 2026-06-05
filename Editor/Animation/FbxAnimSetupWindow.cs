using System.Collections.Generic;
using System.IO;
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

            var hint = new Label(
                "다음 설정을 FBX Import Settings에 일괄 적용합니다.\n" +
                "  • Anim. Compression → Off\n" +
                "  • 클립 이름 → 파일명과 동일하게\n" +
                "  • Root Transform Rotation : Bake Into Pose ✓ / Based Upon = Original\n" +
                "  • Root Transform Position (Y) : Bake Into Pose ✓ / Based Upon = Original\n" +
                "  • Root Transform Position (XZ) : Bake Into Pose ✓ / Based Upon = Original");
            hint.style.fontSize = 11;
            hint.style.color = new StyleColor(new Color(0.65f, 0.65f, 0.65f));
            hint.style.whiteSpace = WhiteSpace.Normal;
            hint.style.marginBottom = 10;
            section.Add(hint);

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

        // ────────────────────────────── Helpers

        private static VisualElement MakeSection(string sectionTitle)
        {
            var box = new VisualElement();
            box.style.marginTop = 8;
            box.style.paddingTop = 6;
            box.style.paddingBottom = 6;
            box.style.paddingLeft = 8;
            box.style.paddingRight = 8;
            var lbl = new Label(sectionTitle);
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            lbl.style.marginBottom = 4;
            box.Add(lbl);
            return box;
        }

        private void SetStatus(string msg)
        {
            if (_statusLabel != null)
                _statusLabel.text = msg;
        }
    }
}
