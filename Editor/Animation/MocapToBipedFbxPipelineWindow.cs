using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.ShortcutManagement;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor
{
    public sealed class MocapToBipedFbxPipelineWindow : EditorWindow
    {
        private const string FbxDirectoryKey = "YAMO.MocapPipeline.FbxDirectory";
        private static MocapToBipedFbxPipelineWindow instance;

        [SerializeField] private Animator targetAnimator;
        [SerializeField] private List<MocapPipelineItem> items = new List<MocapPipelineItem>();
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

        private void OnGUI()
        {
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition);
            EditorGUILayout.LabelField("OptiTrack → Forearm Hinge → 3ds Max FBX", EditorStyles.boldLabel);
            EditorGUILayout.HelpBox(
                "FBX 입력은 _Backup 보존 및 OptiTrack 바인딩 후 처리하고, Anim 입력은 클립을 바로 사용합니다. " +
                "이후 Forearm Hinge Bake와 Max Z-up FBX 출력을 실행합니다. " +
                (hingeBakeMode == MocapHingeBakeMode.PlayMode
                    ? "기본 Play Mode는 실제 Animator를 실행하여 발 IK와 Foot Stabilization을 반영합니다."
                    : "Edit Mode는 빠르게 샘플링하지만 런타임 Foot Stabilization은 반영하지 않습니다."),
                MessageType.Info);

            if (MocapToBipedFbxPlayModeRunner.IsRunning)
                EditorGUILayout.HelpBox("Play Mode Hinge 배치가 실행 중입니다. Edit Mode 복귀 후 FBX Export가 자동으로 이어집니다.", MessageType.Warning);

            DrawTarget();
            EditorGUILayout.Space(8f);
            DrawOutputs();
            EditorGUILayout.Space(8f);
            DrawQueue();
            EditorGUILayout.Space(8f);
            DrawOptions();
            EditorGUILayout.Space(12f);
            DrawRunButton();
            EditorGUILayout.EndScrollView();
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

        private void DrawQueue()
        {
            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField($"3. 모션 큐 ({items.Count})", EditorStyles.boldLabel);
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
                "폴더 추가 시 하위 폴더의 FBX/Anim도 포함",
                includeSubfolders);
            DrawDropArea();

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
                    ? $"전체 파이프라인 실행 ({modeLabel})"
                    : $"전체 파이프라인 실행 ({modeLabel} / Hinge 비활성화)";
                if (GUILayout.Button(label, GUILayout.Height(44f)))
                    RunPipeline();
            }
        }

        private void RunPipeline()
        {
            var settings = new MocapPipelineSettings
            {
                TargetAnimator = targetAnimator,
                FbxOutputDirectory = Path.GetFullPath(fbxOutputDirectory),
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
                   items.Any(item => item != null && item.Enabled && IsMotionSource(item.SourceFbx)) &&
                   !MocapToBipedFbxPlayModeRunner.IsRunning &&
                   !EditorApplication.isPlayingOrWillChangePlaymode &&
                   !EditorApplication.isCompiling;
        }

        private void DrawDropArea()
        {
            var rect = GUILayoutUtility.GetRect(0f, 42f, GUILayout.ExpandWidth(true));
            GUI.Box(rect, "OptiTrack FBX, Anim 또는 프로젝트 폴더를 여기에 드래그", EditorStyles.helpBox);
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
                AddObjects(DragAndDrop.objectReferences);
                currentEvent.Use();
            }
        }

        private void AddFolderFromPanel()
        {
            var selected = EditorUtility.OpenFolderPanel(
                "모캡 FBX/Anim 폴더 선택",
                Application.dataPath,
                string.Empty);
            if (string.IsNullOrEmpty(selected))
                return;

            var assetPath = FileUtil.GetProjectRelativePath(selected).Replace('\\', '/');
            if (string.IsNullOrEmpty(assetPath) || !AssetDatabase.IsValidFolder(assetPath))
            {
                EditorUtility.DisplayDialog(
                    "폴더를 추가할 수 없습니다",
                    "현재 Unity 프로젝트의 Assets 폴더 안에 있는 폴더를 선택하세요.",
                    "확인");
                return;
            }

            AddAssetFolders(new[] { assetPath });
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
                    if (!includeSubfolders &&
                        !string.Equals(
                            Path.GetDirectoryName(path)?.Replace('\\', '/'),
                            folderPath.TrimEnd('/'),
                            StringComparison.OrdinalIgnoreCase))
                    {
                        continue;
                    }

                    if (IsMotionPath(path))
                        destination.Add(path);
                }
            }
        }

        private int AddMotionPaths(IEnumerable<string> paths)
        {
            var existingPaths = new HashSet<string>(
                items.Where(item => item?.SourceFbx != null)
                    .Select(item => AssetDatabase.GetAssetPath(item.SourceFbx)),
                StringComparer.OrdinalIgnoreCase);

            var added = 0;
            foreach (var path in paths ?? Enumerable.Empty<string>())
            {
                if (!existingPaths.Add(path) || !IsMotionPath(path))
                    continue;

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

            if (added > 0)
                ShowNotification(new GUIContent($"모션 {added}개를 큐에 추가했습니다."));
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
    }
}
