using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor
{
    /// <summary>Splits stacked Cloth components without requiring MagicaCloth in the package assembly.</summary>
    public static class MagicaClothComponentSplitter
    {
        public sealed class StackEntry
        {
            public GameObject Host;
            public int ComponentCount;
        }

        public sealed class Report
        {
            public GameObject Root;
            public int TotalCloths;
            public int NewObjectCount;
            public readonly List<StackEntry> Stacks = new List<StackEntry>();
            public readonly List<string> Warnings = new List<string>();
            public string Error;
            public bool CanSplit => string.IsNullOrEmpty(Error) && NewObjectCount > 0;
        }

        public sealed class Result
        {
            public int SplitHosts;
            public int CreatedObjects;
            public int UpdatedReferences;
        }

        private sealed class Move
        {
            public Component Source;
            public Component Target;
        }

        private const string ClothTypeName = "MagicaCloth2.MagicaCloth";
        private static readonly Regex InstanceId = new Regex("\"instanceID\":(-?[0-9]+)");

        private static Type FindClothType()
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                var type = assembly.GetType(ClothTypeName, false);
                if (type != null && typeof(Component).IsAssignableFrom(type)) return type;
            }
            return null;
        }

        public static Report Scan(GameObject root)
        {
            var report = new Report { Root = root };
            if (root == null) report.Error = "대상 아바타를 지정하세요.";
            else if (EditorUtility.IsPersistent(root) || !root.scene.IsValid() || !root.scene.isLoaded)
                report.Error = "씬 또는 Prefab 편집 모드의 오브젝트를 지정하세요.";
            if (report.Error != null) return report;
            var type = FindClothType();
            if (type == null)
            {
                report.Error = "MagicaCloth2가 설치되어 있지 않습니다.";
                return report;
            }
            var hosts = new HashSet<GameObject>();
            int missingReferences = 0;
            foreach (var transform in root.GetComponentsInChildren<Transform>(true))
                if (GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(transform.gameObject) > 0)
                    report.Warnings.Add($"{transform.name}: Missing Script는 변경하지 않습니다.");
            foreach (var component in root.GetComponentsInChildren(type, true))
            {
                report.TotalCloths++;
                var properties = new SerializedObject(component).GetIterator();
                while (properties.Next(true))
                    if (properties.propertyType == SerializedPropertyType.ObjectReference &&
                        properties.objectReferenceValue == null && properties.objectReferenceInstanceIDValue != 0)
                        missingReferences++;
                if (!hosts.Add(component.gameObject)) continue;
                int count = component.GetComponents(type).Length;
                if (count <= 1) continue;
                report.Stacks.Add(new StackEntry { Host = component.gameObject, ComponentCount = count });
                report.NewObjectCount += count - 1;
            }
            if (missingReferences > 0)
                report.Warnings.Add($"기존 Missing 참조 {missingReferences}개는 그대로 보존합니다. 분할 후 물리 설정을 별도로 확인하세요.");
            return report;
        }

        public static Result Split(GameObject root)
        {
            if (EditorApplication.isPlayingOrWillChangePlaymode || EditorApplication.isCompiling)
                throw new InvalidOperationException("편집 모드에서 컴파일이 완료된 후 실행하세요.");
            var report = Scan(root); // Always rescan: objects may have changed since the UI scan.
            if (report.Error != null) throw new InvalidOperationException(report.Error);
            var result = new Result();
            if (!report.CanSplit) return result;

            var type = FindClothType();
            var originals = new Dictionary<Component, string>();
            foreach (var component in root.GetComponentsInChildren(type, true))
                originals.Add(component, EditorJsonUtility.ToJson(component));
            var moves = new List<Move>();
            var replacements = new Dictionary<Object, Object>();
            var reverseIds = new Dictionary<int, int>();
            Undo.IncrementCurrentGroup();
            int group = Undo.GetCurrentGroup();
            Undo.SetCurrentGroupName("Split MagicaCloth Components");
            try
            {
                foreach (var stack in report.Stacks)
                {
                    var host = stack.Host;
                    var components = host.GetComponents(type);
                    for (int i = 1; i < components.Length; i++)
                    {
                        var go = new GameObject(UniqueSiblingName(host, i + 1));
                        Undo.RegisterCreatedObjectUndo(go, "Create Cloth Object");
                        SceneManager.MoveGameObjectToScene(go, host.scene);
                        go.transform.SetParent(host.transform.parent, false);
                        go.transform.localPosition = host.transform.localPosition;
                        go.transform.localRotation = host.transform.localRotation;
                        go.transform.localScale = host.transform.localScale;
                        go.layer = host.layer;
                        go.tag = host.tag;
                        go.hideFlags = host.hideFlags;
                        GameObjectUtility.SetStaticEditorFlags(go, GameObjectUtility.GetStaticEditorFlags(host));
                        go.SetActive(host.activeSelf);
                        var clone = Undo.AddComponent(go, components[i].GetType());
                        EditorUtility.CopySerialized(components[i], clone);
                        moves.Add(new Move { Source = components[i], Target = clone });
                        replacements.Add(components[i], clone);
                        reverseIds.Add(clone.GetInstanceID(), components[i].GetInstanceID());
                        reverseIds.Add(go.GetInstanceID(), host.GetInstanceID());
                        reverseIds.Add(go.transform.GetInstanceID(), host.transform.GetInstanceID());
                        RelocateSelfReferences(clone, host, go);
                    }
                }

                // References can live outside the selected avatar (sync partners or scene controllers).
                foreach (var sceneRoot in root.scene.GetRootGameObjects())
                foreach (var component in sceneRoot.GetComponentsInChildren<Component>(true))
                {
                    if (component == null || replacements.ContainsKey(component)) continue;
                    var serialized = new SerializedObject(component);
                    var property = serialized.GetIterator();
                    bool changed = false;
                    while (property.Next(true))
                    {
                        if (property.propertyType != SerializedPropertyType.ObjectReference) continue;
                        var value = property.objectReferenceValue;
                        if (value == null || !replacements.TryGetValue(value, out var replacement)) continue;
                        if (!changed) Undo.RecordObject(component, "Relink Cloth Reference");
                        changed = true;
                        property.objectReferenceValue = replacement;
                        result.UpdatedReferences++;
                    }
                    if (!changed) continue;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                    PrefabUtility.RecordPrefabInstancePropertyModifications(component);
                }

                // This also preserves pre-existing null/missing references and cached PreBuild data.
                foreach (var original in originals)
                {
                    var target = replacements.TryGetValue(original.Key, out var replacement)
                        ? (Component)replacement : original.Key;
                    string normalized = InstanceId.Replace(EditorJsonUtility.ToJson(target), match =>
                    {
                        int id = int.Parse(match.Groups[1].Value);
                        return "\"instanceID\":" + (reverseIds.TryGetValue(id, out var oldId) ? oldId : id);
                    });
                    if (normalized != original.Value)
                        throw new InvalidOperationException($"설정 검증 실패: {target.name}. 변경을 되돌렸습니다.");
                }
                foreach (var move in moves) Undo.DestroyObjectImmediate(move.Source);
                foreach (var stack in report.Stacks)
                    PrefabUtility.RecordPrefabInstancePropertyModifications(stack.Host);

                var final = Scan(root);
                int outsideRoot = 0;
                foreach (var move in moves)
                    if (!move.Target.transform.IsChildOf(root.transform)) outsideRoot++;
                if (final.TotalCloths + outsideRoot != report.TotalCloths || final.Stacks.Count != 0)
                    throw new InvalidOperationException("분할 개수 검증에 실패했습니다.");
                EditorSceneManager.MarkSceneDirty(root.scene);
                Undo.CollapseUndoOperations(group);
                result.SplitHosts = report.Stacks.Count;
                result.CreatedObjects = moves.Count;
                return result;
            }
            catch
            {
                Undo.RevertAllDownToGroup(group);
                throw;
            }
        }

        private static void RelocateSelfReferences(Component clone, GameObject host, GameObject target)
        {
            var serialized = new SerializedObject(clone);
            bool hostIsRootBone = false;
            var roots = serialized.FindProperty("serializeData.rootBones");
            if (roots != null && roots.isArray)
                for (int i = 0; i < roots.arraySize; i++)
                    if (roots.GetArrayElementAtIndex(i).objectReferenceValue == host.transform) hostIsRootBone = true;
            var property = serialized.GetIterator();
            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference ||
                    property.propertyPath == "m_GameObject" || property.propertyPath == "m_Script") continue;
                // Explicit bone/renderer/normal references are simulation inputs, even when
                // they happen to point to the host. Only relocate private host-space caches.
                if (!property.propertyPath.StartsWith("serializeData2.", StringComparison.Ordinal)) continue;
                if (hostIsRootBone && property.propertyPath != "serializeData2.initData.clothTransformRecord.transform") continue;
                if (property.objectReferenceValue == host.transform) property.objectReferenceValue = target.transform;
                else if (property.objectReferenceValue == host) property.objectReferenceValue = target;
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }

        private static string UniqueSiblingName(GameObject host, int index)
        {
            var names = new HashSet<string>();
            if (host.transform.parent != null)
                foreach (Transform sibling in host.transform.parent) names.Add(sibling.name);
            else
                foreach (var sibling in host.scene.GetRootGameObjects()) names.Add(sibling.name);
            string name;
            do { name = host.name + "_" + index.ToString("00"); index++; } while (names.Contains(name));
            return name;
        }
    }
}
