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
//   1. Avatar Bake & Prefab — 세 파트 (Avatar Bake / Biped Converter / Biped Deconverter) 폴드아웃
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

        // Avatar Bake & Prefab 탭 내부의 세 파트별 폴드아웃 상태.
        [SerializeField] private bool _bakePrefabPartFoldout       = true;
        [SerializeField] private bool _bipedConverterPartFoldout   = true;
        [SerializeField] private bool _bipedDeconverterPartFoldout = false;
        private Vector2 _avatarBakeTabScroll;

        // 탭별 도구 인스턴스. 허브가 살아 있는 동안 상태를 유지.
        // BipedConverter / BipedDeconverter 는 AvatarBakePrefab 탭의 파트로 임베드.
        private AvatarBakePrefabWindow            _bakePrefabInstance;
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
        // Avatar Bake & Prefab tab — 세 파트 구성
        //   1. Avatar Bake & Prefab (원본 풀 파이프라인)
        //   2. Biped Converter (Humanoid → 3ds Max Biped 본 변환)
        //   3. Biped Deconverter (3ds Max Biped → Humanoid 역변환)
        // ============================================================
        private void DrawAvatarBakeTab()
        {
            _avatarBakeTabScroll = EditorGUILayout.BeginScrollView(_avatarBakeTabScroll);

            _bakePrefabPartFoldout = EditorGUILayout.Foldout(
                _bakePrefabPartFoldout,
                "1. Avatar Bake & Prefab",
                toggleOnLabelClick: true,
                EditorStyles.foldoutHeader);
            if (_bakePrefabPartFoldout)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (_bakePrefabInstance != null) _bakePrefabInstance.DrawGUI();
                }
            }

            EditorGUILayout.Space(8);

            _bipedConverterPartFoldout = EditorGUILayout.Foldout(
                _bipedConverterPartFoldout,
                "2. Biped Converter",
                toggleOnLabelClick: true,
                EditorStyles.foldoutHeader);
            if (_bipedConverterPartFoldout)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (_bipedConverterInstance != null) _bipedConverterInstance.DrawGUI();
                }
            }

            EditorGUILayout.Space(8);

            _bipedDeconverterPartFoldout = EditorGUILayout.Foldout(
                _bipedDeconverterPartFoldout,
                "3. Biped Deconverter",
                toggleOnLabelClick: true,
                EditorStyles.foldoutHeader);
            if (_bipedDeconverterPartFoldout)
            {
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    if (_bipedDeconverterInstance != null) _bipedDeconverterInstance.DrawGUI();
                }
            }

            EditorGUILayout.EndScrollView();
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
