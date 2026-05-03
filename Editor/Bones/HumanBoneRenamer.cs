using UnityEngine;
using UnityEditor;
using System.Collections.Generic;

namespace YAMO.UnityTools.Editor
{
/// <summary>
/// 아바타의 휴먼본 이름을 Unity Human Bone 기준으로 변환하는 에디터 툴
/// 3ds Max Biped이나 다른 본 구조를 Unity 표준 이름으로 변환
/// </summary>
public class HumanBoneRenamer : EditorWindow
{
    private Avatar targetAvatar;
    private GameObject targetGameObject;
    private bool showMapping = false;
    private Vector2 scrollPosition;

    // UpperChest 만은 Unity 표준 "UpperChest" 가 아닌 "Chest_Secondary" 로 변경하여
    // Unity 의 자동 매핑이 UpperChest 슬롯을 인식하지 않게 우회.
    private const string UpperChestReplacementName = "Chest_Secondary";

    // 표준 Humanoid chain (Spine → Chest → UpperChest → Neck → Head) 기준
    // 정상 intermediates 개수 최댓값. 그보다 크면 mocap 등 비정상 chain.
    private const int MaxChainIntermediatesNormal = 2;
    
    // Unity Human Bone 매핑 테이블
    private static readonly Dictionary<HumanBodyBones, string> humanBoneNames = new Dictionary<HumanBodyBones, string>()
    {
        // Body
        { HumanBodyBones.Hips, "Hips" },
        { HumanBodyBones.Spine, "Spine" },
        { HumanBodyBones.Chest, "Chest" },
        { HumanBodyBones.UpperChest, "UpperChest" },
        { HumanBodyBones.Neck, "Neck" },
        { HumanBodyBones.Head, "Head" },
        
        // Left Arm
        { HumanBodyBones.LeftShoulder, "LeftShoulder" },
        { HumanBodyBones.LeftUpperArm, "LeftUpperArm" },
        { HumanBodyBones.LeftLowerArm, "LeftLowerArm" },
        { HumanBodyBones.LeftHand, "LeftHand" },
        
        // Right Arm  
        { HumanBodyBones.RightShoulder, "RightShoulder" },
        { HumanBodyBones.RightUpperArm, "RightUpperArm" },
        { HumanBodyBones.RightLowerArm, "RightLowerArm" },
        { HumanBodyBones.RightHand, "RightHand" },
        
        // Left Leg
        { HumanBodyBones.LeftUpperLeg, "LeftUpperLeg" },
        { HumanBodyBones.LeftLowerLeg, "LeftLowerLeg" },
        { HumanBodyBones.LeftFoot, "LeftFoot" },
        { HumanBodyBones.LeftToes, "LeftToes" },
        
        // Right Leg
        { HumanBodyBones.RightUpperLeg, "RightUpperLeg" },
        { HumanBodyBones.RightLowerLeg, "RightLowerLeg" },
        { HumanBodyBones.RightFoot, "RightFoot" },
        { HumanBodyBones.RightToes, "RightToes" },
        
        // Left Hand Fingers
        { HumanBodyBones.LeftThumbProximal, "LeftThumbProximal" },
        { HumanBodyBones.LeftThumbIntermediate, "LeftThumbIntermediate" },
        { HumanBodyBones.LeftThumbDistal, "LeftThumbDistal" },
        { HumanBodyBones.LeftIndexProximal, "LeftIndexProximal" },
        { HumanBodyBones.LeftIndexIntermediate, "LeftIndexIntermediate" },
        { HumanBodyBones.LeftIndexDistal, "LeftIndexDistal" },
        { HumanBodyBones.LeftMiddleProximal, "LeftMiddleProximal" },
        { HumanBodyBones.LeftMiddleIntermediate, "LeftMiddleIntermediate" },
        { HumanBodyBones.LeftMiddleDistal, "LeftMiddleDistal" },
        { HumanBodyBones.LeftRingProximal, "LeftRingProximal" },
        { HumanBodyBones.LeftRingIntermediate, "LeftRingIntermediate" },
        { HumanBodyBones.LeftRingDistal, "LeftRingDistal" },
        { HumanBodyBones.LeftLittleProximal, "LeftLittleProximal" },
        { HumanBodyBones.LeftLittleIntermediate, "LeftLittleIntermediate" },
        { HumanBodyBones.LeftLittleDistal, "LeftLittleDistal" },
        
        // Right Hand Fingers
        { HumanBodyBones.RightThumbProximal, "RightThumbProximal" },
        { HumanBodyBones.RightThumbIntermediate, "RightThumbIntermediate" },
        { HumanBodyBones.RightThumbDistal, "RightThumbDistal" },
        { HumanBodyBones.RightIndexProximal, "RightIndexProximal" },
        { HumanBodyBones.RightIndexIntermediate, "RightIndexIntermediate" },
        { HumanBodyBones.RightIndexDistal, "RightIndexDistal" },
        { HumanBodyBones.RightMiddleProximal, "RightMiddleProximal" },
        { HumanBodyBones.RightMiddleIntermediate, "RightMiddleIntermediate" },
        { HumanBodyBones.RightMiddleDistal, "RightMiddleDistal" },
        { HumanBodyBones.RightRingProximal, "RightRingProximal" },
        { HumanBodyBones.RightRingIntermediate, "RightRingIntermediate" },
        { HumanBodyBones.RightRingDistal, "RightRingDistal" },
        { HumanBodyBones.RightLittleProximal, "RightLittleProximal" },
        { HumanBodyBones.RightLittleIntermediate, "RightLittleIntermediate" },
        { HumanBodyBones.RightLittleDistal, "RightLittleDistal" }
    };
    
    [MenuItem("Tools/YAMO/Bones/Human Bone Renamer")]
    public static void ShowWindow()
    {
        // 변경된 부분: 이미 열려있는지 확인하여 토글(Toggle) 기능 구현
        if (HasOpenInstances<HumanBoneRenamer>())
        {
            // 창이 열려있다면 인스턴스를 가져와서 닫습니다.
            GetWindow<HumanBoneRenamer>().Close();
        }
        else
        {
            // 창이 닫혀있다면 새로 엽니다.
            GetWindow<HumanBoneRenamer>("Human Bone Renamer");
        }
    }
    
    private void OnGUI()
    {
        GUILayout.Label("Human Bone Renamer", EditorStyles.boldLabel);
        GUILayout.Label("아바타의 휴먼본 이름을 Unity 표준으로 변환", EditorStyles.helpBox);
        
        EditorGUILayout.Space();
        
        // 아바타 또는 GameObject 참조
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("Target:", GUILayout.Width(60));
        targetAvatar = EditorGUILayout.ObjectField(targetAvatar, typeof(Avatar), false) as Avatar;
        EditorGUILayout.EndHorizontal();
        
        EditorGUILayout.BeginHorizontal();
        EditorGUILayout.LabelField("또는:", GUILayout.Width(60));
        targetGameObject = EditorGUILayout.ObjectField(targetGameObject, typeof(GameObject), true) as GameObject;
        EditorGUILayout.EndHorizontal();
        
        EditorGUILayout.Space();
        
        // 매핑 정보 표시 토글
        showMapping = EditorGUILayout.Foldout(showMapping, "매핑 정보 보기");
        if (showMapping)
        {
            ShowMappingInfo();
        }
        
        EditorGUILayout.Space();
        
        // 변환 버튼들
        EditorGUILayout.BeginHorizontal();
        
        GUI.enabled = (targetAvatar != null || targetGameObject != null);
        
        if (GUILayout.Button("휴먼본 이름 변환", GUILayout.Height(30)))
        {
            RenameHumanBones();
        }
        
        if (GUILayout.Button("미리보기", GUILayout.Height(30)))
        {
            PreviewRenaming();
        }
        
        GUI.enabled = true;
        EditorGUILayout.EndHorizontal();
        
        EditorGUILayout.Space();
        
        // 도움말
        EditorGUILayout.HelpBox(
            "사용법:\n" +
            "1. Avatar 또는 GameObject를 참조해주세요\n" +
            "2. '미리보기'로 변경될 이름을 확인하세요\n" +
            "3. '휴먼본 이름 변환'을 클릭하여 실행하세요\n\n" +
            "지원하는 본 구조:\n" +
            "• 3ds Max Biped (Bip001 Pelvis, etc.)\n" +
            "• Mixamo 본 구조\n" +
            "• 기타 휴머노이드 본 구조", 
            MessageType.Info);
    }
    
    private void ShowMappingInfo()
    {
        scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(200));
        
        EditorGUILayout.LabelField("Unity Human Bone 목록:", EditorStyles.boldLabel);
        
        foreach (var bone in humanBoneNames)
        {
            if (bone.Key != HumanBodyBones.LastBone)
            {
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField(bone.Key.ToString(), GUILayout.Width(200));
                EditorGUILayout.LabelField("→", GUILayout.Width(20));
                EditorGUILayout.LabelField(bone.Value);
                EditorGUILayout.EndHorizontal();
            }
        }
        
        EditorGUILayout.EndScrollView();
    }
    
    /// <summary>
    /// chain 거리 계산. descendant 의 부모 체인을 거슬러 ancestor 를 만나기 전까지의
    /// transform 수를 반환. 동일 chain 이 아니면 -1.
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
    /// Pre-flight: Spine→Neck, Chest→Head 사이 chain intermediates 가 표준 범위(≤ 2)를
    /// 벗어나는지 검사. 벗어나면 reason 에 사유를 채우고 true 반환(중단 권고).
    /// </summary>
    private bool ShouldAbortDueToChainDistance(
        Dictionary<HumanBodyBones, Transform> bones,
        out int spineToNeck, out int chestToHead, out string reason)
    {
        spineToNeck = -1;
        chestToHead = -1;
        reason = null;

        if (bones.TryGetValue(HumanBodyBones.Spine, out var spineT)
            && bones.TryGetValue(HumanBodyBones.Neck, out var neckT))
        {
            spineToNeck = CountChainIntermediates(neckT, spineT);
            if (spineToNeck > MaxChainIntermediatesNormal)
            {
                reason = $"Spine→Neck 사이 본이 {spineToNeck}개입니다 (정상 ≤ {MaxChainIntermediatesNormal}). " +
                         "이상 본을 수동으로 줄인 뒤 다시 시도하세요.";
            }
        }
        if (bones.TryGetValue(HumanBodyBones.Chest, out var chestT)
            && bones.TryGetValue(HumanBodyBones.Head, out var headT))
        {
            chestToHead = CountChainIntermediates(headT, chestT);
            if (reason == null && chestToHead > MaxChainIntermediatesNormal)
            {
                reason = $"Chest→Head 사이 본이 {chestToHead}개입니다 (정상 ≤ {MaxChainIntermediatesNormal}). " +
                         "이상 본을 수동으로 줄인 뒤 다시 시도하세요.";
            }
        }
        return reason != null;
    }

    private void PreviewRenaming()
    {
        var bones = GetHumanBones();
        if (bones.Count == 0)
        {
            EditorUtility.DisplayDialog("오류", "휴먼본을 찾을 수 없습니다.", "확인");
            return;
        }

        // Pre-flight: 비정상 chain 이면 미리보기 단계에서도 중단 사유 알림
        if (ShouldAbortDueToChainDistance(bones, out var spn, out var cth, out var reason))
        {
            string aborted = $"중단됨: {reason}\n\n" +
                             $"• Spine → Neck 사이: {spn}\n" +
                             $"• Chest → Head 사이: {cth}\n\n" +
                             "본 계층을 먼저 정리한 뒤 다시 시도하세요.";
            EditorUtility.DisplayDialog("미리보기 - 중단", aborted, "확인");
            return;
        }

        string preview = "변경될 본 이름:\n\n";
        int changeCount = 0;
        bool hasUpperChest = false;

        foreach (var bone in bones)
        {
            string newName;
            if (bone.Key == HumanBodyBones.UpperChest)
            {
                hasUpperChest = true;
                newName = UpperChestReplacementName;
            }
            else if (!humanBoneNames.TryGetValue(bone.Key, out newName))
            {
                continue;
            }

            if (bone.Value.name != newName)
            {
                preview += $"{bone.Value.name} → {newName}\n";
                changeCount++;
            }
        }

        if (changeCount == 0)
        {
            preview += "변경할 본이 없습니다. (이미 표준 이름)";
        }
        else
        {
            preview = $"총 {changeCount}개 본 이름이 변경됩니다:\n\n" + preview;
        }
        if (hasUpperChest)
        {
            preview += $"\n참고: UpperChest → '{UpperChestReplacementName}' (Unity 가 UpperChest 슬롯을 자동 인식하지 않도록 우회)";
        }

        EditorUtility.DisplayDialog("미리보기", preview, "확인");
    }

    private void RenameHumanBones()
    {
        var bones = GetHumanBones();
        if (bones.Count == 0)
        {
            EditorUtility.DisplayDialog("오류", "휴먼본을 찾을 수 없습니다.", "확인");
            return;
        }

        // ---- Pre-flight: chain 거리 ----
        if (ShouldAbortDueToChainDistance(bones, out var spn, out var cth, out var reason))
        {
            string aborted = $"중단됨: {reason}\n\n" +
                             $"• Spine → Neck 사이: {spn}\n" +
                             $"• Chest → Head 사이: {cth}\n\n" +
                             "본을 변경하지 않았습니다. 본 계층을 먼저 정리한 뒤 다시 시도하세요.";
            EditorUtility.DisplayDialog("휴먼본 이름 변환 - 중단", aborted, "확인");
            return;
        }

        // Undo 등록
        var transforms = new List<Transform>();
        foreach (var bone in bones) transforms.Add(bone.Value);
        Undo.RecordObjects(transforms.ToArray(), "Rename Human Bones");

        int changeCount = 0;
        bool hasUpperChest = false;
        bool upperChestRenamed = false;

        foreach (var bone in bones)
        {
            string newName;
            if (bone.Key == HumanBodyBones.UpperChest)
            {
                hasUpperChest = true;
                newName = UpperChestReplacementName;
            }
            else if (!humanBoneNames.TryGetValue(bone.Key, out newName))
            {
                continue;
            }

            if (bone.Value.name != newName)
            {
                Debug.Log($"[Human Bone Renamer] {bone.Value.name} → {newName}");
                bone.Value.name = newName;
                changeCount++;
                if (bone.Key == HumanBodyBones.UpperChest) upperChestRenamed = true;
            }
        }

        // 사후 카운트 체크
        int spineCount = 0, chestCount = 0;
        if (targetGameObject != null)
        {
            foreach (var t in targetGameObject.GetComponentsInChildren<Transform>(true))
            {
                if (t.name == "Spine") spineCount++;
                else if (t.name == "Chest") chestCount++;
            }
        }
        bool leftToesDetected  = bones.ContainsKey(HumanBodyBones.LeftToes);
        bool rightToesDetected = bones.ContainsKey(HumanBodyBones.RightToes);

        // 결과 팝업 구성
        string result;
        if (changeCount > 0)
        {
            result = $"{changeCount}개 휴먼본의 이름이 Unity 표준으로 변경되었습니다.";
        }
        else
        {
            result = "변경할 본이 없습니다. 이미 Unity 표준 이름을 사용하고 있습니다.";
        }

        if (hasUpperChest)
        {
            result += upperChestRenamed
                ? $"\n• UpperChest → '{UpperChestReplacementName}' 으로 변경됨 (Unity 가 UpperChest 슬롯을 자동 매핑하지 않음)."
                : $"\n• UpperChest 검출됨; 이미 '{UpperChestReplacementName}' 이름.";
        }

        result += $"\n\nSpine 개수: {spineCount} (정상 = 1)";
        result += $"\nChest 개수: {chestCount} (정상 = 1)";

        bool warnSpine = spineCount != 1;
        bool warnChest = chestCount != 1;
        bool warnLeftToes = !leftToesDetected;
        bool warnRightToes = !rightToesDetected;

        if (warnSpine || warnChest || warnLeftToes || warnRightToes)
        {
            result += "\n\n⚠ 확인 필요:";
            if (warnSpine) result += "\n - Spine 개수가 1이 아님. 계층 점검 필요.";
            if (warnChest) result += "\n - Chest 개수가 1이 아님. 계층 점검 필요.";
            if (warnLeftToes)  result += "\n - 좌측 Toes 가 자동 검출 안 됨. 수동 변환 필요할 수 있음.";
            if (warnRightToes) result += "\n - 우측 Toes 가 자동 검출 안 됨. 수동 변환 필요할 수 있음.";
        }

        EditorUtility.DisplayDialog(changeCount > 0 ? "완료" : "정보", result, "확인");

        if (changeCount > 0 && targetGameObject != null)
        {
            EditorUtility.SetDirty(targetGameObject);
        }
    }
    
    private Dictionary<HumanBodyBones, Transform> GetHumanBones()
    {
        Dictionary<HumanBodyBones, Transform> bones = new Dictionary<HumanBodyBones, Transform>();
        
        if (targetAvatar != null)
        {
            // Avatar에서 휴먼본 가져오기
            if (targetAvatar.isHuman)
            {
                var humanDescription = targetAvatar.humanDescription;
                foreach (var humanBone in humanDescription.human)
                {
                    if (System.Enum.TryParse<HumanBodyBones>(humanBone.humanName, out HumanBodyBones boneType))
                    {
                        // Avatar의 경우 직접 Transform을 가져올 수 없으므로 GameObject를 통해 찾아야 함
                        // 이 경우 GameObject도 함께 제공해야 함
                    }
                }
            }
        }
        
        if (targetGameObject != null)
        {
            // GameObject에서 Animator를 찾아서 휴먼본 가져오기
            Animator animator = targetGameObject.GetComponent<Animator>();
            if (animator != null && animator.avatar != null && animator.avatar.isHuman)
            {
                foreach (HumanBodyBones boneType in System.Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (boneType != HumanBodyBones.LastBone)
                    {
                        Transform boneTransform = animator.GetBoneTransform(boneType);
                        if (boneTransform != null)
                        {
                            bones[boneType] = boneTransform;
                        }
                    }
                }
            }
            else
            {
                // Animator가 없거나 휴먼 아바타가 아닌 경우, 이름으로 추측해서 찾기
                bones = FindBonesbyName(targetGameObject.transform);
            }
        }
        
        return bones;
    }
    
    private Dictionary<HumanBodyBones, Transform> FindBonesbyName(Transform root)
    {
        Dictionary<HumanBodyBones, Transform> bones = new Dictionary<HumanBodyBones, Transform>();
        Transform[] allTransforms = root.GetComponentsInChildren<Transform>();
        
        // 3ds Max Biped 매핑
        Dictionary<string, HumanBodyBones> bipedMapping = new Dictionary<string, HumanBodyBones>()
        {
            // Body
            { "Bip001 Pelvis", HumanBodyBones.Hips },
            { "Bip001 Spine", HumanBodyBones.Spine },
            { "Bip001 Spine1", HumanBodyBones.Chest },
            { "Bip001 Neck", HumanBodyBones.Neck },
            { "Bip001 Head", HumanBodyBones.Head },
            
            // Left Arm
            { "Bip001 L Clavicle", HumanBodyBones.LeftShoulder },
            { "Bip001 L UpperArm", HumanBodyBones.LeftUpperArm },
            { "Bip001 L Forearm", HumanBodyBones.LeftLowerArm },
            { "Bip001 L Hand", HumanBodyBones.LeftHand },
            
            // Right Arm
            { "Bip001 R Clavicle", HumanBodyBones.RightShoulder },
            { "Bip001 R UpperArm", HumanBodyBones.RightUpperArm },
            { "Bip001 R Forearm", HumanBodyBones.RightLowerArm },
            { "Bip001 R Hand", HumanBodyBones.RightHand },
            
            // Left Leg
            { "Bip001 L Thigh", HumanBodyBones.LeftUpperLeg },
            { "Bip001 L Calf", HumanBodyBones.LeftLowerLeg },
            { "Bip001 L Foot", HumanBodyBones.LeftFoot },
            { "Bip001 L Toe0", HumanBodyBones.LeftToes },
            
            // Right Leg
            { "Bip001 R Thigh", HumanBodyBones.RightUpperLeg },
            { "Bip001 R Calf", HumanBodyBones.RightLowerLeg },
            { "Bip001 R Foot", HumanBodyBones.RightFoot },
            { "Bip001 R Toe0", HumanBodyBones.RightToes },
            
            // Left Hand Fingers
            { "Bip001 L Finger0", HumanBodyBones.LeftThumbProximal },
            { "Bip001 L Finger01", HumanBodyBones.LeftThumbIntermediate },
            { "Bip001 L Finger02", HumanBodyBones.LeftThumbDistal },
            { "Bip001 L Finger1", HumanBodyBones.LeftIndexProximal },
            { "Bip001 L Finger11", HumanBodyBones.LeftIndexIntermediate },
            { "Bip001 L Finger12", HumanBodyBones.LeftIndexDistal },
            { "Bip001 L Finger2", HumanBodyBones.LeftMiddleProximal },
            { "Bip001 L Finger21", HumanBodyBones.LeftMiddleIntermediate },
            { "Bip001 L Finger22", HumanBodyBones.LeftMiddleDistal },
            { "Bip001 L Finger3", HumanBodyBones.LeftRingProximal },
            { "Bip001 L Finger31", HumanBodyBones.LeftRingIntermediate },
            { "Bip001 L Finger32", HumanBodyBones.LeftRingDistal },
            { "Bip001 L Finger4", HumanBodyBones.LeftLittleProximal },
            { "Bip001 L Finger41", HumanBodyBones.LeftLittleIntermediate },
            { "Bip001 L Finger42", HumanBodyBones.LeftLittleDistal },
            
            // Right Hand Fingers
            { "Bip001 R Finger0", HumanBodyBones.RightThumbProximal },
            { "Bip001 R Finger01", HumanBodyBones.RightThumbIntermediate },
            { "Bip001 R Finger02", HumanBodyBones.RightThumbDistal },
            { "Bip001 R Finger1", HumanBodyBones.RightIndexProximal },
            { "Bip001 R Finger11", HumanBodyBones.RightIndexIntermediate },
            { "Bip001 R Finger12", HumanBodyBones.RightIndexDistal },
            { "Bip001 R Finger2", HumanBodyBones.RightMiddleProximal },
            { "Bip001 R Finger21", HumanBodyBones.RightMiddleIntermediate },
            { "Bip001 R Finger22", HumanBodyBones.RightMiddleDistal },
            { "Bip001 R Finger3", HumanBodyBones.RightRingProximal },
            { "Bip001 R Finger31", HumanBodyBones.RightRingIntermediate },
            { "Bip001 R Finger32", HumanBodyBones.RightRingDistal },
            { "Bip001 R Finger4", HumanBodyBones.RightLittleProximal },
            { "Bip001 R Finger41", HumanBodyBones.RightLittleIntermediate },
            { "Bip001 R Finger42", HumanBodyBones.RightLittleDistal }
        };
        
        // 이름으로 본 찾기
        foreach (Transform t in allTransforms)
        {
            if (bipedMapping.TryGetValue(t.name, out HumanBodyBones boneType))
            {
                bones[boneType] = t;
            }
        }
        
        return bones;
    }
}
}