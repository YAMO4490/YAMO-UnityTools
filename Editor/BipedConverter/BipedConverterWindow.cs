using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    public class BipedConverterWindow : EditorWindow
    {
        [MenuItem("Tools/YAMO/Biped Converter")]
        public static void Open()
        {
            var win = GetWindow<BipedConverterWindow>("Biped Converter");
            win.minSize = new Vector2(380, 360);
            win.Show();
        }

        GameObject _source;
        Vector2 _scroll;
        string _resultText = "";
        MessageType _resultLevel = MessageType.Info;

        string[] _templatePaths = new string[0];
        string[] _templateNames = new string[0];
        int _selectedTemplateIndex = 0;

        GameObject _customTemplate;
        string _customTemplateWarning = "";

        void OnEnable() => RefreshTemplates();
        void OnFocus() => RefreshTemplates();

        void RefreshTemplates()
        {
            var folder = BipedConverter.TemplatesFolder;
            if (!Directory.Exists(folder))
            {
                _templatePaths = new string[0];
                _templateNames = new string[0];
                return;
            }

            var files = Directory.GetFiles(folder, "*.fbx", SearchOption.TopDirectoryOnly);
            _templatePaths = files
                .Select(f => f.Replace('\\', '/'))
                .OrderBy(p => p)
                .ToArray();
            _templateNames = _templatePaths
                .Select(p => Path.GetFileNameWithoutExtension(p))
                .ToArray();

            if (_selectedTemplateIndex >= _templatePaths.Length)
                _selectedTemplateIndex = 0;
        }

        private void OnGUI() => DrawGUI();

        public void DrawGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("원본 Armature 루트", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _source = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Armature Root"), _source, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck())
            {
                _resultText = "";
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Biped 템플릿", EditorStyles.boldLabel);

            if (_templatePaths.Length == 0 && _customTemplate == null)
            {
                EditorGUILayout.HelpBox(
                    $"템플릿 FBX가 없습니다.\n다음 폴더에 캐릭터별 Biped 템플릿 FBX를 추가하거나,\n[불러오기]로 직접 선택하세요.",
                    MessageType.Warning);
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("새로고침"))
                        RefreshTemplates();
                    if (GUILayout.Button("불러오기…"))
                        OpenTemplatePicker();
                }
            }
            else
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    using (new EditorGUI.DisabledScope(_customTemplate != null || _templatePaths.Length == 0))
                    {
                        if (_templatePaths.Length > 0)
                            _selectedTemplateIndex = EditorGUILayout.Popup(
                                new GUIContent("템플릿"), _selectedTemplateIndex, _templateNames);
                        else
                            EditorGUILayout.Popup(new GUIContent("템플릿"), 0, new[] { "—" });
                    }
                    if (GUILayout.Button("…", GUILayout.Width(28)))
                        OpenTemplatePicker();
                }

                if (_customTemplate != null)
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        using (new EditorGUI.DisabledScope(true))
                        {
                            EditorGUILayout.ObjectField(
                                new GUIContent("커스텀 템플릿"), _customTemplate, typeof(GameObject), false);
                        }
                        if (GUILayout.Button("✕", GUILayout.Width(22)))
                        {
                            _customTemplate = null;
                            _customTemplateWarning = "";
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(_customTemplateWarning))
            {
                EditorGUILayout.HelpBox(_customTemplateWarning, MessageType.Warning);
            }

            EditorGUILayout.Space(8);

            bool canRun = _source != null && (_templatePaths.Length > 0 || _customTemplate != null);
            using (new EditorGUI.DisabledScope(!canRun))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("검사", GUILayout.Height(30)))
                    {
                        RunValidate();
                    }
                    if (GUILayout.Button("생성", GUILayout.Height(30)))
                    {
                        RunConvert();
                    }
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("결과", EditorStyles.boldLabel);

            if (string.IsNullOrEmpty(_resultText))
            {
                EditorGUILayout.HelpBox(
                    "Armature 루트와 템플릿을 지정한 뒤\n[검사]로 본 매칭 상태를 확인하거나 [생성]을 누르세요.",
                    MessageType.None);
            }
            else
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                EditorGUILayout.HelpBox(_resultText, _resultLevel);
                EditorGUILayout.EndScrollView();
            }
        }

        void OpenTemplatePicker()
        {
            string startDir;
            try
            {
                startDir = Path.GetFullPath(BipedConverter.TemplatesFolder);
                if (!Directory.Exists(startDir))
                    startDir = Application.dataPath;
            }
            catch
            {
                startDir = Application.dataPath;
            }

            string absolutePath = EditorUtility.OpenFilePanelWithFilters(
                "Biped 템플릿 선택", startDir,
                new[] { "Biped 템플릿 (FBX/Prefab)", "fbx,prefab", "모든 파일", "*" });
            if (string.IsNullOrEmpty(absolutePath)) return;

            absolutePath = absolutePath.Replace('\\', '/');
            string projectRoot = Path.GetDirectoryName(Application.dataPath).Replace('\\', '/');

            string relativePath = null;
            if (absolutePath.StartsWith(projectRoot + "/"))
                relativePath = absolutePath.Substring(projectRoot.Length + 1);

            if (string.IsNullOrEmpty(relativePath))
            {
                _customTemplate = null;
                _customTemplateWarning =
                    "프로젝트 외부 파일은 사용할 수 없습니다.\n현재 Unity 프로젝트 폴더 안의 FBX/Prefab을 선택하세요.";
                Repaint();
                return;
            }

            var asset = AssetDatabase.LoadAssetAtPath<GameObject>(relativePath);
            if (asset == null)
            {
                _customTemplate = null;
                _customTemplateWarning = $"에셋을 불러올 수 없습니다:\n{relativePath}";
                Repaint();
                return;
            }

            string warning = BipedConverter.ValidateBipedTemplate(asset);
            if (warning != null)
            {
                _customTemplate = null;
                _customTemplateWarning = $"'{asset.name}': {warning}";
            }
            else
            {
                _customTemplate = asset;
                _customTemplateWarning = "";
            }
            Repaint();
        }

        void RunValidate()
        {
            var report = BipedConverter.Validate(_source);
            _resultText = report.ToText();
            if (!report.IsValid)
                _resultLevel = MessageType.Error;
            else if (report.Warnings.Count > 0)
                _resultLevel = MessageType.Warning;
            else
                _resultLevel = MessageType.Info;
        }

        void RunConvert()
        {
            var report = BipedConverter.Validate(_source);
            if (!report.IsValid)
            {
                _resultLevel = MessageType.Error;
                _resultText = "변환 불가\n\n" + report.ToText();
                return;
            }

            string templatePath;
            string templateName;
            if (_customTemplate != null)
            {
                templatePath = AssetDatabase.GetAssetPath(_customTemplate);
                templateName = _customTemplate.name;
            }
            else
            {
                templatePath = _templatePaths[_selectedTemplateIndex];
                templateName = _templateNames[_selectedTemplateIndex];
            }

            Undo.SetCurrentGroupName($"Convert {_source.name} to Biped ({templateName})");
            int g = Undo.GetCurrentGroup();
            var bipedRoot = BipedConverter.Convert(_source, templatePath);
            Undo.CollapseUndoOperations(g);

            if (bipedRoot != null)
            {
                Selection.activeGameObject = bipedRoot;
                EditorGUIUtility.PingObject(bipedRoot);
                _resultLevel = report.Warnings.Count > 0 ? MessageType.Warning : MessageType.Info;
                _resultText =
                    $"✓ 변환 완료 (템플릿: {templateName})\n" +
                    $"  • 작업본: '{bipedRoot.name}' (원본은 그대로 보존됨)\n" +
                    $"  • 작업본 안에 Biped 본 구조 적용, 원본 Armature/Animator 정리됨\n\n" +
                    report.ToText();
            }
            else
            {
                _resultLevel = MessageType.Error;
                _resultText = "변환 도중 오류 발생. 콘솔을 확인하세요.";
            }
        }
    }
}
