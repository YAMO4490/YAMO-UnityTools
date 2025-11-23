using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class ObjectNameModifier : EditorWindow
{
    private string prefixText = string.Empty;
    private string suffixText = string.Empty;

    private GameObject rootObject;
    private Dictionary<string, List<Transform>> duplicateGroups = new();
    private Vector2 scroll;

    [MenuItem("Tools/Object Name Modifier")]
    public static void ShowWindow()
    {
        // 변경된 부분: 이미 창이 열려있는지 확인하여 토글(Toggle) 기능 구현
        if (HasOpenInstances<ObjectNameModifier>())
        {
            GetWindow<ObjectNameModifier>().Close();
        }
        else
        {
            GetWindow<ObjectNameModifier>("Object Name Modifier");
        }
    }

    private void OnGUI()
    {
        GUILayout.Label("Modify Object Names", EditorStyles.boldLabel);

        prefixText = EditorGUILayout.TextField("Prefix", prefixText);
        suffixText = EditorGUILayout.TextField("Suffix", suffixText);

        if (GUILayout.Button("Apply Prefix and Suffix"))
            ApplyPrefixAndSuffix();

        GUILayout.Space(5);

        if (GUILayout.Button("Remove First Character"))
            RemoveFirstCharacter();

        if (GUILayout.Button("Remove Last Character"))
            RemoveLastCharacter();

        GUILayout.Space(5);

        if (GUILayout.Button("Replace Spaces with Underscores"))
            ReplaceSpacesWithUnderscores();

        GUILayout.Space(5);

        // ▼▼▼ 추가된 부분: 하위 오브젝트 이름순 정렬 버튼 ▼▼▼
        if (GUILayout.Button("Sort Children by Name (A-Z)") && Selection.activeGameObject != null)
        {
            SortChildrenByName(Selection.activeGameObject);
        }
        // ▲▲▲ 추가된 부분 끝 ▲▲▲

        GUILayout.Space(20);
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

        GUILayout.Label("Find and Fix Duplicate Names", EditorStyles.boldLabel);

        rootObject = (GameObject)EditorGUILayout.ObjectField("Target Root Object", rootObject, typeof(GameObject), true);

        if (GUILayout.Button("Find Duplicate Names") && rootObject != null)
        {
            FindDuplicateNames(rootObject);
        }

        if (duplicateGroups.Count > 0)
        {
            EditorGUILayout.Space();
            EditorGUILayout.LabelField("Duplicate Name List", EditorStyles.boldLabel);
            scroll = EditorGUILayout.BeginScrollView(scroll, GUILayout.Height(200));

            foreach (var kvp in duplicateGroups)
            {
                EditorGUILayout.LabelField($"{kvp.Key} ({kvp.Value.Count} items)");
                foreach (var t in kvp.Value)
                {
                    EditorGUILayout.ObjectField("  ↳", t, typeof(Transform), true);
                }
            }

            EditorGUILayout.EndScrollView();

            if (GUILayout.Button("Auto-Rename Duplicates"))
            {
                RenameDuplicates();
            }
        }
        else if (rootObject != null)
        {
            EditorGUILayout.HelpBox("No duplicate names found.", MessageType.Info);
        }
    }

    void ApplyPrefixAndSuffix()
    {
        foreach (GameObject obj in Selection.gameObjects)
        {
            Undo.RecordObject(obj, "Change Object Name");
            obj.name = prefixText + obj.name + suffixText;
        }
    }

    void RemoveFirstCharacter()
    {
        foreach (GameObject obj in Selection.gameObjects)
        {
            if (obj.name.Length > 0)
            {
                Undo.RecordObject(obj, "Remove First Character");
                obj.name = obj.name.Substring(1);
            }
        }
    }

    void RemoveLastCharacter()
    {
        foreach (GameObject obj in Selection.gameObjects)
        {
            if (obj.name.Length > 0)
            {
                Undo.RecordObject(obj, "Remove Last Character");
                obj.name = obj.name.Substring(0, obj.name.Length - 1);
            }
        }
    }

    void ReplaceSpacesWithUnderscores()
    {
        foreach (GameObject obj in Selection.gameObjects)
        {
            Undo.RecordObject(obj, "Replace Spaces");
            obj.name = obj.name.Replace(" ", "_");
        }
    }

    void FindDuplicateNames(GameObject root)
    {
        duplicateGroups.Clear();
        Dictionary<string, List<Transform>> nameMap = new();

        foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
        {
            if (!nameMap.ContainsKey(t.name))
                nameMap[t.name] = new List<Transform>();

            nameMap[t.name].Add(t);
        }

        foreach (var kvp in nameMap)
        {
            if (kvp.Value.Count > 1)
            {
                duplicateGroups[kvp.Key] = kvp.Value;
            }
        }
    }

    void RenameDuplicates()
    {
        foreach (var kvp in duplicateGroups)
        {
            for (int i = 0; i < kvp.Value.Count; i++)
            {
                string newName = (i == 0) ? kvp.Key : $"{kvp.Key}_{i}";
                Undo.RecordObject(kvp.Value[i], "Rename Duplicate");
                kvp.Value[i].name = newName;
            }
        }

        Debug.Log("Duplicate names have been renamed.");
        FindDuplicateNames(rootObject); // 재검사
    }

    // ▼▼▼ 수정된 부분: 하위 오브젝트 이름순 정렬 함수 ▼▼▼
    void SortChildrenByName(GameObject parent)
    {
        if (parent == null) return;

        // 하위 오브젝트 리스트 가져오기
        List<Transform> children = new List<Transform>();
        for (int i = 0; i < parent.transform.childCount; i++)
        {
            children.Add(parent.transform.GetChild(i));
        }

        // 유니티 기본 정렬 순서로 정렬 (대소문자 구분 없음, 자연스러운 숫자 정렬)
        children.Sort((a, b) => EditorUtility.NaturalCompare(a.name, b.name));

        // 정렬된 순서대로 SiblingIndex 재설정
        for (int i = 0; i < children.Count; i++)
        {
            Undo.SetTransformParent(children[i], parent.transform, "Sort Children By Name");
            children[i].SetSiblingIndex(i);
        }

        Debug.Log($"{parent.name}의 하위 오브젝트들이 이름순으로 정렬되었습니다.");
    }
    // ▲▲▲ 수정된 부분 끝 ▲▲▲
}