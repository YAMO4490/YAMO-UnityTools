using System.Collections.Generic;
using System.Text;
using UnityEditor;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace YAMO.UnityTools.Editor
{
    public static class BipedConverter
    {
        // ------------------------------------------------------------------
        // Public API
        // ------------------------------------------------------------------

        public class ValidationReport
        {
            public bool IsValid = true;
            public List<string> Errors = new List<string>();
            public List<string> Warnings = new List<string>();
            public List<string> Info = new List<string>();

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

            var src = CollectSourceBones(armatureRoot);

            string[] required = { "Hips", "LeftUpperLeg", "RightUpperLeg", "Spine", "Chest", "Head" };
            foreach (var n in required)
            {
                if (!src.ContainsKey(n))
                {
                    r.IsValid = false;
                    r.Errors.Add($"필수 본 누락: '{n}'");
                }
            }

            int totalOptional = 0;
            int missingOptional = 0;
            foreach (var s in Specs)
            {
                if (s.Source == null) continue;
                if (System.Array.IndexOf(required, s.Source) >= 0) continue;
                totalOptional++;
                if (!src.ContainsKey(s.Source))
                {
                    missingOptional++;
                    r.Warnings.Add($"선택 본 누락: '{s.Source}' → '{s.Biped}' 생성 건너뜀");
                }
            }

            if (r.IsValid)
            {
                var hips = src["Hips"];
                var lt = src["LeftUpperLeg"];
                var rt = src["RightUpperLeg"];
                float comY = (lt.position.y + rt.position.y) * 0.5f;
                var com = new Vector3(hips.position.x, comY, hips.position.z);

                r.Info.Add($"발견된 본: 총 {src.Count}개");
                r.Info.Add($"매칭된 선택 본: {totalOptional - missingOptional}/{totalOptional}");
                r.Info.Add($"COM 위치 (예정): ({com.x:F3}, {com.y:F3}, {com.z:F3})");
                r.Info.Add($"  - Hips world Y: {hips.position.y:F3}");
                r.Info.Add($"  - Thigh world Y (avg): {comY:F3}");
                r.Info.Add($"  - 보정량 (Spine 첫 segment로 흡수): {hips.position.y - comY:F3}");
            }

            return r;
        }

        public static string ValidateBipedTemplate(GameObject template)
        {
            if (template == null) return "템플릿이 null입니다.";

            var names = new HashSet<string>();
            foreach (var t in template.GetComponentsInChildren<Transform>(true))
                names.Add(t.name);

            if (!names.Contains("Bip001"))
                return "'Bip001' 본을 찾을 수 없습니다. 유효한 3ds Max Biped 형식이 아닙니다.";

            string[] coreBones = {
                "Bip001 Pelvis", "Bip001 Spine", "Bip001 Spine1",
                "Bip001 Neck", "Bip001 Head",
                "Bip001 L Thigh", "Bip001 R Thigh",
                "Bip001 L Calf", "Bip001 R Calf",
                "Bip001 L UpperArm", "Bip001 R UpperArm",
                "Bip001 L Forearm", "Bip001 R Forearm",
            };

            var missing = new List<string>();
            foreach (var b in coreBones)
                if (!names.Contains(b)) missing.Add(b);

            if (missing.Count > 0)
                return $"기본 Biped 본이 부족합니다:\n{string.Join(", ", missing)}";

            return null;
        }

        public const string TemplatesFolder = "Packages/com.yamo.unitytools/Editor/BipedConverter/Templates";

        public static GameObject Convert(GameObject armatureRoot, string templatePath)
        {
            var report = Validate(armatureRoot);
            if (!report.IsValid)
            {
                Debug.LogError("[BipedConverter] 변환 실패:\n" + report.ToText());
                return null;
            }

            if (string.IsNullOrEmpty(templatePath))
            {
                Debug.LogError("[BipedConverter] 템플릿 경로가 지정되지 않았습니다.");
                return null;
            }

            var workingCopy = PrepareWorkingCopy(armatureRoot);
            if (workingCopy == null)
            {
                Debug.LogError("[BipedConverter] 작업용 복사본 생성 실패");
                return null;
            }
            armatureRoot = workingCopy;

            var templatePrefab = AssetDatabase.LoadAssetAtPath<GameObject>(templatePath);
            if (templatePrefab == null)
            {
                Debug.LogError($"[BipedConverter] Biped 템플릿 FBX를 찾을 수 없습니다: {templatePath}");
                return null;
            }

            var wrapper = (GameObject)Object.Instantiate(templatePrefab);
            Undo.RegisterCreatedObjectUndo(wrapper, "Instantiate Biped Template");
            if (wrapper.scene != armatureRoot.scene)
                SceneManager.MoveGameObjectToScene(wrapper, armatureRoot.scene);

            Transform bip001 = null;
            foreach (var t in wrapper.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Bip001") { bip001 = t; break; }
            }
            if (bip001 == null)
            {
                Debug.LogError("[BipedConverter] 템플릿에 'Bip001'을 찾을 수 없습니다.");
                Undo.DestroyObjectImmediate(wrapper);
                return null;
            }

            var biped = new Dictionary<string, Transform>();
            foreach (var t in bip001.GetComponentsInChildren<Transform>(true))
                biped[t.name] = t;

            DeleteBoneIfExists(biped, "Bip001 Footsteps");
            DeleteBoneIfExists(biped, "Bip001 HeadNub");
            DeleteBoneIfExists(biped, "Bip001 L Toe0Nub");
            DeleteBoneIfExists(biped, "Bip001 R Toe0Nub");
            for (int f = 0; f <= 4; f++)
            {
                DeleteBoneIfExists(biped, $"Bip001 L Finger{f}Nub");
                DeleteBoneIfExists(biped, $"Bip001 R Finger{f}Nub");
            }

            var src = CollectSourceBones(armatureRoot);
            Vector3 com = ComputeComPosition(src);

            Transform originalArmatureContainer = src["Hips"].parent;

            var targetPos = new Dictionary<string, Vector3>();
            foreach (var spec in Specs)
            {
                if (spec.Source == null)
                {
                    targetPos[spec.Biped] = com;
                }
                else if (src.TryGetValue(spec.Source, out var sioBone))
                {
                    var p = sioBone.position;
                    if (spec.ForceComY) p = new Vector3(p.x, com.y, p.z);
                    targetPos[spec.Biped] = p;
                }
            }

            foreach (var spec in Specs)
            {
                if (!biped.TryGetValue(spec.Biped, out var t)) continue;
                if (!targetPos.TryGetValue(spec.Biped, out var pos)) continue;
                t.position = pos;
            }

            EnforceSpine1Vertical(biped);
            foreach (var chain in StraightenChains)
                StraightenFingerChain(biped, chain);

            ReparentOriginalBones(src, biped);

            ReparentPreservingWorld(bip001, armatureRoot.transform);

            Undo.DestroyObjectImmediate(wrapper);

            if (originalArmatureContainer != null
                && originalArmatureContainer != armatureRoot.transform
                && originalArmatureContainer.childCount == 0)
            {
                Undo.DestroyObjectImmediate(originalArmatureContainer.gameObject);
            }

            var animator = armatureRoot.GetComponent<Animator>();
            if (animator != null)
                Undo.DestroyObjectImmediate(animator);

            return armatureRoot;
        }

        static GameObject PrepareWorkingCopy(GameObject source)
        {
            bool isProjectPrefabAsset = !source.scene.IsValid();
            GameObject copy;

            if (isProjectPrefabAsset)
            {
                var targetScene = SceneManager.GetActiveScene();
                copy = (GameObject)PrefabUtility.InstantiatePrefab(source, targetScene);
                if (copy == null) return null;
                Undo.RegisterCreatedObjectUndo(copy, "Instantiate Prefab for Biped Conversion");
            }
            else
            {
                copy = Object.Instantiate(source, source.transform.parent, worldPositionStays: true);
                if (copy == null) return null;
                Undo.RegisterCreatedObjectUndo(copy, "Clone for Biped Conversion");
                if (copy.scene != source.scene)
                    SceneManager.MoveGameObjectToScene(copy, source.scene);
            }

            copy.name = $"{source.name}_Biped";

            if (PrefabUtility.IsPartOfPrefabInstance(copy))
            {
                PrefabUtility.UnpackPrefabInstance(
                    copy, PrefabUnpackMode.Completely, InteractionMode.AutomatedAction);
            }

            return copy;
        }

        static void DeleteBoneIfExists(Dictionary<string, Transform> bones, string name)
        {
            if (bones.TryGetValue(name, out var t) && t != null)
            {
                Undo.DestroyObjectImmediate(t.gameObject);
                bones.Remove(name);
            }
        }

        // ------------------------------------------------------------------
        // Internals
        // ------------------------------------------------------------------

        static Dictionary<string, Transform> CollectSourceBones(GameObject root)
        {
            var dict = new Dictionary<string, Transform>();
            foreach (var t in root.GetComponentsInChildren<Transform>(true))
            {
                if (!dict.ContainsKey(t.name))
                    dict[t.name] = t;
            }
            return dict;
        }

        static Vector3 ComputeComPosition(Dictionary<string, Transform> src)
        {
            var hips = src["Hips"];
            var l = src["LeftUpperLeg"];
            var r = src["RightUpperLeg"];
            float comY = (l.position.y + r.position.y) * 0.5f;
            return new Vector3(hips.position.x, comY, hips.position.z);
        }

        // ------------------------------------------------------------------
        // Biped 축 제약 후처리
        // ------------------------------------------------------------------

        static void EnforceSpine1Vertical(Dictionary<string, Transform> biped)
        {
            if (!biped.TryGetValue("Bip001 Spine", out var spine)) return;
            if (!biped.TryGetValue("Bip001 Spine1", out var spine1)) return;
            if (spine == null || spine1 == null) return;

            var sp = spine.position;
            var s1 = spine1.position;
            spine1.position = new Vector3(sp.x, s1.y, sp.z);
        }

        struct FingerChain
        {
            public string Proximal, Intermediate, Distal;
            public FingerChain(string p, string i, string d) { Proximal = p; Intermediate = i; Distal = d; }
        }

        static readonly FingerChain[] StraightenChains = new[]
        {
            new FingerChain("Bip001 L Finger0",  "Bip001 L Finger01", "Bip001 L Finger02"),
            new FingerChain("Bip001 R Finger0",  "Bip001 R Finger01", "Bip001 R Finger02"),
            new FingerChain("Bip001 L Finger1",  "Bip001 L Finger11", "Bip001 L Finger12"),
            new FingerChain("Bip001 R Finger1",  "Bip001 R Finger11", "Bip001 R Finger12"),
            new FingerChain("Bip001 L Finger2",  "Bip001 L Finger21", "Bip001 L Finger22"),
            new FingerChain("Bip001 R Finger2",  "Bip001 R Finger21", "Bip001 R Finger22"),
            new FingerChain("Bip001 L Finger3",  "Bip001 L Finger31", "Bip001 L Finger32"),
            new FingerChain("Bip001 R Finger3",  "Bip001 R Finger31", "Bip001 R Finger32"),
            new FingerChain("Bip001 L Finger4",  "Bip001 L Finger41", "Bip001 L Finger42"),
            new FingerChain("Bip001 R Finger4",  "Bip001 R Finger41", "Bip001 R Finger42"),
        };

        static void StraightenFingerChain(Dictionary<string, Transform> biped, FingerChain c)
        {
            if (!biped.TryGetValue(c.Proximal, out var p) || p == null) return;
            if (!biped.TryGetValue(c.Intermediate, out var i) || i == null) return;
            if (!biped.TryGetValue(c.Distal, out var d) || d == null) return;

            float l1 = Vector3.Distance(p.position, i.position);
            float l2 = Vector3.Distance(i.position, d.position);

            Vector3 currentDir = i.position - p.position;
            Vector3 desiredDir = d.position - p.position;
            if (currentDir.sqrMagnitude < 1e-12f || desiredDir.sqrMagnitude < 1e-12f) return;
            currentDir.Normalize();
            desiredDir.Normalize();

            Quaternion delta = Quaternion.FromToRotation(currentDir, desiredDir);
            p.rotation = delta * p.rotation;

            i.position = p.position + desiredDir * l1;
            d.position = p.position + desiredDir * (l1 + l2);
        }

        // ------------------------------------------------------------------
        // Biped specification table
        // ------------------------------------------------------------------

        struct Spec
        {
            public string Biped, Parent, Source, Child;
            public bool ForceComY;
            public bool HasFixedLocalEuler;
            public Vector3 FixedLocalEuler;

            public Spec(string biped, string parent, string source, string child, bool forceComY = false)
            {
                Biped = biped; Parent = parent; Source = source; Child = child; ForceComY = forceComY;
                HasFixedLocalEuler = false; FixedLocalEuler = default;
            }

            public Spec WithLocalEuler(float x, float y, float z)
            {
                HasFixedLocalEuler = true;
                FixedLocalEuler = new Vector3(x, y, z);
                return this;
            }
        }

        static readonly Spec[] Specs = new[]
        {
            new Spec("Bip001",            "",                 null,                       "Bip001 Spine").WithLocalEuler(-90, 0, -90),
            new Spec("Bip001 Pelvis",     "Bip001",           null,                       "Bip001 Spine").WithLocalEuler(-90, 0,  90),
            new Spec("Bip001 Spine",      "Bip001 Pelvis",    "Spine",                    "Bip001 Spine1").WithLocalEuler(0, 0, 0),
            new Spec("Bip001 Spine1",     "Bip001 Spine",     "Chest",                    "Bip001 Neck"  ).WithLocalEuler(0, 0, 0),
            new Spec("Bip001 Neck",       "Bip001 Spine1",    "Neck",                     "Bip001 Head"  ).WithLocalEuler(0, 0, 0),
            new Spec("Bip001 Head",       "Bip001 Neck",      "Head",                     null           ).WithLocalEuler(0, 0, 0),

            new Spec("Bip001 L Clavicle", "Bip001 Spine1",    "LeftShoulder",             "Bip001 L UpperArm"),
            new Spec("Bip001 L UpperArm", "Bip001 L Clavicle","LeftUpperArm",             "Bip001 L Forearm"),
            new Spec("Bip001 L Forearm",  "Bip001 L UpperArm","LeftLowerArm",             "Bip001 L Hand"),
            new Spec("Bip001 L Hand",     "Bip001 L Forearm", "LeftHand",                 null),

            new Spec("Bip001 L Finger0",  "Bip001 L Hand",    "LeftThumbProximal",        "Bip001 L Finger01"),
            new Spec("Bip001 L Finger01", "Bip001 L Finger0", "LeftThumbIntermediate",    "Bip001 L Finger02"),
            new Spec("Bip001 L Finger02", "Bip001 L Finger01","LeftThumbDistal",          null),
            new Spec("Bip001 L Finger1",  "Bip001 L Hand",    "LeftIndexProximal",        "Bip001 L Finger11"),
            new Spec("Bip001 L Finger11", "Bip001 L Finger1", "LeftIndexIntermediate",    "Bip001 L Finger12"),
            new Spec("Bip001 L Finger12", "Bip001 L Finger11","LeftIndexDistal",          null),
            new Spec("Bip001 L Finger2",  "Bip001 L Hand",    "LeftMiddleProximal",       "Bip001 L Finger21"),
            new Spec("Bip001 L Finger21", "Bip001 L Finger2", "LeftMiddleIntermediate",   "Bip001 L Finger22"),
            new Spec("Bip001 L Finger22", "Bip001 L Finger21","LeftMiddleDistal",         null),
            new Spec("Bip001 L Finger3",  "Bip001 L Hand",    "LeftRingProximal",         "Bip001 L Finger31"),
            new Spec("Bip001 L Finger31", "Bip001 L Finger3", "LeftRingIntermediate",     "Bip001 L Finger32"),
            new Spec("Bip001 L Finger32", "Bip001 L Finger31","LeftRingDistal",           null),
            new Spec("Bip001 L Finger4",  "Bip001 L Hand",    "LeftLittleProximal",       "Bip001 L Finger41"),
            new Spec("Bip001 L Finger41", "Bip001 L Finger4", "LeftLittleIntermediate",   "Bip001 L Finger42"),
            new Spec("Bip001 L Finger42", "Bip001 L Finger41","LeftLittleDistal",         null),

            new Spec("Bip001 R Clavicle", "Bip001 Spine1",    "RightShoulder",            "Bip001 R UpperArm"),
            new Spec("Bip001 R UpperArm", "Bip001 R Clavicle","RightUpperArm",            "Bip001 R Forearm"),
            new Spec("Bip001 R Forearm",  "Bip001 R UpperArm","RightLowerArm",            "Bip001 R Hand"),
            new Spec("Bip001 R Hand",     "Bip001 R Forearm", "RightHand",                null),

            new Spec("Bip001 R Finger0",  "Bip001 R Hand",    "RightThumbProximal",       "Bip001 R Finger01"),
            new Spec("Bip001 R Finger01", "Bip001 R Finger0", "RightThumbIntermediate",   "Bip001 R Finger02"),
            new Spec("Bip001 R Finger02", "Bip001 R Finger01","RightThumbDistal",         null),
            new Spec("Bip001 R Finger1",  "Bip001 R Hand",    "RightIndexProximal",       "Bip001 R Finger11"),
            new Spec("Bip001 R Finger11", "Bip001 R Finger1", "RightIndexIntermediate",   "Bip001 R Finger12"),
            new Spec("Bip001 R Finger12", "Bip001 R Finger11","RightIndexDistal",         null),
            new Spec("Bip001 R Finger2",  "Bip001 R Hand",    "RightMiddleProximal",      "Bip001 R Finger21"),
            new Spec("Bip001 R Finger21", "Bip001 R Finger2", "RightMiddleIntermediate",  "Bip001 R Finger22"),
            new Spec("Bip001 R Finger22", "Bip001 R Finger21","RightMiddleDistal",        null),
            new Spec("Bip001 R Finger3",  "Bip001 R Hand",    "RightRingProximal",        "Bip001 R Finger31"),
            new Spec("Bip001 R Finger31", "Bip001 R Finger3", "RightRingIntermediate",    "Bip001 R Finger32"),
            new Spec("Bip001 R Finger32", "Bip001 R Finger31","RightRingDistal",          null),
            new Spec("Bip001 R Finger4",  "Bip001 R Hand",    "RightLittleProximal",      "Bip001 R Finger41"),
            new Spec("Bip001 R Finger41", "Bip001 R Finger4", "RightLittleIntermediate",  "Bip001 R Finger42"),
            new Spec("Bip001 R Finger42", "Bip001 R Finger41","RightLittleDistal",        null),

            new Spec("Bip001 L Thigh",    "Bip001 Pelvis",    "LeftUpperLeg",             "Bip001 L Calf",  forceComY: true),
            new Spec("Bip001 L Calf",     "Bip001 L Thigh",   "LeftLowerLeg",             "Bip001 L Foot"),
            new Spec("Bip001 L Foot",     "Bip001 L Calf",    "LeftFoot",                 "Bip001 L Toe0"),
            new Spec("Bip001 L Toe0",     "Bip001 L Foot",    "LeftToes",                 null),

            new Spec("Bip001 R Thigh",    "Bip001 Pelvis",    "RightUpperLeg",            "Bip001 R Calf",  forceComY: true),
            new Spec("Bip001 R Calf",     "Bip001 R Thigh",   "RightLowerLeg",            "Bip001 R Foot"),
            new Spec("Bip001 R Foot",     "Bip001 R Calf",    "RightFoot",                "Bip001 R Toe0"),
            new Spec("Bip001 R Toe0",     "Bip001 R Foot",    "RightToes",                null),
        };

        static readonly Dictionary<string, string> ExtraReparents = new Dictionary<string, string>
        {
            { "Hips", "Bip001 Pelvis" },
        };

        // ------------------------------------------------------------------
        // Reparenting (world transform 보존)
        // ------------------------------------------------------------------

        static void ReparentOriginalBones(
            Dictionary<string, Transform> src,
            Dictionary<string, Transform> biped)
        {
            foreach (var s in Specs)
            {
                if (string.IsNullOrEmpty(s.Source)) continue;
                if (!src.ContainsKey(s.Source) || !biped.ContainsKey(s.Biped)) continue;
                ReparentPreservingWorld(src[s.Source], biped[s.Biped]);
            }

            foreach (var kv in ExtraReparents)
            {
                if (!src.ContainsKey(kv.Key) || !biped.ContainsKey(kv.Value)) continue;
                ReparentPreservingWorld(src[kv.Key], biped[kv.Value]);
            }
        }

        static void ReparentPreservingWorld(Transform child, Transform newParent)
        {
            var pos = child.position;
            var rot = child.rotation;
            Undo.SetTransformParent(child, newParent, "Reparent to Biped");
            child.position = pos;
            child.rotation = rot;
        }
    }
}
