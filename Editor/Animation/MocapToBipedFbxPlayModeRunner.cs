using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// Persists a prepared mocap batch across the Play Mode domain reload, then
    /// resumes in Edit Mode to build Generic clips and export Max-compatible FBX.
    /// </summary>
    [InitializeOnLoad]
    public static class MocapToBipedFbxPlayModeRunner
    {
        private const string PendingKey = "YAMO.MocapPipeline.PlayMode.Pending";
        private const string StateKey = "YAMO.MocapPipeline.PlayMode.State";

        [Serializable]
        private sealed class RunnerState
        {
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
            public string SourceName;
            public string OutputName;
            public string StateName;
            public string ResultPath;
            public float StartTime;
            public float Duration;
        }

        public static bool IsRunning => SessionState.GetBool(PendingKey, false);

        static MocapToBipedFbxPlayModeRunner()
        {
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }

        public static void Start(
            MocapPipelineSettings settings,
            IReadOnlyList<MocapPipelineItem> items,
            bool revealAfterExport)
        {
            ValidateStart(settings, items);
            if (IsRunning)
                throw new InvalidOperationException("이미 Play Mode Mocap 파이프라인이 실행 중입니다.");

            var targetAnimator = settings.TargetAnimator;
            var token = Guid.NewGuid().ToString("N");
            var state = CreateState(settings, targetAnimator, token, revealAfterExport);
            var preparedClips = new List<AnimationClip>();
            AnimatorController temporaryController = null;
            YAMO.UnityTools.ForearmHingeBatchRecorder recorder = null;

            try
            {
                PrepareItems(settings, items, state, preparedClips);
                if (state.Items.Count == 0)
                {
                    ShowNoPreparedItems(state.PreparationErrors);
                    return;
                }

                temporaryController = BuildController(
                    state.TemporaryControllerPath,
                    state.Items,
                    preparedClips);
                targetAnimator.runtimeAnimatorController = temporaryController;

                if (targetAnimator.GetComponent<YAMO.UnityTools.ForearmHingeBatchRecorder>() != null ||
                    targetAnimator.GetComponent<YAMO.UnityTools.ForearmHingeRecorder>() != null)
                {
                    throw new InvalidOperationException("대상 Biped에 Forearm Hinge Recorder가 이미 존재합니다.");
                }

                recorder = targetAnimator.gameObject.AddComponent<YAMO.UnityTools.ForearmHingeBatchRecorder>();
                recorder.sampleRate = settings.SampleRate;
                recorder.enableHingeCorrection = settings.EnableHingeCorrection;
                recorder.hingeAxisIndex = (int)settings.HingeAxis;
                recorder.handRotationCompensation = settings.HandRotationCompensation;
                recorder.stateNames = state.Items.Select(item => item.StateName).ToArray();
                recorder.resultPaths = state.Items.Select(item => item.ResultPath).ToArray();

                SessionState.SetString(StateKey, JsonUtility.ToJson(state));
                SessionState.SetBool(PendingKey, true);
                AssetDatabase.SaveAssets();
                EditorUtility.ClearProgressBar();

                Debug.Log(
                    $"[Mocap Pipeline] Play Mode Pose Bake 시작: {state.Items.Count}개, " +
                    $"{state.SampleRate}fps, Hinge={(state.EnableHingeCorrection ? "On" : "Off")}, " +
                    $"Hand Compensation={state.HandRotationCompensation:0.##}");
                EditorApplication.isPlaying = true;
            }
            catch
            {
                if (recorder != null)
                    Object.DestroyImmediate(recorder);
                targetAnimator.runtimeAnimatorController = ResolveOriginalController(state);
                if (temporaryController != null ||
                    !string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(state.TemporaryControllerPath)))
                    AssetDatabase.DeleteAsset(state.TemporaryControllerPath);
                TryDeleteResultDirectory(state.ResultDirectory);
                SessionState.EraseBool(PendingKey);
                SessionState.EraseString(StateKey);
                throw;
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }
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
            var resultDirectory = Path.Combine(projectRoot, "Temp", "YamoMocapPipeline", token);
            return new RunnerState
            {
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

        private static void PrepareItems(
            MocapPipelineSettings settings,
            IReadOnlyList<MocapPipelineItem> items,
            RunnerState state,
            ICollection<AnimationClip> preparedClips)
        {
            Directory.CreateDirectory(state.ResultDirectory);
            Directory.CreateDirectory(state.FbxOutputDirectory);
            var enabledItems = items.Where(item => item != null && item.Enabled).ToList();
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Binding names its output after the FBX take, so per-actor exports of one
            // take all resolve to the same target file. That is worse here than in the
            // one-shot tool: every item is bound up front and its clip is held until
            // Play Mode ends, so a later item deleting an earlier item's motion asset
            // leaves a dangling clip in the temp controller. Plan the names first.
            var bindingNames = OptiTrackMotionBindingService.PlanAnimationNames(
                enabledItems.Select(item => AssetDatabase.GetAssetPath(item.SourceFbx)),
                out var bindingNameNotes);
            foreach (var note in bindingNameNotes)
                Debug.Log($"[MocapPipeline] {note}");

            for (var index = 0; index < enabledItems.Count; index++)
            {
                var item = enabledItems[index];
                try
                {
                    if (EditorUtility.DisplayCancelableProgressBar(
                            "Mocap → Biped FBX",
                            $"[{index + 1}/{enabledItems.Count}] 입력 모션 준비",
                            index / (float)Mathf.Max(1, enabledItems.Count)))
                        throw new OperationCanceledException("Mocap 파이프라인 준비가 취소되었습니다.");

                    var sourcePath = AssetDatabase.GetAssetPath(item.SourceFbx);
                    AnimationClip sourceClip;
                    string fallbackOutputName;
                    if (MocapPipelineSourceUtility.TryGetStandaloneAnimationClip(item.SourceFbx, out sourceClip))
                    {
                        fallbackOutputName = Path.GetFileNameWithoutExtension(sourcePath);
                    }
                    else
                    {
                        if (!MocapPipelineSourceUtility.IsFbxModel(item.SourceFbx))
                            throw new InvalidOperationException(
                                $"{item.SourceFbx?.name}: 지원되는 FBX 또는 Anim 에셋이 아닙니다.");

                        OptiTrackMotionBindingService.EnsureSourceBackup(sourcePath, out _);
                        bindingNames.TryGetValue(sourcePath, out var plannedBindingName);
                        var binding = OptiTrackMotionBindingService.Process(
                            sourcePath,
                            settings.ExistingBindingPolicy,
                            plannedBindingName);
                        if (!binding.Succeeded || binding.AnimationClip == null)
                            throw new InvalidOperationException(binding.Note ?? "OptiTrack 바인딩에 실패했습니다.");

                        sourceClip = binding.AnimationClip;
                        fallbackOutputName = binding.AnimationName;
                    }

                    ValidateRange(item, sourceClip);
                    var outputName = MocapPipelineOutputNaming.MakeUnique(
                        item, fallbackOutputName, sourcePath, usedNames);
                    var preparedIndex = state.Items.Count;
                    state.Items.Add(new PreparedItem
                    {
                        SourceName = sourceClip.name,
                        OutputName = outputName,
                        StateName = $"YAMO_Mocap_{preparedIndex:D4}",
                        ResultPath = Path.Combine(state.ResultDirectory, $"{preparedIndex:D4}.bin"),
                        StartTime = item.StartTime,
                        Duration = item.Duration > 0f
                            ? item.Duration
                            : sourceClip.length - item.StartTime
                    });
                    preparedClips.Add(sourceClip);
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    state.PreparationErrors.Add($"{item.SourceFbx?.name ?? $"#{index + 1}"}: {exception.Message}");
                    Debug.LogException(exception);
                    if (!settings.ContinueOnError)
                        break;
                }
            }
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
            AssetDatabase.SaveAssets();
            return controller;
        }

        private static void OnPlayModeStateChanged(PlayModeStateChange change)
        {
            if (change != PlayModeStateChange.EnteredEditMode || !IsRunning)
                return;
            EditorApplication.delayCall += FinishAfterPlayMode;
        }

        private static void FinishAfterPlayMode()
        {
            var json = SessionState.GetString(StateKey, string.Empty);
            SessionState.EraseBool(PendingKey);
            SessionState.EraseString(StateKey);
            if (string.IsNullOrEmpty(json))
            {
                Debug.LogError("[Mocap Pipeline] Play Mode 복귀 상태를 찾을 수 없습니다.");
                return;
            }

            var state = JsonUtility.FromJson<RunnerState>(json);
            var targetAnimator = ResolveTargetAnimator(state);
            var errors = new List<string>(state.PreparationErrors ?? new List<string>());
            var succeeded = 0;
            try
            {
                CleanupSceneAndController(state, targetAnimator);
                if (targetAnimator == null)
                    throw new InvalidOperationException("Play Mode 복귀 후 대상 Biped Animator를 찾을 수 없습니다.");

                for (var index = 0; index < state.Items.Count; index++)
                {
                    var item = state.Items[index];
                    ForearmHingeBakeResult hingeResult = null;
                    try
                    {
                        EditorUtility.DisplayProgressBar(
                            "Mocap → Biped FBX",
                            $"[{index + 1}/{state.Items.Count}] {item.OutputName} 최종 FBX Export",
                            index / (float)Mathf.Max(1, state.Items.Count));
                        hingeResult = ForearmHingeBakeService.LoadPlayModeResult(
                            item.ResultPath,
                            state.SampleRate,
                            item.OutputName + (state.EnableHingeCorrection ? "_hinged" : "_baked"));

                        BipedFbxExportService.Export(
                            new BipedFbxExportSettings
                            {
                                TargetRoot = targetAnimator.gameObject,
                                Clip = hingeResult.Clip,
                                OutputPath = Path.Combine(state.FbxOutputDirectory, item.OutputName + ".fbx"),
                                StartTime = item.StartTime,
                                Duration = item.Duration,
                                FrameRate = state.SampleRate,
                                RecordBlendShapes = state.RecordBlendShapes,
                                ClampedTangents = state.ClampedTangents,
                                Compression = (MotionFbxCurveCompression)state.Compression,
                                ExportGeometry = state.ExportGeometry,
                                ExportUnrendered = state.ExportUnrendered,
                                UseCompatibleNames = false,
                                KeepInstances = state.KeepInstances,
                                EmbedTextures = state.EmbedTextures,
                                CreateBackup = state.CreateFbxBackup
                            },
                            (message, progress) =>
                            {
                                EditorUtility.DisplayProgressBar(
                                    "Mocap → Biped FBX",
                                    $"[{index + 1}/{state.Items.Count}] {message}",
                                    (index + progress) / Mathf.Max(1, state.Items.Count));
                                return false;
                            });
                        succeeded++;
                    }
                    catch (Exception exception)
                    {
                        errors.Add($"{item.OutputName}: {exception.Message}");
                        Debug.LogException(exception);
                        if (!state.ContinueOnError)
                            break;
                    }
                    finally
                    {
                        if (hingeResult?.Clip != null)
                            Object.DestroyImmediate(hingeResult.Clip);
                    }
                }
            }
            catch (Exception exception)
            {
                errors.Add(exception.Message);
                Debug.LogException(exception);
            }
            finally
            {
                CleanupSceneAndController(state, targetAnimator);
                TryDeleteResultDirectory(state.ResultDirectory);
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
            }

            if (state.RevealAfterExport && succeeded > 0)
                EditorUtility.RevealInFinder(state.FbxOutputDirectory);
            var details = errors.Count == 0
                ? string.Empty
                : "\n\n오류:\n" + string.Join("\n", errors.Take(8));
            EditorUtility.DisplayDialog(
                "Mocap 파이프라인 완료 (Play Mode)",
                $"성공: {succeeded}개\n실패: {errors.Count}개\nFBX: {state.FbxOutputDirectory}{details}",
                "확인");
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
            var allowedRoot = Path.GetFullPath(Path.Combine(projectRoot, "Temp", "YamoMocapPipeline"));
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

        private static void ShowNoPreparedItems(IReadOnlyList<string> errors)
        {
            var detail = errors == null || errors.Count == 0
                ? "처리 가능한 항목이 없습니다."
                : string.Join("\n", errors.Take(8));
            EditorUtility.DisplayDialog("Mocap 파이프라인 실패", detail, "확인");
        }
    }
}
