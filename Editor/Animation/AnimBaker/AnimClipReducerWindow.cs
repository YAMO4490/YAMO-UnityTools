using System.Collections.Generic;
using System.IO;
using UnityEditor;
using UnityEditor.UIElements;
using UnityEngine;
using UnityEngine.UIElements;

namespace YAMO.UnityTools.Editor
{
    public class AnimClipReducerWindow : EditorWindow
    {
        private const string CommonUssPath = "Assets/Scripts/Streamingle/StreamingleControl/Editor/UXML/StreamingleCommon.uss";

        private enum QualityPreset
        {
            Lossless,
            Standard,
            Aggressive,
            High,
            Extreme,
            Custom,
        }

        // Input
        private ObjectField _clipField;
        private TextField _outputPathField;
        private Toggle _overwriteToggle;

        // Quality
        private DropdownField _qualityPresetField;
        private Label _presetDescLabel;

        private static readonly List<string> QualityPresetLabels = new List<string>
        {
            "무손실 (시각 손실 0)",
            "표준 (권장)",
            "공격적",
            "고압축 (30fps 리샘플)",
            "극한 (실험적 포함)",
            "사용자 정의",
        };

        // Precision (advanced)
        private EnumField _fitModeField;
        private FloatField _muscleTolField;
        private FloatField _spineTolField;
        private FloatField _rootPosTolField;
        private FloatField _rootRotTolField;

        // Extra optimization (advanced)
        private Toggle _dropUnusedToggle;
        private FloatField _unusedThresholdField;
        private FloatField _resampleRateField;
        private IntegerField _smoothingWindowField;
        private Toggle _yamlOptimizeToggle;
        private IntegerField _valueDigitsField;
        private IntegerField _slopeDigitsField;
        private Toggle _setCompressedToggle;
        private Toggle _removeEditorCurvesToggle;

        // Experimental (advanced)
        private Toggle _stripDefaultFieldsToggle;

        // Output
        private Label _statsLabel;
        private VisualElement _statsContainer;

        private bool _suppressPresetUpdate;

        [MenuItem("Tools/YAMO/Animation/Anim Clip Reducer")]
        public static void ShowWindow()
        {
            var w = GetWindow<AnimClipReducerWindow>("Anim Clip Reducer");
            w.minSize = new Vector2(460, 600);
        }

        public void CreateGUI()
        {
            var root = rootVisualElement;
            root.AddToClassList("tool-root");
            root.style.paddingTop = 0;
            root.style.paddingBottom = 0;
            root.style.paddingLeft = 0;
            root.style.paddingRight = 0;

            var commonUss = AssetDatabase.LoadAssetAtPath<StyleSheet>(CommonUssPath);
            if (commonUss != null) root.styleSheets.Add(commonUss);

            // Header
            var header = new VisualElement();
            header.style.paddingTop = 8;
            header.style.paddingLeft = 10;
            header.style.paddingRight = 10;
            header.style.borderBottomWidth = 1;
            header.style.borderBottomColor = new StyleColor(new Color(0, 0, 0, 0.2f));
            header.style.marginBottom = 4;
            var title = new Label("머슬 클립 압축기");
            title.AddToClassList("tool-title");
            title.style.marginBottom = 2;
            header.Add(title);
            var sub = new Label("키프레임 리덕션 + YAML 최적화로 .anim 크기 축소");
            sub.style.fontSize = 11;
            sub.style.color = new StyleColor(new Color(0.65f, 0.65f, 0.65f));
            sub.style.marginBottom = 6;
            header.Add(sub);
            root.Add(header);

            // Scrollable body
            var scroll = new ScrollView { mode = ScrollViewMode.Vertical };
            scroll.style.flexGrow = 1;
            scroll.style.paddingLeft = 10;
            scroll.style.paddingRight = 10;
            root.Add(scroll);

            BuildInputSection(scroll);
            BuildQualitySection(scroll);
            BuildPrecisionFoldout(scroll);
            BuildExtraOptFoldout(scroll);
            BuildExperimentalFoldout(scroll);
            BuildRunButton(scroll);
            BuildStatsSection(scroll);

            // Apply default preset on first open.
            ApplyQualityPreset(QualityPreset.Standard);
            UpdatePresetDescription();
        }

        // ────────────────────────────── Sections

        private void BuildInputSection(VisualElement parent)
        {
            var box = MakeSection("입력");

            _clipField = new ObjectField("소스 클립") { objectType = typeof(AnimationClip), allowSceneObjects = false };
            _clipField.RegisterValueChangedCallback(_ => SuggestOutputPath());
            box.Add(_clipField);

            _outputPathField = new TextField("저장 경로");
            _outputPathField.style.marginTop = 2;
            box.Add(_outputPathField);

            var pathRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 2 } };
            var browse = new Button(BrowseOutput) { text = "경로 선택" };
            browse.style.flexGrow = 1;
            pathRow.Add(browse);
            _overwriteToggle = new Toggle("덮어쓰기") { value = true };
            _overwriteToggle.style.marginLeft = 8;
            pathRow.Add(_overwriteToggle);
            box.Add(pathRow);

            parent.Add(box);
        }

        private void BuildQualitySection(VisualElement parent)
        {
            var box = MakeSection("압축 강도");

            _qualityPresetField = new DropdownField("프리셋", QualityPresetLabels, (int)QualityPreset.Standard);
            _qualityPresetField.RegisterValueChangedCallback(evt =>
            {
                int idx = QualityPresetLabels.IndexOf(evt.newValue);
                if (idx < 0) return;
                var preset = (QualityPreset)idx;
                if (preset != QualityPreset.Custom) ApplyQualityPreset(preset);
                UpdatePresetDescription();
            });
            box.Add(_qualityPresetField);

            _presetDescLabel = new Label();
            _presetDescLabel.style.fontSize = 11;
            _presetDescLabel.style.color = new StyleColor(new Color(0.7f, 0.7f, 0.7f));
            _presetDescLabel.style.marginTop = 4;
            _presetDescLabel.style.marginLeft = 2;
            _presetDescLabel.style.whiteSpace = WhiteSpace.Normal;
            box.Add(_presetDescLabel);

            parent.Add(box);
        }

        private QualityPreset GetCurrentPreset()
        {
            int idx = _qualityPresetField != null ? _qualityPresetField.index : -1;
            if (idx < 0 || idx >= QualityPresetLabels.Count) return QualityPreset.Custom;
            return (QualityPreset)idx;
        }

        private void BuildPrecisionFoldout(VisualElement parent)
        {
            var foldout = new Foldout { text = "채널별 정밀도", value = false };
            foldout.AddToClassList("section-foldout");
            foldout.style.marginTop = 6;

            _fitModeField = new EnumField("피팅 방식", AnimClipKeyReducer.FitMode.Auto);
            _fitModeField.tooltip = "Auto: 채널마다 Linear/Cubic 둘 다 계산해서 더 작은 쪽 자동 선택 (권장).\nCubic: 항상 큐빅.\nLinear: 항상 선형.\n※ Root(RootT/RootQ)는 항상 Linear로 강제.";
            _fitModeField.RegisterValueChangedCallback(_ => SetCustomPreset());
            foldout.Add(_fitModeField);

            _muscleTolField = MakeTrackedFloat("머슬 일반", 0.001f, "일반 머슬 채널의 허용 오차. 클수록 압축률 ↑.");
            _spineTolField = MakeTrackedFloat("척추/가슴", 0.0003f, "Spine/Chest/UpperChest. 몸통 자세 영향이 커서 빡빡하게 유지 권장.");
            _rootPosTolField = MakeTrackedFloat("Root 위치 (m)", 0.0002f, "RootT.x/y/z. 미터 단위 월드 좌표.");
            _rootRotTolField = MakeTrackedFloat("Root 회전", 0.0003f, "RootQ.x/y/z/w. 흔들리면 아바타 통째로 기울어지므로 매우 빡빡하게.");

            foldout.Add(_muscleTolField);
            foldout.Add(_spineTolField);
            foldout.Add(_rootPosTolField);
            foldout.Add(_rootRotTolField);

            var note = MakeHintLabel("Root는 항상 Linear (월드 좌표·쿼터니언 안전성). Cubic은 머슬에만.");
            foldout.Add(note);

            parent.Add(foldout);
        }

        private void BuildExtraOptFoldout(VisualElement parent)
        {
            var foldout = new Foldout { text = "추가 최적화", value = false };
            foldout.AddToClassList("section-foldout");
            foldout.style.marginTop = 4;

            _dropUnusedToggle = new Toggle("미사용 머슬 채널 드롭") { value = true };
            _dropUnusedToggle.tooltip = "값이 0 근처인 채널은 .anim에서 통째로 빼도 시각적으로 동일 (Unity가 누락 채널을 0으로 처리).";
            _dropUnusedToggle.RegisterValueChangedCallback(_ => SetCustomPreset());
            foldout.Add(_dropUnusedToggle);

            _unusedThresholdField = MakeTrackedFloat("드롭 임계값 (|value| 최대)", 0.005f, "0.005면 ±0.5% 이내 채널 드롭. 크게 하면 미세 표정 사라질 수 있음.");
            foldout.Add(_unusedThresholdField);

            // Resample row with quick buttons
            var resampleLabel = new Label("리샘플 frame rate (0 = 원본 유지)");
            resampleLabel.style.marginTop = 4;
            resampleLabel.style.fontSize = 11;
            foldout.Add(resampleLabel);
            var resampleRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 2 } };
            _resampleRateField = new FloatField { value = 0f };
            _resampleRateField.style.flexGrow = 1;
            _resampleRateField.tooltip = "60fps 모캡을 30fps로 리샘플링하면 입력 키 수가 절반.";
            _resampleRateField.RegisterValueChangedCallback(_ => SetCustomPreset());
            resampleRow.Add(_resampleRateField);
            resampleRow.Add(MakeQuickButton("원본", () => _resampleRateField.value = 0f));
            resampleRow.Add(MakeQuickButton("30", () => _resampleRateField.value = 30f));
            resampleRow.Add(MakeQuickButton("24", () => _resampleRateField.value = 24f));
            foldout.Add(resampleRow);

            // Pre-smoothing
            var smoothLabel = new Label("Pre-smoothing 윈도우 (0=끔, 3~5 권장)");
            smoothLabel.style.marginTop = 6;
            smoothLabel.style.fontSize = 11;
            foldout.Add(smoothLabel);
            var smoothRow = new VisualElement { style = { flexDirection = FlexDirection.Row, marginTop = 2 } };
            _smoothingWindowField = new IntegerField { value = 0 };
            _smoothingWindowField.style.flexGrow = 1;
            _smoothingWindowField.tooltip = "이동평균으로 모캡 미세 떨림 제거. 0/1=끔. 3=가벼운 평활화 (안전), 5=강한 평활화. RDP 효율 ↑.\n※ Spine/Root는 평활화 안 함 (자세 정확도 보존).";
            _smoothingWindowField.RegisterValueChangedCallback(_ => SetCustomPreset());
            smoothRow.Add(_smoothingWindowField);
            smoothRow.Add(MakeQuickButton("끔", () => _smoothingWindowField.value = 0));
            smoothRow.Add(MakeQuickButton("3", () => _smoothingWindowField.value = 3));
            smoothRow.Add(MakeQuickButton("5", () => _smoothingWindowField.value = 5));
            foldout.Add(smoothRow);

            // YAML post-process
            _yamlOptimizeToggle = new Toggle("YAML 후처리 (float 정밀도 축소)") { value = true };
            _yamlOptimizeToggle.tooltip = "Unity 풀 정밀도(7~9자리)를 줄여 키당 ~30바이트 절감.";
            _yamlOptimizeToggle.RegisterValueChangedCallback(_ => SetCustomPreset());
            _yamlOptimizeToggle.style.marginTop = 6;
            foldout.Add(_yamlOptimizeToggle);

            _valueDigitsField = new IntegerField("값 유효숫자") { value = 5 };
            _valueDigitsField.tooltip = "value 필드 유효숫자. 5면 머슬 1/100,000 정밀도.";
            _valueDigitsField.RegisterValueChangedCallback(_ => SetCustomPreset());
            foldout.Add(_valueDigitsField);

            _slopeDigitsField = new IntegerField("슬로프 유효숫자") { value = 4 };
            _slopeDigitsField.tooltip = "inSlope/outSlope 유효숫자. 4면 1/10,000 정밀도.";
            _slopeDigitsField.RegisterValueChangedCallback(_ => SetCustomPreset());
            foldout.Add(_slopeDigitsField);

            // Asset-level toggles
            _setCompressedToggle = new Toggle("m_Compressed 플래그 ON") { value = true };
            _setCompressedToggle.tooltip = "런타임 메모리 절감 (16비트 양자화). 디스크 크기는 무영향.";
            _setCompressedToggle.style.marginTop = 6;
            foldout.Add(_setCompressedToggle);

            _removeEditorCurvesToggle = new Toggle("m_EditorCurves 제거 (50% 절감)") { value = true };
            _removeEditorCurvesToggle.tooltip = "Animation 창 편집은 못 하지만 재생은 정상.";
            foldout.Add(_removeEditorCurvesToggle);

            parent.Add(foldout);
        }

        private void BuildExperimentalFoldout(VisualElement parent)
        {
            var foldout = new Foldout { text = "실험적 옵션", value = false };
            foldout.AddToClassList("section-foldout");
            foldout.style.marginTop = 4;

            var warning = new Label("⚠ 호환성 100% 보장 안 됨. 압축 후 반드시 재생 테스트.");
            warning.style.fontSize = 10;
            warning.style.color = new StyleColor(new Color(0.95f, 0.7f, 0.3f));
            warning.style.whiteSpace = WhiteSpace.Normal;
            warning.style.marginBottom = 4;
            foldout.Add(warning);

            _stripDefaultFieldsToggle = new Toggle("키프레임 기본값 필드 제거 (~30% 추가 절감)") { value = false };
            _stripDefaultFieldsToggle.tooltip = "weightedMode/inWeight/outWeight 0 라인을 .anim에서 제거. 키당 60~70바이트 추가 절감.";
            _stripDefaultFieldsToggle.RegisterValueChangedCallback(_ => SetCustomPreset());
            foldout.Add(_stripDefaultFieldsToggle);

            parent.Add(foldout);
        }

        private void BuildRunButton(VisualElement parent)
        {
            var btnRow = new VisualElement();
            btnRow.style.marginTop = 12;
            btnRow.style.marginBottom = 8;
            var compressBtn = new Button(Run) { text = "압축 실행" };
            compressBtn.AddToClassList("btn-primary");
            compressBtn.style.height = 36;
            compressBtn.style.fontSize = 13;
            btnRow.Add(compressBtn);
            parent.Add(btnRow);
        }

        private void BuildStatsSection(VisualElement parent)
        {
            _statsContainer = MakeSection("결과");
            _statsLabel = new Label("(아직 압축 안 됨)");
            _statsLabel.style.whiteSpace = WhiteSpace.Normal;
            _statsLabel.style.unityTextAlign = TextAnchor.UpperLeft;
            _statsContainer.Add(_statsLabel);
            parent.Add(_statsContainer);
        }

        // ────────────────────────────── Helpers

        private static VisualElement MakeSection(string title)
        {
            var box = new VisualElement();
            box.AddToClassList("section");
            box.style.marginTop = 6;
            box.style.paddingTop = 6;
            box.style.paddingBottom = 6;
            box.style.paddingLeft = 8;
            box.style.paddingRight = 8;
            var lbl = new Label(title);
            lbl.style.unityFontStyleAndWeight = FontStyle.Bold;
            lbl.style.marginBottom = 4;
            box.Add(lbl);
            return box;
        }

        private static Label MakeHintLabel(string text)
        {
            var l = new Label(text);
            l.style.fontSize = 10;
            l.style.color = new StyleColor(new Color(0.6f, 0.6f, 0.6f));
            l.style.marginTop = 4;
            l.style.whiteSpace = WhiteSpace.Normal;
            return l;
        }

        private FloatField MakeTrackedFloat(string label, float defaultValue, string tooltip)
        {
            var f = new FloatField(label) { value = defaultValue };
            if (!string.IsNullOrEmpty(tooltip)) f.tooltip = tooltip;
            f.RegisterValueChangedCallback(_ => SetCustomPreset());
            return f;
        }

        private static Button MakeQuickButton(string label, System.Action onClick)
        {
            var b = new Button(onClick) { text = label };
            b.style.marginLeft = 4;
            b.style.minWidth = 36;
            b.style.paddingLeft = 6;
            b.style.paddingRight = 6;
            return b;
        }

        private void UpdatePresetDescription()
        {
            var preset = GetCurrentPreset();
            _presetDescLabel.text = preset switch
            {
                QualityPreset.Lossless => "거의 무손실. 모캡 미세 떨림까지 보존. 파일 큼.",
                QualityPreset.Standard => "권장 기본값. 머슬 0.001 + 채널 드롭. 시각 무손실.",
                QualityPreset.Aggressive => "머슬 0.003 + smoothing 3. 모캡 노이즈 제거.",
                QualityPreset.High => "머슬 0.01 + 30fps 리샘플 + smoothing 3.",
                QualityPreset.Extreme => "머슬 0.03 + 30fps + smoothing 5 + 실험적 필드 제거.",
                QualityPreset.Custom => "아래 옵션이 사용자가 설정한 그대로 사용됨.",
                _ => "",
            } + "\n※ 모든 프리셋에서 Root(힙) 채널은 항상 안전 모드 — 리샘플·평활화 적용 안 됨.";
        }

        private void ApplyQualityPreset(QualityPreset preset)
        {
            _suppressPresetUpdate = true;
            try
            {
                switch (preset)
                {
                    // Root tolerances (RootPos/RootRot) are LOCKED tight across all presets —
                    // hip drives the avatar world transform, so any error is amplified body-wide.
                    case QualityPreset.Lossless:
                        _muscleTolField.value = 0.0005f;
                        _spineTolField.value = 0.0002f;
                        _rootPosTolField.value = 0.0001f;
                        _rootRotTolField.value = 0.0002f;
                        _dropUnusedToggle.value = false;
                        _unusedThresholdField.value = 0.001f;
                        _resampleRateField.value = 0f;
                        _smoothingWindowField.value = 0;
                        _yamlOptimizeToggle.value = true;
                        _valueDigitsField.value = 6;
                        _slopeDigitsField.value = 5;
                        _stripDefaultFieldsToggle.value = false;
                        _fitModeField.value = AnimClipKeyReducer.FitMode.Auto;
                        break;
                    case QualityPreset.Standard:
                        _muscleTolField.value = 0.001f;
                        _spineTolField.value = 0.0003f;
                        _rootPosTolField.value = 0.0001f;
                        _rootRotTolField.value = 0.0002f;
                        _dropUnusedToggle.value = true;
                        _unusedThresholdField.value = 0.005f;
                        _resampleRateField.value = 0f;
                        _smoothingWindowField.value = 0;
                        _yamlOptimizeToggle.value = true;
                        _valueDigitsField.value = 5;
                        _slopeDigitsField.value = 4;
                        _stripDefaultFieldsToggle.value = false;
                        _fitModeField.value = AnimClipKeyReducer.FitMode.Auto;
                        break;
                    case QualityPreset.Aggressive:
                        _muscleTolField.value = 0.003f;
                        _spineTolField.value = 0.0003f;
                        _rootPosTolField.value = 0.0001f;
                        _rootRotTolField.value = 0.0002f;
                        _dropUnusedToggle.value = true;
                        _unusedThresholdField.value = 0.005f;
                        _resampleRateField.value = 0f;
                        _smoothingWindowField.value = 3;
                        _yamlOptimizeToggle.value = true;
                        _valueDigitsField.value = 5;
                        _slopeDigitsField.value = 4;
                        _stripDefaultFieldsToggle.value = false;
                        _fitModeField.value = AnimClipKeyReducer.FitMode.Auto;
                        break;
                    case QualityPreset.High:
                        _muscleTolField.value = 0.01f;
                        _spineTolField.value = 0.0005f;
                        _rootPosTolField.value = 0.0001f;
                        _rootRotTolField.value = 0.0002f;
                        _dropUnusedToggle.value = true;
                        _unusedThresholdField.value = 0.01f;
                        _resampleRateField.value = 30f;
                        _smoothingWindowField.value = 3;
                        _yamlOptimizeToggle.value = true;
                        _valueDigitsField.value = 5;
                        _slopeDigitsField.value = 4;
                        _stripDefaultFieldsToggle.value = false;
                        _fitModeField.value = AnimClipKeyReducer.FitMode.Auto;
                        break;
                    case QualityPreset.Extreme:
                        _muscleTolField.value = 0.03f;
                        _spineTolField.value = 0.001f;
                        _rootPosTolField.value = 0.0001f;
                        _rootRotTolField.value = 0.0002f;
                        _dropUnusedToggle.value = true;
                        _unusedThresholdField.value = 0.02f;
                        _resampleRateField.value = 30f;
                        _smoothingWindowField.value = 5;
                        _yamlOptimizeToggle.value = true;
                        _valueDigitsField.value = 5;
                        _slopeDigitsField.value = 4;
                        _stripDefaultFieldsToggle.value = true;
                        _fitModeField.value = AnimClipKeyReducer.FitMode.Auto;
                        break;
                    case QualityPreset.Custom:
                        // No-op — user settings remain.
                        break;
                }
            }
            finally
            {
                _suppressPresetUpdate = false;
            }
        }

        private void SetCustomPreset()
        {
            if (_suppressPresetUpdate) return;
            if (_qualityPresetField == null) return;
            if (GetCurrentPreset() == QualityPreset.Custom) return;
            _suppressPresetUpdate = true;
            _qualityPresetField.index = (int)QualityPreset.Custom;
            _suppressPresetUpdate = false;
            UpdatePresetDescription();
        }

        private void SuggestOutputPath()
        {
            var clip = _clipField.value as AnimationClip;
            if (clip == null) return;
            string clipPath = AssetDatabase.GetAssetPath(clip);
            if (string.IsNullOrEmpty(clipPath)) return;
            string dir = Path.GetDirectoryName(clipPath).Replace('\\', '/');
            string name = Path.GetFileNameWithoutExtension(clipPath);
            _outputPathField.value = $"{dir}/{name}_reduced.anim";
        }

        private void BrowseOutput()
        {
            string defaultName = "clip_reduced.anim";
            if (!string.IsNullOrEmpty(_outputPathField.value))
                defaultName = Path.GetFileName(_outputPathField.value);
            string path = EditorUtility.SaveFilePanelInProject("AnimationClip 출력 경로", defaultName, "anim", "출력 경로 선택");
            if (string.IsNullOrEmpty(path)) return;
            _outputPathField.value = path.Replace('\\', '/');
        }

        // ────────────────────────────── Run

        private void Run()
        {
            var clip = _clipField.value as AnimationClip;
            if (clip == null) { EditorUtility.DisplayDialog("머슬 클립 압축기", "AnimationClip을 선택하세요.", "확인"); return; }
            if (string.IsNullOrEmpty(_outputPathField.value)) SuggestOutputPath();
            if (string.IsNullOrEmpty(_outputPathField.value)) { EditorUtility.DisplayDialog("머슬 클립 압축기", "출력 경로를 지정하세요.", "확인"); return; }

            var opts = AnimClipKeyReducer.Options.Default;
            opts.MuscleTolerance = Mathf.Max(0f, _muscleTolField.value);
            opts.SpineTolerance = Mathf.Max(0f, _spineTolField.value);
            opts.RootPosTolerance = Mathf.Max(0f, _rootPosTolField.value);
            opts.RootRotTolerance = Mathf.Max(0f, _rootRotTolField.value);
            opts.SetCompressedFlag = _setCompressedToggle.value;
            opts.RemoveEditorCurves = _removeEditorCurvesToggle.value;
            opts.Fit = (AnimClipKeyReducer.FitMode)_fitModeField.value;
            opts.DropUnusedChannels = _dropUnusedToggle.value;
            opts.UnusedChannelThreshold = Mathf.Max(0f, _unusedThresholdField.value);
            opts.ResampleFrameRate = Mathf.Max(0f, _resampleRateField.value);
            opts.SmoothingWindow = Mathf.Max(0, _smoothingWindowField.value);

            string srcPath = AssetDatabase.GetAssetPath(clip);
            long srcBytes = !string.IsNullOrEmpty(srcPath) && File.Exists(srcPath) ? new FileInfo(srcPath).Length : 0;

            AnimationClip newClip;
            AnimClipKeyReducer.Stats stats;
            try
            {
                newClip = AnimClipKeyReducer.Reduce(clip, opts, out stats,
                    (p, msg) => EditorUtility.DisplayProgressBar("머슬 클립 압축", msg, p));
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (newClip == null) { EditorUtility.DisplayDialog("머슬 클립 압축기", "압축 실패", "확인"); return; }

            string outPath = _outputPathField.value;
            if (!outPath.StartsWith("Assets/")) outPath = "Assets/" + outPath.TrimStart('/');

            if (File.Exists(outPath))
            {
                if (!_overwriteToggle.value)
                {
                    EditorUtility.DisplayDialog("머슬 클립 압축기", $"파일이 이미 존재합니다: {outPath}", "확인");
                    Object.DestroyImmediate(newClip);
                    return;
                }
                AssetDatabase.DeleteAsset(outPath);
            }

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outPath));
                AssetDatabase.CreateAsset(newClip, outPath);
                if (opts.SetCompressedFlag) AnimClipKeyReducer.SetCompressedFlag(newClip, true);
                if (opts.RemoveEditorCurves) AnimClipKeyReducer.ClearEditorCurves(newClip);
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }
            catch (System.Exception e)
            {
                EditorUtility.DisplayDialog("머슬 클립 압축기", $"저장 실패: {e.Message}", "확인");
                return;
            }

            long preYamlBytes = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
            AnimYamlOptimizer.Stats yamlStats = default;
            if (_yamlOptimizeToggle.value && File.Exists(outPath))
            {
                var yamlOpts = AnimYamlOptimizer.Options.Default;
                yamlOpts.ValueSignificantDigits = Mathf.Clamp(_valueDigitsField.value, 2, 9);
                yamlOpts.SlopeSignificantDigits = Mathf.Clamp(_slopeDigitsField.value, 2, 9);
                yamlOpts.StripEditorCurves = opts.RemoveEditorCurves;
                yamlOpts.StripDefaultKeyframeFields = _stripDefaultFieldsToggle.value;
                try
                {
                    yamlStats = AnimYamlOptimizer.Optimize(outPath, yamlOpts);
                    AssetDatabase.ImportAsset(outPath, ImportAssetOptions.ForceUpdate);
                }
                catch (System.Exception e)
                {
                    Debug.LogWarning($"[머슬 클립 압축기] YAML 후처리 실패 (무시하고 진행): {e.Message}");
                }
            }

            long outBytes = File.Exists(outPath) ? new FileInfo(outPath).Length : 0;
            DisplayStats(stats, srcBytes, outBytes, preYamlBytes, yamlStats, outPath);

            EditorGUIUtility.PingObject(AssetDatabase.LoadAssetAtPath<AnimationClip>(outPath));
        }

        // ────────────────────────────── Stats display

        private void DisplayStats(AnimClipKeyReducer.Stats stats, long srcBytes, long outBytes, long preYamlBytes, AnimYamlOptimizer.Stats yamlStats, string outPath)
        {
            float sizeRatio = srcBytes > 0 ? (float)outBytes / srcBytes : 0f;
            float sizePct = srcBytes > 0 ? (1f - sizeRatio) * 100f : 0f;
            float keyPct = (1f - stats.ReductionRatio) * 100f;

            var sb = new System.Text.StringBuilder();
            sb.Append("저장 위치: ").Append(outPath).Append("\n\n");

            sb.Append("──── 채널 ────\n");
            sb.Append("입력: ").Append(stats.CurveCount).Append("개  →  ");
            sb.Append("출력: ").Append(stats.OutputCurveCount).Append("개");
            if (stats.DroppedChannels > 0)
                sb.Append("  (드롭 ").Append(stats.DroppedChannels).Append("개)");
            sb.Append("\n\n");

            sb.Append("──── 키프레임 ────\n");
            sb.Append("입력: ").Append(stats.OriginalKeyCount.ToString("N0")).Append("  →  ");
            sb.Append("출력: ").Append(stats.ReducedKeyCount.ToString("N0"));
            sb.Append("  (").Append(keyPct.ToString("F2")).Append("% 감소)\n\n");

            sb.Append("──── 파일 크기 ────\n");
            sb.Append("원본: ").Append(FormatBytes(srcBytes)).Append("  →  ");
            sb.Append("출력: ").Append(FormatBytes(outBytes));
            sb.Append("  (").Append(sizePct.ToString("F2")).Append("% 감소)");

            if (preYamlBytes > 0 && outBytes < preYamlBytes)
            {
                float yamlSavedPct = (1f - (float)outBytes / preYamlBytes) * 100f;
                sb.Append("\n   └ YAML 후처리: ").Append(FormatBytes(preYamlBytes)).Append(" → ").Append(FormatBytes(outBytes));
                sb.Append("  (").Append(yamlSavedPct.ToString("F2")).Append("% 추가)");
                sb.Append("\n     값 라운드: ").Append(yamlStats.RoundedValues.ToString("N0"));
                sb.Append(", 슬로프 라운드: ").Append(yamlStats.RoundedSlopes.ToString("N0"));
                sb.Append(", 0 스냅: ").Append(yamlStats.SlopesSnappedToZero.ToString("N0"));
                if (yamlStats.StrippedDefaultFields > 0)
                    sb.Append(", 기본필드 제거: ").Append(yamlStats.StrippedDefaultFields.ToString("N0"));
            }

            sb.Append("\n\n──── 정확도 ────\n");
            sb.Append("최대 오차: ").Append(stats.MaxError.ToString("F6")).Append("\n");
            sb.Append("평균 오차: ").Append(stats.AvgError.ToString("F6")).Append("\n");
            sb.Append("측정 샘플: ").Append(stats.ErrorSampleCount.ToString("N0"));

            _statsLabel.text = sb.ToString();
        }

        private static string FormatBytes(long bytes)
        {
            if (bytes <= 0) return "?";
            if (bytes < 1024) return $"{bytes} B";
            if (bytes < 1024 * 1024) return $"{bytes / 1024f:F1} KB";
            if (bytes < 1024L * 1024 * 1024) return $"{bytes / (1024f * 1024f):F2} MB";
            return $"{bytes / (1024f * 1024f * 1024f):F2} GB";
        }
    }
}
