using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YAMO.UnityTools.Editor
{
    internal static class InstantiateSelectedPrefab
    {
        private const string MenuPath = "Assets/Add Selected Prefab to Scene";
        private const int MenuPriority = 30;

        [MenuItem(MenuPath, true, MenuPriority)]
        private static bool CanAdd()
        {
            var prefab = Selection.activeObject as GameObject;
            var scene = SceneManager.GetActiveScene();
            return prefab && PrefabUtility.IsPartOfPrefabAsset(prefab) &&
                   AssetDatabase.IsMainAsset(prefab) &&
                   !EditorApplication.isPlayingOrWillChangePlaymode &&
                   PrefabStageUtility.GetCurrentPrefabStage() == null &&
                   scene.IsValid() && scene.isLoaded;
        }

        [MenuItem(MenuPath, false, MenuPriority)]
        private static void Add()
        {
            if (!CanAdd())
                return;

            var instance = (GameObject)PrefabUtility.InstantiatePrefab(
                Selection.activeObject, SceneManager.GetActiveScene());
            if (!instance)
                return;

            Undo.RegisterCreatedObjectUndo(instance, "Add Selected Prefab to Scene");
            Selection.activeGameObject = instance;
        }
    }
}
