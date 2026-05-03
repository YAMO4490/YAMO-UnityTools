// 베이크 전 정리(pre-bake) 작업을 위한 정적 헬퍼.
//
// 출처(독립 복사):
//   - 중복 이름 탐색/리네임:   Packages/com.yamo.unitytools/Editor/Hierarchy/ObjectNameModifier.cs
//   - 휴머노이드 표준 리네임:  Packages/com.yamo.unitytools/Editor/Bones/HumanBoneRenamer.cs
//
// 추후 두 도구가 정식 통합되면 여기를 단일 source-of-truth 로 두고 원본 스크립트는
// fork 하거나 제거할 예정.

using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    public static class AvatarBakePreUtilities
    {
        // ============================================================
        // 중복 이름
        // ============================================================

        /// <summary>
        /// root 트리 내 모든 Transform 을 순회해 동일 이름을 가진 것들을 그룹핑.
        /// 반환 dict 의 key=중복된 이름, value=그 이름을 가진 Transform 들 (2개 이상).
        /// </summary>
        public static Dictionary<string, List<Transform>> FindDuplicateNames(GameObject root)
        {
            var groups = new Dictionary<string, List<Transform>>();
            if (root == null) return groups;

            var nameMap = new Dictionary<string, List<Transform>>();
            foreach (Transform t in root.GetComponentsInChildren<Transform>(true))
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

        /// <summary>
        /// 중복 그룹의 첫 번째는 원래 이름 유지, 두 번째 이후는 "{name}_1", "{name}_2", ... 로
        /// 자동 리네임. Undo 등록.
        /// </summary>
        /// <returns>실제로 이름이 바뀐 Transform 개수.</returns>
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
        // Humanoid Unity 표준 이름으로 리네임
        // ============================================================

        // Unity Human Bone 표준 이름 (HumanBodyBones → 표준 문자열)
        public static readonly Dictionary<HumanBodyBones, string> UnityHumanBoneNames = new Dictionary<HumanBodyBones, string>
        {
            { HumanBodyBones.Hips, "Hips" }, { HumanBodyBones.Spine, "Spine" },
            { HumanBodyBones.Chest, "Chest" }, { HumanBodyBones.UpperChest, "UpperChest" },
            { HumanBodyBones.Neck, "Neck" }, { HumanBodyBones.Head, "Head" },

            { HumanBodyBones.LeftShoulder, "LeftShoulder" }, { HumanBodyBones.LeftUpperArm, "LeftUpperArm" },
            { HumanBodyBones.LeftLowerArm, "LeftLowerArm" }, { HumanBodyBones.LeftHand, "LeftHand" },
            { HumanBodyBones.RightShoulder, "RightShoulder" }, { HumanBodyBones.RightUpperArm, "RightUpperArm" },
            { HumanBodyBones.RightLowerArm, "RightLowerArm" }, { HumanBodyBones.RightHand, "RightHand" },

            { HumanBodyBones.LeftUpperLeg, "LeftUpperLeg" }, { HumanBodyBones.LeftLowerLeg, "LeftLowerLeg" },
            { HumanBodyBones.LeftFoot, "LeftFoot" }, { HumanBodyBones.LeftToes, "LeftToes" },
            { HumanBodyBones.RightUpperLeg, "RightUpperLeg" }, { HumanBodyBones.RightLowerLeg, "RightLowerLeg" },
            { HumanBodyBones.RightFoot, "RightFoot" }, { HumanBodyBones.RightToes, "RightToes" },

            { HumanBodyBones.LeftThumbProximal, "LeftThumbProximal" }, { HumanBodyBones.LeftThumbIntermediate, "LeftThumbIntermediate" }, { HumanBodyBones.LeftThumbDistal, "LeftThumbDistal" },
            { HumanBodyBones.LeftIndexProximal, "LeftIndexProximal" }, { HumanBodyBones.LeftIndexIntermediate, "LeftIndexIntermediate" }, { HumanBodyBones.LeftIndexDistal, "LeftIndexDistal" },
            { HumanBodyBones.LeftMiddleProximal, "LeftMiddleProximal" }, { HumanBodyBones.LeftMiddleIntermediate, "LeftMiddleIntermediate" }, { HumanBodyBones.LeftMiddleDistal, "LeftMiddleDistal" },
            { HumanBodyBones.LeftRingProximal, "LeftRingProximal" }, { HumanBodyBones.LeftRingIntermediate, "LeftRingIntermediate" }, { HumanBodyBones.LeftRingDistal, "LeftRingDistal" },
            { HumanBodyBones.LeftLittleProximal, "LeftLittleProximal" }, { HumanBodyBones.LeftLittleIntermediate, "LeftLittleIntermediate" }, { HumanBodyBones.LeftLittleDistal, "LeftLittleDistal" },

            { HumanBodyBones.RightThumbProximal, "RightThumbProximal" }, { HumanBodyBones.RightThumbIntermediate, "RightThumbIntermediate" }, { HumanBodyBones.RightThumbDistal, "RightThumbDistal" },
            { HumanBodyBones.RightIndexProximal, "RightIndexProximal" }, { HumanBodyBones.RightIndexIntermediate, "RightIndexIntermediate" }, { HumanBodyBones.RightIndexDistal, "RightIndexDistal" },
            { HumanBodyBones.RightMiddleProximal, "RightMiddleProximal" }, { HumanBodyBones.RightMiddleIntermediate, "RightMiddleIntermediate" }, { HumanBodyBones.RightMiddleDistal, "RightMiddleDistal" },
            { HumanBodyBones.RightRingProximal, "RightRingProximal" }, { HumanBodyBones.RightRingIntermediate, "RightRingIntermediate" }, { HumanBodyBones.RightRingDistal, "RightRingDistal" },
            { HumanBodyBones.RightLittleProximal, "RightLittleProximal" }, { HumanBodyBones.RightLittleIntermediate, "RightLittleIntermediate" }, { HumanBodyBones.RightLittleDistal, "RightLittleDistal" },
        };

        // 3ds Max Biped 이름 → HumanBodyBones (Animator 가 없을 때 fallback)
        public static readonly Dictionary<string, HumanBodyBones> BipedNameToHumanBone = new Dictionary<string, HumanBodyBones>
        {
            { "Bip001 Pelvis", HumanBodyBones.Hips },
            { "Bip001 Spine", HumanBodyBones.Spine },
            { "Bip001 Spine1", HumanBodyBones.Chest },
            { "Bip001 Neck", HumanBodyBones.Neck },
            { "Bip001 Head", HumanBodyBones.Head },

            { "Bip001 L Clavicle", HumanBodyBones.LeftShoulder },
            { "Bip001 L UpperArm", HumanBodyBones.LeftUpperArm },
            { "Bip001 L Forearm", HumanBodyBones.LeftLowerArm },
            { "Bip001 L Hand", HumanBodyBones.LeftHand },
            { "Bip001 R Clavicle", HumanBodyBones.RightShoulder },
            { "Bip001 R UpperArm", HumanBodyBones.RightUpperArm },
            { "Bip001 R Forearm", HumanBodyBones.RightLowerArm },
            { "Bip001 R Hand", HumanBodyBones.RightHand },

            { "Bip001 L Thigh", HumanBodyBones.LeftUpperLeg },
            { "Bip001 L Calf", HumanBodyBones.LeftLowerLeg },
            { "Bip001 L Foot", HumanBodyBones.LeftFoot },
            { "Bip001 L Toe0", HumanBodyBones.LeftToes },
            { "Bip001 R Thigh", HumanBodyBones.RightUpperLeg },
            { "Bip001 R Calf", HumanBodyBones.RightLowerLeg },
            { "Bip001 R Foot", HumanBodyBones.RightFoot },
            { "Bip001 R Toe0", HumanBodyBones.RightToes },

            { "Bip001 L Finger0",  HumanBodyBones.LeftThumbProximal },
            { "Bip001 L Finger01", HumanBodyBones.LeftThumbIntermediate },
            { "Bip001 L Finger02", HumanBodyBones.LeftThumbDistal },
            { "Bip001 L Finger1",  HumanBodyBones.LeftIndexProximal },
            { "Bip001 L Finger11", HumanBodyBones.LeftIndexIntermediate },
            { "Bip001 L Finger12", HumanBodyBones.LeftIndexDistal },
            { "Bip001 L Finger2",  HumanBodyBones.LeftMiddleProximal },
            { "Bip001 L Finger21", HumanBodyBones.LeftMiddleIntermediate },
            { "Bip001 L Finger22", HumanBodyBones.LeftMiddleDistal },
            { "Bip001 L Finger3",  HumanBodyBones.LeftRingProximal },
            { "Bip001 L Finger31", HumanBodyBones.LeftRingIntermediate },
            { "Bip001 L Finger32", HumanBodyBones.LeftRingDistal },
            { "Bip001 L Finger4",  HumanBodyBones.LeftLittleProximal },
            { "Bip001 L Finger41", HumanBodyBones.LeftLittleIntermediate },
            { "Bip001 L Finger42", HumanBodyBones.LeftLittleDistal },

            { "Bip001 R Finger0",  HumanBodyBones.RightThumbProximal },
            { "Bip001 R Finger01", HumanBodyBones.RightThumbIntermediate },
            { "Bip001 R Finger02", HumanBodyBones.RightThumbDistal },
            { "Bip001 R Finger1",  HumanBodyBones.RightIndexProximal },
            { "Bip001 R Finger11", HumanBodyBones.RightIndexIntermediate },
            { "Bip001 R Finger12", HumanBodyBones.RightIndexDistal },
            { "Bip001 R Finger2",  HumanBodyBones.RightMiddleProximal },
            { "Bip001 R Finger21", HumanBodyBones.RightMiddleIntermediate },
            { "Bip001 R Finger22", HumanBodyBones.RightMiddleDistal },
            { "Bip001 R Finger3",  HumanBodyBones.RightRingProximal },
            { "Bip001 R Finger31", HumanBodyBones.RightRingIntermediate },
            { "Bip001 R Finger32", HumanBodyBones.RightRingDistal },
            { "Bip001 R Finger4",  HumanBodyBones.RightLittleProximal },
            { "Bip001 R Finger41", HumanBodyBones.RightLittleIntermediate },
            { "Bip001 R Finger42", HumanBodyBones.RightLittleDistal },
        };

        /// <summary>
        /// target 의 휴먼본을 식별해 (HumanBodyBones → Transform) 사전을 반환.
        /// 우선순위:
        ///   1. Animator 가 있고 isHuman 이면 Animator.GetBoneTransform 사용
        ///   2. 그 외에는 BipedNameToHumanBone 매핑으로 이름 기반 추정
        /// </summary>
        public static Dictionary<HumanBodyBones, Transform> GetHumanBones(GameObject target)
        {
            var bones = new Dictionary<HumanBodyBones, Transform>();
            if (target == null) return bones;

            var animator = target.GetComponent<Animator>();
            if (animator != null && animator.avatar != null && animator.avatar.isHuman)
            {
                foreach (HumanBodyBones boneType in System.Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (boneType == HumanBodyBones.LastBone) continue;
                    var t = animator.GetBoneTransform(boneType);
                    if (t != null) bones[boneType] = t;
                }
                return bones;
            }

            // Fallback: 이름 기반 (Biped 매핑)
            foreach (var t in target.GetComponentsInChildren<Transform>(true))
            {
                if (BipedNameToHumanBone.TryGetValue(t.name, out var boneType))
                {
                    bones[boneType] = t;
                }
            }
            return bones;
        }

        /// <summary>
        /// 휴머노이드 리네임 결과 + 사후 진단 리포트.
        /// </summary>
        public class HumanoidRenameReport
        {
            // ---- abort 정보 (pre-flight 실패 시) ----
            public bool   Aborted;
            public string AbortReason;

            // ---- 계층 거리 진단 ----
            // 표준 Humanoid 의 chain : Spine → Chest → UpperChest → Neck → Head
            // 따라서 정상 범위:
            //   Spine→Neck 사이 intermediates ≤ 2 (Chest, UpperChest)
            //   Chest→Head 사이 intermediates ≤ 2 (UpperChest, Neck)
            // 그보다 크면 mocap 등 비정상 chain 으로 판단해 중단.
            public int SpineToNeckIntermediates = -1;   // -1 = 측정 안 함 (본 누락 등)
            public int ChestToHeadIntermediates = -1;

            // ---- 리네임 결과 ----
            public int  RenamedCount;
            public bool BonesDetected;
            public int  SpineCount;
            public int  ChestCount;
            public bool HasUpperChest;
            public bool UpperChestRenamedToSecondary;
            public bool LeftToesDetected;
            public bool RightToesDetected;
        }

        public const string UpperChestReplacementName = "Chest_Secondary";

        // 정상으로 허용할 chain intermediates 최댓값 (Humanoid 표준 = 2).
        private const int MaxChainIntermediatesNormal = 2;

        /// <summary>
        /// 본 chain 의 intermediates 개수를 셉니다. descendant 의 ancestor 체인을 거슬러
        /// 올라가면서 ancestor 를 만나기 전까지의 transform 수를 반환.
        /// 둘이 동일 chain 상에 있지 않으면 -1.
        /// </summary>
        private static int CountChainIntermediates(Transform descendant, Transform ancestor)
        {
            if (descendant == null || ancestor == null) return -1;
            int hops = 0;
            var t = descendant.parent;
            while (t != null && t != ancestor)
            {
                hops++;
                t = t.parent;
            }
            return t == null ? -1 : hops;
        }

        /// <summary>
        /// 휴먼본을 Unity 표준 이름으로 일괄 리네임. Undo 등록.
        /// 추가로 UpperChest 만은 표준 "UpperChest" 가 아니라 "Chest_Secondary" 로 명명하여
        /// Unity 가 UpperChest 슬롯을 자동 인식하지 않도록 우회합니다.
        ///
        /// Pre-flight: Spine→Neck, Chest→Head 사이 chain intermediates 가
        /// 표준 범위(≤ 2)를 벗어나면 중단하고 리포트만 반환합니다 (mocap 등 비정상 chain
        /// 에서 잘못 매핑되는 것을 방지).
        /// </summary>
        public static HumanoidRenameReport RenameToUnityHumanoidNames(GameObject target)
        {
            var report = new HumanoidRenameReport();
            var bones = GetHumanBones(target);
            report.BonesDetected = bones.Count > 0;
            if (!report.BonesDetected) return report;

            // ---- Pre-flight: chain 거리 ----
            if (bones.TryGetValue(HumanBodyBones.Spine, out var spineT)
                && bones.TryGetValue(HumanBodyBones.Neck, out var neckT))
            {
                report.SpineToNeckIntermediates = CountChainIntermediates(neckT, spineT);
                if (report.SpineToNeckIntermediates > MaxChainIntermediatesNormal)
                {
                    report.Aborted = true;
                    report.AbortReason = $"Spine→Neck has {report.SpineToNeckIntermediates} intermediate bone(s) " +
                                         $"(expected ≤ {MaxChainIntermediatesNormal}). " +
                                         "Reduce extra spine/neck bones manually before retrying.";
                }
            }
            if (bones.TryGetValue(HumanBodyBones.Chest, out var chestT)
                && bones.TryGetValue(HumanBodyBones.Head, out var headT))
            {
                report.ChestToHeadIntermediates = CountChainIntermediates(headT, chestT);
                if (!report.Aborted && report.ChestToHeadIntermediates > MaxChainIntermediatesNormal)
                {
                    report.Aborted = true;
                    report.AbortReason = $"Chest→Head has {report.ChestToHeadIntermediates} intermediate bone(s) " +
                                         $"(expected ≤ {MaxChainIntermediatesNormal}). " +
                                         "Reduce extra spine/neck bones manually before retrying.";
                }
            }
            if (report.Aborted) return report;

            var transforms = new List<Transform>(bones.Values);
            Undo.RecordObjects(transforms.ToArray(), "Rename Human Bones");

            foreach (var kv in bones)
            {
                string newName;
                if (kv.Key == HumanBodyBones.UpperChest)
                {
                    report.HasUpperChest = true;
                    newName = UpperChestReplacementName;
                }
                else if (!UnityHumanBoneNames.TryGetValue(kv.Key, out newName))
                {
                    continue;
                }

                if (kv.Value.name != newName)
                {
                    kv.Value.name = newName;
                    report.RenamedCount++;
                    if (kv.Key == HumanBodyBones.UpperChest)
                    {
                        report.UpperChestRenamedToSecondary = true;
                    }
                }
            }

            // 사후 카운트 체크 — 트리 전체를 한번만 순회
            foreach (var t in target.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Spine") report.SpineCount++;
                else if (t.name == "Chest") report.ChestCount++;
            }

            // 토즈 검출 여부 (자동 서치 단계에서)
            report.LeftToesDetected  = bones.ContainsKey(HumanBodyBones.LeftToes);
            report.RightToesDetected = bones.ContainsKey(HumanBodyBones.RightToes);

            if (report.RenamedCount > 0) EditorUtility.SetDirty(target);
            return report;
        }
    }
}
