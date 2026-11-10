using System;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    [InitializeOnLoad]
    internal static class UnityPipelineShortcut
    {
        private static MethodInfo pendingStart;
        private static double startAfter;

        static UnityPipelineShortcut()
        {
            // Pipeline 0.7 leaves its before-reload handler empty. Close the
            // old listener while its dispatcher is still attached to this domain.
            AssemblyReloadEvents.beforeAssemblyReload += StopBeforeReload;
        }

        private static void StopBeforeReload()
        {
            EditorApplication.update -= StartWhenReady;
            pendingStart = null;
            FindStartup()?.GetMethod("StopServer", BindingFlags.Public | BindingFlags.Static)?.Invoke(null, null);
        }

        private static Type FindStartup() => Type.GetType(
            "Unity.Pipeline.Editor.PipelineServerStartup, Unity.Pipeline.Editor");

        [MenuItem("Tools/YAMO/Restart Unity CLI Server %#i")]
        private static void RestartServer()
        {
            if (pendingStart != null)
                return;

            var startup = FindStartup();
            const BindingFlags flags = BindingFlags.Public | BindingFlags.Static;
            var stop = startup?.GetMethod("StopServer", flags);
            var start = startup?.GetMethod("EnsureServerStarted", flags);
            if (stop == null || start == null)
            {
                Debug.LogError("Unity Pipeline server controls are unavailable in the installed package.");
                return;
            }

            stop.Invoke(null, null);
            pendingStart = start;
            startAfter = EditorApplication.timeSinceStartup + 1;
            EditorApplication.update += StartWhenReady;
        }

        private static void StartWhenReady()
        {
            if (EditorApplication.timeSinceStartup < startAfter ||
                EditorApplication.isCompiling || EditorApplication.isUpdating)
                return;

            EditorApplication.update -= StartWhenReady;
            var start = pendingStart;
            pendingStart = null;
            start.Invoke(null, null);
        }
    }
}
