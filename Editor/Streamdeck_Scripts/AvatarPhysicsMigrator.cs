using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using F10.StreamDeckIntegration;
using F10.StreamDeckIntegration.Attributes;

// Namespace handling for optional dependencies
using MagicaCloth2;
using VRM;

namespace YAMO.Tools
{
    [InitializeOnLoad]
    public class AvatarPhysicsMigrator : EditorWindow
    {
        static AvatarPhysicsMigrator()
        {
            StreamDeck.AddStatic(typeof(AvatarPhysicsMigrator));
        }

        public static void ShowWindow()
        {
            GetWindow<AvatarPhysicsMigrator>("Physics Migrator");
        }

        [MenuItem("Tools/Avatar Physics Migrator")]
        [StreamDeckButton("PhysicsMigrator_Toggle")]
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
        private bool isProcessing = false;
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

            // Check for duplicate names in Source
            if (HasDuplicateNames(sourceAvatar.transform))
            {
                return false;
            }

            return true;
        }

        private bool HasDuplicateNames(Transform root)
        {
            var names = new HashSet<string>();
            var duplicates = new List<string>();
            
            void Traverse(Transform t)
            {
                if (!names.Add(t.name))
                {
                    duplicates.Add(t.name);
                }
                foreach (Transform child in t) Traverse(child);
            }

            Traverse(root);

            if (duplicates.Count > 0)
            {
                LogError($"Duplicate names found in Source Avatar: {string.Join(", ", duplicates.Distinct())}");
                LogError("Please rename duplicate objects to ensure unique mapping.");
                return true;
            }

            return false;
        }

        private void Migrate()
        {
            isProcessing = true;
            Log("Starting migration...");

            // 1. Build Bone Map
            var boneMap = BuildBoneMap(sourceAvatar.transform, targetAvatar.transform);
            if (boneMap == null)
            {
                isProcessing = false;
                return;
            }

            // 2. Copy Components
            CopyColliders(sourceAvatar.transform, boneMap);
            CopyPhysicsComponents(sourceAvatar.transform, boneMap);

            Log("Migration completed successfully!");
            isProcessing = false;
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

        private Dictionary<Transform, Transform> BuildBoneMap(Transform sourceRoot, Transform targetRoot)
        {
            var map = new Dictionary<Transform, Transform>();
            var sourceAnimator = sourceAvatar.GetComponent<Animator>();
            var targetAnimator = targetAvatar.GetComponent<Animator>();

            // Phase 1: Humanoid Mapping
            // Ensure Root is mapped
            map[sourceRoot] = targetRoot;

            if (sourceAnimator != null && sourceAnimator.isHuman && targetAnimator != null && targetAnimator.isHuman)
            {
                foreach (HumanBodyBones bone in System.Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (bone == HumanBodyBones.LastBone) continue;

                    var sBone = sourceAnimator.GetBoneTransform(bone);
                    var tBone = targetAnimator.GetBoneTransform(bone);

                    if (sBone != null && tBone != null)
                    {
                        map[sBone] = tBone;
                    }
                }
                Log($"Mapped {map.Count} humanoid bones.");
            }
            else
            {
                Log("Warning: One or both avatars are not Humanoid. Skipping Humanoid mapping.");
            }

            // Phase 2: Name Mapping (for non-humanoid bones)
            // We traverse Source and try to find matching name in Target
            // Optimization: Cache Target transforms by name? 
            // Issue: Target might have duplicates too? We assume Target is clean or we just find first match.
            // Better: Traverse Source, if not in map, search in Target.
            
            // To avoid finding wrong objects in Target, we should search relative to the mapped parent if possible,
            // but structure is different (Armature vs Biped). 
            // So global name search in Target is the fallback.
            
            var targetTransforms = targetRoot.GetComponentsInChildren<Transform>(true)
                .GroupBy(t => t.name)
                .ToDictionary(g => g.Key, g => g.First()); // Take first if duplicates exist in Target (User didn't ask to check Target duplicates, but good to know)

            void MapRecursive(Transform current)
            {
                if (!map.ContainsKey(current))
                {
                    if (targetTransforms.TryGetValue(current.name, out var targetMatch))
                    {
                        map[current] = targetMatch;
                    }
                }

                foreach (Transform child in current)
                {
                    MapRecursive(child);
                }
            }

            MapRecursive(sourceRoot);
            
            Log($"Total mapped transforms: {map.Count}");
            return map;
        }

        private void CopyColliders(Transform sourceRoot, Dictionary<Transform, Transform> boneMap)
        {
            // Magica Cloth 2 Colliders
            // MagicaCapsuleCollider, MagicaSphereCollider, MagicaPlaneCollider
            // VRM SpringBoneColliderGroup

            // Collect only transforms that have the relevant components
            var transformsWithColliders = new HashSet<Transform>();
            
            void Collect<T>() where T : Component
            {
                foreach (var c in sourceRoot.GetComponentsInChildren<T>(true))
                {
                    transformsWithColliders.Add(c.transform);
                }
            }

            Collect<MagicaCloth2.MagicaCapsuleCollider>();
            Collect<MagicaCloth2.MagicaSphereCollider>();
            Collect<MagicaCloth2.MagicaPlaneCollider>();
            Collect<VRM.VRMSpringBoneColliderGroup>();

            foreach (var src in transformsWithColliders)
            {
                // Check if we have a destination parent
                Transform destParent = null;
                if (boneMap.TryGetValue(src, out var mappedDest))
                {
                    destParent = mappedDest;
                }
                else
                {
                    // Find nearest mapped parent
                    var p = src.parent;
                    while (p != null)
                    {
                        if (boneMap.TryGetValue(p, out var m))
                        {
                            // Check if it already exists by name under the mapped parent
                            var existing = m.Find(src.name);
                            if (existing != null) destParent = existing;
                            else
                            {
                                // Duplicate the source object with explicit World Position and Rotation
                                // This effectively "unlinks" it from the source hierarchy during creation
                                var newObj = Instantiate(src.gameObject, src.position, src.rotation);
                                newObj.name = src.name;
                                
                                // Parent to the target bone, MAINTAINING world position/rotation
                                newObj.transform.SetParent(m, true);
                                
                                destParent = newObj.transform;
                                
                                // Add to map for children
                                boneMap[src] = destParent;
                                
                                // Since we instantiated, we already have the components!
                                // We don't need to CopyComponent again for this object.
                                // However, we must continue to the next iteration to avoid adding duplicates below.
                                goto NextItem; 
                            }
                            break;
                        }
                        p = p.parent;
                    }
                }

                if (destParent == null) continue;

                // Copy Components
                CopyComponent<MagicaCloth2.MagicaCapsuleCollider>(src, destParent);
                CopyComponent<MagicaCloth2.MagicaSphereCollider>(src, destParent);
                CopyComponent<MagicaCloth2.MagicaPlaneCollider>(src, destParent);
                CopyComponent<VRM.VRMSpringBoneColliderGroup>(src, destParent);

                NextItem:;
            }
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

        private void CopyPhysicsComponents(Transform sourceRoot, Dictionary<Transform, Transform> boneMap)
        {
            // Helper to get or create destination transform
            Transform GetOrCreateDestination(Transform src, bool ignoreParent = false)
            {
                if (boneMap.TryGetValue(src, out var d)) return d;

                Transform mappedParent = null;

                if (ignoreParent)
                {
                    mappedParent = targetAvatar.transform;
                }
                else
                {
                    // Find nearest mapped parent
                    var p = src.parent;
                    while (p != null)
                    {
                        if (boneMap.TryGetValue(p, out var m))
                        {
                            mappedParent = m;
                            break;
                        }
                        p = p.parent;
                    }
                }

                // Fallback to root if still null
                if (mappedParent == null) mappedParent = targetAvatar.transform;

                // Check if it already exists by name under the mapped parent
                var existing = mappedParent.Find(src.name);
                if (existing != null)
                {
                    boneMap[src] = existing;
                    return existing;
                }

                // Create new
                var newObj = new GameObject(src.name);
                newObj.transform.SetParent(mappedParent, false);
                newObj.transform.localPosition = src.localPosition;
                newObj.transform.localRotation = src.localRotation;
                newObj.transform.localScale = src.localScale;
                
                boneMap[src] = newObj.transform;
                return newObj.transform;
            }

            // MagicaCloth2
            var magicaCloths = sourceRoot.GetComponentsInChildren<MagicaCloth2.MagicaCloth>(true);
            foreach (var mc in magicaCloths)
            {
                // User requested to ignore parentage for MagicaCloth and put under Root
                var dest = GetOrCreateDestination(mc.transform, true);
                if (dest != null)
                {
                    var newMc = GetOrAddComponent<MagicaCloth2.MagicaCloth>(dest.gameObject);
                    EditorUtility.CopySerialized(mc, newMc);
                    
                    // Remap References
                    var so = new SerializedObject(newMc);
                    
                    // Root Bones (Transforms)
                    var rootListProp = so.FindProperty("serializeData.rootBones");
                    if (rootListProp != null) RemapTransformList(rootListProp, boneMap);
                    else Log("Warning: Could not find 'serializeData.rootBones'.");

                    // Colliders (Components)
                    var colliderListProp = so.FindProperty("serializeData.colliderCollisionConstraint.colliderList");
                    if (colliderListProp != null) RemapComponentList(colliderListProp, boneMap);
                    else Log("Warning: Could not find 'serializeData.colliderCollisionConstraint.colliderList'.");

                    // Source Renderers (Components)
                    // Try common property names for renderers
                    var rendererListProp = so.FindProperty("serializeData.sourceRenderers"); 
                    if (rendererListProp != null) RemapComponentList(rendererListProp, boneMap);
                    else 
                    {
                        // Fallback or log
                        Log("Info: Could not find 'serializeData.sourceRenderers'. Checking for 'sourceRenderers'...");
                        rendererListProp = so.FindProperty("sourceRenderers");
                        if (rendererListProp != null) RemapComponentList(rendererListProp, boneMap);
                    }

                    so.ApplyModifiedProperties();
                }
                else
                {
                    Log($"Warning: Could not find a place to copy MagicaCloth from '{mc.name}'.");
                }
            }

            // VRMSpringBone
            var vrmSprings = sourceRoot.GetComponentsInChildren<VRM.VRMSpringBone>(true);
            foreach (var vs in vrmSprings)
            {
                var dest = GetOrCreateDestination(vs.transform, false);
                if (dest != null)
                {
                    var newVs = GetOrAddComponent<VRM.VRMSpringBone>(dest.gameObject);
                    EditorUtility.CopySerialized(vs, newVs);

                    newVs.RootBones = RemapList(vs.RootBones, boneMap);
                    newVs.ColliderGroups = RemapColliderGroups(vs.ColliderGroups, boneMap);
                }
                else
                {
                    Log($"Warning: Could not find a place to copy VRMSpringBone from '{vs.name}'.");
                }
            }
        }

        // Helpers
        private void CopyComponent<T>(Transform src, Transform dest) where T : Component
        {
            var comps = src.GetComponents<T>();
            foreach (var comp in comps)
            {
                var newComp = GetOrAddComponent<T>(dest.gameObject);
                EditorUtility.CopySerialized(comp, newComp);
            }
        }

        private T GetOrAddComponent<T>(GameObject go) where T : Component
        {
            var comp = go.GetComponent<T>();
            if (comp == null) comp = go.AddComponent<T>();
            return comp;
        }

        private void RemapComponentList(SerializedProperty listProp, Dictionary<Transform, Transform> map)
        {
            if (listProp == null) return;

            for (int i = listProp.arraySize - 1; i >= 0; i--)
            {
                var elem = listProp.GetArrayElementAtIndex(i);
                var originalComp = elem.objectReferenceValue as Component;

                if (originalComp == null) continue;

                if (map.TryGetValue(originalComp.transform, out var mappedTransform))
                {
                    // Try to find the same component type on the mapped transform
                    var newComp = mappedTransform.GetComponent(originalComp.GetType());
                    if (newComp != null)
                    {
                        elem.objectReferenceValue = newComp;
                    }
                    else
                    {
                        Log($"Warning: Mapped transform '{mappedTransform.name}' does not have component '{originalComp.GetType().Name}'.");
                        elem.objectReferenceValue = null;
                    }
                }
                else
                {
                    Log($"Warning: Could not map transform for component '{originalComp.name}' ({originalComp.GetType().Name}).");
                    elem.objectReferenceValue = null;
                }
            }
        }

        private void RemapTransformList(SerializedProperty listProp, Dictionary<Transform, Transform> map)
        {
            if (listProp == null) return;
            
            for (int i = listProp.arraySize - 1; i >= 0; i--)
            {
                var elem = listProp.GetArrayElementAtIndex(i);
                var original = elem.objectReferenceValue as Transform;
                
                if (original == null) continue;

                if (map.TryGetValue(original, out var mapped))
                {
                    elem.objectReferenceValue = mapped;
                }
                else
                {
                    // If not mapped, remove? Or keep null?
                    // Keeping it might cause errors. Removing is safer for physics.
                    // But let's warn.
                    Log($"Warning: Could not map transform '{original.name}' in list.");
                    elem.objectReferenceValue = null; 
                }
            }
        }

        private List<Transform> RemapList(List<Transform> sourceList, Dictionary<Transform, Transform> map)
        {
            var newList = new List<Transform>();
            foreach (var t in sourceList)
            {
                if (t != null && map.TryGetValue(t, out var mapped))
                {
                    newList.Add(mapped);
                }
            }
            return newList;
        }

        private VRM.VRMSpringBoneColliderGroup[] RemapColliderGroups(VRM.VRMSpringBoneColliderGroup[] sourceList, Dictionary<Transform, Transform> map)
        {
            var newList = new List<VRM.VRMSpringBoneColliderGroup>();
            foreach (var c in sourceList)
            {
                if (c != null && map.TryGetValue(c.transform, out var mappedTransform))
                {
                    var mappedCollider = mappedTransform.GetComponent<VRM.VRMSpringBoneColliderGroup>();
                    if (mappedCollider != null)
                    {
                        newList.Add(mappedCollider);
                    }
                }
            }
            return newList.ToArray();
        }

        private void MigrateBlendShapes()
        {
            Log("Starting BlendShape migration...");
            var sourceRenderers = sourceAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var targetRenderers = targetAvatar.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var targetDict = targetRenderers.ToDictionary(r => r.name, r => r);

            int migratedCount = 0;

            foreach (var sourceSMR in sourceRenderers)
            {
                if (targetDict.TryGetValue(sourceSMR.name, out var targetSMR))
                {
                    var sourceMesh = sourceSMR.sharedMesh;
                    var targetMesh = targetSMR.sharedMesh;

                    if (sourceMesh == null || targetMesh == null) continue;

                    int shapeCount = sourceMesh.blendShapeCount;
                    bool anyChanged = false;

                    for (int i = 0; i < shapeCount; i++)
                    {
                        string shapeName = sourceMesh.GetBlendShapeName(i);
                        float weight = sourceSMR.GetBlendShapeWeight(i);

                        // Find index in target
                        int targetIndex = targetMesh.GetBlendShapeIndex(shapeName);
                        if (targetIndex != -1)
                        {
                            targetSMR.SetBlendShapeWeight(targetIndex, weight);
                            anyChanged = true;
                        }
                    }

                    if (anyChanged)
                    {
                        migratedCount++;
                    }
                }
            }

            Log($"BlendShape migration completed. Updated {migratedCount} SkinnedMeshRenderers.");
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
