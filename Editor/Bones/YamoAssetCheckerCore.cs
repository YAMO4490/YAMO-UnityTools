// YamoAssetChecker 의 각 버튼이 호출하는 정적 코어 로직.
//
// 출처 (독립 복사):
//   - ObjectNameModifier.cs   : 이름 일괄 변경 / 중복 검출 / 자식 정렬 / 휴머노이드 스케일 점검
//   - YamoAssetChecker (구 FindUnusedBones) : 사용되지 않는 본 검출
//   - MissingScriptRemover.cs : 미싱/비활성 스크립트 제거
//   - FindMissingBones.cs     : SkinnedMeshRenderer 의 누락 본 검사
//
// 정책: 코어는 UI 와 무관하게 순수 데이터 작업만 수행. UI 는 호출 결과를 받아서 표시.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    public static class YamoAssetCheckerCore
    {
        // ============================================================
        // [1] Object Name Tools
        // ============================================================

        public static int ApplyPrefixAndSuffix(IEnumerable<GameObject> targets, string prefix, string suffix)
        {
            int n = 0;
            foreach (var obj in targets)
            {
                if (obj == null) continue;
                Undo.RecordObject(obj, "Change Object Name");
                obj.name = prefix + obj.name + suffix;
                n++;
            }
            return n;
        }

        public static int RemoveFirstCharacter(IEnumerable<GameObject> targets)
        {
            int n = 0;
            foreach (var obj in targets)
            {
                if (obj == null || obj.name.Length == 0) continue;
                Undo.RecordObject(obj, "Remove First Character");
                obj.name = obj.name.Substring(1);
                n++;
            }
            return n;
        }

        public static int RemoveLastCharacter(IEnumerable<GameObject> targets)
        {
            int n = 0;
            foreach (var obj in targets)
            {
                if (obj == null || obj.name.Length == 0) continue;
                Undo.RecordObject(obj, "Remove Last Character");
                obj.name = obj.name.Substring(0, obj.name.Length - 1);
                n++;
            }
            return n;
        }

        public static int ReplaceSpacesWithUnderscores(IEnumerable<GameObject> targets)
        {
            int n = 0;
            foreach (var obj in targets)
            {
                if (obj == null) continue;
                Undo.RecordObject(obj, "Replace Spaces with Underscores");
                obj.name = obj.name.Replace(" ", "_");
                n++;
            }
            return n;
        }

        public static int SortChildrenByName(GameObject parent)
        {
            if (parent == null) return 0;
            var children = new List<Transform>();
            for (int i = 0; i < parent.transform.childCount; i++)
                children.Add(parent.transform.GetChild(i));

            children.Sort((a, b) => EditorUtility.NaturalCompare(a.name, b.name));
            for (int i = 0; i < children.Count; i++)
            {
                Undo.SetTransformParent(children[i], parent.transform, "Sort Children By Name");
                children[i].SetSiblingIndex(i);
            }
            return children.Count;
        }

        // ============================================================
        // [2] Duplicate names (AvatarBakePreUtilities 와 의도적 중복)
        // ============================================================

        public static Dictionary<string, List<Transform>> FindDuplicateNames(GameObject root)
        {
            var groups = new Dictionary<string, List<Transform>>();
            if (root == null) return groups;

            var nameMap = new Dictionary<string, List<Transform>>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!nameMap.ContainsKey(t.name)) nameMap[t.name] = new List<Transform>();
                nameMap[t.name].Add(t);
            }
            foreach (var kv in nameMap)
            {
                if (kv.Value.Count > 1) groups[kv.Key] = kv.Value;
            }
            return groups;
        }

        public static int AutoRenameDuplicates(Dictionary<string, List<Transform>> groups)
        {
            int changed = 0;
            foreach (var kv in groups)
            {
                for (int i = 0; i < kv.Value.Count; i++)
                {
                    string newName = (i == 0) ? kv.Key : $"{kv.Key}_{i}";
                    if (kv.Value[i].name == newName) continue;
                    Undo.RecordObject(kv.Value[i], "Rename Duplicate");
                    kv.Value[i].name = newName;
                    changed++;
                }
            }
            return changed;
        }

        // ============================================================
        // [3] Humanoid bone scale check
        // ============================================================

        /// <summary>
        /// Humanoid Animator 의 본들 중 localScale 이 (1,1,1) 이 아닌 본을 찾아 반환.
        /// Humanoid 가 아니면 null 반환.
        /// </summary>
        public static List<Transform> FindHumanoidBonesWithNonOneScale(GameObject target)
        {
            if (target == null) return null;
            var animator = target.GetComponent<Animator>();
            if (animator == null || !animator.isHuman) return null;

            var invalid = new List<Transform>();
            foreach (HumanBodyBones bone in System.Enum.GetValues(typeof(HumanBodyBones)))
            {
                if (bone == HumanBodyBones.LastBone) continue;
                var t = animator.GetBoneTransform(bone);
                if (t != null && Vector3.Distance(t.localScale, Vector3.one) > 0.0001f)
                    invalid.Add(t);
            }
            return invalid;
        }

        // ============================================================
        // [4] Unused Bones (구 FindUnusedBones)
        // ============================================================

        public class UnusedBoneOptions
        {
            public List<string> ExcludeStrings = new List<string>();
            public bool ExcludeMagicaColliders = true;
            public bool ExcludeVRMSpringBones = true;
        }

        /// <summary>
        /// 어떤 SkinnedMeshRenderer 도 참조하지 않는 본/오브젝트를 찾아 반환.
        /// 옵션:
        ///   - ExcludeStrings : 이름에 이 부분문자열 중 하나라도 포함되면 결과에서 제외
        ///   - ExcludeMagicaColliders / ExcludeVRMSpringBones : 해당 컴포넌트를 가진 본 제외
        /// </summary>
        public static List<Transform> FindUnusedBones(GameObject root, UnusedBoneOptions options)
        {
            var unused = new List<Transform>();
            if (root == null) return unused;
            options ??= new UnusedBoneOptions();

            var rootT = root.transform;
            var smrs = rootT.GetComponentsInChildren<SkinnedMeshRenderer>(true);

            var usedBones = new HashSet<Transform>();
            var excludedObjects = new HashSet<Transform>();

            foreach (var smr in smrs)
            {
                if (smr.bones != null)
                    foreach (var b in smr.bones) if (b != null) usedBones.Add(b);

                var current = smr.transform;
                while (current != null)
                {
                    excludedObjects.Add(current);
                    current = current.parent;
                }
            }

            foreach (var bone in rootT.GetComponentsInChildren<Transform>(true))
            {
                bool excludeBone = false;

                // 부분문자열 필터
                foreach (var s in options.ExcludeStrings)
                {
                    if (!string.IsNullOrEmpty(s)
                        && bone.name.IndexOf(s, System.StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        excludeBone = true;
                        break;
                    }
                }

                // 컴포넌트 기반 필터 (이름 문자열만으로 — 외부 패키지 의존 회피)
                if (!excludeBone && (options.ExcludeMagicaColliders || options.ExcludeVRMSpringBones))
                {
                    foreach (var c in bone.GetComponents<Component>())
                    {
                        if (c == null) continue;
                        var typeName = c.GetType().Name;
                        if (options.ExcludeMagicaColliders &&
                            (typeName == "MagicaCapsuleCollider" || typeName == "MagicaSphereCollider" || typeName == "MagicaPlaneCollider"))
                        {
                            excludeBone = true; break;
                        }
                        if (options.ExcludeVRMSpringBones &&
                            (typeName == "VRMSpringBone" || typeName == "VRMSpringBoneColliderGroup"))
                        {
                            excludeBone = true; break;
                        }
                    }
                }

                if (excludeBone) continue;
                if (usedBones.Contains(bone)) continue;
                if (excludedObjects.Contains(bone)) continue;
                if (bone == rootT) continue;

                unused.Add(bone);
            }

            return unused;
        }

        // ============================================================
        // [5] Missing / Disabled Scripts
        // ============================================================

        public static (int missing, int disabled) CountScripts(GameObject root)
        {
            if (root == null) return (0, 0);
            int missing = GameObjectUtility.GetMonoBehavioursWithMissingScriptCount(root);
            int disabled = 0;
            foreach (var s in root.GetComponents<MonoBehaviour>())
                if (s != null && !s.enabled) disabled++;
            foreach (Transform child in root.transform)
            {
                var (cm, cd) = CountScripts(child.gameObject);
                missing += cm;
                disabled += cd;
            }
            return (missing, disabled);
        }

        public static int RemoveMissingScripts(GameObject root)
        {
            if (root == null) return 0;
            int n = GameObjectUtility.RemoveMonoBehavioursWithMissingScript(root);
            foreach (Transform child in root.transform)
                n += RemoveMissingScripts(child.gameObject);
            return n;
        }

        public static int RemoveDisabledScripts(GameObject root)
        {
            if (root == null) return 0;
            int n = 0;
            foreach (var s in root.GetComponents<MonoBehaviour>())
            {
                if (s != null && !s.enabled)
                {
                    Undo.DestroyObjectImmediate(s);
                    n++;
                }
            }
            foreach (Transform child in root.transform)
                n += RemoveDisabledScripts(child.gameObject);
            return n;
        }

        public static (int missing, int disabled) RemoveAllScripts(GameObject root)
        {
            int m = RemoveMissingScripts(root);
            int d = RemoveDisabledScripts(root);
            return (m, d);
        }

        // ============================================================
        // [6] Missing bones (SkinnedMeshRenderer.bones[i] == null)
        // ============================================================

        public class MissingBoneResult
        {
            public SkinnedMeshRenderer Renderer;
            public List<int> MissingBoneIndices;
            public bool RootBoneMissing;
        }

        public static List<MissingBoneResult> CheckMissingBonesInScene()
        {
            var results = new List<MissingBoneResult>();
            var renderers = Object.FindObjectsByType<SkinnedMeshRenderer>(FindObjectsSortMode.None);
            foreach (var smr in renderers)
            {
                var r = CheckRenderer(smr);
                if (r != null) results.Add(r);
            }
            return results;
        }

        public static List<MissingBoneResult> CheckMissingBonesInChildren(GameObject root)
        {
            var results = new List<MissingBoneResult>();
            if (root == null) return results;
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                var r = CheckRenderer(smr);
                if (r != null) results.Add(r);
            }
            return results;
        }

        private static MissingBoneResult CheckRenderer(SkinnedMeshRenderer smr)
        {
            if (smr == null) return null;
            var missingIndices = new List<int>();
            bool rootMissing = smr.rootBone == null;
            if (smr.bones != null)
            {
                for (int i = 0; i < smr.bones.Length; i++)
                    if (smr.bones[i] == null) missingIndices.Add(i);
            }
            if (missingIndices.Count == 0 && !rootMissing) return null;
            return new MissingBoneResult
            {
                Renderer = smr,
                MissingBoneIndices = missingIndices,
                RootBoneMissing = rootMissing,
            };
        }

        // ============================================================
        // [7] Smart Empty Object Cleaner
        // ============================================================

        public class EmptyObjectScanResult
        {
            public List<EmptyObjectEntry> Stage1 = new List<EmptyObjectEntry>();
            public List<EmptyObjectEntry> Stage2 = new List<EmptyObjectEntry>();
        }

        public class EmptyObjectEntry
        {
            public GameObject Object;
            /// Stage1: 자신 포함 삭제될 총 오브젝트 수
            /// Stage2: 직속 자식 수 (리패런팅 대상)
            public int TotalCount;
        }

        /// <summary>
        /// root 하위에서 빈 오브젝트를 두 단계로 분류한다.
        ///   Stage1: 자신+하위 전부 SMR 미참조 → 안전 삭제 가능
        ///   Stage2: 자신은 미참조이나 하위에 참조 본 존재 → 리패런팅 후 삭제 가능
        /// 비활성 SkinnedMeshRenderer 의 본 참조도 모두 포함한다.
        /// </summary>
        public static EmptyObjectScanResult ScanEmptyObjects(GameObject root)
        {
            var result = new EmptyObjectScanResult();
            if (root == null) return result;

            var rootT = root.transform;

            // [A] 모든 SMR (비활성 포함) 에서 참조 본 수집
            var smrs = rootT.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            var referencedBones = new HashSet<Transform>();
            foreach (var smr in smrs)
            {
                if (smr.bones != null)
                    foreach (var b in smr.bones) if (b != null) referencedBones.Add(b);
                if (smr.rootBone != null) referencedBones.Add(smr.rootBone);
            }

            // [B] Protected set 구성
            //   · 아바타 루트 자체
            //   · rootT 의 직속 자식 중 참조 본을 자손으로 보유한 것 (topArmatureRoot)
            var protectedSet = new HashSet<Transform>();
            protectedSet.Add(rootT);
            for (int i = 0; i < rootT.childCount; i++)
            {
                var child = rootT.GetChild(i);
                if (EmptyCleanerHasRefOrRefDescendant(child, referencedBones))
                    protectedSet.Add(child);
            }

            // [C] bottom-up 으로 "완전 삭제 가능" 여부 캐시 구성
            //     GetComponentsInChildren 은 depth-first 순서 → 역순 = bottom-up
            var allTransforms = rootT.GetComponentsInChildren<Transform>(true);
            var deletable = new HashSet<Transform>();

            for (int i = allTransforms.Length - 1; i >= 0; i--)
            {
                var t = allTransforms[i];
                if (t == rootT) continue;
                if (protectedSet.Contains(t)) continue;
                if (referencedBones.Contains(t)) continue;
                if (EmptyCleanerHasNonTransformComp(t)) continue;

                bool allChildDeletable = true;
                for (int c = 0; c < t.childCount; c++)
                {
                    if (!deletable.Contains(t.GetChild(c))) { allChildDeletable = false; break; }
                }
                if (allChildDeletable) deletable.Add(t);
            }

            // [D] Stage1: deletable 이며 부모가 deletable 이 아닌 것 (삭제 서브트리의 루트)
            foreach (var t in allTransforms)
            {
                if (!deletable.Contains(t)) continue;
                if (t.parent != null && deletable.Contains(t.parent)) continue;

                result.Stage1.Add(new EmptyObjectEntry
                {
                    Object     = t.gameObject,
                    TotalCount = 1 + EmptyCleanerCountDescendantsInSet(t, deletable),
                });
            }

            // [E] Stage2: 자신은 빈 오브젝트, 하위에 참조 본 존재
            var ancestorOfRef = new HashSet<Transform>();
            foreach (var bone in referencedBones)
            {
                var cur = bone.parent;
                while (cur != null && cur != rootT)
                {
                    ancestorOfRef.Add(cur);
                    cur = cur.parent;
                }
            }

            foreach (var t in allTransforms)
            {
                if (t == rootT) continue;
                if (protectedSet.Contains(t)) continue;
                if (referencedBones.Contains(t)) continue;
                if (EmptyCleanerHasNonTransformComp(t)) continue;
                if (deletable.Contains(t)) continue;           // Stage1 에서 처리
                if (!ancestorOfRef.Contains(t)) continue;

                result.Stage2.Add(new EmptyObjectEntry
                {
                    Object     = t.gameObject,
                    TotalCount = t.childCount,
                });
            }

            return result;
        }

        /// <summary>Stage1 전체 삭제. Undo 가능. 삭제된 총 오브젝트 수를 반환.</summary>
        public static int ExecuteStage1(List<EmptyObjectEntry> entries)
        {
            if (entries == null || entries.Count == 0) return 0;

            Undo.SetCurrentGroupName("Smart Empty Cleaner: Stage 1");
            int group = Undo.GetCurrentGroup();

            // 깊은 오브젝트부터 처리 (bottom-up 안전 순서)
            var sorted = new List<EmptyObjectEntry>(entries);
            sorted.Sort((a, b) => EmptyCleanerGetDepth(b.Object.transform)
                                 - EmptyCleanerGetDepth(a.Object.transform));

            int total = 0;
            foreach (var e in sorted)
            {
                if (e.Object == null) continue;
                total += e.TotalCount;
                Undo.DestroyObjectImmediate(e.Object);
            }

            Undo.CollapseUndoOperations(group);
            return total;
        }

        /// <summary>Stage2 선택 항목을 리패런팅 후 삭제. Undo 가능. 삭제된 오브젝트 수 반환.</summary>
        public static int ExecuteStage2Selected(List<EmptyObjectEntry> entries, IList<bool> selected)
        {
            if (entries == null || entries.Count == 0) return 0;

            Undo.SetCurrentGroupName("Smart Empty Cleaner: Stage 2");
            int group = Undo.GetCurrentGroup();

            var toProcess = new List<EmptyObjectEntry>();
            for (int i = 0; i < entries.Count; i++)
            {
                if (i < selected.Count && selected[i] && entries[i].Object != null)
                    toProcess.Add(entries[i]);
            }
            // bottom-up: 중첩 Stage2 처리 시 깊은 것부터
            toProcess.Sort((a, b) => EmptyCleanerGetDepth(b.Object.transform)
                                    - EmptyCleanerGetDepth(a.Object.transform));

            int count = 0;
            foreach (var e in toProcess)
            {
                if (e.Object == null) continue;
                var t      = e.Object.transform;
                var parent = t.parent;

                // 실행 시점에 직속 자식 수집 (이전 처리에서 변경됐을 수 있음)
                var children = new List<Transform>(t.childCount);
                for (int i = 0; i < t.childCount; i++) children.Add(t.GetChild(i));

                foreach (var child in children)
                    Undo.SetTransformParent(child, parent, "Smart Empty Cleaner: Reparent");

                Undo.DestroyObjectImmediate(e.Object);
                count++;
            }

            Undo.CollapseUndoOperations(group);
            return count;
        }

        // ── 내부 헬퍼 (EmptyCleaner 전용) ─────────────────────────────────────────

        private static bool EmptyCleanerHasRefOrRefDescendant(Transform t, HashSet<Transform> refs)
        {
            if (refs.Contains(t)) return true;
            for (int i = 0; i < t.childCount; i++)
                if (EmptyCleanerHasRefOrRefDescendant(t.GetChild(i), refs)) return true;
            return false;
        }

        private static bool EmptyCleanerHasNonTransformComp(Transform t)
        {
            foreach (var c in t.GetComponents<Component>())
            {
                if (c == null || c is Transform) continue;
                return true;
            }
            return false;
        }

        private static int EmptyCleanerCountDescendantsInSet(Transform t, HashSet<Transform> set)
        {
            int n = 0;
            for (int i = 0; i < t.childCount; i++)
            {
                var child = t.GetChild(i);
                if (set.Contains(child))
                    n += 1 + EmptyCleanerCountDescendantsInSet(child, set);
            }
            return n;
        }

        private static int EmptyCleanerGetDepth(Transform t)
        {
            int d = 0;
            while (t.parent != null) { d++; t = t.parent; }
            return d;
        }

        // ============================================================
        // [8] Inactive Object Finder
        // ============================================================

        /// <summary>
        /// root 하위에서 activeSelf == false 인 오브젝트를 모두 찾아 반환.
        /// 비활성 부모의 자식도 포함한다.
        /// </summary>
        public static List<GameObject> FindInactiveObjects(GameObject root)
        {
            var result = new List<GameObject>();
            if (root == null) return result;

            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (t.gameObject == root) continue;
                if (!t.gameObject.activeSelf)
                    result.Add(t.gameObject);
            }
            return result;
        }

        // ============================================================
        // [9] MagicaCloth2 Collider Symmetry Target Fixer
        // ============================================================
        //
        // Biped 변환 아바타에서는 Animator 의 휴머노이드 본이 Bip001 본이므로
        // MagicaCloth2 의 Automatic 시메트리가 반대편 본을 찾지 못한다.
        // 콜라이더의 조상 중 원본(Primary) 팔/다리 본을 찾아, 반대편 Primary 본을
        // Symmetry Target 으로 지정하고 모드를 X_Symmetry 로 고정한다.
        // MagicaCloth2 어셈블리에 의존하지 않도록 SerializedObject 로만 접근한다.

        private const int MagicaSymmetryNone = 0;
        private const int MagicaSymmetryX    = 100;

        public class MagicaSymmetryEntry
        {
            public Component Collider;
            public Transform PrimaryBone;    // 콜라이더가 속한 원본 본 (예: LeftHand)
            public Transform MirrorBone;     // 새 타겟이 될 반대편 원본 본 (예: RightHand). 없으면 null
            public Transform CurrentTarget;
            public string CurrentModeName;
            public bool Fixable => Collider != null && MirrorBone != null;
        }

        /// <summary>
        /// root 하위의 MagicaCloth2 콜라이더 중, 팔(어깨 이하)/다리 Primary 본 아래에 있고
        /// 시메트리가 켜져 있으나 타겟이 비었거나 Biped(휴머노이드 매핑) 본인 것을 찾는다.
        /// </summary>
        public static List<MagicaSymmetryEntry> ScanMagicaSymmetryTargets(GameObject root)
        {
            var result = new List<MagicaSymmetryEntry>();
            if (root == null) return result;

            var rootT = root.transform;

            // Animator 에 매핑된 본(= Biped 본)은 Primary 후보와 타겟에서 제외
            var mappedBones = new HashSet<Transform>();
            var animator = root.GetComponentInChildren<Animator>(true);
            if (animator != null && animator.isHuman)
            {
                foreach (HumanBodyBones hb in System.Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (hb == HumanBodyBones.LastBone) continue;
                    var t = animator.GetBoneTransform(hb);
                    if (t != null) mappedBones.Add(t);
                }
            }

            var limbNames = MagicaSymmetryBuildLimbNameMap();

            // 이름 → Primary 본 (매핑 본 제외, 첫 번째 우선)
            var primaryByName = new Dictionary<string, Transform>();
            foreach (var t in rootT.GetComponentsInChildren<Transform>(true))
            {
                if (!limbNames.ContainsKey(t.name)) continue;
                if (mappedBones.Contains(t) || MagicaSymmetryIsBipedName(t.name)) continue;
                if (!primaryByName.ContainsKey(t.name)) primaryByName[t.name] = t;
            }

            foreach (var c in rootT.GetComponentsInChildren<Component>(true))
            {
                if (c == null || !MagicaSymmetryIsCollider(c)) continue;

                var so = new SerializedObject(c);
                var modeProp   = so.FindProperty("symmetryMode");
                var targetProp = so.FindProperty("symmetryTarget");
                if (modeProp == null || targetProp == null) continue;
                if (modeProp.intValue == MagicaSymmetryNone) continue;

                // 가장 가까운 Primary 팔/다리 본 조상
                Transform primary = null;
                for (var cur = c.transform.parent; cur != null && cur != rootT; cur = cur.parent)
                {
                    if (primaryByName.TryGetValue(cur.name, out var p) && p == cur) { primary = cur; break; }
                }
                if (primary == null) continue;

                primaryByName.TryGetValue(limbNames[primary.name], out var mirror);

                var curTarget = targetProp.objectReferenceValue as Transform;
                bool targetBroken = curTarget == null
                                    || mappedBones.Contains(curTarget)
                                    || MagicaSymmetryIsBipedName(curTarget.name);
                if (!targetBroken) continue;

                int enumIdx = modeProp.enumValueIndex;
                result.Add(new MagicaSymmetryEntry
                {
                    Collider        = c,
                    PrimaryBone     = primary,
                    MirrorBone      = mirror,
                    CurrentTarget   = curTarget,
                    CurrentModeName = (enumIdx >= 0 && enumIdx < modeProp.enumNames.Length)
                                        ? modeProp.enumNames[enumIdx] : modeProp.intValue.ToString(),
                });
            }
            return result;
        }

        /// <summary>Fixable 항목에 X_Symmetry + 반대편 Primary 본을 적용. Undo 가능. 수정된 수 반환.</summary>
        public static int FixMagicaSymmetryTargets(List<MagicaSymmetryEntry> entries)
        {
            if (entries == null) return 0;
            int n = 0;
            foreach (var e in entries)
            {
                if (e == null || !e.Fixable) continue;
                var so = new SerializedObject(e.Collider);
                var modeProp   = so.FindProperty("symmetryMode");
                var targetProp = so.FindProperty("symmetryTarget");
                if (modeProp == null || targetProp == null) continue;

                modeProp.intValue = MagicaSymmetryX;
                targetProp.objectReferenceValue = e.MirrorBone;
                so.ApplyModifiedProperties();   // Undo + 프리팹 오버라이드 기록 포함
                n++;
            }
            return n;
        }

        /// 팔(어깨 이하, 손가락 포함)/다리 휴머노이드 본 이름 → 반대편 이름
        private static Dictionary<string, string> MagicaSymmetryBuildLimbNameMap()
        {
            var map = new Dictionary<string, string>();
            foreach (var name in System.Enum.GetNames(typeof(HumanBodyBones)))
            {
                string mirror;
                if (name.StartsWith("Left")) mirror = "Right" + name.Substring(4);
                else if (name.StartsWith("Right")) mirror = "Left" + name.Substring(5);
                else continue;
                if (name.EndsWith("Eye")) continue;
                map[name] = mirror;
            }
            return map;
        }

        private static bool MagicaSymmetryIsBipedName(string name)
        {
            return name.StartsWith("Bip001", System.StringComparison.OrdinalIgnoreCase);
        }

        private static bool MagicaSymmetryIsCollider(Component c)
        {
            for (var t = c.GetType(); t != null; t = t.BaseType)
                if (t.FullName == "MagicaCloth2.ColliderComponent") return true;
            return false;
        }

        // ============================================================
        // [10] Boneless SkinnedMeshRenderer Fixer
        // ============================================================
        //
        // 블렌드셰이프만 있고 본이 없는 메시는 Unity 가 SMR 로 임포트하지만(bones/bindposes 0개,
        // rootBone = 자기 자신), FBX/VRM 익스포터는 스킨 클러스터가 없는 스킨드 메시를 기록하지 못해
        // 메시가 통째로 소실된다. SMR 과 같은 부모·같은 로컬 트랜스폼에 본 오브젝트를 새로 만들고
        // 모든 버텍스를 그 본에 100% 스키닝해 정상적인 스킨드 메시로 만든다.

        private const string BonelessSmrFallbackFolder = "Assets/YAMO_Generated/BonelessSmrFix";

        public class BonelessSmrEntry
        {
            public SkinnedMeshRenderer Renderer;
            public int BoneCount;
            public int BindposeCount;
        }

        public static List<BonelessSmrEntry> ScanBonelessSmrsInScene()
        {
            return ScanBonelessSmrs(Object.FindObjectsByType<SkinnedMeshRenderer>(
                FindObjectsInactive.Include, FindObjectsSortMode.None));
        }

        public static List<BonelessSmrEntry> ScanBonelessSmrsInChildren(GameObject root)
        {
            if (root == null) return new List<BonelessSmrEntry>();
            return ScanBonelessSmrs(root.GetComponentsInChildren<SkinnedMeshRenderer>(true));
        }

        private static List<BonelessSmrEntry> ScanBonelessSmrs(IEnumerable<SkinnedMeshRenderer> smrs)
        {
            var results = new List<BonelessSmrEntry>();
            foreach (var smr in smrs)
            {
                if (smr == null) continue;
                var mesh = smr.sharedMesh;
                if (mesh == null || mesh.vertexCount == 0) continue;

                int boneCount     = smr.bones != null ? smr.bones.Length : 0;
                int bindposeCount = mesh.bindposes.Length;
                if (boneCount > 0 && bindposeCount > 0) continue;

                results.Add(new BonelessSmrEntry
                {
                    Renderer      = smr,
                    BoneCount     = boneCount,
                    BindposeCount = bindposeCount,
                });
            }
            return results;
        }

        /// <summary>
        /// 각 SMR 옆(같은 부모, 같은 로컬 트랜스폼)에 본을 생성하고 100% 스키닝한 메시 사본으로 교체.
        /// 원본 메시 에셋은 수정하지 않는다. Undo 가능(생성된 메시 에셋 파일은 남음). 수정된 수 반환.
        /// </summary>
        public static int FixBonelessSmrs(List<BonelessSmrEntry> entries)
        {
            if (entries == null || entries.Count == 0) return 0;

            Undo.SetCurrentGroupName("Fix Boneless SkinnedMeshRenderer");
            int group = Undo.GetCurrentGroup();

            // 본이 SMR 과 동일한 트랜스폼이라 bindpose 가 항상 identity → 같은 원본 메시는 사본 공유 가능
            var skinnedByMesh = new Dictionary<Mesh, Mesh>();
            int n = 0;
            foreach (var e in entries)
            {
                if (e == null || e.Renderer == null || e.Renderer.sharedMesh == null) continue;
                var smr = e.Renderer;
                var src = smr.sharedMesh;

                if (!skinnedByMesh.TryGetValue(src, out var skinned))
                {
                    skinned = BonelessSmrCreateSkinnedMesh(src);
                    if (skinned == null)
                    {
                        Debug.LogError($"[AssetChecker] '{smr.name}': 메시 '{src.name}' 에 스킨 정보를 쓸 수 없습니다. 모델 임포트 설정의 Read/Write 를 켠 뒤 다시 시도하세요.", smr);
                        continue;
                    }
                    skinnedByMesh[src] = skinned;
                }

                var smrT = smr.transform;
                var boneGo = new GameObject(BonelessSmrUniqueBoneName(smrT));
                Undo.RegisterCreatedObjectUndo(boneGo, "Create Bone");
                var bone = boneGo.transform;
                bone.SetParent(smrT.parent, false);
                bone.localPosition = smrT.localPosition;
                bone.localRotation = smrT.localRotation;
                bone.localScale    = smrT.localScale;
                bone.SetSiblingIndex(smrT.GetSiblingIndex() + 1);

                Undo.RecordObject(smr, "Fix Boneless SkinnedMeshRenderer");
                smr.sharedMesh = skinned;
                smr.bones      = new[] { bone };
                smr.rootBone   = bone;      // 기존 rootBone(자기 자신)과 같은 트랜스폼이라 localBounds 유지
                PrefabUtility.RecordPrefabInstancePropertyModifications(smr);
                EditorUtility.SetDirty(smr);
                n++;
            }

            Undo.CollapseUndoOperations(group);
            return n;
        }

        private static Mesh BonelessSmrCreateSkinnedMesh(Mesh src)
        {
            var mesh = Object.Instantiate(src);
            mesh.name = src.name + "_Skinned";

            if (!mesh.isReadable)
            {
                // 에디터에서는 CPU 데이터가 남아 있으므로 사본의 읽기 플래그만 켠다
                var so = new SerializedObject(mesh);
                var readable = so.FindProperty("m_IsReadable");
                if (readable != null)
                {
                    readable.boolValue = true;
                    so.ApplyModifiedPropertiesWithoutUndo();
                }
                if (!mesh.isReadable)
                {
                    Object.DestroyImmediate(mesh);
                    return null;
                }
            }

            var weights = new BoneWeight[mesh.vertexCount];
            for (int i = 0; i < weights.Length; i++)
                weights[i] = new BoneWeight { boneIndex0 = 0, weight0 = 1f };
            mesh.boneWeights = weights;
            mesh.bindposes   = new[] { Matrix4x4.identity };

            string srcPath = AssetDatabase.GetAssetPath(src);
            string folder  = !string.IsNullOrEmpty(srcPath) && srcPath.StartsWith("Assets/")
                ? System.IO.Path.GetDirectoryName(srcPath).Replace('\\', '/')
                : BonelessSmrFallbackFolder;
            if (!AssetDatabase.IsValidFolder(folder))
            {
                System.IO.Directory.CreateDirectory(folder);
                AssetDatabase.Refresh();
            }

            string fileName = mesh.name;
            foreach (var c in System.IO.Path.GetInvalidFileNameChars()) fileName = fileName.Replace(c, '_');
            AssetDatabase.CreateAsset(mesh, AssetDatabase.GenerateUniqueAssetPath($"{folder}/{fileName}.asset"));
            return mesh;
        }

        /// 익스포트/베이크 시 이름 충돌이 없도록 계층 전체에서 유일한 본 이름을 만든다.
        private static string BonelessSmrUniqueBoneName(Transform smrT)
        {
            var names = new HashSet<string>();
            foreach (var t in smrT.root.GetComponentsInChildren<Transform>(true)) names.Add(t.name);

            string baseName = smrT.name + "_Bone";
            string name = baseName;
            for (int i = 1; names.Contains(name); i++) name = $"{baseName}_{i}";
            return name;
        }
    }
}
