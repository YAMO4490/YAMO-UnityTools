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
        [SerializeField] private ForearmHingeAxis hingeAxis = ForearmHingeAxis.Z;
        [SerializeField] private ExistingMotionAssetPolicy existingBindingPolicy = ExistingMotionAssetPolicy.Fail;
        [SerializeField] private bool recordBlendShapes = true;
        [SerializeField] private bool clampedTangents = true;
        [SerializeField] private MotionFbxCurveCompression compression = MotionFbxCurveCompression.Disabled;
        [SerializeField] private bool exportGeometry;
        [SerializeField] private bool exportUnrendered = true;
        [SerializeField] private bool keepInstances = true;
        [SerializeField] private bool embedTextures;
        [SerializeField] private bool createFbxBackup = true;
        [SerializeField] private bool continueOnError = true;
        [SerializeField] private bool revealAfterExport = true;
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
                "원본 FBX를 _Backup으로 보존한 뒤 바인딩, 메모리상 Forearm Hinge Bake, Max Z-up FBX 출력을 실행합니다. " +
                "Hinge 중간 클립은 에셋으로 저장하지 않습니다. 현재 통합 모드는 Edit Mode Hinge Bake입니다.",
                MessageType.Info);

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
                EditorGUILayout.LabelField($"3. 모캡 FBX 큐 ({items.Count})", EditorStyles.boldLabel);
                if (GUILayout.Button("선택 FBX 추가", GUILayout.Width(105f)))
                    AddObjects(Selection.objects);
                if (GUILayout.Button("빈 항목", GUILayout.Width(70f)))
                    items.Add(new MocapPipelineItem());
                if (GUILayout.Button("전체 삭제", GUILayout.Width(70f)))
                    items.Clear();
            }

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
                            new GUIContent("출력 이름", "비워두면 원본 FBX 이름을 사용합니다."),
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
            hingeAxis = (ForearmHingeAxis)EditorGUILayout.EnumPopup("Forearm Hinge Axis", hingeAxis);
            existingBindingPolicy = (ExistingMotionAssetPolicy)EditorGUILayout.EnumPopup(
                new GUIContent("기존 Motion/_T 충돌", "Fail은 기존 에셋을 보호하고, Overwrite는 기존 도구와 동일하게 교체합니다."),
                existingBindingPolicy);
            continueOnError = EditorGUILayout.Toggle("오류 시 다음 항목 계속", continueOnError);

            if (sampleRate != 60)
                EditorGUILayout.HelpBox("현재 작업 기준은 60fps입니다. 특별한 이유가 없다면 60을 사용하세요.", MessageType.Warning);
            if (existingBindingPolicy == ExistingMotionAssetPolicy.Overwrite)
                EditorGUILayout.HelpBox("기존 Motion FBX와 _T FBX가 삭제 후 교체될 수 있습니다.", MessageType.Warning);

            showAdvanced = EditorGUILayout.Foldout(showAdvanced, "FBX 고급 옵션", true);
            if (!showAdvanced)
                return;

            recordBlendShapes = EditorGUILayout.Toggle("BlendShape 기록", recordBlendShapes);
            compression = (MotionFbxCurveCompression)EditorGUILayout.EnumPopup("키 압축", compression);
            clampedTangents = EditorGUILayout.Toggle("Clamped Tangents", clampedTangents);
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
                if (GUILayout.Button("전체 파이프라인 실행", GUILayout.Height(44f)))
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
                HingeAxis = hingeAxis,
                ExistingBindingPolicy = existingBindingPolicy,
                RecordBlendShapes = recordBlendShapes,
                ClampedTangents = clampedTangents,
                Compression = compression,
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
                    $"성공: {succeeded}개\n실패: {failed}개\n\n원본 백업: 입력 FBX 옆 *_Backup.fbx\nFBX: {fbxOutputDirectory}",
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
                   items.Any(item => item != null && item.Enabled && IsFbx(item.SourceFbx)) &&
                   !EditorApplication.isPlayingOrWillChangePlaymode &&
                   !EditorApplication.isCompiling;
        }

        private void DrawDropArea()
        {
            var rect = GUILayoutUtility.GetRect(0f, 42f, GUILayout.ExpandWidth(true));
            GUI.Box(rect, "OptiTrack FBX를 여기에 드래그", EditorStyles.helpBox);
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

        private void AddObjects(IEnumerable<Object> objects)
        {
            var existingPaths = new HashSet<string>(
                items.Where(item => item?.SourceFbx != null)
                    .Select(item => AssetDatabase.GetAssetPath(item.SourceFbx)),
                StringComparer.OrdinalIgnoreCase);

            foreach (var candidate in objects ?? Enumerable.Empty<Object>())
            {
                if (!IsFbx(candidate))
                    continue;
                var path = AssetDatabase.GetAssetPath(candidate);
                if (!existingPaths.Add(path))
                    continue;
                items.Add(new MocapPipelineItem
                {
                    SourceFbx = candidate
                });
            }
        }

        private static bool IsFbx(Object candidate)
        {
            if (candidate == null)
                return false;
            var path = AssetDatabase.GetAssetPath(candidate);
            return path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) &&
                   AssetImporter.GetAtPath(path) is ModelImporter;
        }
    }
}
