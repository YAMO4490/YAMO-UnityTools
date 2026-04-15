using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace YAMO.UnityTools.Editor
{
public class FindMissingBones : EditorWindow
{
    private Vector2 scrollPosition;
    private List<Result> results = new List<Result>();
    private bool hasSearched = false;

    private struct Result
    {
        public SkinnedMeshRenderer renderer;
        public List<int> missingBoneIndices;
        public bool rootBoneMissing;
    }

    [MenuItem("Tools/YAMO/Bones/Find Missing Bones")]
    public static void ShowWindow()
    {
        if (HasOpenInstances<FindMissingBones>())
        {
            GetWindow<FindMissingBones>().Close();
        }
        else
        {
            GetWindow<FindMissingBones>("Find Missing Bones");
        }
    }

    private void OnGUI()
    {
        GUILayout.Label("SkinnedMeshRenderer Missing Bone Checker", EditorStyles.boldLabel);
        GUILayout.Space(5);
        EditorGUILayout.HelpBox(
            "Scene에 있는 모든 SkinnedMeshRenderer를 검사하여\n" +
            "bones 배열에 null(누락된) 항목이 있는지 확인합니다.",
            MessageType.Info);

        GUILayout.Space(10);

        if (GUILayout.Button("Scene 전체 검사", GUILayout.Height(30)))
        {
            SearchScene();
        }

        GUILayout.Space(5);

        if (GUILayout.Button("선택한 오브젝트 하위 검사", GUILayout.Height(25)))
        {
            SearchSelection();
        }

        GUILayout.Space(10);

        if (hasSearched)
        {
            DrawResults();
        }
    }

    private void SearchScene()
    {
        results.Clear();
        hasSearched = true;

        var renderers = FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None);
        foreach (var smr in renderers)
        {
            CheckRenderer(smr);
        }

        LogSummary("Scene");
    }

    private void SearchSelection()
    {
        results.Clear();
        hasSearched = true;

        if (Selection.activeGameObject == null)
        {
            Debug.LogWarning("[FindMissingBones] 선택된 오브젝트가 없습니다.");
            return;
        }

        var renderers = Selection.activeGameObject.GetComponentsInChildren<SkinnedMeshRenderer>(true);
        foreach (var smr in renderers)
        {
            CheckRenderer(smr);
        }

        LogSummary(Selection.activeGameObject.name);
    }

    private void CheckRenderer(SkinnedMeshRenderer smr)
    {
        var missingIndices = new List<int>();
        bool rootMissing = smr.rootBone == null;

        if (smr.bones != null)
        {
            for (int i = 0; i < smr.bones.Length; i++)
            {
                if (smr.bones[i] == null)
                {
                    missingIndices.Add(i);
                }
            }
        }

        if (missingIndices.Count > 0 || rootMissing)
        {
            results.Add(new Result
            {
                renderer = smr,
                missingBoneIndices = missingIndices,
                rootBoneMissing = rootMissing
            });
        }
    }

    private void LogSummary(string scope)
    {
        if (results.Count == 0)
        {
            Debug.Log($"[FindMissingBones] '{scope}' 검사 완료: 누락된 본이 없습니다.");
        }
        else
        {
            int totalMissing = 0;
            foreach (var r in results)
            {
                totalMissing += r.missingBoneIndices.Count;
                if (r.rootBoneMissing) totalMissing++;
            }
            Debug.LogWarning($"[FindMissingBones] '{scope}' 검사 완료: {results.Count}개의 SkinnedMeshRenderer에서 총 {totalMissing}개의 누락된 본 발견.");
        }
    }

    private void DrawResults()
    {
        if (results.Count == 0)
        {
            EditorGUILayout.HelpBox("누락된 본이 없습니다!", MessageType.None);
            return;
        }

        EditorGUILayout.LabelField($"문제 발견: {results.Count}개의 SkinnedMeshRenderer", EditorStyles.boldLabel);

        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);

        foreach (var result in results)
        {
            if (result.renderer == null) continue;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.ObjectField(result.renderer, typeof(SkinnedMeshRenderer), true);
            if (GUILayout.Button("선택", GUILayout.Width(40)))
            {
                Selection.activeGameObject = result.renderer.gameObject;
                EditorGUIUtility.PingObject(result.renderer.gameObject);
            }
            EditorGUILayout.EndHorizontal();

            if (result.rootBoneMissing)
            {
                EditorGUILayout.LabelField("  ⚠ Root Bone이 누락됨", EditorStyles.miniLabel);
            }

            if (result.missingBoneIndices.Count > 0)
            {
                string indices = string.Join(", ", result.missingBoneIndices);
                EditorGUILayout.LabelField($"  ⚠ 누락된 본 인덱스: [{indices}] (총 {result.missingBoneIndices.Count}개)", EditorStyles.miniLabel);
            }

            EditorGUILayout.EndVertical();
        }

        EditorGUILayout.EndScrollView();
    }
}
}
