#if UNITY_EDITOR

using UnityEngine;
using UnityEditor;

namespace YAMO.UnityTools.Editor
{
    public class MissingScriptRemover : EditorWindow
    {
        private GameObject targetObject;

        [MenuItem("Tools/YAMO/Assets/Missing Script Remover")]
        public static void ShowWindow()
        {
            var window = GetWindow<MissingScriptRemover>("MissingScriptRemover");
            window.minSize = new Vector2(400, 260);
            window.Show();
        }

        private void OnGUI()
        {
            GUILayout.Space(10);
            EditorGUILayout.LabelField("대상 오브젝트 설정", EditorStyles.boldLabel);
            
            targetObject = (GameObject)EditorGUILayout.ObjectField("대상 오브젝트", targetObject, typeof(GameObject), true);

            GUILayout.Space(20);
            
            GUI.enabled = targetObject != null;

            if (GUILayout.Button("스크립트 상태 조사 (미싱 및 비활성)", GUILayout.Height(30)))
            {
                SearchScripts();
            }

            GUILayout.Space(10);
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("미싱 스크립트 삭제", GUILayout.Height(30)))
            {
                RemoveMissingScripts();
            }

            if (GUILayout.Button("비활성 스크립트 삭제", GUILayout.Height(30)))
            {
                RemoveDisabledScripts();
            }
            EditorGUILayout.EndHorizontal();

            GUILayout.Space(5);
            if (GUILayout.Button("미싱 및 비활성 스크립트 모두 삭제", GUILayout.Height(30)))
            {
                RemoveAllTargetScripts();
            }

            GUI.enabled = true;

            if (targetObject == null)
            {
                GUILayout.Space(10);
                EditorGUILayout.HelpBox("조사하거나 삭제할 대상 오브젝트를 먼저 할당해 주십시오.", MessageType.Info);
            }
        }

        private void SearchScripts()
        {
            if (targetObject == null) return;
            var (missing, disabled) = SearchScripts_Recursive(targetObject);
            
            if (missing == 0 && disabled == 0)
            {
                Debug.Log($"[{targetObject.name}] 발견된 미싱 스크립트나 비활성 스크립트가 없습니다.");
            }
            else
            {
                Debug.Log($"[{targetObject.name}] 검색 완료. (미싱 스크립트: {missing}개, 비활성 스크립트: {disabled}개)");
            }
        }

        private void RemoveMissingScripts()
        {
            if (targetObject == null) return;
            int removed = RemoveMissingScripts_Recursive(targetObject);
            
            if (removed == 0)
            {
                Debug.Log($"[{targetObject.name}] 제거할 미싱 스크립트가 발견되지 않았습니다.");
            }
            else
            {
                Debug.Log($"[{targetObject.name}] 미싱 스크립트 {removed}개 제거가 완료되었습니다.");
            }
        }

        private void RemoveDisabledScripts()
        {
            if (targetObject == null) return;
            int removed = RemoveDisabledScripts_Recursive(targetObject);
            
            if (removed == 0)
            {
                Debug.Log($"[{targetObject.name}] 제거할 비활성 스크립트가 발견되지 않았습니다.");
            }
            else
            {
                Debug.Log($"[{targetObject.name}] 비활성 스크립트 {removed}개 제거가 완료되었습니다.");
            }
        }

        private void RemoveAllTargetScripts()
        {
            if (targetObject == null) return;
            var (missing, disabled) = RemoveAllScripts_Recursive(targetObject);

            if (missing == 0 && disabled == 0)
            {
                Debug.Log($"[{targetObject.name}] 제거할 대상(미싱 스크립트 또는 비활성 스크립트)이 발견되지 않았습니다.");
            }
            else
            {
                Debug.Log($"[{targetObject.name}] 정리가 성공적으로 완료되었습니다. (미싱 스크립트 제거: {missing}개, 비활성 스크립트 제거: {disabled}개)");
            }
        }

        private (int, int) SearchScripts_Recursive(GameObject obj)
        {
            int missingCount = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(obj);
            int disabledCount = 0;

            MonoBehaviour[] scripts = obj.GetComponents<MonoBehaviour>();
            foreach (var script in scripts)
            {
                if (script != null && !script.enabled)
                {
                    disabledCount++;
                }
            }

            foreach (Transform child in obj.transform)
            {
                var (childMissing, childDisabled) = SearchScripts_Recursive(child.gameObject);
                missingCount += childMissing;
                disabledCount += childDisabled;
            }

            return (missingCount, disabledCount);
        }

        private int RemoveMissingScripts_Recursive(GameObject obj)
        {
            int missingRemoved = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(obj);

            foreach (Transform child in obj.transform)
            {
                missingRemoved += RemoveMissingScripts_Recursive(child.gameObject);
            }

            return missingRemoved;
        }

        private int RemoveDisabledScripts_Recursive(GameObject obj)
        {
            int disabledRemoved = 0;

            MonoBehaviour[] scripts = obj.GetComponents<MonoBehaviour>();
            foreach (var script in scripts)
            {
                // script가 null이 아니고 비활성화된 상태일 때 제거
                if (script != null && !script.enabled)
                {
                    Undo.DestroyObjectImmediate(script);
                    disabledRemoved++;
                }
            }

            foreach (Transform child in obj.transform)
            {
                disabledRemoved += RemoveDisabledScripts_Recursive(child.gameObject);
            }

            return disabledRemoved;
        }

        private (int, int) RemoveAllScripts_Recursive(GameObject obj)
        {
            int missingRemoved = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(obj);
            int disabledRemoved = 0;

            MonoBehaviour[] scripts = obj.GetComponents<MonoBehaviour>();
            foreach (var script in scripts)
            {
                if (script != null && !script.enabled)
                {
                    Undo.DestroyObjectImmediate(script);
                    disabledRemoved++;
                }
            }

            foreach (Transform child in obj.transform)
            {
                var (childMissing, childDisabled) = RemoveAllScripts_Recursive(child.gameObject);
                missingRemoved += childMissing;
                disabledRemoved += childDisabled;
            }

            return (missingRemoved, disabledRemoved);
        }
    }
}
#endif