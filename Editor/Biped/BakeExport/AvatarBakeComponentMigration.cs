using System;
using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    /// <summary>NiloToon 설치를 필수 의존성으로 만들지 않고 컨트롤러와 내부 참조를 이관한다.</summary>
    public static class AvatarBakeComponentMigration
    {
        private const string NiloControllerTypeName =
            "NiloToon.NiloToonURP.NiloToonPerCharacterRenderController";

        public static int MigrateNiloControllers(GameObject source,
            Dictionary<Transform, Transform> transformMap, IMigrationLog log)
        {
            var controllers = source.GetComponentsInChildren<MonoBehaviour>(true)
                .Where(c => c != null && c.GetType().FullName == NiloControllerTypeName).ToArray();
            var destinations = new Dictionary<Component, Component>();

            // 먼저 모두 생성해 컨트롤러 사이의 참조도 대상 쪽에서 찾을 수 있게 한다.
            foreach (var controller in controllers)
            {
                if (!transformMap.TryGetValue(controller.transform, out var target))
                    throw new InvalidOperationException("NiloToon 컨트롤러의 대상 오브젝트를 찾지 못했습니다: " + controller.name);
                var type = controller.GetType();
                var destination = target.GetComponent(type) ?? target.gameObject.AddComponent(type);
                if (destination == null)
                    throw new InvalidOperationException("NiloToon 컨트롤러를 추가하지 못했습니다: " + target.name);
                destinations[controller] = destination;
            }

            foreach (var pair in destinations)
            {
                EditorUtility.CopySerialized(pair.Key, pair.Value);
                RemapInternalReferences(pair.Value, source.transform, transformMap, log);
            }
            if (controllers.Length > 0)
                log.Info($"NiloToon render controllers migrated: {controllers.Length} (settings and internal references).");
            return controllers.Length;
        }

        private static void RemapInternalReferences(Component destination, Transform sourceRoot,
            Dictionary<Transform, Transform> transformMap, IMigrationLog log)
        {
            var serialized = new SerializedObject(destination);
            var property = serialized.GetIterator();
            // Next를 사용하여 Inspector에서 숨겨진 참조와 목록의 원소도 처리한다.
            while (property.Next(true))
            {
                if (property.propertyType != SerializedPropertyType.ObjectReference
                    || property.propertyPath == "m_Script" || property.propertyPath == "m_GameObject")
                    continue;

                var reference = property.objectReferenceValue;
                if (reference == null || EditorUtility.IsPersistent(reference)) continue;
                var component = reference as Component;
                var gameObject = reference as GameObject;
                var sourceTransform = component != null ? component.transform
                    : gameObject != null ? gameObject.transform : null;
                if (sourceTransform == null
                    || (sourceTransform != sourceRoot && !sourceTransform.IsChildOf(sourceRoot)))
                    continue; // 외부 자산/아바타 외부 참조는 유지.

                UnityEngine.Object replacement = null;
                if (transformMap.TryGetValue(sourceTransform, out var mapped))
                {
                    if (reference is Transform) replacement = mapped;
                    else if (gameObject != null) replacement = mapped.gameObject;
                    else
                    {
                        // 같은 타입이 여러 개인 경우에도 원래 컴포넌트의 순서를 보존.
                        var sourceComponents = sourceTransform.GetComponents(component.GetType());
                        var targetComponents = mapped.GetComponents(component.GetType());
                        int index = Array.IndexOf(sourceComponents, component);
                        if (index >= 0 && index < targetComponents.Length)
                            replacement = targetComponents[index];
                    }
                }
                property.objectReferenceValue = replacement;
                if (replacement == null)
                    log.Warning($"NiloToon reference '{property.propertyPath}' to '{reference.name}' has no baked counterpart; cleared.");
            }
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
