#if UNITY_6000_0_OR_NEWER
using System;
using System.IO;
using System.Collections.Generic;
using UnityEngine.SceneManagement;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    public sealed class AvatarSnapshotWindow : EditorWindow
    {
        private const string MenuPath = "Tools/YAMO/Animation/Avatar Snapshot";
        private static string FolderKey => "YAMO.AvatarSnapshot.Folder:" + Application.dataPath;
        [SerializeField] private GameObject target;
        [SerializeField] private string snapshotName = "";
        [SerializeField] private GameObject lastSnapshot;
        [SerializeField] private string result;
        [SerializeField] private MessageType resultType;

        [MenuItem(MenuPath)]
        public static void Open() => GetWindow<AvatarSnapshotWindow>("Avatar Snapshot");

        private void OnEnable()
        {
            if (!target && IsSceneObject(Selection.activeGameObject))
                target = Selection.activeGameObject;
            EditorApplication.playModeStateChanged += OnPlayModeChanged;
        }

        private void OnDisable() => EditorApplication.playModeStateChanged -= OnPlayModeChanged;
        private void OnPlayModeChanged(PlayModeStateChange _) => Repaint();

        private void OnGUI()
        {
            EditorGUILayout.HelpBox("플레이 중인 아바타의 포즈와 블렌드셰이프를 편집 가능한 프리팹으로 저장합니다. 원하는 프레임에서 Unity를 일시정지한 뒤 저장할 수 있습니다.", MessageType.Info);

            using (new EditorGUILayout.HorizontalScope())
            {
                target = (GameObject)EditorGUILayout.ObjectField("아바타", target, typeof(GameObject), true);
                using (new EditorGUI.DisabledScope(!IsSceneObject(Selection.activeGameObject)))
                    if (GUILayout.Button("선택 사용", GUILayout.Width(76)))
                        target = Selection.activeGameObject;
            }

            var root = ResolveRoot(target);
            if (root && root != target)
                EditorGUILayout.LabelField("캡처할 프리팹 루트", root.name);

            var folderPath = EditorPrefs.GetString(FolderKey, "");
            var folder = AssetDatabase.LoadAssetAtPath<DefaultAsset>(folderPath);
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUI.BeginChangeCheck();
                var picked = (DefaultAsset)EditorGUILayout.ObjectField("저장 폴더 (선택)", folder, typeof(DefaultAsset), false);
                if (EditorGUI.EndChangeCheck())
                {
                    var pickedPath = AssetDatabase.GetAssetPath(picked);
                    if (!picked || (IsAssetsPath(pickedPath) && AssetDatabase.IsValidFolder(pickedPath)))
                    {
                        folderPath = pickedPath;
                        EditorPrefs.SetString(FolderKey, folderPath);
                        result = null;
                    }
                    else
                    {
                        result = "Assets 안의 폴더를 선택하세요.";
                        resultType = MessageType.Warning;
                    }
                }
                using (new EditorGUI.DisabledScope(string.IsNullOrEmpty(folderPath)))
                    if (GUILayout.Button("기본값", GUILayout.Width(76)))
                    {
                        folderPath = "";
                        EditorPrefs.DeleteKey(FolderKey);
                        result = null;
                    }
            }

            var destination = folderPath.Length == 0 ? DefaultFolder(root) : folderPath;
            var validFolder = IsAssetsPath(destination) &&
                              (folderPath.Length == 0 || AssetDatabase.IsValidFolder(folderPath));
            EditorGUILayout.LabelField("실제 저장 경로", destination ?? "저장된 씬 또는 Assets 폴더가 필요합니다.");
            snapshotName = EditorGUILayout.TextField("이름 (선택)", snapshotName);
            if (!EditorApplication.isPlaying)
                EditorGUILayout.HelpBox("Play Mode에서 캡처할 수 있습니다.", MessageType.Info);
            if (folderPath.Length > 0 && !validFolder)
                EditorGUILayout.HelpBox("지정 폴더를 찾을 수 없습니다. 다시 선택하거나 기본값으로 되돌리세요.", MessageType.Warning);

            using (new EditorGUI.DisabledScope(!EditorApplication.isPlaying || !root || !validFolder))
            {
                if (GUILayout.Button("현재 포즈를 프리팹으로 저장", GUILayout.Height(30)))
                {
                    try
                    {
                        lastSnapshot = Save(root, destination, snapshotName, out int omitted);
                        result = AssetDatabase.GetAssetPath(lastSnapshot);
                        if (omitted > 0)
                            result += $"\n프리팹에 저장할 수 없는 외부 씬/런타임 참조 {omitted}개를 비웠습니다.";
                        resultType = omitted == 0 ? MessageType.Info : MessageType.Warning;
                        EditorGUIUtility.PingObject(lastSnapshot);
                    }
                    catch (Exception e)
                    {
                        result = e.Message;
                        resultType = MessageType.Error;
                        Debug.LogException(e);
                    }
                }
            }

            if (!string.IsNullOrEmpty(result))
                EditorGUILayout.HelpBox(result, resultType);
            if (lastSnapshot && GUILayout.Button("마지막 스냅샷 찾기"))
                EditorGUIUtility.PingObject(lastSnapshot);
        }

        internal static bool IsSceneObject(GameObject go) =>
            go && !EditorUtility.IsPersistent(go) && go.scene.IsValid() && go.scene.isLoaded &&
            !EditorSceneManager.IsPreviewScene(go.scene) && PrefabStageUtility.GetPrefabStage(go) == null;

        internal static GameObject ResolveRoot(GameObject go)
        {
            if (!IsSceneObject(go))
                return null;
            var nearest = PrefabUtility.GetNearestPrefabInstanceRoot(go);
            if (nearest)
                return nearest;
            for (var current = go.transform; current; current = current.parent)
                if (SnapshotPrefabOrigins.GetPath(current.gameObject) != null)
                    return current.gameObject;
            return go;
        }

        internal static string DefaultFolder(GameObject root)
        {
            if (!root)
                return null;
            var scenePath = root.scene.path;
            if (string.IsNullOrEmpty(scenePath))
                scenePath = UnityEngine.SceneManagement.SceneManager.GetActiveScene().path;
            return IsAssetsPath(scenePath) ? scenePath.Substring(0, scenePath.LastIndexOf('/')) + "/Snapshot" : null;
        }

        private static bool IsAssetsPath(string path) => path == "Assets" ||
            (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/", StringComparison.Ordinal) &&
             Array.TrueForAll(path.Split('/'), part => part.Length > 0 && part != "." && part != ".."));

        internal static GameObject Save(GameObject target, string folder, string name, out int omitted)
        {
            omitted = 0;
            var source = ResolveRoot(target);
            if (!source)
                throw new ArgumentException("Hierarchy에서 캡처할 아바타를 선택하세요.");
            if (!IsAssetsPath(folder))
                throw new ArgumentException("저장 경로는 Assets 안에 있어야 합니다.");
            if (PrefabUtility.IsPrefabAssetMissing(source))
                throw new InvalidOperationException("원본 프리팹이 누락되어 Variant로 저장할 수 없습니다.");

            var basePath = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(source);
            if (string.IsNullOrEmpty(basePath))
                basePath = SnapshotPrefabOrigins.GetPath(source);
            var baseAsset = string.IsNullOrEmpty(basePath) ? null : AssetDatabase.LoadAssetAtPath<GameObject>(basePath);
            if (!string.IsNullOrEmpty(basePath) && !baseAsset)
                throw new InvalidOperationException("원본 프리팹 에셋을 찾을 수 없습니다: " + basePath);
            var pose = new Pose(source.transform);
            var rootPosition = source.transform.position;
            var rootRotation = source.transform.rotation;
            var rootScale = source.transform.lossyScale;
            var active = source.activeSelf;
            GameObject copy = null;
            try
            {
                // Editor duplication retains the prefab links and remaps references within the hierarchy.
                copy = GameObjectUtility.DuplicateGameObject(source);
                if (!copy)
                    throw new InvalidOperationException("아바타를 복제할 수 없습니다.");

                var preview = EditorSceneManager.NewPreviewScene();
                try
                {
                    copy.SetActive(false);
                    copy.transform.SetParent(null, false);
                    if (baseAsset && !PrefabUtility.IsPartOfPrefabInstance(copy))
                    {
                        PrefabUtility.ConvertToPrefabInstance(copy, baseAsset, new ConvertToPrefabInstanceSettings
                        {
                            objectMatchMode = ObjectMatchMode.ByHierarchy,
                            recordPropertyOverridesOfMatches = true,
                            gameObjectsNotMatchedBecomesOverride = true,
                            componentsNotMatchedBecomesOverride = false
                        }, InteractionMode.AutomatedAction);
                    }

                    if (baseAsset && (!PrefabUtility.IsOutermostPrefabInstanceRoot(copy) ||
                                      PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(copy) != basePath))
                        throw new InvalidOperationException("원본 프리팹 연결을 복원할 수 없습니다. 아바타 프리팹을 독립된 인스턴스로 배치한 뒤 다시 시도하세요.");

                    SceneManager.MoveGameObjectToScene(copy, preview);
                    FreezeDrivers(copy);
                    omitted = ClearExternalReferences(copy);
                    pose.Apply(copy.transform);
                    copy.transform.SetPositionAndRotation(rootPosition, rootRotation);
                    copy.transform.localScale = rootScale;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(copy.transform);
                    copy.SetActive(active);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(copy);

                    var safeName = string.IsNullOrWhiteSpace(name) ? source.name + "_Snapshot" : name.Trim();
                    foreach (char c in Path.GetInvalidFileNameChars())
                        safeName = safeName.Replace(c, '_');
                    safeName = safeName.TrimEnd('.', ' ');
                    if (string.IsNullOrEmpty(safeName))
                        throw new ArgumentException("프리팹 이름을 입력하세요.");

                    EnsureFolder(folder);
                    var path = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + safeName + ".prefab");
                    var saved = PrefabUtility.SaveAsPrefabAsset(copy, path, out bool success);
                    if (!success || !saved)
                        throw new IOException("스냅샷 프리팹을 저장하지 못했습니다: " + path);
                    return saved;
                }
                finally
                {
                    if (copy)
                        DestroyImmediate(copy);
                    EditorSceneManager.ClosePreviewScene(preview);
                }
            }
            finally
            {
                if (copy)
                    DestroyImmediate(copy);
            }
        }

        private static void EnsureFolder(string folder)
        {
            if (AssetDatabase.IsValidFolder(folder))
                return;
            var slash = folder.LastIndexOf('/');
            var parent = folder.Substring(0, slash);
            EnsureFolder(parent);
            var guid = AssetDatabase.CreateFolder(parent, folder.Substring(slash + 1));
            if (string.IsNullOrEmpty(guid) || !AssetDatabase.IsValidFolder(folder))
                throw new IOException("저장 폴더를 만들 수 없습니다: " + folder);
        }

        private static void FreezeDrivers(GameObject copy)
        {
            foreach (var component in copy.GetComponentsInChildren<Behaviour>(true))
            {
                // Keep the rig components available for later editing without letting them drive the saved frame.
                if (component is Animator || component is Animation || component is MonoBehaviour ||
                    component is UnityEngine.Playables.PlayableDirector ||
                    component is UnityEngine.Animations.IConstraint)
                {
                    component.enabled = false;
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                }
            }
            foreach (var cloth in copy.GetComponentsInChildren<Cloth>(true))
            {
                cloth.enabled = false;
                PrefabUtility.RecordPrefabInstancePropertyModifications(cloth);
            }
            foreach (var body in copy.GetComponentsInChildren<Rigidbody>(true))
            {
                body.isKinematic = true;
                PrefabUtility.RecordPrefabInstancePropertyModifications(body);
            }
        }

        private static int ClearExternalReferences(GameObject root)
        {
            int count = 0;
            foreach (var component in root.GetComponentsInChildren<Component>(true))
            {
                if (!component || component is Transform)
                    continue;
                using (var serialized = new SerializedObject(component))
                {
                    var property = serialized.GetIterator();
                    bool changed = false;
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference || !property.editable ||
                            property.propertyPath == "m_PrefabInstance" || property.propertyPath == "m_CorrespondingSourceObject" ||
                            property.propertyPath == "m_PrefabAsset" || property.propertyPath == "m_GameObject")
                            continue;
                        var reference = property.objectReferenceValue;
                        if (!reference || EditorUtility.IsPersistent(reference))
                            continue;
                        var transform = reference is Component c ? c.transform :
                            reference is GameObject go ? go.transform : null;
                        if (transform && (transform == root.transform || transform.IsChildOf(root.transform)))
                            continue;
                        property.objectReferenceValue = null;
                        changed = true;
                        count++;
                    }
                    if (changed)
                    {
                        serialized.ApplyModifiedPropertiesWithoutUndo();
                        PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                    }
                }
            }
            return count;
        }

        private sealed class Pose
        {
            private readonly Vector3 position, scale;
            private readonly Quaternion rotation;
            private readonly bool active;
            private readonly float[][] weights;
            private readonly Pose[] children;

            internal Pose(Transform source)
            {
                position = source.localPosition;
                rotation = source.localRotation;
                scale = source.localScale;
                active = source.gameObject.activeSelf;
                var renderers = source.GetComponents<SkinnedMeshRenderer>();
                weights = new float[renderers.Length][];
                for (int i = 0; i < renderers.Length; i++)
                {
                    var renderer = renderers[i];
                    var mesh = renderer.sharedMesh;
                    weights[i] = new float[mesh ? mesh.blendShapeCount : 0];
                    for (int j = 0; j < weights[i].Length; j++)
                        weights[i][j] = renderer.GetBlendShapeWeight(j);
                }
                children = new Pose[source.childCount];
                for (int i = 0; i < children.Length; i++)
                    children[i] = new Pose(source.GetChild(i));
            }

            internal void Apply(Transform copy, bool isRoot = true)
            {
                var renderers = copy.GetComponents<SkinnedMeshRenderer>();
                if (copy.childCount != children.Length || renderers.Length != weights.Length)
                    throw new InvalidOperationException("복제하는 동안 아바타 계층이 바뀌어 캡처할 수 없습니다.");

                copy.SetLocalPositionAndRotation(position, rotation);
                copy.localScale = scale;
                PrefabUtility.RecordPrefabInstancePropertyModifications(copy);
                if (!isRoot)
                {
                    copy.gameObject.SetActive(active);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(copy.gameObject);
                }

                for (int i = 0; i < renderers.Length; i++)
                {
                    var mesh = renderers[i].sharedMesh;
                    if (!mesh)
                        continue;
                    for (int j = 0; j < Math.Min(weights[i].Length, mesh.blendShapeCount); j++)
                        renderers[i].SetBlendShapeWeight(j, weights[i][j]);
                    PrefabUtility.RecordPrefabInstancePropertyModifications(renderers[i]);
                }

                for (int i = 0; i < children.Length; i++)
                    children[i].Apply(copy.GetChild(i), false);
            }
        }
    }
    // Scene instances can lose prefab metadata in a player-style Play Mode reload.
    [InitializeOnLoad]
    internal static class SnapshotPrefabOrigins
    {
        private const string Key = "YAMO.AvatarSnapshot.Origins";

        [Serializable]
        private sealed class Entry
        {
            public int scene;
            public string scenePath;
            public int[] indices;
            public string[] names;
            public string prefabPath;
        }

        [Serializable]
        private sealed class Entries
        {
            public List<Entry> values = new List<Entry>();
        }

        private static readonly Dictionary<GameObject, string> Paths = new Dictionary<GameObject, string>();

        static SnapshotPrefabOrigins()
        {
            EditorApplication.playModeStateChanged += state =>
            {
                switch (state)
                {
                    case PlayModeStateChange.ExitingEditMode: Record(); break;
                    case PlayModeStateChange.EnteredPlayMode: Bind(); break;
                    case PlayModeStateChange.EnteredEditMode:
                        Paths.Clear();
                        SessionState.EraseString(Key);
                        break;
                }
            };
            if (EditorApplication.isPlaying)
                Bind();
        }

        internal static string GetPath(GameObject target)
        {
            Paths.TryGetValue(target, out var path);
            return path;
        }

        private static void Record()
        {
            Paths.Clear();
            var entries = new Entries();
            for (int i = 0; i < SceneManager.sceneCount; i++)
            {
                var scene = SceneManager.GetSceneAt(i);
                if (!scene.isLoaded || EditorSceneManager.IsPreviewScene(scene))
                    continue;
                foreach (var root in scene.GetRootGameObjects())
                    foreach (var item in root.GetComponentsInChildren<Transform>(true))
                    {
                        var go = item.gameObject;
                        if (PrefabUtility.GetNearestPrefabInstanceRoot(go) != go)
                            continue;
                        var path = PrefabUtility.GetPrefabAssetPathOfNearestInstanceRoot(go);
                        if (string.IsNullOrEmpty(path))
                            continue;
                        var indices = new List<int>();
                        var names = new List<string>();
                        for (var t = item; t; t = t.parent)
                        {
                            indices.Add(t.GetSiblingIndex());
                            names.Add(t.name);
                        }
                        indices.Reverse();
                        names.Reverse();
                        entries.values.Add(new Entry { scene = i, scenePath = scene.path, indices = indices.ToArray(),
                                                       names = names.ToArray(), prefabPath = path });
                    }
            }
            SessionState.SetString(Key, JsonUtility.ToJson(entries));
        }

        private static void Bind()
        {
            var json = SessionState.GetString(Key, "");
            if (string.IsNullOrEmpty(json))
                return;
            var roots = new Dictionary<int, GameObject[]>();
            foreach (var entry in JsonUtility.FromJson<Entries>(json).values)
            {
                if (entry.scene >= SceneManager.sceneCount)
                    continue;
                var scene = SceneManager.GetSceneAt(entry.scene);
                if (!scene.isLoaded || scene.path != entry.scenePath)
                    continue;
                if (!roots.TryGetValue(entry.scene, out var objects))
                    roots[entry.scene] = objects = scene.GetRootGameObjects();
                var first = entry.indices[0];
                if (first >= objects.Length || objects[first].name != entry.names[0])
                    continue;
                var t = objects[first].transform;
                for (int i = 1; i < entry.indices.Length && t; i++)
                {
                    var index = entry.indices[i];
                    t = index < t.childCount && t.GetChild(index).name == entry.names[i] ? t.GetChild(index) : null;
                }
                if (t)
                    Paths[t.gameObject] = entry.prefabPath;
            }
        }
    }
}
#endif
