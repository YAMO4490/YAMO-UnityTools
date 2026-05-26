using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// BipedConverter.Convert() 의 역 작업.
    /// Biped(3ds Max) 계층 구조를 Unity 정규 휴머노이드 본 계층으로 되돌립니다.
    /// </summary>
    public static class BipedDeconverter
    {
        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        public class ValidationReport
        {
            public bool IsValid = true;
            public List<string> Errors   = new List<string>();
            public List<string> Warnings = new List<string>();
            public List<string> Info     = new List<string>();

            public string ToText()
            {
                var sb = new StringBuilder();
                if (Errors.Count > 0)
                {
                    sb.AppendLine("[오류]");
                    foreach (var e in Errors) sb.AppendLine("  ✗ " + e);
                }
                if (Warnings.Count > 0)
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine("[경고]");
                    foreach (var w in Warnings) sb.AppendLine("  ⚠ " + w);
                }
                if (Info.Count > 0)
                {
                    if (sb.Length > 0) sb.AppendLine();
                    sb.AppendLine("[정보]");
                    foreach (var i in Info) sb.AppendLine("  • " + i);
                }
                return sb.ToString().TrimEnd();
            }
        }

        public static ValidationReport Validate(GameObject armatureRoot)
        {
            var r = new ValidationReport();
            if (armatureRoot == null)
            {
                r.IsValid = false;
                r.Errors.Add("Armature 루트가 지정되지 않았습니다.");
                return r;
            }

            var all = CollectAllBones(armatureRoot);

            if (!all.ContainsKey("Bip001"))
            {
                r.IsValid = false;
                r.Errors.Add("'Bip001' 본을 찾을 수 없습니다. Biped 구조가 아니거나 이미 변환된 오브젝트입니다.");
                return r;
            }

            if (!all.ContainsKey("Hips"))
            {
                r.IsValid = false;
                r.Errors.Add("필수 본 'Hips'를 찾을 수 없습니다.");
            }

            int found = 0, missing = 0;
            foreach (var spec in Specs)
            {
                if (spec.Source == null) continue;
                if (all.ContainsKey(spec.Source))
                    found++;
                else
                {
                    missing++;
                    r.Warnings.Add($"Source 본 없음: '{spec.Source}' (Biped: '{spec.Biped}') — 건너뜀");
                }
            }

            r.Info.Add($"발견된 총 Transform: {all.Count}개");
            r.Info.Add($"매칭된 Source 본: {found}개 / {found + missing}개");

            return r;
        }

        public static GameObject Deconvert(GameObject armatureRoot)
        {
            var report = Validate(armatureRoot);
            if (!report.IsValid)
            {
                Debug.LogError("[BipedDeconverter] 역변환 실패:\n" + report.ToText());
                return null;
            }

            var workingCopy = PrepareWorkingCopy(armatureRoot);
            if (workingCopy == null)
            {
                Debug.LogError("[BipedDeconverter] 작업용 복사본 생성 실패");
                return null;
            }
            armatureRoot = workingCopy;

            // Humanoid Animator가 reparent 후 world position을 덮어쓰는 것을 방지
            foreach (var anim in armatureRoot.GetComponentsInChildren<Animator>(true))
                Undo.DestroyObjectImmediate(anim);

            var all = CollectAllBones(armatureRoot);

            var bipedToSource = new Dictionary<string, string>();
            foreach (var spec in Specs)
                if (spec.Source != null)
                    bipedToSource[spec.Biped] = spec.Source;

            // Step 1: 각 Biped 본의 비-Biped/비-Source 자식들을 Source 본 하위로 이동
            //         Bip001 삭제 시 Magica 콜라이더·물리 본 소실 방지
            foreach (var spec in Specs)
            {
                if (spec.Source == null) continue;
                if (!all.TryGetValue(spec.Biped,   out var bipedBone))  continue;
                if (!all.TryGetValue(spec.Source,  out var sourceBone)) continue;

                var toMove = new List<Transform>();
                for (int i = 0; i < bipedBone.childCount; i++)
                {
                    var child = bipedBone.GetChild(i);
                    if (child == sourceBone)             continue;
                    if (child.name.StartsWith("Bip001")) continue;
                    toMove.Add(child);
                }
                foreach (var child in toMove)
                    ReparentPreservingWorld(child, sourceBone);
            }

            // Step 2: ExtraReparents 역 — Hips를 armatureRoot 직접 자식으로 이동
            if (all.TryGetValue("Hips", out var hipsBone))
                ReparentPreservingWorld(hipsBone, armatureRoot.transform);

            // Step 3: Spec 테이블 순서(상위→하위)로 각 Source 본을 정규 부모로 reparent
            foreach (var spec in Specs)
            {
                if (spec.Source == null) continue;
                if (!all.TryGetValue(spec.Source, out var sourceBone)) continue;

                var newParent = ResolveNewParent(spec.Parent, armatureRoot.transform, all, bipedToSource);
                if (newParent == null) continue;

                ReparentPreservingWorld(sourceBone, newParent);
            }

            // Step 4: Bip001 트리 안 미매핑 본 구출(boneHead 등) → 가장 가까운 Source 조상으로 이동
            if (all.TryGetValue("Bip001", out var bip001))
            {
                var rescue = new List<Transform>();
                foreach (var t in bip001.GetComponentsInChildren<Transform>(true))
                {
                    if (t.parent != null
                        && t.parent.name.StartsWith("Bip001")
                        && !t.name.StartsWith("Bip001"))
                        rescue.Add(t);
                }
                foreach (var orphan in rescue)
                {
                    var target = FindNearestSourceAncestorOutside(orphan, all, armatureRoot.transform);
                    Debug.LogWarning($"[BipedDeconverter] 미매핑 본 구출: '{orphan.name}' → '{target.name}'");
                    ReparentPreservingWorld(orphan, target);
                }

                // Step 5: Bip001 트리 전체 삭제
                Undo.DestroyObjectImmediate(bip001.gameObject);
            }

            if (armatureRoot.name.EndsWith("_Biped"))
                armatureRoot.name = armatureRoot.name.Substring(0, armatureRoot.name.Length - "_Biped".Length);

            return armatureRoot;
        }

        // ------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------

        static Transform FindNearestSourceAncestorOutside(
            Transform orphan,
            Dictionary<string, Transform> all,
            Transform fallback)
        {
            var sourceBones = new HashSet<Transform>();
            foreach (var spec in Specs)
                if (spec.Source != null && all.TryGetValue(spec.Source, out var t))
                    sourceBones.Add(t);

            var current = orphan.parent;
            while (current != null)
            {
                if (current.name.StartsWith("Bip001")) { current = current.parent; continue; }
                if (sourceBones.Contains(current)) return current;
                current = current.parent;
            }
            return fallback;
        }

        static Transform ResolveNewParent(
            string parentBipedName,
            Transform armatureRootTransform,
            Dictionary<string, Transform> all,
            Dictionary<string, string> bipedToSource)
        {
            if (string.IsNullOrEmpty(parentBipedName) || parentBipedName == "Bip001")
                return armatureRootTransform;

            // Bip001 Pelvis는 Source=null이나 ExtraReparents로 Hips가 그 자식
            // 역: Bip001 Pelvis 자식 Source 본들의 새 부모 = Hips
            if (parentBipedName == "Bip001 Pelvis")
            {
                if (all.TryGetValue("Hips", out var hips)) return hips;
                return armatureRootTransform;
            }

            if (bipedToSource.TryGetValue(parentBipedName, out var parentSourceName)
                && all.TryGetValue(parentSourceName, out var parentSource))
                return parentSource;

            return null;
        }

        static GameObject PrepareWorkingCopy(GameObject source)
        {
            bool isProjectPrefabAsset = !source.scene.IsValid();
            GameObject copy;

            if (isProjectPrefabAsset)
            {
                copy = (GameObject)PrefabUtility.InstantiatePrefab(source, SceneManager.GetActiveScene());
                if (copy == null) return null;
                Undo.RegisterCreatedObjectUndo(copy, "Instantiate Prefab for Biped Deconversion");
            }
            else
            {
                copy = Object.Instantiate(source, source.transform.parent, worldPositionStays: true);
                if (copy == null) return null;
                Undo.RegisterCreatedObjectUndo(copy, "Clone for Biped Deconversion");
                if (copy.scene != source.scene)
                    SceneManager.MoveGameObjectToScene(copy, source.scene);
            }

            copy.name = source.name + "_Deconverted";

            if (PrefabUtility.IsPartOfPrefabInstance(copy))
                PrefabUtility.UnpackPrefabInstance(copy, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);

            return copy;
        }

        static Dictionary<string, Transform> CollectAllBones(GameObject root)
        {
            var dict = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
                if (!dict.ContainsKey(t.name))
                    dict[t.name] = t;
            return dict;
        }

        static void ReparentPreservingWorld(Transform child, Transform newParent)
        {
            var pos        = child.position;
            var rot        = child.rotation;
            var worldScale = child.lossyScale;

            Undo.RegisterCompleteObjectUndo(child, "Reparent for Biped Deconversion");
            child.SetParent(newParent, worldPositionStays: true);
            child.position = pos;
            child.rotation = rot;

            var ps = newParent.lossyScale;
            child.localScale = new Vector3(
                ps.x != 0f ? worldScale.x / ps.x : 1f,
                ps.y != 0f ? worldScale.y / ps.y : 1f,
                ps.z != 0f ? worldScale.z / ps.z : 1f);
        }

        // ------------------------------------------------------------------
        // Spec 테이블 — BipedConverter.Specs 와 동일한 Biped/Parent/Source 정의
        // ------------------------------------------------------------------

        struct Spec
        {
            public string Biped, Parent, Source;
            public Spec(string biped, string parent, string source)
            { Biped = biped; Parent = parent; Source = source; }
        }

        static readonly Spec[] Specs =
        {
            new Spec("Bip001",            "",                 null),
            new Spec("Bip001 Pelvis",     "Bip001",           null),
            new Spec("Bip001 Spine",      "Bip001 Pelvis",    "Spine"),
            new Spec("Bip001 Spine1",     "Bip001 Spine",     "Chest"),
            new Spec("Bip001 Neck",       "Bip001 Spine1",    "Neck"),
            new Spec("Bip001 Head",       "Bip001 Neck",      "Head"),

            new Spec("Bip001 L Clavicle", "Bip001 Spine1",    "LeftShoulder"),
            new Spec("Bip001 L UpperArm", "Bip001 L Clavicle","LeftUpperArm"),
            new Spec("Bip001 L Forearm",  "Bip001 L UpperArm","LeftLowerArm"),
            new Spec("Bip001 L Hand",     "Bip001 L Forearm", "LeftHand"),

            new Spec("Bip001 L Finger0",  "Bip001 L Hand",    "LeftThumbProximal"),
            new Spec("Bip001 L Finger01", "Bip001 L Finger0", "LeftThumbIntermediate"),
            new Spec("Bip001 L Finger02", "Bip001 L Finger01","LeftThumbDistal"),
            new Spec("Bip001 L Finger1",  "Bip001 L Hand",    "LeftIndexProximal"),
            new Spec("Bip001 L Finger11", "Bip001 L Finger1", "LeftIndexIntermediate"),
            new Spec("Bip001 L Finger12", "Bip001 L Finger11","LeftIndexDistal"),
            new Spec("Bip001 L Finger2",  "Bip001 L Hand",    "LeftMiddleProximal"),
            new Spec("Bip001 L Finger21", "Bip001 L Finger2", "LeftMiddleIntermediate"),
            new Spec("Bip001 L Finger22", "Bip001 L Finger21","LeftMiddleDistal"),
            new Spec("Bip001 L Finger3",  "Bip001 L Hand",    "LeftRingProximal"),
            new Spec("Bip001 L Finger31", "Bip001 L Finger3", "LeftRingIntermediate"),
            new Spec("Bip001 L Finger32", "Bip001 L Finger31","LeftRingDistal"),
            new Spec("Bip001 L Finger4",  "Bip001 L Hand",    "LeftLittleProximal"),
            new Spec("Bip001 L Finger41", "Bip001 L Finger4", "LeftLittleIntermediate"),
            new Spec("Bip001 L Finger42", "Bip001 L Finger41","LeftLittleDistal"),

            new Spec("Bip001 R Clavicle", "Bip001 Spine1",    "RightShoulder"),
            new Spec("Bip001 R UpperArm", "Bip001 R Clavicle","RightUpperArm"),
            new Spec("Bip001 R Forearm",  "Bip001 R UpperArm","RightLowerArm"),
            new Spec("Bip001 R Hand",     "Bip001 R Forearm", "RightHand"),

            new Spec("Bip001 R Finger0",  "Bip001 R Hand",    "RightThumbProximal"),
            new Spec("Bip001 R Finger01", "Bip001 R Finger0", "RightThumbIntermediate"),
            new Spec("Bip001 R Finger02", "Bip001 R Finger01","RightThumbDistal"),
            new Spec("Bip001 R Finger1",  "Bip001 R Hand",    "RightIndexProximal"),
            new Spec("Bip001 R Finger11", "Bip001 R Finger1", "RightIndexIntermediate"),
            new Spec("Bip001 R Finger12", "Bip001 R Finger11","RightIndexDistal"),
            new Spec("Bip001 R Finger2",  "Bip001 R Hand",    "RightMiddleProximal"),
            new Spec("Bip001 R Finger21", "Bip001 R Finger2", "RightMiddleIntermediate"),
            new Spec("Bip001 R Finger22", "Bip001 R Finger21","RightMiddleDistal"),
            new Spec("Bip001 R Finger3",  "Bip001 R Hand",    "RightRingProximal"),
            new Spec("Bip001 R Finger31", "Bip001 R Finger3", "RightRingIntermediate"),
            new Spec("Bip001 R Finger32", "Bip001 R Finger31","RightRingDistal"),
            new Spec("Bip001 R Finger4",  "Bip001 R Hand",    "RightLittleProximal"),
            new Spec("Bip001 R Finger41", "Bip001 R Finger4", "RightLittleIntermediate"),
            new Spec("Bip001 R Finger42", "Bip001 R Finger41","RightLittleDistal"),

            new Spec("Bip001 L Thigh",    "Bip001 Pelvis",    "LeftUpperLeg"),
            new Spec("Bip001 L Calf",     "Bip001 L Thigh",   "LeftLowerLeg"),
            new Spec("Bip001 L Foot",     "Bip001 L Calf",    "LeftFoot"),
            new Spec("Bip001 L Toe0",     "Bip001 L Foot",    "LeftToes"),

            new Spec("Bip001 R Thigh",    "Bip001 Pelvis",    "RightUpperLeg"),
            new Spec("Bip001 R Calf",     "Bip001 R Thigh",   "RightLowerLeg"),
            new Spec("Bip001 R Foot",     "Bip001 R Calf",    "RightFoot"),
            new Spec("Bip001 R Toe0",     "Bip001 R Foot",    "RightToes"),
        };
    }
}
