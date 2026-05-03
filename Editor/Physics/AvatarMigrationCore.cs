// 이 파일은 MagicaCloth2 + VRM(UniVRM 0.x) 둘 다 설치된 환경에서만 컴파일됩니다.
// 같은 폴더의 YAMO.UnityTools.Physics.Editor.asmdef가
// defineConstraints = [YAMO_HAS_MAGICACLOTH, YAMO_HAS_VRM] 로 컴파일을 게이팅합니다.
//
// 본 파일의 목적:
//   AvatarPhysicsMigrator.cs(EditorWindow)에 들어 있던 비-UI 마이그레이션 로직을
//   재사용 가능한 정적 API로 추출한 것. 기존 EditorWindow 동작은 보존하며,
//   이후 다른 도구(예: Bake & Prefab 파이프라인)도 같은 코어를 호출합니다.
//
// 주의:
//   - 동일한 동작 보존이 최우선. 메서드 본문은 가급적 원본과 동일하게 유지합니다.
//   - 로깅은 IMigrationLog 추상화로 분리하여 EditorWindow 패널 / Debug.Log 양쪽 호환.

using System.Collections.Generic;
using System.Linq;
using MagicaCloth2;
using UnityEditor;
using UnityEngine;
using UnityEngine.Animations;
using VRM;

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// 마이그레이션 진행 중 메시지 출력 추상화. EditorWindow는 자체 패널 로그로,
    /// 비대화형 호출자(파이프라인)는 DebugMigrationLog로 사용.
    /// </summary>
    public interface IMigrationLog
    {
        void Info(string message);
        void Warning(string message);
        void Error(string message);
    }

    /// <summary>UnityEngine.Debug 로 출력하는 기본 IMigrationLog 구현.</summary>
    public sealed class DebugMigrationLog : IMigrationLog
    {
        private readonly string _prefix;
        public DebugMigrationLog(string prefix = "[AvatarMigration] ") { _prefix = prefix; }
        public void Info(string message)    { Debug.Log(_prefix + message); }
        public void Warning(string message) { Debug.LogWarning(_prefix + message); }
        public void Error(string message)   { Debug.LogError(_prefix + message); }
    }

    /// <summary>
    /// 아바타 간 물리/콜라이더/블렌드셰이프 마이그레이션의 정적 코어.
    /// 모든 메서드는 Source/Target GameObject를 인자로 받으며,
    /// 외부 상태(EditorWindow 필드 등)에 의존하지 않습니다.
    /// </summary>
    public static class AvatarMigrationCore
    {
        // ------------------------------------------------------------
        // Validation
        // ------------------------------------------------------------

        /// <summary>
        /// Source 트리 내에 같은 이름을 가진 Transform이 둘 이상 있는지 검사합니다.
        /// 마이그레이션은 이름 기반 매핑을 사용하므로 중복 이름이 있으면 안전하지 않습니다.
        /// </summary>
        /// <returns>중복이 없으면 true(통과). 중복이 있으면 false.</returns>
        public static bool ValidateNoDuplicateNames(Transform root, IMigrationLog log)
        {
            var names = new HashSet<string>();
            var duplicates = new List<string>();

            void Traverse(Transform t)
            {
                if (!names.Add(t.name)) duplicates.Add(t.name);
                foreach (Transform child in t) Traverse(child);
            }

            Traverse(root);

            if (duplicates.Count > 0)
            {
                log.Error($"Duplicate names found in Source Avatar: {string.Join(", ", duplicates.Distinct())}");
                log.Error("Please rename duplicate objects to ensure unique mapping.");
                return false;
            }
            return true;
        }

        // ------------------------------------------------------------
        // Bone mapping
        // ------------------------------------------------------------

        /// <summary>
        /// Source → Target 의 Transform 매핑을 구축합니다.
        /// 1순위: Humanoid Avatar 의 HumanBodyBones 매핑(양쪽이 모두 isHuman 일 때).
        /// 2순위: 이름 기반 매핑(Target 이름 사전에서 1차 매치).
        /// </summary>
        public static Dictionary<Transform, Transform> BuildBoneMap(
            GameObject source, GameObject target, IMigrationLog log)
        {
            var map = new Dictionary<Transform, Transform>();
            var sourceAnimator = source.GetComponent<Animator>();
            var targetAnimator = target.GetComponent<Animator>();

            // 루트는 무조건 매핑
            map[source.transform] = target.transform;

            // Phase 1: Humanoid mapping
            if (sourceAnimator != null && sourceAnimator.isHuman
                && targetAnimator != null && targetAnimator.isHuman)
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
                log.Info($"Mapped {map.Count} humanoid bones.");
            }
            else
            {
                log.Info("Warning: One or both avatars are not Humanoid. Skipping Humanoid mapping.");
            }

            // Phase 2: Name-based mapping (fallback for non-humanoid bones)
            // Target 측 중복 이름이 있더라도 우선 첫 매치를 채택 (원본과 동일한 정책).
            var targetTransforms = target.transform.GetComponentsInChildren<Transform>(true)
                .GroupBy(t => t.name)
                .ToDictionary(g => g.Key, g => g.First());

            void MapRecursive(Transform current)
            {
                if (!map.ContainsKey(current))
                {
                    if (targetTransforms.TryGetValue(current.name, out var targetMatch))
                    {
                        map[current] = targetMatch;
                    }
                }
                foreach (Transform child in current) MapRecursive(child);
            }

            MapRecursive(source.transform);

            log.Info($"Total mapped transforms: {map.Count}");
            return map;
        }

        // ------------------------------------------------------------
        // Colliders (Magica + VRMSpringBoneColliderGroup)
        // ------------------------------------------------------------

        /// <summary>
        /// Source 트리에 부착된 콜라이더 컴포넌트(Magica 3종 + VRM ColliderGroup) 를
        /// Target 의 대응 본/부모로 옮깁니다.
        ///
        /// 매핑이 없는 콜라이더 GO 의 경우, 가장 가까운 매핑 부모 아래에
        /// "월드 포즈를 보존한 채" Instantiate 후 SetParent(parent, true) 로 부착합니다.
        /// 이는 베이크 후 본 로컬 회전이 identity가 되어도 콜라이더의 시각 위치가
        /// 어긋나지 않게 하기 위한 핵심 트릭입니다.
        /// </summary>
        public static void MigrateColliders(
            Transform sourceRoot, Transform targetRoot,
            Dictionary<Transform, Transform> boneMap, IMigrationLog log)
        {
            var transformsWithColliders = new HashSet<Transform>();

            void Collect<T>() where T : Component
            {
                foreach (var c in sourceRoot.GetComponentsInChildren<T>(true))
                {
                    transformsWithColliders.Add(c.transform);
                }
            }

            Collect<MagicaCapsuleCollider>();
            Collect<MagicaSphereCollider>();
            Collect<MagicaPlaneCollider>();
            Collect<VRMSpringBoneColliderGroup>();

            foreach (var src in transformsWithColliders)
            {
                Transform destParent = null;

                if (boneMap.TryGetValue(src, out var mappedDest))
                {
                    destParent = mappedDest;
                }
                else
                {
                    // 가장 가까운 매핑된 조상 찾기
                    var p = src.parent;
                    while (p != null)
                    {
                        if (boneMap.TryGetValue(p, out var m))
                        {
                            // 이미 같은 이름의 자식이 존재하면 그것을 사용
                            var existing = m.Find(src.name);
                            if (existing != null)
                            {
                                destParent = existing;
                            }
                            else
                            {
                                // 월드 포즈 보존 Instantiate. 이후 SetParent(true) 로 월드 유지.
                                var newObj = Object.Instantiate(src.gameObject, src.position, src.rotation);
                                newObj.name = src.name;
                                newObj.transform.SetParent(m, true);

                                destParent = newObj.transform;
                                boneMap[src] = destParent;

                                // Instantiate 가 컴포넌트도 복제했으므로 추가 CopyComponent 불필요.
                                goto NextItem;
                            }
                            break;
                        }
                        p = p.parent;
                    }
                }

                if (destParent == null) continue;

                CopyComponentsOfType<MagicaCapsuleCollider>(src, destParent);
                CopyComponentsOfType<MagicaSphereCollider>(src, destParent);
                CopyComponentsOfType<MagicaPlaneCollider>(src, destParent);
                CopyComponentsOfType<VRMSpringBoneColliderGroup>(src, destParent);

                NextItem:;
            }
        }

        // ------------------------------------------------------------
        // MagicaCloth (정책: Target 루트 직속 배치)
        // ------------------------------------------------------------

        /// <summary>
        /// Source 의 MagicaCloth 컴포넌트들을 Target 루트 직속 GameObject 로 옮기고,
        /// 내부 SerializedProperty 에 들어 있는 Transform/Component 참조를
        /// boneMap 으로 리매핑합니다.
        ///
        /// MagicaCloth 는 통상 한 GameObject 에 1개지만, 사용자가 여러 개 부착한 경우라도
        /// 누락 없이 모두 옮기기 위해 매번 AddComponent 합니다.
        /// 재실행 안전성: dst GameObject 를 처음 만날 때 1회 기존 MagicaCloth 를 정리하여
        /// 누적되지 않도록 합니다.
        /// </summary>
        public static void MigrateMagicaCloth(
            Transform sourceRoot, Transform targetRoot,
            Dictionary<Transform, Transform> boneMap, IMigrationLog log)
        {
            var magicaCloths = sourceRoot.GetComponentsInChildren<MagicaCloth>(true);

            var clearedDsts = new HashSet<GameObject>();

            foreach (var mc in magicaCloths)
            {
                // MagicaCloth 는 부모 무시(Target 루트 직속)
                var dest = GetOrCreateDestination(mc.transform, targetRoot, boneMap, ignoreParent: true, log);
                if (dest == null)
                {
                    log.Warning($"Could not find a place to copy MagicaCloth from '{mc.name}'.");
                    continue;
                }

                if (clearedDsts.Add(dest.gameObject))
                {
                    foreach (var existing in dest.gameObject.GetComponents<MagicaCloth>())
                    {
                        Object.DestroyImmediate(existing);
                    }
                }

                var newMc = dest.gameObject.AddComponent<MagicaCloth>();
                EditorUtility.CopySerialized(mc, newMc);

                var so = new SerializedObject(newMc);

                var rootListProp = so.FindProperty("serializeData.rootBones");
                if (rootListProp != null) RemapTransformListProperty(rootListProp, boneMap, log);
                else log.Info("Could not find 'serializeData.rootBones'.");

                var colliderListProp = so.FindProperty("serializeData.colliderCollisionConstraint.colliderList");
                if (colliderListProp != null) RemapComponentListProperty(colliderListProp, boneMap, log);
                else log.Info("Could not find 'serializeData.colliderCollisionConstraint.colliderList'.");

                var rendererListProp = so.FindProperty("serializeData.sourceRenderers");
                if (rendererListProp != null) RemapComponentListProperty(rendererListProp, boneMap, log);
                else
                {
                    log.Info("Could not find 'serializeData.sourceRenderers'. Trying 'sourceRenderers'...");
                    rendererListProp = so.FindProperty("sourceRenderers");
                    if (rendererListProp != null) RemapComponentListProperty(rendererListProp, boneMap, log);
                }

                so.ApplyModifiedProperties();
            }
        }

        // ------------------------------------------------------------
        // VRMSpringBone
        // ------------------------------------------------------------

        /// <summary>
        /// Source 의 VRMSpringBone 컴포넌트를 boneMap 으로 매핑된 본에 부착하고,
        /// public 필드(RootBones, ColliderGroups)를 리매핑합니다.
        ///
        /// VRMSpringBone 은 하나의 GameObject(통상 "secondary")에 여러 인스턴스가
        /// 부착되는 패턴(머리/스커트/꼬리 등 그룹별 분리) 이 정상이므로,
        /// GetOrAddComponent 가 아닌 매번 AddComponent 로 새 인스턴스를 추가합니다.
        ///
        /// 재실행 안전성: 매 실행 시 dst GameObject 의 기존 VRMSpringBone 을 먼저 모두
        /// 제거하여 재실행 시 컴포넌트가 누적되지 않게 합니다 (단, 같은 dst 에 처음 추가되는
        /// 시점에만 1회 정리).
        /// </summary>
        public static void MigrateVRMSpringBone(
            Transform sourceRoot, Transform targetRoot,
            Dictionary<Transform, Transform> boneMap, IMigrationLog log)
        {
            var vrmSprings = sourceRoot.GetComponentsInChildren<VRMSpringBone>(true);

            // 이 마이그레이션 호출에서 처음 만나는 dst GameObject 만 1회 정리하여 누적 방지.
            var clearedDsts = new HashSet<GameObject>();

            foreach (var vs in vrmSprings)
            {
                var dest = GetOrCreateDestination(vs.transform, targetRoot, boneMap, ignoreParent: false, log);
                if (dest == null)
                {
                    log.Warning($"Could not find a place to copy VRMSpringBone from '{vs.name}'.");
                    continue;
                }

                if (clearedDsts.Add(dest.gameObject))
                {
                    foreach (var existing in dest.gameObject.GetComponents<VRMSpringBone>())
                    {
                        Object.DestroyImmediate(existing);
                    }
                }

                var newVs = dest.gameObject.AddComponent<VRMSpringBone>();
                EditorUtility.CopySerialized(vs, newVs);

                newVs.RootBones = RemapTransformList(vs.RootBones, boneMap);
                newVs.ColliderGroups = RemapVRMColliderGroups(vs.ColliderGroups, boneMap);
            }
        }

        // ------------------------------------------------------------
        // BlendShape weights
        // ------------------------------------------------------------

        /// <summary>
        /// Source 와 Target 의 SkinnedMeshRenderer 를 같은 GameObject 이름으로 매칭하고,
        /// BlendShape 인덱스가 아닌 "이름" 기준으로 weight 값을 복사합니다.
        /// 인덱스가 변경되어도(혹은 일부 shape 이 사라져도) 안전합니다.
        /// </summary>
        /// <returns>weight 가 적용된 SkinnedMeshRenderer 개수.</returns>
        public static int MigrateBlendShapes(GameObject source, GameObject target, IMigrationLog log)
        {
            var sourceRenderers = source.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var targetRenderers = target.GetComponentsInChildren<SkinnedMeshRenderer>(true)
                .GroupBy(r => r.name)
                .ToDictionary(g => g.Key, g => g.First());

            int migratedCount = 0;
            foreach (var sourceSMR in sourceRenderers)
            {
                if (!targetRenderers.TryGetValue(sourceSMR.name, out var targetSMR)) continue;

                var sourceMesh = sourceSMR.sharedMesh;
                var targetMesh = targetSMR.sharedMesh;
                if (sourceMesh == null || targetMesh == null) continue;

                int shapeCount = sourceMesh.blendShapeCount;
                bool anyChanged = false;
                for (int i = 0; i < shapeCount; i++)
                {
                    string shapeName = sourceMesh.GetBlendShapeName(i);
                    float weight = sourceSMR.GetBlendShapeWeight(i);
                    int targetIndex = targetMesh.GetBlendShapeIndex(shapeName);
                    if (targetIndex != -1)
                    {
                        targetSMR.SetBlendShapeWeight(targetIndex, weight);
                        anyChanged = true;
                    }
                }
                if (anyChanged) migratedCount++;
            }
            log.Info($"BlendShape migration completed. Updated {migratedCount} SkinnedMeshRenderers.");
            return migratedCount;
        }

        // ------------------------------------------------------------
        // Active states (On/Off)
        // ------------------------------------------------------------

        /// <summary>
        /// boneMap 에 매핑된 모든 쌍에 대해 Source 의 GameObject.activeSelf 를
        /// Target 에 그대로 적용합니다.
        ///
        /// 주의: BuildBoneMap 은 Source 의 모든 Transform 을 매핑하지 않을 수 있으므로
        /// (이름이 Target 에 없거나 Humanoid 매핑 외부) 일부 노드는 누락될 수 있습니다.
        /// 베이크 파이프라인에서는 BoneNormalizer 가 비활성 자식을 제외하므로,
        /// "원래 비활성이라 베이크에서 사라진 노드" 는 적용 대상이 아닙니다.
        /// </summary>
        /// <returns>실제로 활성 상태가 변경된 GameObject 개수.</returns>
        public static int MigrateActiveStates(
            Dictionary<Transform, Transform> boneMap, IMigrationLog log)
        {
            int changed = 0;
            foreach (var kv in boneMap)
            {
                var src = kv.Key;
                var dst = kv.Value;
                if (src == null || dst == null) continue;

                bool srcActive = src.gameObject.activeSelf;
                if (dst.gameObject.activeSelf != srcActive)
                {
                    dst.gameObject.SetActive(srcActive);
                    changed++;
                }
            }
            log.Info($"Active states applied: {changed} GameObjects toggled.");
            return changed;
        }

        // ------------------------------------------------------------
        // Constraints (Unity 빌트인 IConstraint 6종)
        // ------------------------------------------------------------

        /// <summary>
        /// Source 트리에 있는 Unity 빌트인 Constraint 컴포넌트들을
        /// boneMap 으로 매핑된 Target 본에 부착합니다.
        ///
        /// 처리 대상: PositionConstraint, RotationConstraint, ScaleConstraint,
        ///           ParentConstraint, LookAtConstraint, AimConstraint
        ///
        /// 정책 (좌표계 드리프트 회피):
        ///   복사함: ConstraintSource 목록(sourceTransform 을 boneMap 으로 리매핑) + 각 source 의 weight,
        ///            IConstraint.weight, constraintActive, locked
        ///   복사 안 함: 모든 offset / rest 값(translationOffset, rotationOffset, *AtRest 등),
        ///                 LookAt/Aim 의 aimVector / upVector / worldUpVector / worldUpType / worldUpObject / roll,
        ///                 ParentConstraint 의 per-source translationOffsets / rotationOffsets
        ///
        /// 즉, "어떤 Transform 을 따라가는가" 와 "각각의 비중" 만 옮기고
        /// 나머지는 새 본 자세 기준으로 Unity 가 AddComponent 시 자동 캡처하는 기본값을 사용합니다.
        /// </summary>
        /// <returns>복사된 Constraint 컴포넌트 총 개수.</returns>
        public static int MigrateConstraints(
            Transform sourceRoot,
            Dictionary<Transform, Transform> boneMap,
            IMigrationLog log)
        {
            int total = 0;
            total += MigrateConstraintType<PositionConstraint>(sourceRoot, boneMap, log);
            total += MigrateConstraintType<RotationConstraint>(sourceRoot, boneMap, log);
            total += MigrateConstraintType<ScaleConstraint>(sourceRoot, boneMap, log);
            total += MigrateConstraintType<ParentConstraint>(sourceRoot, boneMap, log);
            total += MigrateConstraintType<LookAtConstraint>(sourceRoot, boneMap, log);
            total += MigrateConstraintType<AimConstraint>(sourceRoot, boneMap, log);
            log.Info($"Constraints migrated: {total} components total.");
            return total;
        }

        private static int MigrateConstraintType<T>(
            Transform sourceRoot,
            Dictionary<Transform, Transform> boneMap,
            IMigrationLog log) where T : Component, IConstraint
        {
            int count = 0;
            var list = sourceRoot.GetComponentsInChildren<T>(true);
            foreach (var src in list)
            {
                if (!boneMap.TryGetValue(src.transform, out var dstTransform))
                {
                    log.Warning($"No mapping for transform '{src.name}' carrying {typeof(T).Name}; skipping.");
                    continue;
                }

                var dst = GetOrAddComponent<T>(dstTransform.gameObject);
                ApplyConstraintSourcesAndWeights(src, dst, boneMap, log);
                count++;
            }
            return count;
        }

        /// <summary>
        /// IConstraint 의 sources 목록과 weight/flag 만 src -> dst 로 옮깁니다.
        /// offset, rest, axis vector 등 좌표계 의존 값은 모두 복사하지 않습니다.
        /// 매핑되지 않은 sourceTransform 은 그대로 둡니다(외부 참조 보존).
        /// </summary>
        private static void ApplyConstraintSourcesAndWeights(
            IConstraint src, IConstraint dst,
            Dictionary<Transform, Transform> boneMap, IMigrationLog log)
        {
            // 1) sources (각 source 의 sourceTransform 을 boneMap 으로 리매핑)
            var srcSources = new List<ConstraintSource>();
            src.GetSources(srcSources);

            for (int i = 0; i < srcSources.Count; i++)
            {
                var s = srcSources[i];
                if (s.sourceTransform != null
                    && boneMap.TryGetValue(s.sourceTransform, out var mapped))
                {
                    s.sourceTransform = mapped;
                }
                // weight 는 ConstraintSource struct 안의 값이라 그대로 유지됨
                srcSources[i] = s;
            }
            dst.SetSources(srcSources);

            // 2) overall flags (좌표계 무관)
            dst.weight = src.weight;
            dst.constraintActive = src.constraintActive;
            dst.locked = src.locked;
        }

        // ------------------------------------------------------------
        // Helpers (internal: 같은 어셈블리에서만 사용)
        // ------------------------------------------------------------

        /// <summary>
        /// 컴포넌트의 모든 SerializedProperty 를 순회하면서 ObjectReference 가
        /// Source 트리의 Transform / GameObject 를 가리키고 있으면
        /// boneMap 을 통해 Target 트리의 대응 객체로 재작성합니다.
        ///
        /// 매핑이 없는 참조는 그대로 둡니다(끊는 대신 보존).
        /// </summary>
        internal static void RewriteTransformReferencesGeneric(
            UnityEngine.Object target,
            Dictionary<Transform, Transform> boneMap,
            IMigrationLog log)
        {
            var so = new SerializedObject(target);
            var p = so.GetIterator();
            bool enterChildren = true;
            while (p.NextVisible(enterChildren))
            {
                enterChildren = true;
                if (p.propertyType != SerializedPropertyType.ObjectReference) continue;

                var refVal = p.objectReferenceValue;
                if (refVal == null) continue;

                if (refVal is Transform t && boneMap.TryGetValue(t, out var mappedT))
                {
                    p.objectReferenceValue = mappedT;
                }
                else if (refVal is GameObject go && boneMap.TryGetValue(go.transform, out var mappedGo))
                {
                    p.objectReferenceValue = mappedGo.gameObject;
                }
            }
            so.ApplyModifiedProperties();
        }


        /// <summary>
        /// boneMap 에 src 가 있으면 그 매핑을 반환. 없으면 가장 가까운 매핑된 부모 아래에
        /// (또는 ignoreParent=true 일 때 targetRoot 아래에) 같은 이름의 GameObject 를
        /// 찾거나 새로 생성합니다. 새로 생성된 transform 은 boneMap 에 추가됩니다.
        /// </summary>
        internal static Transform GetOrCreateDestination(
            Transform src, Transform targetRoot,
            Dictionary<Transform, Transform> boneMap,
            bool ignoreParent, IMigrationLog log)
        {
            if (boneMap.TryGetValue(src, out var d)) return d;

            Transform mappedParent = null;

            if (ignoreParent)
            {
                mappedParent = targetRoot;
            }
            else
            {
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

            if (mappedParent == null) mappedParent = targetRoot;

            var existing = mappedParent.Find(src.name);
            if (existing != null)
            {
                boneMap[src] = existing;
                return existing;
            }

            var newObj = new GameObject(src.name);
            newObj.transform.SetParent(mappedParent, false);
            newObj.transform.localPosition = src.localPosition;
            newObj.transform.localRotation = src.localRotation;
            newObj.transform.localScale = src.localScale;

            boneMap[src] = newObj.transform;
            return newObj.transform;
        }

        internal static T GetOrAddComponent<T>(GameObject go) where T : Component
        {
            var comp = go.GetComponent<T>();
            if (comp == null) comp = go.AddComponent<T>();
            return comp;
        }

        internal static void CopyComponentsOfType<T>(Transform src, Transform dest) where T : Component
        {
            var comps = src.GetComponents<T>();
            foreach (var comp in comps)
            {
                var newComp = GetOrAddComponent<T>(dest.gameObject);
                EditorUtility.CopySerialized(comp, newComp);
            }
        }

        // ----- Serialized property remappers -----

        internal static void RemapTransformListProperty(
            SerializedProperty listProp, Dictionary<Transform, Transform> map, IMigrationLog log)
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
                    log.Warning($"Could not map transform '{original.name}' in list.");
                    elem.objectReferenceValue = null;
                }
            }
        }

        internal static void RemapComponentListProperty(
            SerializedProperty listProp, Dictionary<Transform, Transform> map, IMigrationLog log)
        {
            if (listProp == null) return;
            for (int i = listProp.arraySize - 1; i >= 0; i--)
            {
                var elem = listProp.GetArrayElementAtIndex(i);
                var originalComp = elem.objectReferenceValue as Component;
                if (originalComp == null) continue;

                if (map.TryGetValue(originalComp.transform, out var mappedTransform))
                {
                    var newComp = mappedTransform.GetComponent(originalComp.GetType());
                    if (newComp != null)
                    {
                        elem.objectReferenceValue = newComp;
                    }
                    else
                    {
                        log.Warning($"Mapped transform '{mappedTransform.name}' does not have component '{originalComp.GetType().Name}'.");
                        elem.objectReferenceValue = null;
                    }
                }
                else
                {
                    log.Warning($"Could not map transform for component '{originalComp.name}' ({originalComp.GetType().Name}).");
                    elem.objectReferenceValue = null;
                }
            }
        }

        // ----- Public-field remappers -----

        internal static List<Transform> RemapTransformList(List<Transform> sourceList, Dictionary<Transform, Transform> map)
        {
            var newList = new List<Transform>();
            if (sourceList == null) return newList;
            foreach (var t in sourceList)
            {
                if (t != null && map.TryGetValue(t, out var mapped))
                {
                    newList.Add(mapped);
                }
            }
            return newList;
        }

        internal static VRMSpringBoneColliderGroup[] RemapVRMColliderGroups(
            VRMSpringBoneColliderGroup[] sourceList, Dictionary<Transform, Transform> map)
        {
            var newList = new List<VRMSpringBoneColliderGroup>();
            if (sourceList == null) return newList.ToArray();
            foreach (var c in sourceList)
            {
                if (c != null && map.TryGetValue(c.transform, out var mappedTransform))
                {
                    var mappedCollider = mappedTransform.GetComponent<VRMSpringBoneColliderGroup>();
                    if (mappedCollider != null)
                    {
                        newList.Add(mappedCollider);
                    }
                }
            }
            return newList.ToArray();
        }
    }
}
