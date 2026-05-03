using System.Collections.Generic;
using UnityEditor;
using UnityEditor.IMGUI.Controls;
using UnityEngine;
using YAMO.UnityTools;

namespace YAMO.UnityTools.Editor
{
    [CustomEditor(typeof(BlendShapeLink))]
    public class BlendShapeLinkEditor : UnityEditor.Editor
    {
        SerializedProperty _rules;

        string[] _blendShapeNames;   // index i = 블렌드셰이프 i 의 이름
        int _blendShapeCount;

        void OnEnable()
        {
            _rules = serializedObject.FindProperty(nameof(BlendShapeLink.rules));
            RefreshBlendShapeNames();
        }

        void RefreshBlendShapeNames()
        {
            var link = (BlendShapeLink)target;
            var smr = link.GetComponent<SkinnedMeshRenderer>();
            if (smr == null || smr.sharedMesh == null)
            {
                _blendShapeNames = new string[0];
                _blendShapeCount = 0;
                return;
            }

            var mesh = smr.sharedMesh;
            _blendShapeCount = mesh.blendShapeCount;
            _blendShapeNames = new string[_blendShapeCount];
            for (int i = 0; i < _blendShapeCount; i++)
                _blendShapeNames[i] = mesh.GetBlendShapeName(i);
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var smr = ((BlendShapeLink)target).GetComponent<SkinnedMeshRenderer>();

            if (smr == null || smr.sharedMesh == null)
            {
                EditorGUILayout.HelpBox("SkinnedMeshRenderer 또는 sharedMesh 가 없습니다.", MessageType.Warning);
                return;
            }
            if (_blendShapeCount != smr.sharedMesh.blendShapeCount)
                RefreshBlendShapeNames();

            if (_blendShapeCount == 0)
            {
                EditorGUILayout.HelpBox("이 메쉬에는 블렌드셰이프가 없습니다.", MessageType.Warning);
                return;
            }

            EditorGUILayout.HelpBox("이 컴포넌트는 플레이 모드에서만 동작합니다.", MessageType.None);

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField("Link Rules", EditorStyles.boldLabel);

            for (int i = 0; i < _rules.arraySize; i++)
            {
                var rule = _rules.GetArrayElementAtIndex(i);
                var sourceIndex = rule.FindPropertyRelative(nameof(BlendShapeLink.LinkRule.sourceIndex));
                var targetIndex = rule.FindPropertyRelative(nameof(BlendShapeLink.LinkRule.targetIndex));
                var multiplier  = rule.FindPropertyRelative(nameof(BlendShapeLink.LinkRule.multiplier));
                var enabledProp = rule.FindPropertyRelative(nameof(BlendShapeLink.LinkRule.enabled));

                EditorGUILayout.BeginVertical("box");

                EditorGUILayout.BeginHorizontal();
                enabledProp.boolValue = EditorGUILayout.ToggleLeft($"Rule {i}", enabledProp.boolValue, GUILayout.Width(80));
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("▲", GUILayout.Width(24)) && i > 0)
                    _rules.MoveArrayElement(i, i - 1);
                if (GUILayout.Button("▼", GUILayout.Width(24)) && i < _rules.arraySize - 1)
                    _rules.MoveArrayElement(i, i + 1);
                if (GUILayout.Button("X", GUILayout.Width(24)))
                {
                    _rules.DeleteArrayElementAtIndex(i);
                    EditorGUILayout.EndHorizontal();
                    EditorGUILayout.EndVertical();
                    break;
                }
                EditorGUILayout.EndHorizontal();

                DrawBlendShapePicker("Source", i, true);
                DrawBlendShapePicker("Target", i, false);

                multiplier.floatValue = EditorGUILayout.FloatField(
                    new GUIContent("Multiplier", "Source 값에 곱해질 배율. 1 = 그대로, 0.5 = 절반"),
                    multiplier.floatValue);

                var modeProp = rule.FindPropertyRelative("mode");
                if (modeProp != null)
                {
                    EditorGUILayout.PropertyField(modeProp,
                        new GUIContent("Mode",
                            "Multiply: source 는 그대로 두고 target 에 값 기여.\n" +
                            "Override: target 에 값 기여 후 source 를 0 으로 리셋 (값 이전/swap)."));
                }

                if (sourceIndex.intValue >= 0 && targetIndex.intValue >= 0 &&
                    sourceIndex.intValue == targetIndex.intValue)
                {
                    EditorGUILayout.HelpBox(
                        "Source 와 Target 이 같음 → 자기 자신을 Multiplier 로 감쇠합니다. " +
                        "애니메이션이 매 프레임 값을 새로 써주지 않으면 0 으로 수렴하니 주의.",
                        MessageType.Info);
                }

                EditorGUILayout.EndVertical();
                EditorGUILayout.Space(2);
            }

            EditorGUILayout.Space(4);
            if (GUILayout.Button("+ Add Rule"))
            {
                _rules.arraySize++;
                var newRule = _rules.GetArrayElementAtIndex(_rules.arraySize - 1);
                newRule.FindPropertyRelative(nameof(BlendShapeLink.LinkRule.sourceIndex)).intValue = -1;
                newRule.FindPropertyRelative(nameof(BlendShapeLink.LinkRule.targetIndex)).intValue = -1;
                newRule.FindPropertyRelative(nameof(BlendShapeLink.LinkRule.multiplier)).floatValue = 1f;
                newRule.FindPropertyRelative(nameof(BlendShapeLink.LinkRule.enabled)).boolValue = true;
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "• 같은 Target 에 여러 규칙이 걸리면, (Source × Multiplier) 중 가장 큰 값이 Target 에 덮어써집니다 (Max 방식).\n" +
                "• Mode = Override 인 규칙은 Source 를 0 으로 리셋합니다. 서로 독립적인 셰이프키 사이의 '값 이전' 용도.",
                MessageType.Info);

            if (serializedObject.ApplyModifiedProperties())
            {
                if (!Application.isPlaying)
                    EditorUtility.SetDirty(target);
            }
        }

        /// <summary>블렌드셰이프 인덱스를 검색 가능한 드롭다운(AdvancedDropdown)으로 선택.</summary>
        void DrawBlendShapePicker(string label, int ruleIndex, bool isSource)
        {
            var link = (BlendShapeLink)target;
            var rule = link.rules[ruleIndex];
            int cur = isSource ? rule.sourceIndex : rule.targetIndex;

            string currentLabel = (cur >= 0 && cur < _blendShapeCount)
                ? $"{cur}: {_blendShapeNames[cur]}"
                : "(None)";

            var rect = EditorGUILayout.GetControlRect(true, EditorGUIUtility.singleLineHeight);
            var labelRect = new Rect(rect.x, rect.y, EditorGUIUtility.labelWidth, rect.height);
            var buttonRect = new Rect(labelRect.xMax, rect.y, rect.width - EditorGUIUtility.labelWidth, rect.height);

            EditorGUI.LabelField(labelRect, label);

            if (EditorGUI.DropdownButton(buttonRect, new GUIContent(currentLabel), FocusType.Keyboard))
            {
                // 콜백이 비동기로 호출되므로 SerializedProperty 대신 타겟 오브젝트와 인덱스만 캡처.
                var capturedLink = link;
                int capturedRuleIndex = ruleIndex;
                bool capturedIsSource = isSource;

                var dropdown = new BlendShapeDropdown(
                    new AdvancedDropdownState(),
                    _blendShapeNames,
                    selectedIndex =>
                    {
                        if (capturedLink == null) return;
                        if (capturedRuleIndex < 0 || capturedRuleIndex >= capturedLink.rules.Count) return;

                        Undo.RecordObject(capturedLink, "Change BlendShape Link");
                        var r = capturedLink.rules[capturedRuleIndex];
                        if (capturedIsSource)
                            r.sourceIndex = selectedIndex;
                        else
                            r.targetIndex = selectedIndex;
                        EditorUtility.SetDirty(capturedLink);
                        Repaint();
                    });
                dropdown.Show(buttonRect);
            }
        }

        // ──────────────────────────────────────────────
        //  AdvancedDropdown: 검색창 + 스크롤이 있는 팝업
        // ──────────────────────────────────────────────
        class ShapeItem : AdvancedDropdownItem
        {
            public int shapeIndex;
            public ShapeItem(string name, int idx) : base(name) { shapeIndex = idx; }
        }

        class BlendShapeDropdown : AdvancedDropdown
        {
            readonly string[] _names;
            readonly System.Action<int> _onSelect;

            public BlendShapeDropdown(AdvancedDropdownState state, string[] names, System.Action<int> onSelect)
                : base(state)
            {
                _names = names;
                _onSelect = onSelect;
                minimumSize = new Vector2(300f, 400f);
            }

            protected override AdvancedDropdownItem BuildRoot()
            {
                var root = new AdvancedDropdownItem("BlendShape");
                root.AddChild(new ShapeItem("(None)", -1));
                for (int i = 0; i < _names.Length; i++)
                    root.AddChild(new ShapeItem($"{i}: {_names[i]}", i));
                return root;
            }

            protected override void ItemSelected(AdvancedDropdownItem item)
            {
                if (item is ShapeItem si)
                    _onSelect?.Invoke(si.shapeIndex);
            }
        }
    }
}
