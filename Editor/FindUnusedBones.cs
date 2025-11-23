using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class FindUnusedBones : EditorWindow
{
    private List<string> excludeStrings = new List<string>(); // 제외할 문자열 리스트

    [MenuItem("Tools/Find Unused Bones")]
    public static void ShowWindow()
    {
        // 변경된 부분: 이미 창이 열려있는지 확인하여 토글(Toggle) 기능 구현
        if (HasOpenInstances<FindUnusedBones>())
        {
            GetWindow<FindUnusedBones>().Close();
        }
        else
        {
            GetWindow<FindUnusedBones>("Find Unused Bones");
        }
    }

    private void OnGUI()
    {
        GUILayout.Label("Exclude Strings", EditorStyles.boldLabel);

        // 동적으로 문자열 입력 필드와 + - 버튼을 추가
        for (int i = 0; i < excludeStrings.Count; i++)
        {
            GUILayout.BeginHorizontal();
            excludeStrings[i] = EditorGUILayout.TextField($"Exclude String {i + 1}", excludeStrings[i]);

            if (GUILayout.Button("-", GUILayout.Width(20)))
            {
                excludeStrings.RemoveAt(i);
            }
            GUILayout.EndHorizontal();
        }

        if (GUILayout.Button("+", GUILayout.Width(20)))
        {
            excludeStrings.Add(string.Empty); // 빈 문자열 입력 필드 추가
        }

        GUILayout.Space(20);

        if (GUILayout.Button("Find and Select Unused Bones"))
        {
            FindAndSelectUnusedBones();
        }
    }

    private void FindAndSelectUnusedBones()
    {
        if (Selection.activeGameObject == null)
        {
            Debug.LogWarning("No GameObject selected!");
            return;
        }

        Transform root = Selection.activeGameObject.transform;
        SkinnedMeshRenderer[] skinnedMeshRenderers = root.GetComponentsInChildren<SkinnedMeshRenderer>();

        HashSet<Transform> usedBones = new HashSet<Transform>();
        HashSet<Transform> excludedObjects = new HashSet<Transform>();

        // Add all bones used by SkinnedMeshRenderers to usedBones set
        foreach (var skinnedMeshRenderer in skinnedMeshRenderers)
        {
            foreach (var bone in skinnedMeshRenderer.bones)
            {
                usedBones.Add(bone);
            }
            // Add the SkinnedMeshRenderer's gameObject and its parents to excludedObjects set
            Transform current = skinnedMeshRenderer.transform;
            while (current != null)
            {
                excludedObjects.Add(current);
                current = current.parent;
            }
        }

        List<Transform> unusedBones = new List<Transform>();
        Transform[] allBones = root.GetComponentsInChildren<Transform>();
        foreach (var bone in allBones)
        {
            // 문자열 필터링: 사용자가 입력한 문자열을 포함한 오브젝트는 제외
            bool excludeBone = false;
            foreach (var excludeString in excludeStrings)
            {
                if (!string.IsNullOrEmpty(excludeString) && bone.name.ToLower().Contains(excludeString.ToLower()))
                {
                    excludeBone = true;
                    break;
                }
            }

            if (!usedBones.Contains(bone) &&
                !excludedObjects.Contains(bone) &&
                bone != root &&
                !excludeBone) // 필터링된 오브젝트 제외
            {
                unusedBones.Add(bone);
            }
        }

        if (unusedBones.Count > 0)
        {
            Selection.objects = unusedBones.ConvertAll(b => b.gameObject).ToArray();
            Debug.Log($"Found {unusedBones.Count} unused bones. Selected in the hierarchy.");
        }
        else
        {
            Debug.Log("No unused bones found.");
        }
    }
}