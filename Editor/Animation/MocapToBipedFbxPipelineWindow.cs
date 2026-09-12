using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using UnityEngine.Serialization;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// Mocap tool hub. Browser-style tabs keep each source format's queue apart:
    /// the OptiTrack and MMRP pipelines (bind → Forearm Hinge → 3ds Max FBX) and
    /// the FBX animation import setup that used to be its own window.
    /// </summary>
    public sealed class MocapToBipedFbxPipelineWindow : EditorWindow
    {
        private enum Tab
        {
            OptiTrack,
            Mmrp,
            FbxSetup
        }

        private static readonly string[] TabLabels =
        {
            "OptiTrack 파이프라인",
            "MMRP 파이프라인",
            "FBX 애니메이션 설정"
        };

        private const string FbxDirectoryKey = "YAMO.MocapPipeline.FbxDirectory";
        private const string SetupReadmePath =
            "Packages/com.yamo.unitytools/Editor/Animation/FbxAnimSetup_README.md";
        private const string PipelineReadmePath =
            "Packages/com.yamo.unitytools/Editor/Animation/MocapToBipedFbxPipeline_README.md";
        private static MocapToBipedFbxPipelineWindow instance;

        [SerializeField] private Tab activeTab;
        [SerializeField] private Animator targetAnimator;
        [SerializeField, FormerlySerializedAs("items")]
        private List<MocapPipelineItem> optiTrackItems = new List<MocapPipelineItem>();
        [SerializeField] private List<MocapPipelineItem> mmrpItems = new List<MocapPipelineItem>();
        [SerializeField] private string fbxOutputDirectory;
        [SerializeField] private int sampleRate = 60;
        [SerializeField] private MocapHingeBakeMode hingeBakeMode = MocapHingeBakeMode.PlayMode;
        [SerializeField] private bool enableHingeCorrection = true;
        [SerializeField] private ForearmHingeAxis hingeAxis = ForearmHingeAxis.Z;
        [SerializeField, Range(0f, 1f)] private float handRotationCompensation = 1f;
        [SerializeField] private ExistingMotionAssetPolicy existingBindingPolicy = ExistingMotionAssetPolicy.Fail;
        [SerializeField] private bool exportGeometry;
        [SerializeField] private bool exportUnrendered = true;
        [SerializeField] private bool keepInstances = true;
        [SerializeField] private bool embedTextures;
        [SerializeField] private bool createFbxBackup = true;
        [SerializeField] private bool continueOnError = true;
        [SerializeField] private bool revealAfterExport = true;
        [SerializeField] private bool includeSubfolders = true;
        [SerializeField] private bool showAdvanced;

        // FBX 애니메이션 설정 tab
        [SerializeField] private List<Object> setupTargets = new List<Object>();
        [SerializeField] private List<Object> bindingTargets = new List<Object>();
        [SerializeField] private MocapSourceFormat bindingFormat = MocapSourceFormat.OptiTrack;
        private string setupStatus;
        private string bindingStatus;

        private Vector2 scrollPosition;

        [MenuItem("Tools/YAMO/Animation/Mocap to Biped FBX Pipeline")]
        public static void Open()
        {
            var window = GetWindow<MocapToBipedFbxPipelineWindow>("Mocap → Biped FBX");
            window.minSize = new Vector2(650f, 620f);
            window.InitializeDefaults();
            window.Show();
        }

        [Shortcut("YAMO/Mocap to Biped FBX Pipeline", KeyCode.Alpha8, ShortcutModifiers.None)]
        private static void ToggleShortcut()
        {
            if (instance != null)
            {
                instance.Close();
                return;
            }

            Open();
        }

        private void OnEnable()
        {
            instance = this;
            InitializeDefaults();
        }

        private void OnDisable()
        {
            if (instance == this)
                instance = null;
        }

        private void InitializeDefaults()
        {
            if (string.IsNullOrWhiteSpace(fbxOutputDirectory))
            {
                fbxOutputDirectory = EditorPrefs.GetString(
                    FbxDirectoryKey,
                    Path.GetFullPath(Path.Combine(Application.dataPath, "..", "Recordings")));
            }

            if (targetAnimator == null && Selection.activeGameObject != null)
                targetAnimator = Selection.activeGameObject.GetComponent<Animator>();
        }

        private MocapSourceFormat CurrentFormat =>
            activeTab == Tab.Mmrp ? MocapSourceFormat.MMRP : MocapSourceFormat.OptiTrack;

        private List<MocapPipelineItem> CurrentItems =>
            activeTab == Tab.Mmrp ? mmrpItems : optiTrackItems;

        private void OnGUI()
        {
            DrawTabBar();
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            if (activeTab == Tab.FbxSetup)
                DrawFbxSetupPage();
            else
                DrawPipelinePage(CurrentFormat);
            EditorGUILayout.EndScrollView();
        }

        private void DrawTabBar()
        {
            var newIndex = GUILayout.Toolbar((int)activeTab, TabLabels, GUILayout.Height(28f));
            if (newIndex != (int)activeTab)
            {
                activeTab = (Tab)newIndex;
                GUI.FocusControl(null);
            }

            EditorGUILayout.Space(2f);
            EditorGUILayout.LabelField(GUIContent.none, GUI.skin.horizontalSlider);
        }

        // ══════════════════════════════ Pipeline tabs (OptiTrack / MMRP)

        private void DrawPipelinePage(MocapSourceFormat format)
        {
            EditorGUILayout.LabelField(
                $"{format.GetLabel()} → Forearm Hinge → 3ds Max FBX",
                EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                DescribeFormat(format) + " Anim 입력은 클립을 바로 사용합니다. " +
                "이후 Forearm Hinge Bake와 Max Z-up FBX 출력을 실행합니다. " +
                (hingeBakeMode == MocapHingeBakeMode.PlayMode
                    ? "기본 Play Mode는 실제 Animator를 실행하여 발 IK와 Foot Stabilization을 반영합니다."
                    : "Edit Mode는 빠르게 샘플링하지만 런타임 Foot Stabilization은 반영하지 않습니다."),
                MessageType.Info);
            DrawReadmeButton(PipelineReadmePath);

            if (MocapToBipedFbxPlayModeRunner.IsRunning)
                EditorGUILayout.HelpBox("Play Mode Hinge 배치가 실행 중입니다. Edit Mode 복귀 후 FBX Export가 자동으로 이어집니다.", MessageType.Warning);

            DrawTarget();
            EditorGUILayout.Space(8f);
            DrawOutputs();
            EditorGUILayout.Space(8f);
            DrawQueue(CurrentItems, format);
            EditorGUILayout.Space(8f);
            DrawOptions();
            EditorGUILayout.Space(12f);
            DrawRunButton();
        }

        private static string DescribeFormat(MocapSourceFormat format)
        {
            return format == MocapSourceFormat.MMRP
                ? "MMRP(Mingle Motion Replayer) FBX는 본 이름이 'Hips', 'LeftUpperArm'처럼 Unity 표준 휴머노이드 이름입니다. " +
                  "_Backup 보존 후 표준 이름으로 _T 아바타를 만들어 바인딩하며, 출력 이름은 파일 이름을 따릅니다."
                : "OptiTrack(Motive) FBX는 본 이름이 '001_Hips'처럼 액터 접두사를 가집니다. " +
                  "_Backup 보존 후 접두사를 탐지해 _T 아바타를 만들어 바인딩하며, 출력 이름은 테이크 이름을 따릅니다.";
        }

        private void DrawTarget()
        {
            EditorGUILayout.LabelField("1. 대상 Biped", EditorStyles.boldLabel);
            targetAnimator = (Animator)EditorGUILayout.ObjectField(
                "Biped Animator",
                targetAnimator,
                typeof(Animator),
                true);

            if (targetAnimator == null)
            {
                EditorGUILayout.HelpBox("씬의 Biped 루트 Animator를 지정하세요.", MessageType.Warning);
                return;
            }

            var avatarValid = targetAnimator.avatar != null &&
                              targetAnimator.avatar.isValid &&
                              targetAnimator.avatar.isHuman;
            EditorGUILayout.HelpBox(
                avatarValid ? "유효한 Humanoid Avatar" : "유효한 Humanoid Avatar가 아닙니다.",
                avatarValid ? MessageType.None : MessageType.Error);
        }

        private void DrawOutputs()
        {
            EditorGUILayout.LabelField("2. 출력", EditorStyles.boldLabel);
            using (new EditorGUILayout.HorizontalScope())
            {
                fbxOutputDirectory = EditorGUILayout.TextField("최종 FBX 폴더", fbxOutputDirectory);
                if (GUILayout.Button("...", GUILayout.Width(34f)))
                {
                    var selected = EditorUtility.OpenFolderPanel(
                        "최종 FBX 출력 폴더",
                        fbxOutputDirectory,
                        string.Empty);
                    if (!string.IsNullOrEmpty(selected))
                        fbxOutputDirectory = selected;
                }
            }
        }

        private void DrawQueue(List<MocapPipelineItem> items, MocapSourceFormat format)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"3. {format.GetLabel()} 모션 큐 ({items.Count})", EditorStyles.boldLabel);
                if (GUILayout.Button("선택 추가", GUILayout.Width(78f)))
                    AddObjects(Selection.objects);
                if (GUILayout.Button("폴더 추가...", GUILayout.Width(88f)))
                    AddFolderFromPanel();
                if (GUILayout.Button("빈 항목", GUILayout.Width(70f)))
                    items.Add(new MocapPipelineItem());
                if (GUILayout.Button("전체 삭제", GUILayout.Width(70f)))
                    items.Clear();
            }

            includeSubfolders = EditorGUILayout.ToggleLeft(
                "폴더 추가 시 하위 폴더의 FBX/Anim도 포함 (_T, _Backup 파일은 항상 제외)",
                includeSubfolders);
            DrawDropArea(
                $"{format.GetLabel()} FBX, Anim 또는 프로젝트 폴더를 여기에 드래그",
                dropped => AddObjects(dropped));

            for (var index = 0; index < items.Count; index++)
            {
                var item = items[index] ?? (items[index] = new MocapPipelineItem());
                using (new EditorGUILayout.VerticalScope(EditorStyles.helpBox))
                {
                    using (new EditorGUILayout.HorizontalScope())
                    {
                        item.Enabled = EditorGUILayout.Toggle(item.Enabled, GUILayout.Width(18f));
                        EditorGUILayout.LabelField($"#{index + 1}", GUILayout.Width(28f));
                        item.SourceFbx = EditorGUILayout.ObjectField(item.SourceFbx, typeof(Object), false);
                        if (GUILayout.Button("×", GUILayout.Width(26f)))
                        {
                            items.RemoveAt(index);
                            GUIUtility.ExitGUI();
                        }
                    }

                    using (new EditorGUI.DisabledScope(!item.Enabled))
                    {
                        using (new EditorGUILayout.HorizontalScope())
                        {
                            item.StartTime = Mathf.Max(0f, EditorGUILayout.FloatField("시작 (초)", item.StartTime));
                            item.Duration = Mathf.Max(0f, EditorGUILayout.FloatField("길이", item.Duration));
                        }
                        item.OutputName = EditorGUILayout.TextField(
                            new GUIContent("출력 이름", "비워두면 입력 FBX 또는 Anim 이름을 사용합니다."),
                            item.OutputName);
                        EditorGUILayout.LabelField("길이 0은 바인딩된 클립의 끝까지 처리합니다.", EditorStyles.miniLabel);
                    }
                }
            }
        }

        private void DrawOptions()
        {
            EditorGUILayout.LabelField("4. 처리 설정", EditorStyles.boldLabel);
            sampleRate = Mathf.Clamp(EditorGUILayout.IntField("공통 Sample Rate", sampleRate), 1, 120);
            hingeBakeMode = (MocapHingeBakeMode)EditorGUILayout.EnumPopup(
                new GUIContent(
                    "Hinge Bake Mode",
                    "Play Mode: 실시간 발 IK/Foot Stabilization 포함. Edit Mode: 빠른 오프라인 샘플링."),
                hingeBakeMode);

            enableHingeCorrection = GUILayout.Toggle(
                enableHingeCorrection,
                enableHingeCorrection
                    ? "Forearm Hinge 보정: 활성화"
                    : "Forearm Hinge 보정: 비활성화",
                GUI.skin.button,
                GUILayout.Height(28f));

            using (new EditorGUI.DisabledScope(!enableHingeCorrection))
            {
                hingeAxis = (ForearmHingeAxis)EditorGUILayout.EnumPopup("Forearm Hinge Axis", hingeAxis);
                handRotationCompensation = EditorGUILayout.Slider(
                    new GUIContent(
                        "손목 과회전 제거량",
                        "0: 기존처럼 손 월드 회전 보존. 1: 원래 손 로컬 회전을 유지해 Forearm에서 제거된 회전이 손목으로 넘어오는 것을 방지."),
                    handRotationCompensation,
                    0f,
                    1f);
            }

            if (!enableHingeCorrection)
            {
                EditorGUILayout.HelpBox(
                    "Forearm Hinge 보정을 건너뜁니다. Humanoid 포즈의 Generic Transform 베이크와 FBX 출력은 계속 실행됩니다.",
                    MessageType.Info);
            }
            existingBindingPolicy = (ExistingMotionAssetPolicy)EditorGUILayout.EnumPopup(
                new GUIContent("기존 Motion/_T 충돌",
                    "Fail은 기존 에셋을 보호하고, Overwrite는 교체하며, "
                    + "Disambiguate는 이름 뒤에 번호를 붙여 양쪽 모두 남깁니다."),
                existingBindingPolicy);
            continueOnError = EditorGUILayout.Toggle("오류 시 다음 항목 계속", continueOnError);

            if (sampleRate != 60)
                EditorGUILayout.HelpBox("현재 작업 기준은 60fps입니다. 특별한 이유가 없다면 60을 사용하세요.", MessageType.Warning);
            if (existingBindingPolicy == ExistingMotionAssetPolicy.Overwrite)
                EditorGUILayout.HelpBox("기존 Motion FBX와 _T FBX가 삭제 후 교체될 수 있습니다.", MessageType.Warning);

            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "FBX 고급 옵션", true);
            if (!showAdvanced)
                return;

            exportGeometry = EditorGUILayout.Toggle("Geometry 포함", exportGeometry);
            exportUnrendered = EditorGUILayout.Toggle("렌더러 없는 노드 포함", exportUnrendered);
            keepInstances = EditorGUILayout.Toggle("메시 인스턴스 유지", keepInstances);
            embedTextures = EditorGUILayout.Toggle("텍스처 임베드", embedTextures);
            createFbxBackup = EditorGUILayout.Toggle("최종 FBX 덮어쓰기 백업", createFbxBackup);
            revealAfterExport = EditorGUILayout.Toggle("완료 후 폴더 표시", revealAfterExport);
        }

        private void DrawRunButton()
        {
            using (new EditorGUI.DisabledScope(!CanRun()))
            {
                var modeLabel = hingeBakeMode == MocapHingeBakeMode.PlayMode
                    ? "Play Mode"
                    : "Edit Mode";
                var label = enableHingeCorrection
                    ? $"{CurrentFormat.GetLabel()} 파이프라인 실행 ({modeLabel})"
                    : $"{CurrentFormat.GetLabel()} 파이프라인 실행 ({modeLabel} / Hinge 비활성화)";
                if (GUILayout.Button(label, GUILayout.Height(44f)))
                    RunPipeline();
            }
        }

        private void RunPipeline()
        {
            var items = CurrentItems;
            var settings = new MocapPipelineSettings
            {
                TargetAnimator = targetAnimator,
                FbxOutputDirectory = Path.GetFullPath(fbxOutputDirectory),
                SourceFormat = CurrentFormat,
                SampleRate = sampleRate,
                HingeBakeMode = hingeBakeMode,
                EnableHingeCorrection = enableHingeCorrection,
                HingeAxis = hingeAxis,
                HandRotationCompensation = handRotationCompensation,
                ExistingBindingPolicy = existingBindingPolicy,
                RecordBlendShapes = false,
                ClampedTangents = false,
                Compression = MotionFbxCurveCompression.Disabled,
                ExportGeometry = exportGeometry,
                ExportUnrendered = exportUnrendered,
                KeepInstances = keepInstances,
                EmbedTextures = embedTextures,
                CreateFbxBackup = createFbxBackup,
                ContinueOnError = continueOnError
            };

            try
            {
                EditorPrefs.SetString(FbxDirectoryKey, fbxOutputDirectory);
                if (hingeBakeMode == MocapHingeBakeMode.PlayMode)
                {
                    MocapToBipedFbxPlayModeRunner.Start(settings, items, revealAfterExport);
                    return;
                }

                var results = MocapToBipedFbxPipeline.Run(
                    settings,
                    items,
                    (message, progress) => EditorUtility.DisplayCancelableProgressBar(
                        "Mocap → Biped FBX",
                        message,
                        progress));

                var succeeded = results.Count(result => result.Succeeded);
                var failed = results.Count - succeeded;
                if (revealAfterExport && succeeded > 0)
                    EditorUtility.RevealInFinder(fbxOutputDirectory);
                EditorUtility.DisplayDialog(
                    "Mocap 파이프라인 완료",
                    $"성공: {succeeded}개\n실패: {failed}개\n\n원본 백업: FBX 입력 옆 *_Backup.fbx\nFBX: {fbxOutputDirectory}",
                    "확인");
            }
            catch (OperationCanceledException)
            {
                Debug.LogWarning("[Mocap Pipeline] 사용자가 작업을 취소했습니다.");
            }
            catch (Exception exception)
            {
                Debug.LogException(exception);
                EditorUtility.DisplayDialog("Mocap 파이프라인 실패", exception.Message, "확인");
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }
        }

        private bool CanRun()
        {
            return targetAnimator != null &&
                   targetAnimator.avatar != null &&
                   targetAnimator.avatar.isValid &&
                   targetAnimator.avatar.isHuman &&
                   sampleRate > 0 &&
                   !string.IsNullOrWhiteSpace(fbxOutputDirectory) &&
                   CurrentItems.Any(item => item != null && item.Enabled && IsMotionSource(item.SourceFbx)) &&
                   !IsEditorBusy();
        }

        private static bool IsEditorBusy()
        {
            return MocapToBipedFbxPlayModeRunner.IsRunning ||
                   EditorApplication.isPlayingOrWillChangePlaymode ||
                   EditorApplication.isCompiling;
        }

        private static void DrawDropArea(string text, Action<Object[]> onDrop)
        {
            var rect = GUILayoutUtility.GetRect(0f, 42f, GUILayout.ExpandWidth(true));
            GUI.Box(rect, text, EditorStyles.helpBox);
            var currentEvent = Event.current;
            if (!rect.Contains(currentEvent.mousePosition))
                return;

            if (currentEvent.type == EventType.DragUpdated)
            {
                DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                currentEvent.Use();
            }
            else if (currentEvent.type == EventType.DragPerform)
            {
                DragAndDrop.AcceptDrag();
                onDrop(DragAndDrop.objectReferences);
                currentEvent.Use();
            }
        }

        private static void DrawReadmeButton(string readmePath)
        {
            if (GUILayout.Button("📖 사용법 (README 열기)", EditorStyles.miniButton, GUILayout.Width(170f)))
                OpenReadme(readmePath);
        }

        private static void OpenReadme(string readmePath)
        {
            var asset = AssetDatabase.LoadAssetAtPath<Object>(readmePath);
            if (asset != null)
            {
                AssetDatabase.OpenAsset(asset);
                return;
            }

            var absolute = Path.GetFullPath(readmePath);
            if (File.Exists(absolute))
                Application.OpenURL("file:///" + absolute.Replace('\\', '/'));
            else
                Debug.LogWarning($"[Mocap Pipeline] README를 찾을 수 없습니다: {readmePath}");
        }

        // ────────────────────────────── Pipeline queue management

        private void AddFolderFromPanel()
        {
            var assetPath = PickProjectFolder("모캡 FBX/Anim 폴더 선택");
            if (assetPath != null)
                AddAssetFolders(new[] { assetPath });
        }

        private static string PickProjectFolder(string title)
        {
            var selected = EditorUtility.OpenFolderPanel(title, Application.dataPath, string.Empty);
            if (string.IsNullOrEmpty(selected))
                return null;

            var assetPath = FileUtil.GetProjectRelativePath(selected).Replace('\\', '/');
            if (!string.IsNullOrEmpty(assetPath) && AssetDatabase.IsValidFolder(assetPath))
                return assetPath;

            EditorUtility.DisplayDialog(
                "폴더를 추가할 수 없습니다",
                "현재 Unity 프로젝트의 Assets 폴더 안에 있는 폴더를 선택하세요.",
                "확인");
            return null;
        }

        private int AddObjects(IEnumerable<Object> objects)
        {
            var motionPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var folderPaths = new List<string>();
            foreach (var candidate in objects ?? Enumerable.Empty<Object>())
            {
                if (candidate == null)
                    continue;

                var path = AssetDatabase.GetAssetPath(candidate);
                if (AssetDatabase.IsValidFolder(path))
                    folderPaths.Add(path);
                else if (IsMotionSource(candidate))
                    motionPaths.Add(path);
            }

            AddMotionPathsFromFolders(folderPaths, motionPaths);
            return AddMotionPaths(motionPaths);
        }

        private int AddAssetFolders(IEnumerable<string> folderPaths)
        {
            var motionPaths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            AddMotionPathsFromFolders(folderPaths, motionPaths);
            return AddMotionPaths(motionPaths);
        }

        private void AddMotionPathsFromFolders(
            IEnumerable<string> folderPaths,
            ISet<string> destination)
        {
            foreach (var folderPath in folderPaths ?? Enumerable.Empty<string>())
            {
                if (!AssetDatabase.IsValidFolder(folderPath))
                    continue;

                var guids = AssetDatabase.FindAssets("t:Model", new[] { folderPath })
                    .Concat(AssetDatabase.FindAssets("t:AnimationClip", new[] { folderPath }))
                    .Distinct();
                foreach (var guid in guids)
                {
                    var path = AssetDatabase.GUIDToAssetPath(guid);
                    if (!includeSubfolders && !IsDirectChild(path, folderPath))
                        continue;

                    // Never queue the pipeline's own leftovers (_T avatars, _Backup copies).
                    if (OptiTrackMotionBindingService.IsGeneratedAsset(path))
                        continue;

                    if (IsMotionPath(path))
                        destination.Add(path);
                }
            }
        }

        private static bool IsDirectChild(string assetPath, string folderPath)
        {
            return string.Equals(
                Path.GetDirectoryName(assetPath)?.Replace('\\', '/'),
                folderPath.TrimEnd('/'),
                StringComparison.OrdinalIgnoreCase);
        }

        private int AddMotionPaths(IEnumerable<string> paths)
        {
            var items = CurrentItems;
            var format = CurrentFormat;
            var existingPaths = new HashSet<string>(
                items.Where(item => item?.SourceFbx != null)
                    .Select(item => AssetDatabase.GetAssetPath(item.SourceFbx)),
                StringComparer.OrdinalIgnoreCase);

            var added = 0;
            var otherFormat = 0;
            foreach (var path in paths ?? Enumerable.Empty<string>())
            {
                if (!existingPaths.Add(path) || !IsMotionPath(path))
                    continue;

                // A file whose skeleton belongs to the other tab would only fail at
                // run time; keep it out of this queue and tell the user where it goes.
                if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                {
                    var detected = OptiTrackMotionBindingService.DetectSourceFormat(path);
                    if (detected.HasValue && detected.Value != format)
                    {
                        otherFormat++;
                        continue;
                    }
                }

                var candidate = AssetDatabase.LoadMainAssetAtPath(path);
                if (candidate == null)
                    continue;
                items.Add(new MocapPipelineItem
                {
                    SourceFbx = candidate,
                    OutputName = path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase)
                        ? Path.GetFileNameWithoutExtension(path)
                        : null
                });
                added++;
            }

            var otherLabel = (format == MocapSourceFormat.MMRP
                ? MocapSourceFormat.OptiTrack
                : MocapSourceFormat.MMRP).GetLabel();
            if (added > 0)
            {
                ShowNotification(new GUIContent(
                    otherFormat > 0
                        ? $"모션 {added}개를 큐에 추가했습니다. ({otherLabel} 형식 {otherFormat}개는 {otherLabel} 탭에서 추가하세요.)"
                        : $"모션 {added}개를 큐에 추가했습니다."));
            }
            else if (otherFormat > 0)
            {
                ShowNotification(new GUIContent(
                    $"{otherLabel} 형식 FBX {otherFormat}개는 이 탭이 아니라 {otherLabel} 탭에서 추가하세요."));
            }

            return added;
        }

        private static bool IsMotionSource(Object candidate)
        {
            return MocapPipelineSourceUtility.IsSupported(candidate);
        }

        private static bool IsMotionPath(string path)
        {
            if (string.IsNullOrEmpty(path))
                return false;
            if (path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                return AssetImporter.GetAtPath(path) is ModelImporter;
            return path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase) &&
                   AssetDatabase.LoadMainAssetAtPath(path) is AnimationClip;
        }

        // ══════════════════════════════ FBX 애니메이션 설정 tab

        private void DrawFbxSetupPage()
        {
            EditorGUILayout.LabelField("FBX 애니메이션 설정", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "압축 Off · 클립명 → 파일명 · Root Bake Into Pose + Based Upon = Original 일괄 적용",
                MessageType.Info);
            DrawReadmeButton(SetupReadmePath);

            EditorGUILayout.Space(8f);
            DrawFbxList("1. 대상 FBX 목록", setupTargets, "설정을 적용할 FBX 또는 프로젝트 폴더를 여기에 드래그", false, SetSetupStatus);

            EditorGUILayout.Space(8f);
            EditorGUILayout.LabelField("2. 설정 적용", EditorStyles.boldLabel);
            using (new EditorGUI.DisabledScope(setupTargets.Count == 0 || IsEditorBusy()))
            {
                if (GUILayout.Button("설정 적용", GUILayout.Height(36f)))
                    ApplySetup();
            }
            DrawStatus(setupStatus);

            EditorGUILayout.Space(12f);
            EditorGUILayout.LabelField("3. 모션 바인딩만 실행", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "Forearm Hinge와 FBX 출력 없이 _T 아바타 생성과 바인딩만 수행합니다. " +
                "파이프라인 탭과 같은 규칙을 쓰며, 기존 이름과 겹치면 번호를 붙여 어떤 파일도 지우지 않습니다.",
                MessageType.None);
            bindingFormat = (MocapSourceFormat)EditorGUILayout.EnumPopup(
                new GUIContent("소스 형식", "OptiTrack: 접두사 본 이름(001_Hips). MMRP: Unity 표준 본 이름(Hips)."),
                bindingFormat);
            DrawFbxList("바인딩 FBX 목록", bindingTargets, $"{bindingFormat.GetLabel()} FBX 또는 폴더를 여기에 드래그 (_T, _Backup 제외)", true, SetBindingStatus);
            using (new EditorGUI.DisabledScope(bindingTargets.Count == 0 || IsEditorBusy()))
            {
                if (GUILayout.Button($"{bindingFormat.GetLabel()} 바인딩 실행", GUILayout.Height(36f)))
                    RunBindingOnly();
            }
            DrawStatus(bindingStatus);
        }

        private void SetSetupStatus(string message) => setupStatus = message;
        private void SetBindingStatus(string message) => bindingStatus = message;

        private static void DrawStatus(string status)
        {
            if (!string.IsNullOrEmpty(status))
                EditorGUILayout.HelpBox(status, MessageType.None);
        }

        private void DrawFbxList(
            string title,
            List<Object> targets,
            string dropText,
            bool skipGenerated,
            Action<string> setStatus)
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"{title} ({targets.Count})", EditorStyles.boldLabel);
                if (GUILayout.Button("선택 추가", GUILayout.Width(78f)))
                    AddFbxObjects(targets, Selection.objects, skipGenerated, setStatus);
                if (GUILayout.Button("폴더 추가...", GUILayout.Width(88f)))
                {
                    var folder = PickProjectFolder("FBX를 추가할 폴더 선택");
                    if (folder != null)
                        AddFbxPaths(targets, CollectFbxPaths(new[] { folder }, skipGenerated), setStatus);
                }
                if (GUILayout.Button("전체 삭제", GUILayout.Width(70f)))
                {
                    targets.Clear();
                    setStatus(null);
                }
            }

            DrawDropArea(dropText, dropped => AddFbxObjects(targets, dropped, skipGenerated, setStatus));

            for (var index = 0; index < targets.Count; index++)
            {
                using (new EditorGUILayout.HorizontalScope(EditorStyles.helpBox))
                {
                    EditorGUILayout.LabelField($"#{index + 1}", GUILayout.Width(28f));
                    var replacement = EditorGUILayout.ObjectField(targets[index], typeof(Object), false);
                    if (replacement != targets[index] &&
                        (replacement == null || MocapPipelineSourceUtility.IsFbxModel(replacement)))
                        targets[index] = replacement;
                    if (GUILayout.Button("×", GUILayout.Width(26f)))
                    {
                        targets.RemoveAt(index);
                        GUIUtility.ExitGUI();
                    }
                }
            }
        }

        private void AddFbxObjects(
            List<Object> targets,
            IEnumerable<Object> objects,
            bool skipGenerated,
            Action<string> setStatus)
        {
            var paths = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
            var folders = new List<string>();
            foreach (var candidate in objects ?? Enumerable.Empty<Object>())
            {
                if (candidate == null)
                    continue;
                var path = AssetDatabase.GetAssetPath(candidate);
                if (AssetDatabase.IsValidFolder(path))
                    folders.Add(path);
                else if (MocapPipelineSourceUtility.IsFbxModel(candidate))
                    paths.Add(path);
            }

            foreach (var path in CollectFbxPaths(folders, skipGenerated))
                paths.Add(path);
            AddFbxPaths(targets, paths, setStatus);
        }

        private static IEnumerable<string> CollectFbxPaths(IEnumerable<string> folders, bool skipGenerated)
        {
            var folderArray = folders.Where(AssetDatabase.IsValidFolder).ToArray();
            if (folderArray.Length == 0)
                return Enumerable.Empty<string>();

            return AssetDatabase.FindAssets("t:Model", folderArray)
                .Select(AssetDatabase.GUIDToAssetPath)
                .Where(path => path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                .Where(path => !skipGenerated || !OptiTrackMotionBindingService.IsGeneratedAsset(path))
                .Where(path => AssetImporter.GetAtPath(path) is ModelImporter);
        }

        private void AddFbxPaths(List<Object> targets, IEnumerable<string> paths, Action<string> setStatus)
        {
            var existing = new HashSet<string>(
                targets.Where(target => target != null).Select(AssetDatabase.GetAssetPath),
                StringComparer.OrdinalIgnoreCase);
            var added = 0;
            foreach (var path in paths)
            {
                if (!existing.Add(path))
                    continue;
                var asset = AssetDatabase.LoadMainAssetAtPath(path);
                if (asset == null)
                    continue;
                targets.Add(asset);
                added++;
            }

            setStatus(added > 0 ? $"{added}개 추가됨." : "추가 가능한 FBX가 없습니다 (이미 추가됐거나 FBX가 아님).");
            if (added > 0)
                ShowNotification(new GUIContent($"FBX {added}개를 추가했습니다."));
        }

        private void ApplySetup()
        {
            var success = 0;
            var failed = 0;
            AssetDatabase.StartAssetEditing();
            try
            {
                for (var index = 0; index < setupTargets.Count; index++)
                {
                    var target = setupTargets[index];
                    if (target == null)
                    {
                        failed++;
                        continue;
                    }

                    EditorUtility.DisplayProgressBar(
                        "FBX 애니메이션 설정 적용",
                        $"{target.name} 처리 중...",
                        index / (float)setupTargets.Count);
                    if (FbxAnimationSetupService.Apply(AssetDatabase.GetAssetPath(target)))
                        success++;
                    else
                        failed++;
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.StopAssetEditing();
                AssetDatabase.SaveAssets();
            }

            setupStatus = failed == 0
                ? $"완료: {success}개 처리됨."
                : $"완료: {success}개 성공, {failed}개 실패.";
        }

        private void RunBindingOnly()
        {
            // NOTE: binding relies on synchronous re-imports mid-process (Import
            // Animation off → capture T-pose → on), so it must NOT run inside
            // AssetDatabase.StartAssetEditing (which defers imports).
            var ok = 0;
            var failed = 0;
            var notes = new List<string>();
            var sourcePaths = bindingTargets
                .Select(target => target != null ? AssetDatabase.GetAssetPath(target) : null)
                .ToList();
            var namePlan = OptiTrackMotionBindingService.PlanAnimationNames(
                sourcePaths,
                bindingFormat,
                out var planNotes);
            notes.AddRange(planNotes);

            try
            {
                for (var index = 0; index < bindingTargets.Count; index++)
                {
                    var path = sourcePaths[index];
                    if (string.IsNullOrEmpty(path))
                    {
                        failed++;
                        notes.Add($"{index + 1}번 항목: 에셋을 찾을 수 없어 건너뜁니다.");
                        continue;
                    }

                    EditorUtility.DisplayProgressBar(
                        $"{bindingFormat.GetLabel()} 모션 바인딩",
                        $"{Path.GetFileName(path)} 처리 중...",
                        index / (float)bindingTargets.Count);
                    try
                    {
                        namePlan.TryGetValue(path, out var plannedName);
                        var result = OptiTrackMotionBindingService.Process(
                            path,
                            ExistingMotionAssetPolicy.Disambiguate,
                            plannedName,
                            bindingFormat);
                        if (result.Succeeded)
                            ok++;
                        else
                            failed++;
                        if (!string.IsNullOrEmpty(result.Note))
                            notes.Add(result.Note);
                    }
                    catch (Exception exception)
                    {
                        failed++;
                        notes.Add($"{path}: 예외 - {exception.Message}");
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.SaveAssets();
                AssetDatabase.Refresh();
            }

            var summary = failed == 0 ? $"완료: {ok}개 처리됨." : $"완료: {ok}개 성공, {failed}개 실패.";
            if (notes.Count > 0)
                summary += "\n" + string.Join("\n", notes);
            bindingStatus = summary;
        }
    }
}
