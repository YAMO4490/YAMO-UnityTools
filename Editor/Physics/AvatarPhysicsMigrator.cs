// 이 파일은 MagicaCloth2와 VRM(UniVRM 0.x) 둘 다 설치된 환경에서만 컴파일됩니다.
// 조건부 컴파일은 같은 폴더의 YAMO.UnityTools.Physics.Editor.asmdef가
// defineConstraints = [YAMO_HAS_MAGICACLOTH, YAMO_HAS_VRM] 로 담당하며,
// 해당 심볼은 Editor/Internal/YamoDependencyDetector.cs가 자동 주입합니다.
//
// 마이그레이션 로직 본체는 AvatarMigrationCore.cs 로 분리되었고,
// 본 파일은 EditorWindow UI + 편의기능(PreBuild, Collider Cleanup, Reset BlendShapes 등)을
// 담당합니다.

using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using MagicaCloth2;
using VRM;

namespace YAMO.UnityTools.Editor
{
    public class AvatarPhysicsMigrator : EditorWindow
    {
        public static void ShowWindow()
        {
            GetWindow<AvatarPhysicsMigrator>("Physics Migrator");
        }

        [MenuItem("Tools/YAMO/Physics/Avatar Physics Migrator")]
        public static void ToggleWindow()
        {
            var windows = Resources.FindObjectsOfTypeAll<AvatarPhysicsMigrator>();
            if (windows != null && windows.Length > 0)
            {
                windows[0].Close();
            }
            else
            {
                ShowWindow();
            }
        }

        private GameObject sourceAvatar;
        private GameObject targetAvatar;
        private Vector2 scrollPosition;

        private List<string> logMessages = new List<string>();
        private string preBuildFolderPath = "Assets/MagicaPreBuildData";
        private AnalysisResult analysisResult;

        private class AnalysisResult
        {
            public int totalTransforms;
            public int nameMatchCount;
            public int duplicateCount;
            public List<string> duplicateNames;

            public int magicaClothCount;
            public int vrmSpringCount;
            public int capsuleColliderCount;
            public int sphereColliderCount;
            public int planeColliderCount;
            public int vrmColliderGroupCount;
        }

        /// <summary>
        /// AvatarMigrationCore 에 전달할 IMigrationLog 어댑터.
        /// EditorWindow의 logMessages 패널과 Debug 콘솔에 동시 출력합니다.
        /// </summary>
        private class WindowLog : IMigrationLog
        {
            private readonly AvatarPhysicsMigrator _w;
            public WindowLog(AvatarPhysicsMigrator w) { _w = w; }
            public void Info(string m)    { _w.Log(m); }
            public void Warning(string m) { _w.Log("Warning: " + m); }
            public void Error(string m)   { _w.LogError(m); }
        }

        private void OnGUI()
        {
            GUILayout.Label("Avatar Physics Migrator", EditorStyles.boldLabel);
            EditorGUILayout.Space();

            sourceAvatar = (GameObject)EditorGUILayout.ObjectField("Source Avatar (Armature)", sourceAvatar, typeof(GameObject), true);
            targetAvatar = (GameObject)EditorGUILayout.ObjectField("Target Avatar (Biped)", targetAvatar, typeof(GameObject), true);

            EditorGUILayout.Space();

            if (GUILayout.Button("Analyze Source Avatar"))
            {
                if (ValidateInputs())
                {
                    Analyze();
                }
            }

            if (analysisResult != null)
            {
                EditorGUILayout.Space();
                GUILayout.Label("Analysis Results:", EditorStyles.boldLabel);
                EditorGUI.indentLevel++;

                // Name Match Rate
                float matchRate = analysisResult.totalTransforms > 0 ? (float)analysisResult.nameMatchCount / analysisResult.totalTransforms * 100f : 0f;
                EditorGUILayout.LabelField($"Name Match Rate: {analysisResult.nameMatchCount} / {analysisResult.totalTransforms} ({matchRate:F1}%)");

                // Duplicate Check
                if (analysisResult.duplicateCount > 0)
                {
                    EditorGUILayout.HelpBox($"Found {analysisResult.duplicateCount} duplicate names in Source Avatar!", MessageType.Warning);
                    if (GUILayout.Button("Show Duplicates"))
                    {
                        foreach (var name in analysisResult.duplicateNames.Take(10)) // Show first 10
                        {
                            Log($"Duplicate: {name}");
                        }
                        if (analysisResult.duplicateNames.Count > 10) Log($"...and {analysisResult.duplicateNames.Count - 10} more.");
                    }
                }
                else
                {
                    EditorGUILayout.LabelField("Source Duplicates: None (OK)", EditorStyles.miniLabel);
                }

                EditorGUILayout.Space();
                EditorGUILayout.LabelField($"Magica Cloths: {analysisResult.magicaClothCount}");
                EditorGUILayout.LabelField($"VRM Spring Bones: {analysisResult.vrmSpringCount}");
                EditorGUILayout.LabelField("Colliders:");
                EditorGUI.indentLevel++;
                EditorGUILayout.LabelField($"Capsule: {analysisResult.capsuleColliderCount}");
                EditorGUILayout.LabelField($"Sphere: {analysisResult.sphereColliderCount}");
                EditorGUILayout.LabelField($"Plane: {analysisResult.planeColliderCount}");
                EditorGUILayout.LabelField($"VRM Groups: {analysisResult.vrmColliderGroupCount}");
                EditorGUI.indentLevel--;
                EditorGUI.indentLevel--;
                EditorGUILayout.Space();
            }

            if (GUILayout.Button("Migrate Physics Components"))
            {
                if (ValidateInputs())
                {
                    Migrate();
                }
            }

            if (targetAvatar != null)
            {
                EditorGUILayout.Space();
                GUILayout.Label("MagicaCloth PreBuild Automation", EditorStyles.boldLabel);
                preBuildFolderPath = EditorGUILayout.TextField("Save Path", preBuildFolderPath);

                if (GUILayout.Button("Auto Create PreBuild Data (Target)"))
                {
                    AutoCreatePreBuildData();
                }
            }

            EditorGUILayout.Space();
            GUILayout.Label("Collider Cleanup (Selection)", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox("Hierarchy에서 선택한 오브젝트 하위의 콜라이더 오브젝트를 선택하거나 삭제합니다.", MessageType.Info);

            using (new EditorGUI.DisabledScope(Selection.activeGameObject == null))
            {
                if (GUILayout.Button("Select All Collider Objects"))
                {
                    SelectColliderObjects();
                }

                if (GUILayout.Button("Delete All Collider Objects (Safe)"))
                {
                    DeleteColliderObjectsSafe();
                }
            }

            EditorGUILayout.Space();
            GUILayout.Label("BlendShape Tools", EditorStyles.boldLabel);
            if (GUILayout.Button("Migrate BlendShapes"))
            {
                if (ValidateInputs())
                {
                    MigrateBlendShapes();
                }
            }
            if (GUILayout.Button("Reset All BlendShapes (Selected)"))
            {
                ResetBlendShapes();
            }

            EditorGUILayout.Space();
            GUILayout.Label("Log:", EditorStyles.boldLabel);
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(200));
            foreach (var log in logMessages)
            {
                GUILayout.Label(log);
            }
            EditorGUILayout.EndScrollView();
        }

        private void Log(string message)
        {
            logMessages.Add(message);
            Debug.Log($"[PhysicsMigrator] {message}");
            Repaint();
        }

        private void LogError(string message)
        {
            logMessages.Add($"[Error] {message}");
            Debug.LogError($"[PhysicsMigrator] {message}");
            Repaint();
        }

        private bool ValidateInputs()
        {
            logMessages.Clear();
            if (sourceAvatar == null || targetAvatar == null)
            {
                LogError("Please assign both Source and Target avatars.");
                return false;
            }

            if (sourceAvatar == targetAvatar)
            {
                LogError("Source and Target cannot be the same object.");
                return false;
            }

            // 중복 이름 검사를 코어로 위임. 통과(true)면 OK, 실패(false)면 중단.
            if (!AvatarMigrationCore.ValidateNoDuplicateNames(sourceAvatar.transform, new WindowLog(this)))
            {
                return false;
            }

            return true;
        }

        private void Migrate()
        {
            Log("Starting migration...");

            var log = new WindowLog(this);

            // 1. Build bone map
            var boneMap = AvatarMigrationCore.BuildBoneMap(sourceAvatar, targetAvatar, log);

            // 2. Migrate colliders + physics components
            AvatarMigrationCore.MigrateColliders(sourceAvatar.transform, targetAvatar.transform, boneMap, log);
            AvatarMigrationCore.MigrateMagicaCloth(sourceAvatar.transform, targetAvatar.transform, boneMap, log);
            AvatarMigrationCore.MigrateVRMSpringBone(sourceAvatar.transform, targetAvatar.transform, boneMap, log);

            Log("Migration completed successfully!");
        }

        private void Analyze()
        {
            Log("Analyzing source avatar...");
            analysisResult = new AnalysisResult();

            var sourceTransforms = sourceAvatar.GetComponentsInChildren<Transform>(true);
            var targetTransforms = targetAvatar.GetComponentsInChildren<Transform>(true);

            analysisResult.totalTransforms = sourceTransforms.Length;

            // 1. Name Match Rate
            var targetNames = new HashSet<string>(targetTransforms.Select(t => t.name));
            analysisResult.nameMatchCount = sourceTransforms.Count(t => targetNames.Contains(t.name));

            // 2. Duplicate Check
            var nameCounts = new Dictionary<string, int>();
            foreach (var t in sourceTransforms)
            {
                if (!nameCounts.ContainsKey(t.name)) nameCounts[t.name] = 0;
                nameCounts[t.name]++;
            }

            analysisResult.duplicateNames = nameCounts.Where(kv => kv.Value > 1).Select(kv => kv.Key).ToList();
            analysisResult.duplicateCount = analysisResult.duplicateNames.Count;

            // 3. Component Counts
            analysisResult.magicaClothCount = sourceAvatar.GetComponentsInChildren<MagicaCloth2.MagicaCloth>(true).Length;
            analysisResult.vrmSpringCount = sourceAvatar.GetComponentsInChildren<VRM.VRMSpringBone>(true).Length;

            analysisResult.capsuleColliderCount = sourceAvatar.GetComponentsInChildren<MagicaCloth2.MagicaCapsuleCollider>(true).Length;
            analysisResult.sphereColliderCount = sourceAvatar.GetComponentsInChildren<MagicaCloth2.MagicaSphereCollider>(true).Length;
            analysisResult.planeColliderCount = sourceAvatar.GetComponentsInChildren<MagicaCloth2.MagicaPlaneCollider>(true).Length;
            analysisResult.vrmColliderGroupCount = sourceAvatar.GetComponentsInChildren<VRM.VRMSpringBoneColliderGroup>(true).Length;

            Log("Analysis completed.");
        }

        private void AutoCreatePreBuildData()
        {
            if (targetAvatar == null) return;

            var cloths = targetAvatar.GetComponentsInChildren<MagicaCloth2.MagicaCloth>(true);
            if (cloths.Length == 0)
            {
                Log("No MagicaCloth components found on Target Avatar.");
                return;
            }

            string folderPath = $"{preBuildFolderPath}/{targetAvatar.name}";
            if (!AssetDatabase.IsValidFolder(folderPath))
            {
                // Create folder recursively if needed
                if (!AssetDatabase.IsValidFolder(preBuildFolderPath))
                {
                    string[] folders = preBuildFolderPath.Split('/');
                    string currentPath = folders[0];
                    for (int i = 1; i < folders.Length; i++)
                    {
                        if (!AssetDatabase.IsValidFolder($"{currentPath}/{folders[i]}"))
                        {
                            AssetDatabase.CreateFolder(currentPath, folders[i]);
                        }
                        currentPath += $"/{folders[i]}";
                    }
                }
                AssetDatabase.CreateFolder(preBuildFolderPath, targetAvatar.name);
            }

            int successCount = 0;
            foreach (var cloth in cloths)
            {
                try
                {
                    var preBuildData = cloth.GetSerializeData2().preBuildData;

                    // Enable PreBuild
                    preBuildData.enabled = true;

                    // Create ScriptableObject if missing
                    if (preBuildData.preBuildScriptableObject == null)
                    {
                        string assetName = $"PreBuild_{cloth.name}_{System.Guid.NewGuid().ToString().Substring(0, 8)}.asset";
                        string assetPath = $"{folderPath}/{assetName}";

                        var sobj = ScriptableObject.CreateInstance<MagicaCloth2.PreBuildScriptableObject>();
                        AssetDatabase.CreateAsset(sobj, assetPath);

                        preBuildData.preBuildScriptableObject = sobj;
                        EditorUtility.SetDirty(cloth);
                    }

                    // Run PreBuild
                    var result = MagicaCloth2.PreBuildDataCreation.CreatePreBuildData(cloth, false); // false = no dialog

                    if (result.IsSuccess())
                    {
                        successCount++;
                        Log($"[Success] PreBuild for '{cloth.name}'");
                    }
                    else
                    {
                        LogError($"[Fail] PreBuild for '{cloth.name}': {result.GetResultString()}");
                    }
                }
                catch (System.Exception e)
                {
                    LogError($"[Exception] PreBuild for '{cloth.name}': {e.Message}");
                }
            }

            AssetDatabase.SaveAssets();
            AssetDatabase.Refresh();
            Log($"Auto PreBuild Completed. Success: {successCount} / {cloths.Length}");
        }

        // ------------------------------------------------------------
        // Collider cleanup (selection-based utilities, UI 직속 편의기능)
        // ------------------------------------------------------------

        private List<GameObject> FindColliderObjects(GameObject root)
        {
            var result = new HashSet<GameObject>();

            foreach (var c in root.GetComponentsInChildren<MagicaCloth2.MagicaCapsuleCollider>(true))
                result.Add(c.gameObject);
            foreach (var c in root.GetComponentsInChildren<MagicaCloth2.MagicaSphereCollider>(true))
                result.Add(c.gameObject);
            foreach (var c in root.GetComponentsInChildren<MagicaCloth2.MagicaPlaneCollider>(true))
                result.Add(c.gameObject);
            foreach (var c in root.GetComponentsInChildren<VRM.VRMSpringBoneColliderGroup>(true))
                result.Add(c.gameObject);

            return result.ToList();
        }

        private HashSet<Transform> CollectAllBones(GameObject root)
        {
            var bones = new HashSet<Transform>();
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.rootBone != null)
                    bones.Add(smr.rootBone);
                if (smr.bones != null)
                {
                    foreach (var b in smr.bones)
                    {
                        if (b != null) bones.Add(b);
                    }
                }
            }
            return bones;
        }

        private void SelectColliderObjects()
        {
            var root = Selection.activeGameObject;
            if (root == null) return;

            var colliderObjects = FindColliderObjects(root);
            if (colliderObjects.Count == 0)
            {
                Log($"'{root.name}' 하위에 콜라이더 오브젝트가 없습니다.");
                return;
            }

            Selection.objects = colliderObjects.ToArray();
            Log($"{colliderObjects.Count}개의 콜라이더 오브젝트를 선택했습니다.");
        }

        private void DeleteColliderObjectsSafe()
        {
            var root = Selection.activeGameObject;
            if (root == null) return;

            var colliderObjects = FindColliderObjects(root);
            if (colliderObjects.Count == 0)
            {
                Log($"'{root.name}' 하위에 콜라이더 오브젝트가 없습니다.");
                return;
            }

            var allBones = CollectAllBones(root);
            var boneConflicts = colliderObjects
                .Where(go => allBones.Contains(go.transform))
                .ToList();

            if (boneConflicts.Count > 0)
            {
                var names = string.Join("\n  ", boneConflicts.Select(go => go.name));
                var message = $"{boneConflicts.Count}개의 콜라이더 오브젝트가 SkinnedMeshRenderer 본으로 사용 중입니다:\n  {names}\n\n" +
                    "이 오브젝트들은 삭제에서 제외하고, 컴포넌트만 제거합니다.\n나머지 오브젝트는 삭제합니다. 계속하시겠습니까?";

                if (!EditorUtility.DisplayDialog("Bone Conflict Detected", message, "Continue", "Cancel"))
                    return;
            }
            else
            {
                if (!EditorUtility.DisplayDialog("Delete Collider Objects",
                    $"{colliderObjects.Count}개의 콜라이더 오브젝트를 삭제합니다.\n계속하시겠습니까?", "Delete", "Cancel"))
                    return;
            }

            Undo.SetCurrentGroupName("Delete Collider Objects");
            int deletedCount = 0;
            int strippedCount = 0;

            foreach (var go in colliderObjects)
            {
                if (go == null) continue;

                if (allBones.Contains(go.transform))
                {
                    foreach (var c in go.GetComponents<MagicaCloth2.MagicaCapsuleCollider>())
                        Undo.DestroyObjectImmediate(c);
                    foreach (var c in go.GetComponents<MagicaCloth2.MagicaSphereCollider>())
                        Undo.DestroyObjectImmediate(c);
                    foreach (var c in go.GetComponents<MagicaCloth2.MagicaPlaneCollider>())
                        Undo.DestroyObjectImmediate(c);
                    foreach (var c in go.GetComponents<VRM.VRMSpringBoneColliderGroup>())
                        Undo.DestroyObjectImmediate(c);
                    strippedCount++;
                }
                else
                {
                    Undo.DestroyObjectImmediate(go);
                    deletedCount++;
                }
            }

            Log($"삭제: {deletedCount}개 오브젝트 / 컴포넌트만 제거: {strippedCount}개 (본 보호)");
        }

        // ------------------------------------------------------------
        // BlendShape (UI 진입점 — 코어 호출)
        // ------------------------------------------------------------

        private void MigrateBlendShapes()
        {
            Log("Starting BlendShape migration...");
            AvatarMigrationCore.MigrateBlendShapes(sourceAvatar, targetAvatar, new WindowLog(this));
        }

        private void ResetBlendShapes()
        {
            var selected = Selection.activeGameObject;
            if (selected == null)
            {
                Log("Please select a GameObject to reset BlendShapes.");
                return;
            }

            var renderers = selected.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            int resetCount = 0;

            foreach (var smr in renderers)
            {
                if (smr.sharedMesh == null) continue;

                int count = smr.sharedMesh.blendShapeCount;
                if (count > 0)
                {
                    for (int i = 0; i < count; i++)
                    {
                        smr.SetBlendShapeWeight(i, 0f);
                    }
                    resetCount++;
                }
            }

            Log($"Reset BlendShapes for {resetCount} renderers in '{selected.name}'.");
        }
    }
}
