using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    public class BipedDeconverterWindow : EditorWindow
    {
        [MenuItem("Tools/YAMO/Biped/Biped Deconverter")]
        public static void Open()
        {
            var win = GetWindow<BipedDeconverterWindow>("Biped Deconverter");
            win.minSize = new Vector2(380, 280);
            win.Show();
        }

        GameObject _source;
        Vector2 _scroll;
        string _resultText = "";
        MessageType _resultLevel = MessageType.Info;

        private void OnGUI() => DrawGUI();

        public void DrawGUI()
        {
            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Biped → Unity 휴머노이드 역변환", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "BipedConverter로 변환된 Biped 계층 구조를 Unity 정규 휴머노이드 본 계층으로 되돌립니다.\n" +
                "원본은 그대로 유지되고 '_Deconverted' 접미사가 붙은 복사본에 작업이 수행됩니다.",
                MessageType.None);

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("Armature 루트 (Bip001 포함)", EditorStyles.boldLabel);

            EditorGUI.BeginChangeCheck();
            _source = (GameObject)EditorGUILayout.ObjectField(
                new GUIContent("Armature Root"), _source, typeof(GameObject), true);
            if (EditorGUI.EndChangeCheck())
                _resultText = "";

            EditorGUILayout.Space(8);

            using (new EditorGUI.DisabledScope(_source == null))
            {
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("검사", GUILayout.Height(30)))
                        RunValidate();
                    if (GUILayout.Button("역변환 실행", GUILayout.Height(30)))
                        RunDeconvert();
                }
            }

            EditorGUILayout.Space(8);
            EditorGUILayout.LabelField("결과", EditorStyles.boldLabel);

            if (string.IsNullOrEmpty(_resultText))
            {
                EditorGUILayout.HelpBox(
                    "Armature 루트를 지정한 뒤 [검사]로 상태를 확인하거나 [역변환 실행]을 누르세요.",
                    MessageType.None);
            }
            else
            {
                _scroll = EditorGUILayout.BeginScrollView(_scroll);
                EditorGUILayout.HelpBox(_resultText, _resultLevel);
                EditorGUILayout.EndScrollView();
            }
        }

        void RunValidate()
        {
            var report = BipedDeconverter.Validate(_source);
            _resultText  = report.ToText();
            _resultLevel = !report.IsValid       ? MessageType.Error
                         : report.Warnings.Count > 0 ? MessageType.Warning
                         : MessageType.Info;
        }

        void RunDeconvert()
        {
            var report = BipedDeconverter.Validate(_source);
            if (!report.IsValid)
            {
                _resultLevel = MessageType.Error;
                _resultText  = "역변환 불가\n\n" + report.ToText();
                return;
            }

            Undo.SetCurrentGroupName($"Biped Deconvert: {_source.name}");
            int g      = Undo.GetCurrentGroup();
            var result = BipedDeconverter.Deconvert(_source);
            Undo.CollapseUndoOperations(g);

            if (result != null)
            {
                Selection.activeGameObject = result;
                EditorGUIUtility.PingObject(result);
                _resultLevel = report.Warnings.Count > 0 ? MessageType.Warning : MessageType.Info;
                _resultText  =
                    $"✓ 역변환 완료\n" +
                    $"  • 작업본: '{result.name}' (원본은 그대로 보존됨)\n" +
                    $"  • Bip001 계층 제거, Unity 정규 휴머노이드 구조로 재배치됨\n\n" +
                    report.ToText();
            }
            else
            {
                _resultLevel = MessageType.Error;
                _resultText  = "역변환 도중 오류 발생. 콘솔을 확인하세요.";
            }
        }
    }
}
