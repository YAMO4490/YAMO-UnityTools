// 여러 YAMO 도구를 한 창에서 탭 형태로 사용할 수 있는 마스터 윈도우.
//
// 임베드 방식:
//   각 도구의 EditorWindow 인스턴스를 ScriptableObject.CreateInstance 로 만들어
//   허브 안에 보관하고, OnGUI 에서 해당 인스턴스의 public DrawGUI() 를 호출.
//   인스턴스는 표시되지 않지만 상태를 보유하므로 탭 전환 시 작업 컨텍스트가 유지됨.
//
//   Animation 탭만 예외: AnimClipReducerWindow 가 UI Toolkit (CreateGUI) 기반이라
//   IMGUI 임베드가 어려워, 3개 도구를 launcher 형태(별도 창 열기 버튼)로 노출.
//
// 의존:
//   - AvatarBakePrefabWindow (같은 폴더)
//   - BipedConverterWindow (Editor/BipedConverter/) — Avatar Bake 탭의 두번째 파트로 임베드
//   - BipedDeconverterWindow (Editor/BipedConverter/) — Avatar Bake 탭의 세번째 파트로 임베드
//   - MaterialAndTextureCollectorWindow (Editor/Assets/)
//   - YamoAssetChecker (Editor/Bones/)
//   - FacialAnimationBaker, ForearmHingeBaker, AnimClipReducerWindow (Editor/Animation/)
//
// 탭 구성 (총 4개):
//   1. Avatar Bake & Prefab — 네 하위 탭 (Avatar Bake / Biped Converter / Biped Deconverter / Scale Bake)
//   2. Material & Texture
//   3. Asset Checker
//   4. Animation

using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    public class YamoToolHub : EditorWindow
    {
        private enum Tab
        {
            AvatarBakePrefab,
            MaterialAndTexture,
            YamoAssetChecker,
            Animation,
        }

        private static readonly string[] TabLabels =
        {
            "Avatar Bake & Prefab",
            "Material & Texture",
            "Asset Checker",
            "Animation",
        };

        [SerializeField] private Tab _activeTab = Tab.AvatarBakePrefab;

        private enum AvatarBakeTab
        {
            AvatarBakePrefab,
            BipedConverter,
            BipedDeconverter,
            ScaleBakePrefab,
        }

        private static readonly string[] AvatarBakeTabLabels =
        {
            "1. Avatar Bake",
            "2. Biped Converter",
            "3. Biped Deconverter",
            "4. Scale Bake",
        };

        [SerializeField] private AvatarBakeTab _activeAvatarBakeTab = AvatarBakeTab.AvatarBakePrefab;

        // 탭별 도구 인스턴스. 허브가 살아 있는 동안 상태를 유지.
        // BipedConverter / BipedDeconverter 는 AvatarBakePrefab 탭의 파트로 임베드.
        private AvatarBakePrefabWindow            _bakePrefabInstance;
        private AvatarBakePrefabWindow            _scaleBakeInstance;
        private BipedConverterWindow              _bipedConverterInstance;
        private BipedDeconverterWindow            _bipedDeconverterInstance;
        private MaterialAndTextureCollectorWindow _materialTextureInstance;
        private YamoAssetChecker                  _assetCheckerInstance;

        [MenuItem("Tools/YAMO/⚡ Tool Hub")]
        public static void Open()
        {
            if (HasOpenInstances<YamoToolHub>())
            {
                GetWindow<YamoToolHub>().Close();
            }
            else
            {
                var w = GetWindow<YamoToolHub>("YAMO Hub");
                w.minSize = new Vector2(640, 480);
            }
        }

        // ────────────────────────────────────────────────────────────
        // 단축키
        // ────────────────────────────────────────────────────────────
        // Unity ShortcutManager 로 등록. 기본값은 그냥 6 (modifier 없음).
        // 메인 키보드의 숫자 6 (KeyCode.Alpha6). Numpad 6 은 KeyCode.Keypad6 로 별개.
        // 사용자는 Edit ▸ Shortcuts 창의 "YAMO/Open Tool Hub" 항목에서 자유롭게 재할당 가능.
        [Shortcut("YAMO/Open Tool Hub",
                  KeyCode.Alpha6,
                  ShortcutModifiers.None)]
        private static void OpenViaShortcut()
        {
            Open();
        }

        private void OnEnable()
        {
            if (_bakePrefabInstance == null)
                _bakePrefabInstance = ScriptableObject.CreateInstance<AvatarBakePrefabWindow>();
            if (_scaleBakeInstance == null)
                _scaleBakeInstance = ScriptableObject.CreateInstance<AvatarBakePrefabWindow>();
            if (_bipedConverterInstance == null)
                _bipedConverterInstance = ScriptableObject.CreateInstance<BipedConverterWindow>();
            if (_bipedDeconverterInstance == null)
                _bipedDeconverterInstance = ScriptableObject.CreateInstance<BipedDeconverterWindow>();
            if (_materialTextureInstance == null)
                _materialTextureInstance = ScriptableObject.CreateInstance<MaterialAndTextureCollectorWindow>();
            if (_assetCheckerInstance == null)
                _assetCheckerInstance = ScriptableObject.CreateInstance<YamoAssetChecker>();
        }

        private void OnDisable()
        {
            if (_bakePrefabInstance != null)       DestroyImmediate(_bakePrefabInstance);
            if (_scaleBakeInstance != null)        DestroyImmediate(_scaleBakeInstance);
            if (_bipedConverterInstance != null)   DestroyImmediate(_bipedConverterInstance);
            if (_bipedDeconverterInstance != null) DestroyImmediate(_bipedDeconverterInstance);
            if (_materialTextureInstance != null)  DestroyImmediate(_materialTextureInstance);
            if (_assetCheckerInstance != null)     DestroyImmediate(_assetCheckerInstance);
        }

        private void OnGUI()
        {
            // 상단 탭 바
            int newIndex = GUILayout.Toolbar((int)_activeTab, TabLabels, GUILayout.Height(28));
            if (newIndex != (int)_activeTab)
            {
                _activeTab = (Tab)newIndex;
                GUI.FocusControl(null);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.LabelField(GUIContent.none, GUI.skin.horizontalSlider);

            switch (_activeTab)
            {
                case Tab.AvatarBakePrefab:
                    DrawAvatarBakeTab();
                    break;
                case Tab.MaterialAndTexture:
                    if (_materialTextureInstance != null) _materialTextureInstance.DrawGUI();
                    break;
                case Tab.YamoAssetChecker:
                    if (_assetCheckerInstance != null) _assetCheckerInstance.DrawGUI();
                    break;
                case Tab.Animation:
                    DrawAnimationTab();
                    break;
            }
        }

        // ============================================================
        // Avatar Bake & Prefab tab — 네 하위 탭 구성
        //   1. Avatar Bake & Prefab (원본 풀 파이프라인)
        //   2. Biped Converter (Humanoid → 3ds Max Biped 본 변환)
        //   3. Biped Deconverter (3ds Max Biped → Humanoid 역변환)
        //   4. Scale Bake & Prefab (루트 배율 베이크 → 원본 프리팹 옆에 저장)
        // ============================================================
        private void DrawAvatarBakeTab()
        {
            // 도구의 스크롤 바깥에 고정하여 긴 내용에서도 1~4번에 바로 접근한다.
            int newIndex = GUILayout.Toolbar(
                (int)_activeAvatarBakeTab, AvatarBakeTabLabels, GUILayout.Height(26));
            if (newIndex != (int)_activeAvatarBakeTab)
            {
                _activeAvatarBakeTab = (AvatarBakeTab)newIndex;
                GUI.FocusControl(null);
                GUIUtility.ExitGUI();
            }

            EditorGUILayout.Space(4);
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox, GUILayout.ExpandHeight(true)))
            {
                // 각 도구가 전체 내용의 스크롤과 입력 상태를 별도로 유지한다.
                switch (_activeAvatarBakeTab)
                {
                    case AvatarBakeTab.AvatarBakePrefab:
                        if (_bakePrefabInstance != null) _bakePrefabInstance.DrawGUI();
                        break;
                    case AvatarBakeTab.BipedConverter:
                        if (_bipedConverterInstance != null) _bipedConverterInstance.DrawGUI();
                        break;
                    case AvatarBakeTab.BipedDeconverter:
                        if (_bipedDeconverterInstance != null) _bipedDeconverterInstance.DrawGUI();
                        break;
                    case AvatarBakeTab.ScaleBakePrefab:
                        if (_scaleBakeInstance != null) _scaleBakeInstance.DrawGUI(scaleBake: true);
                        break;
                }
            }
        }

        // ============================================================
        // Animation tab — launcher 형태
        // ============================================================
        // AnimClipReducerWindow 가 UI Toolkit 기반이라 IMGUI 임베드가 곤란해
        // 3 도구를 일관된 방식으로 별도 창에서 열도록 함.
        // FacialAnimationBaker / ForearmHingeBaker 도 같은 패턴으로 통일.
        private void DrawAnimationTab()
        {
            EditorGUILayout.LabelField("Animation Tools", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "각 도구는 별도 창으로 열립니다. 도구별 상태/로그가 자체 창에 보존됩니다.",
                MessageType.Info);

            EditorGUILayout.Space(8);

            // 도구별 ShowWindow/Open 메서드 접근성이 제각각이라 GetWindow 로 직접 연다.
            // EditorWindow 인스턴스가 없으면 Unity 가 새로 만들고 OnEnable / CreateGUI 가 호출됨.
            DrawLauncherButton(
                title: "1. Facial Animation Baker",
                desc:  ".anim 페이셜(블렌드셰이프) 클립 커브 최적화로 용량 축소.\n" +
                       "RDP 키 감소, 상수/제로 커브 제거, 정밀도 축소.",
                onClick: () => GetWindow<FacialAnimationBaker>("Facial Anim Baker"));

            DrawLauncherButton(
                title: "2. Forearm Hinge Baker",
                desc:  "Humanoid 클립의 Forearm 비-힌지 회전을 제거하고\n" +
                       "UpperArm 보정으로 Hand 방향 유지. Biped 단축 호환 Generic 클립 생성.",
                onClick: () => GetWindow<ForearmHingeBaker>("Forearm Hinge Baker"));

            DrawLauncherButton(
                title: "3. Anim Clip Reducer",
                desc:  "휴머노이드 머슬 클립 압축. RDP/Cubic Hermite 키 감소,\n" +
                       "채널별 tolerance, 미사용 채널 드롭, YAML 후처리.",
                onClick: () => GetWindow<AnimClipReducerWindow>("Anim Clip Reducer"));
        }

        private static void DrawLauncherButton(string title, string desc, System.Action onClick)
        {
            using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
            {
                EditorGUILayout.LabelField(title, EditorStyles.boldLabel);
                EditorGUILayout.LabelField(desc, EditorStyles.wordWrappedMiniLabel);
                EditorGUILayout.Space(2);
                if (GUILayout.Button("열기", GUILayout.Height(24)))
                {
                    onClick?.Invoke();
                }
            }
            EditorGUILayout.Space(4);
        }
    }
}
