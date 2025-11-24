using UnityEngine;
using UnityEditor;
using System;
using System.Reflection;
using F10.StreamDeckIntegration;
using F10.StreamDeckIntegration.Attributes;

[InitializeOnLoad]
public static class HierarchySearchTools
{
    // 정적 생성자: 유니티가 로드될 때 무조건 실행됨
    static HierarchySearchTools()
    {
        StreamDeck.AddStatic(typeof(HierarchySearchTools));
    }

    // ==================================================================================
    // ▼▼▼ [검색 버튼 등록 구역] ▼▼▼
    // ==================================================================================

    // 0. 검색 초기화
    [MenuItem("Tools/Search/Clear Search")]
    [StreamDeckButton("Search_Clear")]
    public static void ClearSearch() => SetHierarchySearch("");

    // 1. SkinnedMeshRenderer
    [MenuItem("Tools/Search/Find SkinnedMeshRenderer")]
    [StreamDeckButton("Search_SMR")]
    public static void SearchSMR() => SetHierarchySearch("t:SkinnedMeshRenderer");

    // 2. Magica Capsule Collider
    [MenuItem("Tools/Search/Find Magica Capsule")]
    [StreamDeckButton("Search_MagicaCapsule")]
    public static void SearchMagicaCapsule() => SetHierarchySearch("t:MagicaCapsuleCollider");

    // 3. Magica Plane Collider
    [MenuItem("Tools/Search/Find Magica Plane")]
    [StreamDeckButton("Search_MagicaPlane")]
    public static void SearchMagicaPlane() => SetHierarchySearch("t:MagicaPlaneCollider");

    // 4. Magica Sphere Collider
    [MenuItem("Tools/Search/Find Magica Sphere")]
    [StreamDeckButton("Search_MagicaSphere")]
    public static void SearchMagicaSphere() => SetHierarchySearch("t:MagicaSphereCollider");

    // 5. VRM SpringBone Collider Group
    [MenuItem("Tools/Search/Find VRM Collider Group")]
    [StreamDeckButton("Search_VRMCollider")]
    public static void SearchVRMCollider() => SetHierarchySearch("t:VRMSpringBoneColliderGroup");

    // ==================================================================================
    // ▲▲▲ 버튼 등록 구역 끝 ▲▲▲
    // ==================================================================================

    public static void SetHierarchySearch(string filter)
    {
        // 1. Hierarchy Window 타입 찾기
        var hierarchyType = typeof(Editor).Assembly.GetType("UnityEditor.SceneHierarchyWindow");
        var window = EditorWindow.GetWindow(hierarchyType);
        
        if (window != null)
        {
            // 2. 메서드를 이름으로만 검색 (파라미터 타입 무시)
            MethodInfo method = null;
            var methods = hierarchyType.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance);
            
            foreach (var m in methods)
            {
                if (m.Name == "SetSearchFilter")
                {
                    // 첫 번째 인자가 string인지 확인하여 엉뚱한 메서드 방지
                    var p = m.GetParameters();
                    if (p.Length > 0 && p[0].ParameterType == typeof(string))
                    {
                        method = m;
                        break; // 찾았으면 중단
                    }
                }
            }

            if (method != null)
            {
                // 3. 파라미터 개수와 타입에 맞춰서 동적으로 인자 생성
                var parameters = method.GetParameters();
                object[] args = new object[parameters.Length];
                
                args[0] = filter; // 첫 번째는 무조건 검색어

                for (int i = 1; i < parameters.Length; i++)
                {
                    Type pType = parameters[i].ParameterType;

                    if (pType == typeof(bool))
                    {
                        args[i] = false; // bool 타입은 기본값 false
                    }
                    else if (pType.IsEnum) 
                    {
                        // Enum 타입(SearchMode 등)은 정수 0(All/Main)을 해당 Enum으로 변환해서 넣음
                        args[i] = Enum.ToObject(pType, 0); 
                    }
                    else if (pType == typeof(int))
                    {
                        args[i] = 0;
                    }
                    else
                    {
                        args[i] = null;
                    }
                }

                // 4. 실행
                method.Invoke(window, args);
                window.Focus();
            }
            else
            {
                Debug.LogError("[HierarchySearchTools] 호환되는 SetSearchFilter 메서드를 찾을 수 없습니다.");
            }
        }
    }
}