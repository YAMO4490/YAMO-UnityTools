// Camera Composition Overlay — 게임 뷰에 구도 가이드 라인을 띄우는 에디터 도구.
//
// 클린 재구현. 동일 컨셉의 유료 에셋 (Jordan Cassady 의 Camera Composition) 과
// 코드/자산 공유 없음.
//
// 아키텍처:
//   - Canvas/Image 를 쓰지 않고 Camera 렌더 콜백에서 GL 로 직접 그린다.
//     - Built-in pipeline: Camera.onPostRender
//     - SRP (URP/HDRP):    RenderPipelineManager.endCameraRendering
//   - 콜백은 카메라 단위로 호출되므로, Scene View 카메라(cameraType == SceneView)
//     를 필터링해서 거르면 Scene View 에는 절대 그려지지 않는다.
//   - GL.LoadPixelMatrix 로 픽셀 좌표계로 그리므로 thickness/aspect 가 정확.
//   - 구도 (Rule of Thirds / Cross / Safe Area)
//     를 각각 독립 토글 → 동시에 여러 개 활성 가능.

using UnityEditor;
using UnityEngine;
using UnityEngine.Rendering;

namespace YAMO.UnityTools.Editor
{
    public enum CompositionType
    {
        RuleOfThirds = 0,
        Cross        = 1,
        SafeArea     = 2,
    }

    public class CameraCompositionWindow : EditorWindow
    {
        // ----------------------------------------------------------------
        // 상태
        // ----------------------------------------------------------------
        [SerializeField] bool[]  _visible  = { true, false, false };
        [SerializeField] Color[] _perColor = { Color.white, Color.white, Color.white };
        [SerializeField] Color   _lineColor       = Color.white;   // 전역 곱하기 색
        [SerializeField] float   _opacity         = 0.5f;
        [SerializeField] int     _thickness       = 4;             // pixels
        [SerializeField] Camera  _targetCamera;                    // null 이면 Camera.main 폴백

        Vector3    _revertCameraPosition;
        Quaternion _revertCameraRotation;
        bool       _hasRevertCached;

        Material _lineMaterial;

        // ----------------------------------------------------------------
        // 메뉴
        // ----------------------------------------------------------------
        [MenuItem("Tools/YAMO/Camera/Composition Overlay")]
        public static void Open()
        {
            if (HasOpenInstances<CameraCompositionWindow>())
                GetWindow<CameraCompositionWindow>().Close();
            else
            {
                var w = GetWindow<CameraCompositionWindow>("Composition");
                w.minSize = new Vector2(360, 420);
            }
        }

        // ----------------------------------------------------------------
        // 라이프사이클
        // ----------------------------------------------------------------
        void OnEnable()
        {
            Camera.onPostRender                       += OnPostRender;
            RenderPipelineManager.endCameraRendering  += OnEndCameraRendering;
        }

        void OnDisable()
        {
            Camera.onPostRender                       -= OnPostRender;
            RenderPipelineManager.endCameraRendering  -= OnEndCameraRendering;
        }

        void OnDestroy()
        {
            if (_lineMaterial != null)
            {
                DestroyImmediate(_lineMaterial);
                _lineMaterial = null;
            }
        }

        // ----------------------------------------------------------------
        // GUI
        // ----------------------------------------------------------------
        void OnGUI()
        {
            DrawCompositionsSection();
            DrawAppearanceSection();
            DrawOverridesSection();
            DrawCameraSection();

            // 옵션이 변경되면 게임 뷰가 다시 그려지도록 강제
            // (Camera.onPostRender / endCameraRendering 가 그릴 때 최신 상태 반영)
            SceneView.RepaintAll();
            UnityEditorInternal.InternalEditorUtility.RepaintAllViews();
        }

        void DrawCompositionsSection()
        {
            EditorGUILayout.LabelField("Compositions (multi-select)", EditorStyles.boldLabel);

            // _perColor 길이가 enum 변경으로 어긋나면 보정 (알파 포함, 기본값 alpha=1)
            if (_perColor == null || _perColor.Length != CompositionTypeNames.Length)
            {
                var newArr = new Color[CompositionTypeNames.Length];
                for (int j = 0; j < newArr.Length; j++)
                    newArr[j] = (_perColor != null && j < _perColor.Length) ? _perColor[j] : Color.white;
                _perColor = newArr;
            }

            for (int i = 0; i < CompositionTypeNames.Length; i++)
            {
                EditorGUILayout.BeginHorizontal();
                _visible[i]  = EditorGUILayout.ToggleLeft(CompositionTypeNames[i], _visible[i], GUILayout.ExpandWidth(true));
                // showAlpha: true → 스와치에 체크무늬로 투명도 표시, 클릭 시 RGBA 피커 오픈
                _perColor[i] = EditorGUILayout.ColorField(GUIContent.none, _perColor[i], false, true, false, GUILayout.Width(60));
                EditorGUILayout.EndHorizontal();
            }
            EditorGUILayout.Space(4);
        }

        void DrawAppearanceSection()
        {
            EditorGUILayout.LabelField("Appearance (global)", EditorStyles.boldLabel);
            _opacity = EditorGUILayout.Slider("Opacity", _opacity, 0f, 1f);

            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField(new GUIContent("Tint",
                "전역 색상 — 위에서 지정한 항목별 색상에 곱하기로 적용됩니다.\n" +
                "(개별 색상 그대로 쓰려면 Tint 를 White 로 두세요.)"), GUILayout.Width(40));
            if (GUILayout.Button("White", GUILayout.Width(60))) _lineColor = Color.white;
            if (GUILayout.Button("Black", GUILayout.Width(60))) _lineColor = Color.black;
            _lineColor = EditorGUILayout.ColorField(GUIContent.none, _lineColor, false, false, false);
            EditorGUILayout.EndHorizontal();

            _thickness = EditorGUILayout.IntSlider("Thickness (px)", _thickness, 1, 16);
            EditorGUILayout.Space(4);
        }

        void DrawOverridesSection()
        {
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("All On",  GUILayout.Height(20)))
                for (int i = 0; i < _visible.Length; i++) _visible[i] = true;
            if (GUILayout.Button("All Off", GUILayout.Height(20)))
                for (int i = 0; i < _visible.Length; i++) _visible[i] = false;
            EditorGUILayout.EndHorizontal();
            EditorGUILayout.Space(4);
        }

        void DrawCameraSection()
        {
            EditorGUILayout.LabelField("Target Camera", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "오버레이가 표시될 게임 카메라. 미지정 시 Camera.main 자동 사용.\n" +
                "Scene View 의 에디터 카메라에는 절대 그려지지 않습니다.",
                MessageType.None);
            _targetCamera = (Camera)EditorGUILayout.ObjectField(_targetCamera, typeof(Camera), true);

            if (_targetCamera == null)
            {
                EditorGUILayout.LabelField("(현재 fallback: " + (Camera.main != null ? Camera.main.name : "없음") + ")", EditorStyles.miniLabel);
                return;
            }

            EditorGUILayout.Space(2);
            EditorGUILayout.LabelField("Position", EditorStyles.miniBoldLabel);
            var newPos = EditorGUILayout.Vector3Field(GUIContent.none, _targetCamera.transform.position);
            if (newPos != _targetCamera.transform.position)
            {
                Undo.RecordObject(_targetCamera.transform, "Camera Position Change");
                _targetCamera.transform.position = newPos;
            }

            EditorGUILayout.LabelField("Rotation (Euler)", EditorStyles.miniBoldLabel);
            var newEul = EditorGUILayout.Vector3Field(GUIContent.none, _targetCamera.transform.rotation.eulerAngles);
            if (newEul != _targetCamera.transform.rotation.eulerAngles)
            {
                Undo.RecordObject(_targetCamera.transform, "Camera Rotation Change");
                _targetCamera.transform.rotation = Quaternion.Euler(newEul);
            }

            EditorGUILayout.Space(4);
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("Align with Scene View", GUILayout.Height(24)))
                AlignWithSceneView();
            using (new EditorGUI.DisabledScope(!_hasRevertCached))
            {
                if (GUILayout.Button("Revert", GUILayout.Width(80), GUILayout.Height(24)))
                {
                    Undo.RecordObject(_targetCamera.transform, "Revert Camera");
                    _targetCamera.transform.position = _revertCameraPosition;
                    _targetCamera.transform.rotation = _revertCameraRotation;
                }
            }
            EditorGUILayout.EndHorizontal();
        }

        void AlignWithSceneView()
        {
            var sv = SceneView.lastActiveSceneView;
            if (sv == null || sv.camera == null)
            {
                EditorUtility.DisplayDialog("Composition Overlay", "활성 Scene View 가 없습니다.", "확인");
                return;
            }
            _revertCameraPosition = _targetCamera.transform.position;
            _revertCameraRotation = _targetCamera.transform.rotation;
            _hasRevertCached = true;

            Undo.RecordObject(_targetCamera.transform, "Align Camera with Scene View");
            _targetCamera.transform.position = sv.camera.transform.position;
            _targetCamera.transform.rotation = sv.camera.transform.rotation;
        }

        // ----------------------------------------------------------------
        // 카메라 렌더 콜백
        // ----------------------------------------------------------------
        void OnPostRender(Camera cam)
        {
            // Built-in render pipeline 경로
            if (!ShouldDrawOnCamera(cam)) return;
            DrawOverlay(cam);
        }

        void OnEndCameraRendering(ScriptableRenderContext ctx, Camera cam)
        {
            // SRP (URP / HDRP) 경로
            if (!ShouldDrawOnCamera(cam)) return;
            DrawOverlay(cam);
        }

        bool ShouldDrawOnCamera(Camera cam)
        {
            if (cam == null) return false;
            if (cam.cameraType != CameraType.Game) return false;  // SceneView, Preview, Reflection 모두 제외

            Camera target = ResolveCamera();
            if (target != null) return cam == target;
            // target 미지정 + Camera.main 도 없으면 — 그릴 카메라가 없음
            return false;
        }

        Camera ResolveCamera()
        {
            if (_targetCamera != null) return _targetCamera;
            return Camera.main;
        }

        // ----------------------------------------------------------------
        // GL 그리기
        // ----------------------------------------------------------------
        void DrawOverlay(Camera cam)
        {
            EnsureLineMaterial();
            if (_lineMaterial == null) return;

            float w = cam.pixelWidth;
            float h = cam.pixelHeight;
            if (w <= 0 || h <= 0) return;
            if (_opacity <= 0f) return;

            GL.PushMatrix();
            GL.LoadPixelMatrix(0, w, 0, h);
            _lineMaterial.SetPass(0);

            GL.Begin(GL.QUADS);

            float t = _thickness;

            if (_visible[(int)CompositionType.RuleOfThirds])
            {
                GL.Color(ResolveColor((int)CompositionType.RuleOfThirds));
                DrawRuleOfThirds(w, h, t);
            }
            if (_visible[(int)CompositionType.Cross])
            {
                GL.Color(ResolveColor((int)CompositionType.Cross));
                DrawCross(w, h, t);
            }
            if (_visible[(int)CompositionType.SafeArea])
            {
                GL.Color(ResolveColor((int)CompositionType.SafeArea));
                DrawSafeArea(w, h, t);
            }

            GL.End();
            GL.PopMatrix();
        }

        /// <summary>항목 색상 × 전역 Tint, 알파는 항목 색상 알파 × 전역 Opacity.</summary>
        Color ResolveColor(int idx)
        {
            Color per = (_perColor != null && idx < _perColor.Length) ? _perColor[idx] : Color.white;
            Color c = per * _lineColor;      // RGB 곱하기
            c.a = per.a * _opacity;          // 항목 알파 × 전역 Opacity
            return c;
        }

        void EnsureLineMaterial()
        {
            if (_lineMaterial != null) return;
            var shader = Shader.Find("Hidden/Internal-Colored");
            if (shader == null) return;
            _lineMaterial = new Material(shader)
            {
                hideFlags = HideFlags.HideAndDontSave,
            };
            _lineMaterial.SetInt("_SrcBlend", (int)BlendMode.SrcAlpha);
            _lineMaterial.SetInt("_DstBlend", (int)BlendMode.OneMinusSrcAlpha);
            _lineMaterial.SetInt("_Cull",     (int)CullMode.Off);
            _lineMaterial.SetInt("_ZWrite",   0);
            _lineMaterial.SetInt("_ZTest",    (int)CompareFunction.Always);
        }

        // ----------------------------------------------------------------
        // 구도별 라인 그리기 (픽셀 좌표계)
        // ----------------------------------------------------------------
        static void DrawRuleOfThirds(float w, float h, float t)
        {
            DrawHLine(0, h * (1f / 3f), w, t);
            DrawHLine(0, h * (2f / 3f), w, t);
            DrawVLine(w * (1f / 3f), 0, h, t);
            DrawVLine(w * (2f / 3f), 0, h, t);
        }

        static void DrawCross(float w, float h, float t)
        {
            DrawHLine(0, h * 0.5f, w, t);
            DrawVLine(w * 0.5f, 0, h, t);
        }

        /// <summary>
        /// Safe Area: 화면 가장자리 10% 안쪽으로 직사각형 외곽선 (4 변).
        /// 텍스트 / 인터페이스 가독성 가이드 또는 영상 안전 영역으로 활용.
        /// </summary>
        static void DrawSafeArea(float w, float h, float t)
        {
            const float inset = 0.1f;
            float x0 = w * inset;
            float x1 = w * (1f - inset);
            float y0 = h * inset;
            float y1 = h * (1f - inset);

            // 모서리에서 두께 절반만큼 안쪽으로 보정해 두께가 화면 밖으로 삐져나가지 않도록.
            float ht = t * 0.5f;
            DrawHLine(x0 + ht, y0, (x1 - x0) - t, t); // bottom
            DrawHLine(x0 + ht, y1, (x1 - x0) - t, t); // top
            DrawVLine(x0, y0 + ht, (y1 - y0) - t, t); // left
            DrawVLine(x1, y0 + ht, (y1 - y0) - t, t); // right
        }

        // ----------------------------------------------------------------
        // 라인 quad primitive
        // ----------------------------------------------------------------
        static void DrawHLine(float x, float y, float w, float thickness)
        {
            float ht = thickness * 0.5f;
            GL.Vertex3(x,     y - ht, 0);
            GL.Vertex3(x + w, y - ht, 0);
            GL.Vertex3(x + w, y + ht, 0);
            GL.Vertex3(x,     y + ht, 0);
        }

        static void DrawVLine(float x, float y, float h, float thickness)
        {
            float ht = thickness * 0.5f;
            GL.Vertex3(x - ht, y,     0);
            GL.Vertex3(x + ht, y,     0);
            GL.Vertex3(x + ht, y + h, 0);
            GL.Vertex3(x - ht, y + h, 0);
        }

        static void DrawLine(float x0, float y0, float x1, float y1, float thickness)
        {
            float dx = x1 - x0;
            float dy = y1 - y0;
            float len = Mathf.Sqrt(dx * dx + dy * dy);
            if (len < 1e-6f) return;
            float nx = -dy / len * thickness * 0.5f;
            float ny =  dx / len * thickness * 0.5f;
            GL.Vertex3(x0 - nx, y0 - ny, 0);
            GL.Vertex3(x0 + nx, y0 + ny, 0);
            GL.Vertex3(x1 + nx, y1 + ny, 0);
            GL.Vertex3(x1 - nx, y1 - ny, 0);
        }

        // ----------------------------------------------------------------
        // 라벨
        // ----------------------------------------------------------------
        static readonly string[] CompositionTypeNames =
        {
            "Rule of Thirds",
            "Cross",
            "Safe Area (10%)",
        };
    }
}
