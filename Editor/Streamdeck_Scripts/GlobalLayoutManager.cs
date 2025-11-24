using UnityEngine;
using UnityEditor;
using System;
using System.Reflection;
using System.IO;
using System.Linq;

#if YAMO_STREAMDECK
using F10.StreamDeckIntegration;
using F10.StreamDeckIntegration.Attributes;
#endif

[InitializeOnLoad]
public static class GlobalLayoutManager
{
    static GlobalLayoutManager()
    {
#if YAMO_STREAMDECK
        StreamDeck.AddStatic(typeof(GlobalLayoutManager));
#endif
    }

    // ▼▼▼ [레이아웃 버튼 등록 구역] ▼▼▼
    
    [MenuItem("Tools/Layouts/Load 14402560")]
    #if YAMO_STREAMDECK
    [StreamDeckButton("Layout_14402560")]
    #endif
    public static void Load_14402560() => LoadLayout("14402560");

    [MenuItem("Tools/Layouts/Load 14402160")]
    #if YAMO_STREAMDECK
    [StreamDeckButton("Layout_14402160")]
    #endif
    public static void Load_14402160() => LoadLayout("14402160");

    [MenuItem("Tools/Layouts/Load Wide")]
    #if YAMO_STREAMDECK
    [StreamDeckButton("Layout_Wide")]
    #endif
    public static void Load_Wide() => LoadLayout("Wide");

    [MenuItem("Tools/Layouts/Load WideMore")]
    #if YAMO_STREAMDECK
    [StreamDeckButton("Layout_WideMore")]
    #endif
    public static void Load_WideMore() => LoadLayout("WideMore");

    // ▲▲▲ 버튼 등록 구역 끝 ▲▲▲

    private static void LoadLayout(string layoutName)
    {
        EditorApplication.delayCall += () =>
        {
            string path = FindLayoutPath(layoutName);
            if (string.IsNullOrEmpty(path))
            {
                Debug.LogError($"[LayoutManager] '{layoutName}' 파일을 찾을 수 없습니다.");
                return;
            }

            // 로그를 통해 확인된 로직: 첫 번째 인자가 String인 LoadWindowLayout 메서드를 찾아 실행
            var type = typeof(Editor).Assembly.GetType("UnityEditor.WindowLayout");
            var method = type?.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "LoadWindowLayout" && 
                                     m.GetParameters().Length > 0 && 
                                     m.GetParameters()[0].ParameterType == typeof(string));

            if (method != null)
            {
                // 매개변수 개수에 맞춰 배열 생성 (로그상 5개)
                var p = method.GetParameters();
                object[] args = new object[p.Length];
                args[0] = path; 
                // 3번째 인자(KeepMainWindow)는 true로 설정해야 에디터가 깜빡이지 않음
                if (p.Length >= 3 && p[2].ParameterType == typeof(bool)) args[2] = true;

                method.Invoke(null, args);
                Debug.Log($"[LayoutManager] 변경 완료: {layoutName}");
            }
        };
    }

    private static string FindLayoutPath(string name)
    {
        string fileName = name + ".wlt";
        string[] basePaths = {
            Path.Combine(Directory.GetCurrentDirectory(), "Library"),
#if UNITY_EDITOR_WIN
            Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "Unity", "Editor-5.x", "Preferences", "Layouts"),
#elif UNITY_EDITOR_OSX
            Path.Combine(Environment.GetEnvironmentVariable("HOME"), "Library", "Preferences", "Unity", "Editor-5.x", "Layouts"),
#endif
        };

        foreach (var basePath in basePaths)
        {
            if (!Directory.Exists(basePath)) continue;
            string fullPath = Path.Combine(basePath, fileName);
            if (File.Exists(fullPath)) return fullPath;
            string defaultPath = Path.Combine(basePath, "Default", fileName);
            if (File.Exists(defaultPath)) return defaultPath;
        }
        return null;
    }
}