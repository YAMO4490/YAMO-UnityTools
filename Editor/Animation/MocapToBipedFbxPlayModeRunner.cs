using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor
{
    [InitializeOnLoad]
    public static class MocapToBipedFbxPlayModeRunner
    {
        private const string PendingKey = "YAMO.MocapPipeline.PlayMode.Pending";
        private const string StateKey = "YAMO.MocapPipeline.PlayMode.State";
        private const string CancelKey = "YAMO.MocapPipeline.Cancel";
        private const string StatusKey = "YAMO.MocapPipeline.Status";
        private static bool scheduled;
        [Serializable]
        private sealed class RunnerState
        {
            public int CurrentIndex;
            public MocapHingeBakeMode BakeMode;
            public int Succeeded;
            public bool AwaitingPlay;
            public MocapSourceFormat SourceFormat;
            public ExistingMotionAssetPolicy BindingPolicy;
            public string TargetGlobalId;
            public int TargetInstanceId;
            public string OriginalControllerPath;
            public int OriginalControllerInstanceId;
            public string TemporaryControllerPath;
            public string ResultDirectory;
            public string FbxOutputDirectory;
            public int SampleRate;
            public bool EnableHingeCorrection;
            public int HingeAxis;
            public float HandRotationCompensation;
            public int Compression;
            public bool RecordBlendShapes;
            public bool ClampedTangents;
            public bool ExportGeometry;
            public bool ExportUnrendered;
            public bool KeepInstances;
            public bool EmbedTextures;
            public bool CreateFbxBackup;
            public bool ContinueOnError;
            public bool RevealAfterExport;
            public List<PreparedItem> Items = new List<PreparedItem>();
            public List<string> PreparationErrors = new List<string>();
        }

        [Serializable]
        private sealed class PreparedItem
        {
            public string SourcePath;
            public string SourceGuid;
            public string BindingName;
            public string RequestedOutputName;
            public string SourceName;
            public string OutputName;
            public string StateName;
            public string ResultPath;
            public float StartTime;
            public float Duration;
        }


        public static bool IsRunning => SessionState.GetBool(PendingKey, false);
        public static string Status => SessionState.GetString(StatusKey, "대기 중");
        static MocapToBipedFbxPlayModeRunner()
        {
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
            EditorApplication.delayCall += () =>
            {
                if (!IsRunning || EditorApplication.isPlayingOrWillChangePlaymode) return;
                var state = ReadState();
                if (state != null && state.AwaitingPlay) ScheduleFinish();
                else if (state != null) OnFailure(state, new OperationCanceledException("스크립트 리로드로 Mocap 작업이 중단되었습니다."));
            };
        }
        public static void Start(MocapPipelineSettings settings, IReadOnlyList<MocapPipelineItem> items, bool revealAfterExport)
        {
            ValidateStart(settings, items);
            if (IsRunning) throw new InvalidOperationException("Mocap 작업이 이미 실행 중입니다.");
            var target = settings.TargetAnimator;
            if (target.GetComponent<YAMO.UnityTools.ForearmHingeBatchRecorder>() != null ||
                target.GetComponent<YAMO.UnityTools.ForearmHingeRecorder>() != null)
                throw new InvalidOperationException("대상에 Recorder가 이미 있습니다.");
            var state = CreateState(settings, target, Guid.NewGuid().ToString("N"), revealAfterExport);
            state.BakeMode = settings.HingeBakeMode;
            Directory.CreateDirectory(state.ResultDirectory); Directory.CreateDirectory(state.FbxOutputDirectory);
            var enabled = items.Where(x => x != null && x.Enabled).ToList();
            var names = OptiTrackMotionBindingService.PlanAnimationNames(enabled.Select(x => AssetDatabase.GetAssetPath(x.SourceFbx)), settings.SourceFormat, out var notes);
            foreach (var note in notes) Debug.Log("[Mocap Pipeline] " + note);
            foreach (var item in enabled)
            {
                var path = AssetDatabase.GetAssetPath(item.SourceFbx);
                names.TryGetValue(path, out var bindingName);
                var index = state.Items.Count;
                state.Items.Add(new PreparedItem { SourcePath = path, SourceGuid = AssetDatabase.AssetPathToGUID(path), BindingName = bindingName,
                    RequestedOutputName = item.OutputName, StateName = "YAMO_Mocap_Current", ResultPath = Path.Combine(state.ResultDirectory, $"{index:D4}.bin"),
                    StartTime = item.StartTime, Duration = item.Duration });
            }
            SessionState.SetBool(CancelKey, false); SessionState.SetBool(PendingKey, true); Save(state);
            // Return to the window first so it can drop its queued asset references.
            EditorApplication.delayCall += () => StartPreparation(state);
        }
        public static void Cancel()
        {
            if (!IsRunning) return;
            SessionState.SetBool(CancelKey, true);
            if (EditorApplication.isPlaying) EditorApplication.isPlaying = false;
        }
        private static void StartPreparation(RunnerState state)
        {
            MocapMemoryWork.Run(PrepareCurrent(state), exception => OnFailure(state, exception));
        }
        private static IEnumerator PrepareCurrent(RunnerState state)
        {
            if (state.CurrentIndex >= state.Items.Count) { Finish(state, false); yield break; }
            yield return MocapMemoryWork.Reclaim("다음 항목 준비 전");
            CheckCancelled();
            SetStatus(state, "입력 모션 준비", 0f);
            var item = state.Items[state.CurrentIndex];
            var path = AssetDatabase.GUIDToAssetPath(item.SourceGuid);
            if (!string.IsNullOrEmpty(path)) item.SourcePath = path;
            var inputSize = new FileInfo(Path.GetFullPath(Path.Combine(Application.dataPath, "..", item.SourcePath))).Length;
            MocapMemoryWork.RequireHeadroom("입력 모션 준비", checked(inputSize * 12));
            var source = AssetDatabase.LoadMainAssetAtPath(item.SourcePath);
            AnimationClip clip; string fallback;
            if (MocapPipelineSourceUtility.TryGetStandaloneAnimationClip(source, out clip)) fallback = Path.GetFileNameWithoutExtension(item.SourcePath);
            else
            {
                if (!MocapPipelineSourceUtility.IsFbxModel(source)) throw new InvalidOperationException("지원되는 모션 입력이 아닙니다: " + item.SourcePath);
                var error = OptiTrackMotionBindingService.ValidateSourceFormat(item.SourcePath, state.SourceFormat);
                if (error != null) throw new InvalidOperationException(error);
                OptiTrackMotionBindingService.EnsureSourceBackup(item.SourcePath, out _);
                var binding = OptiTrackMotionBindingService.Process(item.SourcePath, state.BindingPolicy, item.BindingName, state.SourceFormat);
                if (!binding.Succeeded || binding.AnimationClip == null) throw new InvalidOperationException(binding.Note);
                clip = binding.AnimationClip; fallback = binding.AnimationName;
            }
            CheckCancelled();
            var input = new MocapPipelineItem { StartTime = item.StartTime, Duration = item.Duration, OutputName = item.RequestedOutputName };
            ValidateRange(input, clip);
            var used = new HashSet<string>(state.Items.Take(state.CurrentIndex).Select(x => x.OutputName).Where(x => !string.IsNullOrEmpty(x)), StringComparer.OrdinalIgnoreCase);
            item.OutputName = MocapPipelineOutputNaming.MakeUnique(input, fallback, item.SourcePath, used);
            item.SourceName = clip.name; item.Duration = item.Duration > 0f ? item.Duration : clip.length - item.StartTime;
            var frames = checked((int)Math.Ceiling((double)item.Duration * state.SampleRate) + 1);
            var boneCount = Math.Max(1, (int)HumanBodyBones.LastBone);
            MocapMemoryWork.RequireHeadroom("녹화/커브 생성", checked((long)frames * boneCount * 7 * 32 * 2));
            var rawBytes = checked((long)frames * boneCount * 28);
            if (new DriveInfo(Path.GetPathRoot(state.ResultDirectory)).AvailableFreeSpace < rawBytes * 2 + 64L * 1024 * 1024)
                throw new IOException("녹화 및 변환용 임시 디스크 공간이 부족합니다.");
            var target = ResolveTargetAnimator(state);
            if (target == null) throw new InvalidOperationException("대상 Animator를 찾을 수 없습니다.");
            if (state.BakeMode == MocapHingeBakeMode.EditMode)
            {
                yield return MocapEditPoseRecorder.Record(target, clip, item.ResultPath, item.StartTime, item.Duration,
                    state.SampleRate, state.EnableHingeCorrection, state.HingeAxis, state.HandRotationCompensation,
                    (message, value) => Progress(state, message, value * 0.2f));
                source = null; clip = null;
                yield return FinishCurrent(state);
                yield break;
            }
            target.runtimeAnimatorController = BuildController(state.TemporaryControllerPath, new[] { item }, new[] { clip });
            var recorder = target.gameObject.AddComponent<YAMO.UnityTools.ForearmHingeBatchRecorder>();
            recorder.sampleRate = state.SampleRate; recorder.enableHingeCorrection = state.EnableHingeCorrection;
            recorder.hingeAxisIndex = state.HingeAxis; recorder.handRotationCompensation = state.HandRotationCompensation;
            recorder.stateNames = new[] { item.StateName }; recorder.resultPaths = new[] { item.ResultPath };
            recorder.startTime = item.StartTime; recorder.duration = item.Duration;
            state.AwaitingPlay = true; Save(state); SetStatus(state, "Play Mode 디스크 녹화", 0.1f);
            EditorUtility.ClearProgressBar();
            source = null; clip = null;
            EditorApplication.isPlaying = true;
        }
        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change == PlayModeStateChange.EnteredEditMode && IsRunning) ScheduleFinish();
        }
        private static void ScheduleFinish()
        {
            if (scheduled) return; scheduled = true;
            EditorApplication.delayCall += () =>
            {
                scheduled = false;
                if (!IsRunning || EditorApplication.isPlayingOrWillChangePlaymode) return;
                var state = ReadState();
                if (state == null) { SessionState.EraseBool(PendingKey); return; }
                MocapMemoryWork.Run(FinishCurrent(state), exception => OnFailure(state, exception));
            };
        }
        private static IEnumerator FinishCurrent(RunnerState state)
        {
            state.AwaitingPlay = false; Save(state);
            var target = ResolveTargetAnimator(state);
            CleanupSceneAndController(state, target);
            if (target == null) throw new InvalidOperationException("대상 Animator를 찾을 수 없습니다.");
            CheckCancelled();
            yield return MocapMemoryWork.Reclaim("녹화 후 컨트롤러 정리");
            var item = state.Items[state.CurrentIndex];
            ForearmHingeBakeResult result = null;
            if (File.Exists(item.ResultPath + ".error.txt"))
                throw new IOException("녹화 단계 실패: " + File.ReadAllText(item.ResultPath + ".error.txt"));
            if (!File.Exists(item.ResultPath))
                throw new OperationCanceledException("녹화가 완료되기 전에 Play Mode가 종료되어 대기열을 취소했습니다.");
            try
            {
                yield return MocapPoseClipBuilder.Build(item.ResultPath, item.OutputName + (state.EnableHingeCorrection ? "_hinged" : "_baked"),
                    (message, value) => Progress(state, message, 0.2f + value * 0.3f), value => result = value);
                yield return null;
                yield return BipedFbxExportStages.Export(new BipedFbxExportSettings {
                    TargetRoot = target.gameObject, Clip = result.Clip,
                    OutputPath = Path.Combine(state.FbxOutputDirectory, item.OutputName + ".fbx"),
                    StartTime = 0f, Duration = result.Clip.length, FrameRate = state.SampleRate,
                    RecordBlendShapes = state.RecordBlendShapes, ClampedTangents = state.ClampedTangents,
                    Compression = (MotionFbxCurveCompression)state.Compression,
                    ExportGeometry = state.ExportGeometry, ExportUnrendered = state.ExportUnrendered,
                    KeepInstances = state.KeepInstances, EmbedTextures = state.EmbedTextures,
                    CreateBackup = state.CreateFbxBackup },
                    (message, value) => Progress(state, message, 0.5f + value * 0.5f), value => { state.Succeeded++; Save(state); });
            }
            finally { if (result?.Clip != null) Object.DestroyImmediate(result.Clip); }
            if (File.Exists(item.ResultPath)) File.Delete(item.ResultPath);
            state.CurrentIndex++; Save(state);
            yield return MocapMemoryWork.Reclaim("항목 완료 후 정리");
            MocapMemoryWork.ContinueWith(() => StartPreparation(state));
        }
        private static bool Progress(RunnerState state, string message, float value)
        {
            SetStatus(state, message, value);
            if (SessionState.GetBool(CancelKey, false)) return true;
            if (EditorUtility.DisplayCancelableProgressBar("Mocap → Biped FBX", Status, (state.CurrentIndex + value) / Math.Max(1, state.Items.Count)))
                SessionState.SetBool(CancelKey, true);
            return SessionState.GetBool(CancelKey, false);
        }
        private static void CheckCancelled()
        {
            if (SessionState.GetBool(CancelKey, false)) throw new OperationCanceledException("사용자가 Mocap 작업을 취소했습니다.");
        }
        private static void SetStatus(RunnerState state, string message, float progress)
        {
            SessionState.SetString(StatusKey, $"[{state.CurrentIndex + 1}/{state.Items.Count}] {message}");
        }
        private static void Save(RunnerState state)
        {
            var json = JsonUtility.ToJson(state);
            SessionState.SetString(StateKey, json);
            // A disk checkpoint also identifies completed outputs after a native crash.
            var path = Path.Combine(state.ResultDirectory, "queue-state.json");
            File.WriteAllText(path + ".new", json);
            if (File.Exists(path)) File.Replace(path + ".new", path, null); else File.Move(path + ".new", path);
        }
        private static RunnerState ReadState()
        {
            var json = SessionState.GetString(StateKey, string.Empty);
            return string.IsNullOrEmpty(json) ? null : JsonUtility.FromJson<RunnerState>(json);
        }
        private static void OnFailure(RunnerState state, Exception exception)
        {
            CleanupSceneAndController(state, ResolveTargetAnimator(state));
            state.AwaitingPlay = false;
            var item = state.CurrentIndex < state.Items.Count ? state.Items[state.CurrentIndex] : null;
            state.PreparationErrors.Add((item?.OutputName ?? item?.SourcePath ?? "작업") + ": " + exception.Message);
            Debug.LogException(exception);
            if (exception is OperationCanceledException || exception is OutOfMemoryException || exception is InsufficientMemoryException || !state.ContinueOnError)
            { Finish(state, exception is OperationCanceledException); return; }
            state.CurrentIndex++; Save(state);
            EditorApplication.delayCall += () => StartPreparation(state);
        }
        private static void Finish(RunnerState state, bool cancelled)
        {
            CleanupSceneAndController(state, ResolveTargetAnimator(state));
            SessionState.EraseBool(PendingKey); SessionState.EraseString(StateKey); SessionState.EraseBool(CancelKey);
            EditorUtility.ClearProgressBar();
            if (state.PreparationErrors.Count == 0 && !cancelled) TryDeleteResultDirectory(state.ResultDirectory);
            else Debug.LogWarning("[Mocap Pipeline] 중단/실패 항목의 녹화 파일과 상태 보관: " + state.ResultDirectory);
            AssetDatabase.Refresh();
            if (state.RevealAfterExport && state.Succeeded > 0) EditorUtility.RevealInFinder(state.FbxOutputDirectory);
            EditorUtility.DisplayDialog("Mocap 파이프라인 " + (cancelled ? "취소" : "완료"),
                $"성공: {state.Succeeded}개\n실패: {state.PreparationErrors.Count}개\nFBX: {state.FbxOutputDirectory}\n" + string.Join("\n", state.PreparationErrors.Take(8)), "확인");
        }
        private static RunnerState CreateState(
            MocapPipelineSettings settings,
            Animator targetAnimator,
            string token,
            bool revealAfterExport)
        {
            var globalId = GlobalObjectId.GetGlobalObjectIdSlow(targetAnimator.gameObject);
            var originalController = targetAnimator.runtimeAnimatorController;
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var resultDirectory = Path.Combine(projectRoot, "Library", "YamoMocapPipeline", token);
            return new RunnerState
            {
                SourceFormat = settings.SourceFormat,
                BindingPolicy = settings.ExistingBindingPolicy,
                TargetGlobalId = globalId.ToString(),
                TargetInstanceId = targetAnimator.gameObject.GetInstanceID(),
                OriginalControllerPath = AssetDatabase.GetAssetPath(originalController),
                OriginalControllerInstanceId = originalController != null ? originalController.GetInstanceID() : 0,
                TemporaryControllerPath = $"Assets/__YAMO_MocapPipeline_{token}.controller",
                ResultDirectory = resultDirectory,
                FbxOutputDirectory = Path.GetFullPath(settings.FbxOutputDirectory),
                SampleRate = settings.SampleRate,
                EnableHingeCorrection = settings.EnableHingeCorrection,
                HingeAxis = (int)settings.HingeAxis,
                HandRotationCompensation = Mathf.Clamp01(settings.HandRotationCompensation),
                Compression = (int)settings.Compression,
                RecordBlendShapes = settings.RecordBlendShapes,
                ClampedTangents = settings.ClampedTangents,
                ExportGeometry = settings.ExportGeometry,
                ExportUnrendered = settings.ExportUnrendered,
                KeepInstances = settings.KeepInstances,
                EmbedTextures = settings.EmbedTextures,
                CreateFbxBackup = settings.CreateFbxBackup,
                ContinueOnError = settings.ContinueOnError,
                RevealAfterExport = revealAfterExport
            };
        }

        private static AnimatorController BuildController(
            string path,
            IReadOnlyList<PreparedItem> items,
            IReadOnlyList<AnimationClip> clips)
        {
            if (!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(path)))
                AssetDatabase.DeleteAsset(path);

            var controller = AnimatorController.CreateAnimatorControllerAtPath(path);
            if (controller == null)
                throw new InvalidOperationException("Play Mode용 임시 AnimatorController 생성에 실패했습니다.");

            var stateMachine = controller.layers[0].stateMachine;
            for (var index = 0; index < items.Count; index++)
            {
                var animatorState = stateMachine.AddState(items[index].StateName);
                animatorState.motion = clips[index];
                animatorState.iKOnFeet = true;
                if (index == 0)
                    stateMachine.defaultState = animatorState;
            }
            EditorUtility.SetDirty(controller);
            AssetDatabase.SaveAssetIfDirty(controller);
            return controller;
        }

        private static Animator ResolveTargetAnimator(RunnerState state)
        {
            if (!string.IsNullOrEmpty(state.TargetGlobalId) &&
                GlobalObjectId.TryParse(state.TargetGlobalId, out var globalId))
            {
                var gameObject = GlobalObjectId.GlobalObjectIdentifierToObjectSlow(globalId) as GameObject;
                if (gameObject != null)
                    return gameObject.GetComponent<Animator>();
            }

            var fallback = EditorUtility.InstanceIDToObject(state.TargetInstanceId) as GameObject;
            return fallback != null ? fallback.GetComponent<Animator>() : null;
        }

        private static RuntimeAnimatorController ResolveOriginalController(RunnerState state)
        {
            if (!string.IsNullOrEmpty(state.OriginalControllerPath))
            {
                var controller = AssetDatabase.LoadAssetAtPath<RuntimeAnimatorController>(state.OriginalControllerPath);
                if (controller != null)
                    return controller;
            }
            return EditorUtility.InstanceIDToObject(state.OriginalControllerInstanceId) as RuntimeAnimatorController;
        }

        private static void CleanupSceneAndController(RunnerState state, Animator animator)
        {
            if (animator != null)
            {
                var recorder = animator.GetComponent<YAMO.UnityTools.ForearmHingeBatchRecorder>();
                if (recorder != null)
                    Object.DestroyImmediate(recorder);
                animator.runtimeAnimatorController = ResolveOriginalController(state);
            }

            if (!string.IsNullOrEmpty(state.TemporaryControllerPath) &&
                !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(state.TemporaryControllerPath)))
                AssetDatabase.DeleteAsset(state.TemporaryControllerPath);
        }

        private static void TryDeleteResultDirectory(string path)
        {
            if (string.IsNullOrEmpty(path) || !Directory.Exists(path))
                return;
            var projectRoot = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            var allowedRoot = Path.GetFullPath(Path.Combine(projectRoot, "Library", "YamoMocapPipeline"));
            var fullPath = Path.GetFullPath(path);
            if (!fullPath.StartsWith(allowedRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException($"임시 결과 폴더 범위를 벗어났습니다: {fullPath}");
            Directory.Delete(fullPath, true);
        }

        private static void ValidateStart(
            MocapPipelineSettings settings,
            IReadOnlyList<MocapPipelineItem> items)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (settings.TargetAnimator == null || settings.TargetAnimator.avatar == null ||
                !settings.TargetAnimator.avatar.isValid || !settings.TargetAnimator.avatar.isHuman)
                throw new InvalidOperationException("대상 Biped에 유효한 Humanoid Animator가 필요합니다.");
            if (settings.SampleRate <= 0)
                throw new InvalidOperationException("Sample Rate는 0보다 커야 합니다.");
            if (settings.HingeBakeMode == MocapHingeBakeMode.PlayMode && !settings.TargetAnimator.isActiveAndEnabled)
                throw new InvalidOperationException("Play Mode 녹화 대상 Animator를 활성화하세요.");
            if (string.IsNullOrWhiteSpace(settings.FbxOutputDirectory))
                throw new InvalidOperationException("FBX 출력 폴더를 지정하세요.");
            if (items == null || !items.Any(item => item != null && item.Enabled && item.SourceFbx != null))
                throw new InvalidOperationException("활성화된 모캡 FBX 또는 Anim이 없습니다.");
            if (EditorApplication.isPlayingOrWillChangePlaymode)
                throw new InvalidOperationException("Edit Mode에서 실행하세요.");
            if (EditorApplication.isCompiling)
                throw new InvalidOperationException("스크립트 컴파일이 끝난 뒤 실행하세요.");
            if (AnimationMode.InAnimationMode())
                throw new InvalidOperationException("Animation Preview를 끈 뒤 실행하세요.");
        }

        private static void ValidateRange(MocapPipelineItem item, AnimationClip clip)
        {
            if (item.StartTime < 0f || item.StartTime >= clip.length)
                throw new InvalidOperationException($"{clip.name}: 시작 시간이 클립 범위를 벗어났습니다.");
            var duration = item.Duration > 0f ? item.Duration : clip.length - item.StartTime;
            if (duration <= 0f || item.StartTime + duration > clip.length + 0.0001f)
                throw new InvalidOperationException($"{clip.name}: 출력 구간이 클립 범위를 벗어났습니다.");
        }

    }
}
