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
    }
}
