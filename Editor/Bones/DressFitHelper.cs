// ----------------------------------------------------------------------------
// DressFitHelper.cs (의상 피팅 도우미)
//
// Originally by Tomoshibi/Tomoya (2020), MIT License
//   - https://tomo-shi-vi.hateblo.jp/
//   - https://opensource.org/licenses/mit-license.php
//   - 원본 도구명: きせかえ支援ツール「キセテネ」(KiseteNe), namespace Tomoya
//   - 원본 파일: KisekaeEditor.cs / KisekaeEditorLib.cs / BoneSetting.cs (partial 3종)
//
// YAMO Unity Tools 통합 변경:
//   - partial 3개 파일을 단일 파일로 통합
//   - namespace Tomoya → YAMO.UnityTools.Editor
//   - 클래스명 KisekaeEditor → DressFitHelper
//   - 메뉴 Tomoya/KiseteNe → Tools/YAMO/Bones/의상 피팅 도우미
//   - UI 텍스트 일본어 → 한국어 번역
//   - 코딩 스타일 K&R → Allman (YamoTools 컨벤션)
//   - 기능 동작은 원본과 동일
// ----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// 의상 피팅 도우미 (Kisekae Helper).
    /// 휴머노이드 아바타에 의상 메시를 자동 본 매핑·조정·부모재설정하여 입혀주는 워크플로우 도구.
    ///
    /// 사용 흐름:
    ///   1) 의상(dress) 루트 오브젝트 지정 → 자동 본 매핑
    ///   2) 슬라이더로 의상 본 회전·스케일·위치 조정 (전체/상반신/하반신, 또는 헤어 모드)
    ///   3) 신체(body) Animator 지정 후 "입히기" 버튼 → 의상 본을 신체 humanoid 본 아래로 SetParent
    /// </summary>
    public class DressFitHelper : EditorWindow
    {
        private GameObject m_dress;

        private Animator m_body;
        private GameObject m_bodyInstance;
        private Animator m_bodyAnim;

        private Transform m_armature;
        private Dictionary<HumanBodyBones, Transform> m_boneList = new Dictionary<HumanBodyBones, Transform>();

        private bool m_boneDetail = false;
        private int m_selectedTabNumber = 0;
        private Vector2 scrollPosition;
        private bool m_isHair = false;
        private bool m_dressBoneError = false;
        private bool m_dressBoneWarn = false;

        private const int RIGHT = 1;
        private const int LEFT = 2;

        // 각 부위 조정값
        private Vector3 m_armRotate = Vector3.zero;
        private Vector3 m_armScale = Vector3.one;
        private Vector3 m_hipsPos = Vector3.zero;
        private Vector3 m_hipScale = Vector3.one;
        private Vector3 m_legRotate = Vector3.zero;
        private Vector3 m_legScale = Vector3.one;
        private float m_SpineRotate = 0;

        // 초기값 보관 (RESET 시 복원용)
        private Vector3 m_defaultHipsPos;
        private Quaternion m_defaultLArmQuat;
        private Quaternion m_defaultRArmQuat;
        private Quaternion m_defaultSpineQuat;
        private Quaternion m_defaultLLegQuat;
        private Quaternion m_defaultRLegQuat;

        [MenuItem("Tools/YAMO/Bones/의상 피팅 도우미")]
        public static void ShowWindow()
        {
            GetWindow<DressFitHelper>("의상 피팅");
        }

        private void OnGUI()
        {
            GUILayout.Label("의상 피팅 도우미 (Kisekae Helper)", EditorStyles.boldLabel);
            GUILayout.Label("의상 오브젝트를 지정하세요", EditorStyles.largeLabel);

            EditorGUI.BeginChangeCheck();
            m_dress = EditorGUILayout.ObjectField("의상", m_dress, typeof(GameObject), true) as GameObject;
            if (EditorGUI.EndChangeCheck())
            {
                m_dressBoneError = false;
                m_dressBoneWarn = false;
                m_isHair = false;
                UpdateBoneList();
            }

            if (m_dress == null)
                return;

            if (PrefabUtility.IsAnyPrefabInstanceRoot(m_dress))
            {
                EditorGUILayout.HelpBox(
                    "의상 오브젝트를 우클릭하여 Unpack Prefab 한 후 지정하세요.",
                    MessageType.Error, true);
                return;
            }

            if (m_dressBoneError)
            {
                EditorGUILayout.HelpBox(
                    "의상의 본을 찾지 못했습니다.\n" +
                    "Armature 나 메시를 지정한 경우, 의상의 루트 오브젝트를 지정하세요.",
                    MessageType.Error, true);
                return;
            }

            if (m_dressBoneWarn)
            {
                EditorGUILayout.HelpBox(
                    "의상의 본을 일부만 찾았습니다.\n" +
                    "조정이 제대로 동작하지 않으면 '본 상세 설정'을 확인하세요.",
                    MessageType.Warning, true);
            }

            m_boneDetail = GUILayout.Toggle(m_boneDetail, "본 상세 설정");
            if (m_boneDetail)
            {
                CreateBoneSettingsUI();
            }

            GUILayout.Space(20);

            if (m_isHair)
            {
                GUILayout.Label("머리카락 조정", EditorStyles.miniLabel);
                CreateHeadUI();
            }
            else
            {
                m_selectedTabNumber = GUILayout.Toolbar(
                    m_selectedTabNumber,
                    new[] { "전체", "상반신", "하반신" },
                    EditorStyles.toolbarButton);

                switch (m_selectedTabNumber)
                {
                    case 0:
                        GUILayout.Label("전체 조정", EditorStyles.miniLabel);
                        CreateFullBodyUI();
                        break;
                    case 1:
                        GUILayout.Label("팔 부위 조정", EditorStyles.miniLabel);
                        CreateTopBodyUI();
                        break;
                    case 2:
                        GUILayout.Label("다리 부위 조정 (스커트에는 영향 없는 항목도 포함)", EditorStyles.miniLabel);
                        CreateBottomBodyUI();
                        break;
                }
            }

            GUILayout.Space(20);

            GUILayout.Label("입히기", EditorStyles.largeLabel);
            GUILayout.Label("신체 오브젝트를 지정하세요", EditorStyles.miniLabel);
            GUILayout.Label("Armature 나 메시가 아닌 루트 오브젝트입니다", EditorStyles.miniLabel);

            m_body = EditorGUILayout.ObjectField("신체", m_body, typeof(Animator), true) as Animator;

            if (m_body != null && !m_body.isHuman)
            {
                EditorGUILayout.HelpBox(
                    "신체가 Humanoid 가 아닙니다. FBX 의 Rig 설정을 확인하세요.",
                    MessageType.Error, true);
            }

            if (GUILayout.Button("입히기"))
            {
                if (m_body == null || !m_body.isHuman)
                    return;

                m_bodyInstance = Instantiate(m_body.gameObject);
                m_bodyInstance.transform.SetParent(m_body.transform);
                m_bodyInstance.transform.SetParent(null);
                m_bodyAnim = m_bodyInstance.GetComponent<Animator>();

                m_dress.transform.SetParent(m_bodyInstance.transform);
                SetBoneListParent();
                m_body.gameObject.SetActive(false);
            }
        }

        private void CreateFullBodyUI()
        {
            EditorGUI.BeginChangeCheck();

            GUILayout.Label("상하");
            CreateButtonUI(ref m_hipsPos.y, 0.0f);
            m_hipsPos.y = EditorGUILayout.Slider(m_hipsPos.y, -1, 1);

            GUILayout.Space(5);

            GUILayout.Label("전후");
            CreateButtonUI(ref m_hipsPos.z, 0.0f);
            m_hipsPos.z = EditorGUILayout.Slider(m_hipsPos.z, -1, 1);

            if (EditorGUI.EndChangeCheck())
            {
                var hips = GetTransform(HumanBodyBones.Hips);
                hips.position = m_defaultHipsPos + m_hipsPos;
            }

            GUILayout.Space(5);

            EditorGUI.BeginChangeCheck();

            GUILayout.Label("크기");
            CreateButtonUI(ref m_hipScale.x, 1.0f);
            m_hipScale.x = EditorGUILayout.Slider(m_hipScale.x, 0.5f, 1.5f);
            if (EditorGUI.EndChangeCheck())
            {
                m_hipScale.y = m_hipScale.z = m_hipScale.x;
                var hips = GetTransform(HumanBodyBones.Hips);
                hips.localScale = m_hipScale;
            }

            GUILayout.Space(5);

            EditorGUI.BeginChangeCheck();
            GUILayout.Label("허리 숙임");
            CreateButtonUI(ref m_SpineRotate, 0.0f, 10);
            m_SpineRotate = EditorGUILayout.Slider(m_SpineRotate, -20, 20);
            if (EditorGUI.EndChangeCheck())
            {
                var spine = GetTransform(HumanBodyBones.Spine);
                if (spine != null)
                {
                    spine.rotation = m_defaultSpineQuat;
                    spine.Rotate(spine.right, m_SpineRotate);
                }
            }
        }

        private void CreateTopBodyUI()
        {
            EditorGUI.BeginChangeCheck();

            GUILayout.Label("팔 올리기");
            CreateButtonUI(ref m_armRotate.z, 0.0f, 10);
            m_armRotate.z = EditorGUILayout.Slider(m_armRotate.z, -50, 50);

            GUILayout.Space(5);

            GUILayout.Label("팔 앞으로 내밀기");
            CreateButtonUI(ref m_armRotate.y, 0.0f, 10);
            m_armRotate.y = EditorGUILayout.Slider(m_armRotate.y, -15, 15);

            if (EditorGUI.EndChangeCheck())
            {
                var left = GetTransform(HumanBodyBones.LeftUpperArm);
                if (left != null)
                {
                    // 0 일 때 원위치로 돌아가도록 회전 전 초기값으로 리셋
                    left.rotation = m_defaultLArmQuat;
                    left.Rotate(new Vector3(0, 0, 1), m_armRotate.z * -1, Space.World);
                    left.Rotate(new Vector3(0, 1, 0), m_armRotate.y, Space.World);
                }

                var right = GetTransform(HumanBodyBones.RightUpperArm);
                if (right != null)
                {
                    right.rotation = m_defaultRArmQuat;
                    right.Rotate(new Vector3(0, 0, 1), m_armRotate.z, Space.World);
                    right.Rotate(new Vector3(0, 1, 0), m_armRotate.y * -1, Space.World);
                }
            }

            EditorGUI.BeginChangeCheck();

            GUILayout.Space(5);
            GUILayout.Label("소매 길이");
            CreateButtonUI(ref m_armScale.y, 1.0f);
            m_armScale.y = EditorGUILayout.Slider(m_armScale.y, 0.5f, 1.5f);

            GUILayout.Space(5);
            GUILayout.Label("소매 굵기");
            CreateButtonUI(ref m_armScale.x, 1.0f);
            m_armScale.x = EditorGUILayout.Slider(m_armScale.x, 0.5f, 1.5f);

            if (EditorGUI.EndChangeCheck())
            {
                var left = GetTransform(HumanBodyBones.LeftUpperArm);
                if (left != null)
                {
                    if (Mathf.Abs(left.forward.y) > Mathf.Abs(left.forward.z))
                    {
                        m_armScale.z = m_armScale.x;
                        left.localScale = m_armScale;
                    }
                    else
                    {
                        // 축이 다르므로 xy 교체
                        if (left.forward.z > 0)
                        {
                            var tmpScale = new Vector3(m_armScale.y, m_armScale.x, m_armScale.x);
                            left.localScale = tmpScale;
                        }
                        else
                        {
                            var tmpScale = new Vector3(m_armScale.x, m_armScale.y, m_armScale.x);
                            left.localScale = tmpScale;
                        }
                    }
                }

                var right = GetTransform(HumanBodyBones.RightUpperArm);
                if (right != null)
                {
                    if (Mathf.Abs(right.forward.y) > Mathf.Abs(right.forward.z))
                    {
                        m_armScale.z = m_armScale.x;
                        right.localScale = m_armScale;
                    }
                    else
                    {
                        if (right.forward.z > 0)
                        {
                            var tmpScale = new Vector3(m_armScale.y, m_armScale.x, m_armScale.x);
                            right.localScale = tmpScale;
                        }
                        else
                        {
                            var tmpScale = new Vector3(m_armScale.x, m_armScale.y, m_armScale.x);
                            right.localScale = tmpScale;
                        }
                    }
                }
            }
        }

        private void CreateBottomBodyUI()
        {
            EditorGUI.BeginChangeCheck();

            GUILayout.Label("다리 벌리기");
            CreateButtonUI(ref m_legRotate.z, 0.0f, 10);
            m_legRotate.z = EditorGUILayout.Slider(m_legRotate.z, -10, 10);

            GUILayout.Space(5);
            GUILayout.Label("다리 앞으로 내밀기");
            CreateButtonUI(ref m_legRotate.y, 0.0f, 10);
            m_legRotate.y = EditorGUILayout.Slider(m_legRotate.y, -10, 10);

            if (EditorGUI.EndChangeCheck())
            {
                var left = GetTransform(HumanBodyBones.LeftUpperLeg);
                if (left != null)
                {
                    left.rotation = m_defaultLLegQuat;
                    left.Rotate(left.forward, m_legRotate.z * -1);
                    left.Rotate(left.right, m_legRotate.y * -1);
                }

                var right = GetTransform(HumanBodyBones.RightUpperLeg);
                if (right != null)
                {
                    right.rotation = m_defaultRLegQuat;
                    right.Rotate(right.forward, m_legRotate.z);
                    right.Rotate(right.right, m_legRotate.y * -1);
                }
            }

            EditorGUI.BeginChangeCheck();

            GUILayout.Space(5);
            GUILayout.Label("치마/바지 길이");
            CreateButtonUI(ref m_legScale.y, 1.0f);
            m_legScale.y = EditorGUILayout.Slider(m_legScale.y, 0.5f, 1.5f);

            GUILayout.Space(5);
            GUILayout.Label("치마/바지 굵기");
            CreateButtonUI(ref m_legScale.x, 1.0f);
            m_legScale.x = EditorGUILayout.Slider(m_legScale.x, 0.5f, 1.5f);

            if (EditorGUI.EndChangeCheck())
            {
                m_legScale.z = m_legScale.x;
                var left = GetTransform(HumanBodyBones.LeftUpperLeg);
                var right = GetTransform(HumanBodyBones.RightUpperLeg);
                if (left != null)
                    left.localScale = m_legScale;

                if (right != null)
                    right.localScale = m_legScale;
            }
        }

        private void CreateButtonUI(ref float setParam, float paramDefault, float paramRatio = 1.0f)
        {
            GUILayout.BeginHorizontal();
            if (GUILayout.Button("RESET"))
                setParam = paramDefault;

            if (GUILayout.Button("--", EditorStyles.miniButtonLeft, GUILayout.Height(20), GUILayout.Width(50)))
                setParam -= 0.01f * paramRatio;

            if (GUILayout.Button("-", EditorStyles.miniButtonMid, GUILayout.Height(20), GUILayout.Width(50)))
                setParam -= 0.001f * paramRatio;

            if (GUILayout.Button("+", EditorStyles.miniButtonMid, GUILayout.Height(20), GUILayout.Width(50)))
                setParam += 0.001f * paramRatio;

            if (GUILayout.Button("++", EditorStyles.miniButtonRight, GUILayout.Height(20), GUILayout.Width(50)))
                setParam += 0.01f * paramRatio;

            GUILayout.EndHorizontal();
        }

        private void CreateHeadUI()
        {
            EditorGUI.BeginChangeCheck();

            GUILayout.Label("상하");
            CreateButtonUI(ref m_hipsPos.y, 0.0f);
            m_hipsPos.y = EditorGUILayout.Slider(m_hipsPos.y, -2, 2);

            GUILayout.Space(5);

            GUILayout.Label("전후");
            CreateButtonUI(ref m_hipsPos.z, 0.0f);
            m_hipsPos.z = EditorGUILayout.Slider(m_hipsPos.z, -1, 1);

            if (EditorGUI.EndChangeCheck())
            {
                m_armature.transform.position = m_hipsPos;
            }

            GUILayout.Space(5);

            EditorGUI.BeginChangeCheck();

            GUILayout.Label("크기");
            CreateButtonUI(ref m_hipScale.x, 1.0f);
            m_hipScale.x = EditorGUILayout.Slider(m_hipScale.x, 0.5f, 2.0f);
            if (EditorGUI.EndChangeCheck())
            {
                m_hipScale.y = m_hipScale.z = m_hipScale.x;
                m_armature.localScale = m_hipScale;
            }
        }

        // ──────── 본 매핑 / 부모재설정 ────────

        // 지정된 의상으로부터 본 구조 자동 매핑
        private void UpdateBoneList()
        {
            m_boneList.Clear();
            for (int i = 0; i <= 20; i++)
                m_boneList.Add((HumanBodyBones)i, null);

            if (m_dress == null)
                return;

            m_armature = FindBone(HumanBodyBones.Hips, m_dress.transform, "armature|root|skelton");
            if (m_armature == null)
            {
                m_dressBoneError = true;
                return;
            }

            // Humanoid 면 가능한 본은 모두 매핑
            var dressAnim = m_dress.GetComponent<Animator>();
            if (dressAnim != null && dressAnim.isHuman)
            {
                for (int i = (int)HumanBodyBones.Hips; i <= (int)HumanBodyBones.RightToes; i++)
                    m_boneList[(HumanBodyBones)i] = dressAnim.GetBoneTransform((HumanBodyBones)i);
            }

            if (m_boneList[HumanBodyBones.Hips] == null)
                m_boneList[HumanBodyBones.Hips] = FindBone(HumanBodyBones.Hips, m_armature, "hip");

            if (m_boneList[HumanBodyBones.Hips] == null)
            {
                // 머리 교체 또는 헤어 전용 케이스
                if (FindBone(HumanBodyBones.Neck, m_armature, "neck"))
                {
                    m_boneList[HumanBodyBones.Neck] = FindBone(HumanBodyBones.Neck, m_armature, "neck");
                    m_boneList[HumanBodyBones.Head] = FindBone(HumanBodyBones.Head, m_boneList[HumanBodyBones.Neck], "head");
                    m_isHair = true;
                }
                else if (FindBone(HumanBodyBones.Head, m_armature, "head"))
                {
                    m_boneList[HumanBodyBones.Head] = FindBone(HumanBodyBones.Head, m_armature, "head");
                    m_isHair = true;
                }
                else
                {
                    m_dressBoneError = true;
                    return;
                }
            }

            m_dressBoneError = false;

            m_boneList[HumanBodyBones.Spine] = FindBone(HumanBodyBones.Spine, m_boneList[HumanBodyBones.Hips], "spine");
            m_boneList[HumanBodyBones.Chest] = FindBone(HumanBodyBones.Chest, m_boneList[HumanBodyBones.Spine], "chest");

            // UpperChest 가 있으면 Head/Shoulder 는 그쪽에서 검색
            var upperChest = FindBone(HumanBodyBones.UpperChest, m_boneList[HumanBodyBones.Chest], "upper");
            m_boneList[HumanBodyBones.Neck] = FindBone(HumanBodyBones.Neck,
                upperChest ? upperChest : m_boneList[HumanBodyBones.Chest], "neck");
            m_boneList[HumanBodyBones.Head] = FindBone(HumanBodyBones.Head, m_boneList[HumanBodyBones.Neck], "head");

            // 왼팔
            m_boneList[HumanBodyBones.LeftShoulder] = FindBone(HumanBodyBones.LeftShoulder,
                upperChest ? upperChest : m_boneList[HumanBodyBones.Chest], "shoulder", LEFT);
            m_boneList[HumanBodyBones.LeftUpperArm] = FindBone(HumanBodyBones.LeftUpperArm, m_boneList[HumanBodyBones.LeftShoulder], "upper|arm");
            m_boneList[HumanBodyBones.LeftLowerArm] = FindBone(HumanBodyBones.LeftLowerArm, m_boneList[HumanBodyBones.LeftUpperArm], "lower|elbow");
            m_boneList[HumanBodyBones.LeftHand] = FindBone(HumanBodyBones.LeftHand, m_boneList[HumanBodyBones.LeftLowerArm], "hand|wrist");

            // 오른팔
            m_boneList[HumanBodyBones.RightShoulder] = FindBone(HumanBodyBones.RightShoulder,
                upperChest ? upperChest : m_boneList[HumanBodyBones.Chest], "shoulder", RIGHT);
            m_boneList[HumanBodyBones.RightUpperArm] = FindBone(HumanBodyBones.RightUpperArm, m_boneList[HumanBodyBones.RightShoulder], "upper|arm");
            m_boneList[HumanBodyBones.RightLowerArm] = FindBone(HumanBodyBones.RightLowerArm, m_boneList[HumanBodyBones.RightUpperArm], "lower|elbow");
            m_boneList[HumanBodyBones.RightHand] = FindBone(HumanBodyBones.RightHand, m_boneList[HumanBodyBones.RightLowerArm], "hand|wrist");

            // 왼다리
            m_boneList[HumanBodyBones.LeftUpperLeg] = FindBone(HumanBodyBones.LeftUpperLeg, m_boneList[HumanBodyBones.Hips], "upper|leg", LEFT);
            m_boneList[HumanBodyBones.LeftLowerLeg] = FindBone(HumanBodyBones.LeftLowerLeg, m_boneList[HumanBodyBones.LeftUpperLeg], "lower|knee");
            m_boneList[HumanBodyBones.LeftFoot] = FindBone(HumanBodyBones.LeftFoot, m_boneList[HumanBodyBones.LeftLowerLeg], "foot|ankle");
            m_boneList[HumanBodyBones.LeftToes] = FindBone(HumanBodyBones.LeftToes, m_boneList[HumanBodyBones.LeftFoot], "toe");

            // 오른다리
            m_boneList[HumanBodyBones.RightUpperLeg] = FindBone(HumanBodyBones.RightUpperLeg, m_boneList[HumanBodyBones.Hips], "upper|leg", RIGHT);
            m_boneList[HumanBodyBones.RightLowerLeg] = FindBone(HumanBodyBones.RightLowerLeg, m_boneList[HumanBodyBones.RightUpperLeg], "lower|knee");
            m_boneList[HumanBodyBones.RightFoot] = FindBone(HumanBodyBones.RightFoot, m_boneList[HumanBodyBones.RightLowerLeg], "foot|ankle");
            m_boneList[HumanBodyBones.RightToes] = FindBone(HumanBodyBones.RightToes, m_boneList[HumanBodyBones.RightFoot], "toe");

            if (m_boneList[HumanBodyBones.Spine] == null ||
                m_boneList[HumanBodyBones.Chest] == null ||
                m_boneList[HumanBodyBones.LeftShoulder] == null ||
                m_boneList[HumanBodyBones.RightShoulder] == null ||
                m_boneList[HumanBodyBones.LeftUpperLeg] == null ||
                m_boneList[HumanBodyBones.RightUpperLeg] == null)
            {
                m_dressBoneWarn = true && !m_isHair;
            }

            SetDefaultQuaternion();
        }

        private void SetDefaultQuaternion()
        {
            m_armRotate = Vector3.zero;
            m_hipsPos = Vector3.zero;
            m_legRotate = Vector3.zero;
            m_armScale = Vector3.one;
            m_hipScale = Vector3.one;
            m_legScale = Vector3.one;
            m_SpineRotate = 0;

            if (GetTransform(HumanBodyBones.LeftUpperArm) != null)
                m_defaultLArmQuat = GetTransform(HumanBodyBones.LeftUpperArm).rotation;
            if (GetTransform(HumanBodyBones.RightUpperArm) != null)
                m_defaultRArmQuat = GetTransform(HumanBodyBones.RightUpperArm).rotation;

            if (GetTransform(HumanBodyBones.Hips) != null)
                m_defaultHipsPos = GetTransform(HumanBodyBones.Hips).position;
            if (GetTransform(HumanBodyBones.Spine) != null)
                m_defaultSpineQuat = GetTransform(HumanBodyBones.Spine).rotation;

            if (GetTransform(HumanBodyBones.LeftUpperLeg) != null)
                m_defaultLLegQuat = GetTransform(HumanBodyBones.LeftUpperLeg).rotation;
            if (GetTransform(HumanBodyBones.RightUpperLeg) != null)
                m_defaultRLegQuat = GetTransform(HumanBodyBones.RightUpperLeg).rotation;
        }

        // 의상 본을 신체 humanoid 본 아래로 부모재설정 (입히기 시점)
        private void SetBoneListParent()
        {
            for (int i = (int)HumanBodyBones.Hips; i <= (int)HumanBodyBones.RightToes; i++)
            {
                var bone = (HumanBodyBones)i;
                var baseBone = GetTransform(bone);
                if (baseBone == null)
                    continue;

                baseBone.SetParent(m_bodyAnim.GetBoneTransform(bone));
            }
        }

        private Transform GetTransform(HumanBodyBones bone)
        {
            return m_boneList[bone];
        }

        private Transform FindBone(HumanBodyBones bone, Transform parent, string matchPattern)
        {
            if (m_boneList.ContainsKey(bone) && m_boneList[bone] != null)
                return m_boneList[bone];

            if (parent == null)
                return null;

            foreach (Transform child in parent)
            {
                if (Regex.IsMatch(child.name, matchPattern, RegexOptions.IgnoreCase))
                    return child;
            }
            return null;
        }

        private Transform FindBone(HumanBodyBones bone, Transform parent, string matchPattern, int side)
        {
            if (m_boneList[bone] != null)
                return m_boneList[bone];

            if (parent == null)
                return null;

            Transform hit1 = null;
            Transform hit2 = null;

            foreach (Transform child in parent)
            {
                if (Regex.IsMatch(child.name, matchPattern, RegexOptions.IgnoreCase))
                {
                    if (hit1 == null)
                        hit1 = child;
                    else
                        hit2 = child;
                }
            }

            if (hit1 == null || hit2 == null)
                return null;

            if (side == RIGHT)
            {
                if (hit1.position.x > hit2.position.x) return hit1;
                else return hit2;
            }
            else if (side == LEFT)
            {
                if (hit1.position.x < hit2.position.x) return hit1;
                else return hit2;
            }

            return null;
        }

        // 의상 본을 수동 매핑하는 UI
        // (Humanoid 가 아니거나 본 이름이 비표준인 의상용)
        private void CreateBoneSettingsUI()
        {
            EditorGUILayout.HelpBox(
                "이 설정은 자동 매핑이 제대로 동작하지 않을 때 사용하세요.\n" +
                "모든 항목을 채울 필요는 없으며, 해당 본이 없으면 None 으로 두세요.",
                MessageType.Warning, true);

            scrollPosition = GUILayout.BeginScrollView(scrollPosition, GUILayout.Height(150));

            m_boneList[HumanBodyBones.Hips]      = EditorGUILayout.ObjectField("Hips",  m_boneList[HumanBodyBones.Hips],  typeof(Transform), true) as Transform;
            m_boneList[HumanBodyBones.Spine]     = EditorGUILayout.ObjectField("Spine", m_boneList[HumanBodyBones.Spine], typeof(Transform), true) as Transform;
            m_boneList[HumanBodyBones.Chest]     = EditorGUILayout.ObjectField("Chest", m_boneList[HumanBodyBones.Chest], typeof(Transform), true) as Transform;
            m_boneList[HumanBodyBones.Neck]      = EditorGUILayout.ObjectField("Neck",  m_boneList[HumanBodyBones.Neck],  typeof(Transform), true) as Transform;
            m_boneList[HumanBodyBones.Head]      = EditorGUILayout.ObjectField("Head",  m_boneList[HumanBodyBones.Head],  typeof(Transform), true) as Transform;

            GUILayout.Label("왼팔", EditorStyles.boldLabel);
            m_boneList[HumanBodyBones.LeftShoulder] = EditorGUILayout.ObjectField("LeftShoulder", m_boneList[HumanBodyBones.LeftShoulder], typeof(Transform), true) as Transform;
            m_boneList[HumanBodyBones.LeftUpperArm] = EditorGUILayout.ObjectField("LeftUpperArm", m_boneList[HumanBodyBones.LeftUpperArm], typeof(Transform), true) as Transform;
            m_boneList[HumanBodyBones.LeftLowerArm] = EditorGUILayout.ObjectField("LeftLowerArm", m_boneList[HumanBodyBones.LeftLowerArm], typeof(Transform), true) as Transform;
            m_boneList[HumanBodyBones.LeftHand]     = EditorGUILayout.ObjectField("LeftHand",     m_boneList[HumanBodyBones.LeftHand],     typeof(Transform), true) as Transform;

            GUILayout.Label("오른팔", EditorStyles.boldLabel);
            m_boneList[HumanBodyBones.RightShoulder] = EditorGUILayout.ObjectField("RightShoulder", m_boneList[HumanBodyBones.RightShoulder], typeof(Transform), true) as Transform;
            m_boneList[HumanBodyBones.RightUpperArm] = EditorGUILayout.ObjectField("RightUpperArm", m_boneList[HumanBodyBones.RightUpperArm], typeof(Transform), true) as Transform;
            m_boneList[HumanBodyBones.RightLowerArm] = EditorGUILayout.ObjectField("RightLowerArm", m_boneList[HumanBodyBones.RightLowerArm], typeof(Transform), true) as Transform;
            m_boneList[HumanBodyBones.RightHand]     = EditorGUILayout.ObjectField("RightHand",     m_boneList[HumanBodyBones.RightHand],     typeof(Transform), true) as Transform;

            GUILayout.Label("왼다리", EditorStyles.boldLabel);
            m_boneList[HumanBodyBones.LeftUpperLeg] = EditorGUILayout.ObjectField("LeftUpperLeg", m_boneList[HumanBodyBones.LeftUpperLeg], typeof(Transform), true) as Transform;
            m_boneList[HumanBodyBones.LeftLowerLeg] = EditorGUILayout.ObjectField("LeftLowerLeg", m_boneList[HumanBodyBones.LeftLowerLeg], typeof(Transform), true) as Transform;
            m_boneList[HumanBodyBones.LeftFoot]     = EditorGUILayout.ObjectField("LeftFoot",     m_boneList[HumanBodyBones.LeftFoot],     typeof(Transform), true) as Transform;
            m_boneList[HumanBodyBones.LeftToes]     = EditorGUILayout.ObjectField("LeftToes",     m_boneList[HumanBodyBones.LeftToes],     typeof(Transform), true) as Transform;

            GUILayout.Label("오른다리", EditorStyles.boldLabel);
            m_boneList[HumanBodyBones.RightUpperLeg] = EditorGUILayout.ObjectField("RightUpperLeg", m_boneList[HumanBodyBones.RightUpperLeg], typeof(Transform), true) as Transform;
            m_boneList[HumanBodyBones.RightLowerLeg] = EditorGUILayout.ObjectField("RightLowerLeg", m_boneList[HumanBodyBones.RightLowerLeg], typeof(Transform), true) as Transform;
            m_boneList[HumanBodyBones.RightFoot]     = EditorGUILayout.ObjectField("RightFoot",     m_boneList[HumanBodyBones.RightFoot],     typeof(Transform), true) as Transform;
            m_boneList[HumanBodyBones.RightToes]     = EditorGUILayout.ObjectField("RightToes",     m_boneList[HumanBodyBones.RightToes],     typeof(Transform), true) as Transform;

            GUILayout.EndScrollView();
        }
    }
}
