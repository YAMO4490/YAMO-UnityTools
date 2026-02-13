using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

public class ObjectNameModifier : EditorWindow
{
    private string prefixText = string.Empty;
    private string suffixText = string.Empty;

    private GameObject rootObject;
    // [수정] Unity 2019 호환성을 위해 명시적 타입 선언으로 변경
    private Dictionary<string, List<Transform>> duplicateGroups = new Dictionary<string, List<Transform>>();
    private Vector2 scroll;
    private Vector2 scaleScroll;
    private List<Transform> invalidScaleBones = new List<Transform>();

    [MenuItem("Tools/Object Name Modifier")]
    public static void ShowWindow()
    {
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

        if (GUILayout.Button("Sort Children by Name (A-Z)") && Selection.activeGameObject != null)
        {
            SortChildrenByName(Selection.activeGameObject);
        }

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

        GUILayout.Space(20);
        EditorGUILayout.LabelField("", GUI.skin.horizontalSlider);

        GUILayout.Label("Humanoid Bone Scale Checker", EditorStyles.boldLabel);

        if (GUILayout.Button("Check Bone Scales"))
        {
            CheckHumanoidScale();
        }

        if (invalidScaleBones.Count > 0)
        {
            GUILayout.Space(10);
            GUILayout.Label($"Found {invalidScaleBones.Count} bones with non-1 scale:", EditorStyles.boldLabel);
            scaleScroll = EditorGUILayout.BeginScrollView(scaleScroll, GUILayout.Height(150));
            foreach (var bone in invalidScaleBones)
            {
                if (bone == null) continue;
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.ObjectField(bone, typeof(Transform), true);
                if (GUILayout.Button("Select", GUILayout.Width(60)))
                {
                    Selection.activeGameObject = bone.gameObject;
                    EditorGUIUtility.PingObject(bone.gameObject);
                }
                EditorGUILayout.LabelField(bone.localScale.ToString(), GUILayout.Width(150));
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.EndScrollView();
        }
        else if (System.Math.Abs(EditorGUILayout.GetControlRect().y - 0) > 0.1f && invalidScaleBones.Count == 0 && GUI.changed) 
        {
             // Optional: visual feedback when empty, but might be tricky with OnGUI redraws. 
             // Keeping it simple for now.
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
        // [수정] Unity 2019 호환성을 위해 명시적 타입 선언으로 변경
        Dictionary<string, List<Transform>> nameMap = new Dictionary<string, List<Transform>>();

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
        FindDuplicateNames(rootObject); 
    }

    void SortChildrenByName(GameObject parent)
    {
        if (parent == null) return;

        List<Transform> children = new List<Transform>();
        for (int i = 0; i < parent.transform.childCount; i++)
        {
            children.Add(parent.transform.GetChild(i));
        }

        children.Sort((a, b) => EditorUtility.NaturalCompare(a.name, b.name));

        for (int i = 0; i < children.Count; i++)
        {
            Undo.SetTransformParent(children[i], parent.transform, "Sort Children By Name");
            children[i].SetSiblingIndex(i);
        }

        Debug.Log($"{parent.name}의 하위 오브젝트들이 이름순으로 정렬되었습니다.");
    }

    void CheckHumanoidScale()
    {
        invalidScaleBones.Clear();
        GameObject activeObj = Selection.activeGameObject;
        if (activeObj == null)
        {
            Debug.LogWarning("No object selected.");
            EditorUtility.DisplayDialog("Error", "Please select a GameObject first.", "OK");
            return;
        }

        Animator animator = activeObj.GetComponent<Animator>();
        if (animator == null || !animator.isHuman)
        {
            Debug.LogWarning("Selected object is not a Humanoid (or has no Animator).");
            EditorUtility.DisplayDialog("Error", "Selected object must have an Animator and be Humanoid.", "OK");
            return;
        }

        foreach (HumanBodyBones boneId in System.Enum.GetValues(typeof(HumanBodyBones)))
        {
            if (boneId == HumanBodyBones.LastBone) continue;

            Transform boneTransform = animator.GetBoneTransform(boneId);
            if (boneTransform != null)
            {
                // Simple epsilon check
                if (Vector3.Distance(boneTransform.localScale, Vector3.one) > 0.0001f)
                {
                    invalidScaleBones.Add(boneTransform);
                }
            }
        }

        if (invalidScaleBones.Count == 0)
        {
            Debug.Log("All humanoid bones have scale (1,1,1).");
            EditorUtility.DisplayDialog("Result", "All humanoid bones have valid scale (1,1,1).", "OK");
        }
    }
}