using UnityEditor;
using UnityEngine;
using YAMO.UnityTools;

namespace YAMO.UnityTools.Editor
{
    [CustomEditor(typeof(BlendShapeMirror))]
    public class BlendShapeMirrorEditor : UnityEditor.Editor
    {
        SerializedProperty _source;
        SerializedProperty _targets;

        void OnEnable()
        {
            _source = serializedObject.FindProperty(nameof(BlendShapeMirror.source));
            _targets = serializedObject.FindProperty(nameof(BlendShapeMirror.targets));
        }

        public override void OnInspectorGUI()
        {
            serializedObject.Update();

            var mirror = (BlendShapeMirror)target;
            var resolvedSrc = (SkinnedMeshRenderer)_source.objectReferenceValue;
            if (resolvedSrc == null) resolvedSrc = mirror.GetComponent<SkinnedMeshRenderer>();

            EditorGUILayout.HelpBox("이 컴포넌트는 플레이 모드에서만 동작합니다.", MessageType.None);
            EditorGUILayout.Space(4);

            EditorGUILayout.PropertyField(_source,
                new GUIContent("Source", "값을 가져올 SMR. 비어있으면 이 오브젝트의 SMR 사용"));

            if (resolvedSrc == null)
            {
                EditorGUILayout.HelpBox(
                    "Source SMR 이 없습니다. 직접 지정하거나 이 오브젝트에 SkinnedMeshRenderer 를 추가하세요.",
                    MessageType.Warning);
            }
            else if (resolvedSrc.sharedMesh == null || resolvedSrc.sharedMesh.blendShapeCount == 0)
            {
                EditorGUILayout.HelpBox("Source 의 메시에 블렌드셰이프가 없습니다.", MessageType.Warning);
            }

            int srcShapeCount = (resolvedSrc != null && resolvedSrc.sharedMesh != null)
                ? resolvedSrc.sharedMesh.blendShapeCount : 0;

            EditorGUILayout.Space(6);
            EditorGUILayout.LabelField($"Targets ({_targets.arraySize})", EditorStyles.boldLabel);

            for (int i = 0; i < _targets.arraySize; i++)
            {
                var elem = _targets.GetArrayElementAtIndex(i);

                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.PropertyField(elem, GUIContent.none);

                var tgt = (SkinnedMeshRenderer)elem.objectReferenceValue;
                string info = ComputeMatchInfo(resolvedSrc, tgt, srcShapeCount);
                GUILayout.Label(info, GUILayout.Width(70));

                if (GUILayout.Button("X", GUILayout.Width(24)))
                {
                    _targets.DeleteArrayElementAtIndex(i);
                    EditorGUILayout.EndHorizontal();
                    break;
                }
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("+ Add Target"))
            {
                _targets.arraySize++;
                _targets.GetArrayElementAtIndex(_targets.arraySize - 1).objectReferenceValue = null;
            }
            if (GUILayout.Button("Auto-fill from Parent", GUILayout.Width(180)))
                AutoFillFromParent(mirror, resolvedSrc);
            EditorGUILayout.EndHorizontal();

            EditorGUILayout.Space(4);
            EditorGUILayout.HelpBox(
                "• Source 의 모든 블렌드셰이프 값을 매 프레임 Targets 에 이름 기준으로 복제합니다.\n" +
                "• 한 Target 에 같은 이름의 블렌드셰이프가 없는 항목은 자동으로 건너뜁니다.\n" +
                "• 우측 N/M 표시는 (이름이 일치하는 셰이프키 / Source 전체 셰이프키) 개수입니다.\n" +
                "• Auto-fill from Parent: Source 부모 하위의 모든 SMR(Source 자신 제외)을 Targets 에 채웁니다.\n" +
                "• Source 자신은 항상 Targets 에서 제외됩니다.",
                MessageType.Info);

            if (serializedObject.ApplyModifiedProperties())
            {
                if (!Application.isPlaying)
                    EditorUtility.SetDirty(target);
            }
        }

        string ComputeMatchInfo(SkinnedMeshRenderer src, SkinnedMeshRenderer tgt, int srcCount)
        {
            if (tgt == null) return "";
            if (src == null) return "";
            if (src == tgt) return "(self)";
            if (src.sharedMesh == null || tgt.sharedMesh == null) return "";

            int matched = 0;
            var smesh = src.sharedMesh;
            var tmesh = tgt.sharedMesh;
            for (int i = 0; i < srcCount; i++)
            {
                string name = smesh.GetBlendShapeName(i);
                if (tmesh.GetBlendShapeIndex(name) >= 0) matched++;
            }
            return $"{matched}/{srcCount}";
        }

        void AutoFillFromParent(BlendShapeMirror mirror, SkinnedMeshRenderer src)
        {
            if (src == null)
            {
                EditorUtility.DisplayDialog("Auto-fill", "Source SMR 이 지정되어 있어야 합니다.", "OK");
                return;
            }

            var searchRoot = src.transform.parent;
            if (searchRoot == null) searchRoot = mirror.transform;

            var smrs = searchRoot.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            Undo.RecordObject(mirror, "Auto-fill BlendShape Mirror Targets");
            mirror.targets.Clear();
            int added = 0;
            foreach (var smr in smrs)
            {
                if (smr == src) continue;
                if (smr.sharedMesh == null) continue;
                if (smr.sharedMesh.blendShapeCount == 0) continue;
                mirror.targets.Add(smr);
                added++;
            }
            EditorUtility.SetDirty(mirror);
            serializedObject.Update();

            Debug.Log($"[BlendShapeMirror] Auto-fill: '{searchRoot.name}' 하위에서 {added} 개의 SMR 을 Targets 에 추가했습니다.", mirror);
        }
    }
}
