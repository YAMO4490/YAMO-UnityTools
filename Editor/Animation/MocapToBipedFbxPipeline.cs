using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor
{
    public enum MocapHingeBakeMode
    {
        PlayMode,
        EditMode
    }

    public enum MocapPipelineStage
    {
        Pending,
        BackingUpSource,
        Binding,
        HingeBake,
        ExportingFbx,
        Completed,
        Failed
    }

    [Serializable]
    public sealed class MocapPipelineItem
    {
        public bool Enabled = true;
        public Object SourceFbx;
        [Min(0f)] public float StartTime;
        [Min(0f)] public float Duration;
        public string OutputName;
    }

    public sealed class MocapPipelineSettings
    {
        public Animator TargetAnimator { get; set; }
        public string FbxOutputDirectory { get; set; }
        public int SampleRate { get; set; } = 60;
        public MocapHingeBakeMode HingeBakeMode { get; set; } = MocapHingeBakeMode.PlayMode;
        public ForearmHingeAxis HingeAxis { get; set; } = ForearmHingeAxis.Z;
        public ExistingMotionAssetPolicy ExistingBindingPolicy { get; set; } = ExistingMotionAssetPolicy.Fail;
        public bool RecordBlendShapes { get; set; } = true;
        public bool ClampedTangents { get; set; } = true;
        public MotionFbxCurveCompression Compression { get; set; } = MotionFbxCurveCompression.Disabled;
        public bool ExportGeometry { get; set; }
        public bool ExportUnrendered { get; set; } = true;
        public bool KeepInstances { get; set; } = true;
        public bool EmbedTextures { get; set; }
        public bool CreateFbxBackup { get; set; } = true;
        public bool ContinueOnError { get; set; } = true;
    }

    public sealed class MocapPipelineResult
    {
        public MocapPipelineItem Item { get; internal set; }
        public MocapPipelineStage Stage { get; internal set; }
        public string SourceBackupPath { get; internal set; }
        public bool SourceBackupCreated { get; internal set; }
        public OptiTrackMotionBindingResult Binding { get; internal set; }
        public BipedFbxExportResult FbxExport { get; internal set; }
        public string Error { get; internal set; }
        public bool Succeeded => Stage == MocapPipelineStage.Completed;
    }

    /// <summary>
    /// One-click synchronous pipeline: FBX backup/binding or direct .anim input ->
    /// in-memory 60 fps forearm hinge -> Biped FBX bake -> Max conversion.
    /// </summary>
    public static class MocapToBipedFbxPipeline
    {
        public static List<MocapPipelineResult> Run(
            MocapPipelineSettings settings,
            IReadOnlyList<MocapPipelineItem> items,
            Func<string, float, bool> progressCallback = null)
        {
            ValidateSettings(settings, items);
            Directory.CreateDirectory(Path.GetFullPath(settings.FbxOutputDirectory));

            var enabledItems = items.Where(item => item != null && item.Enabled).ToList();
            var results = new List<MocapPipelineResult>(enabledItems.Count);
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Binding names its output after the FBX take, so per-actor exports of one
            // take collide. Plan the batch up front: colliding sources keep their
            // original file name as a suffix instead of fighting over one target file.
            var bindingNames = OptiTrackMotionBindingService.PlanAnimationNames(
                enabledItems.Select(item => AssetDatabase.GetAssetPath(item.SourceFbx)),
                out var bindingNameNotes);
            foreach (var note in bindingNameNotes)
                Debug.Log($"[MocapPipeline] {note}");

            for (var index = 0; index < enabledItems.Count; index++)
            {
                var item = enabledItems[index];
                var result = new MocapPipelineResult
                {
                    Item = item,
                    Stage = MocapPipelineStage.Pending
                };
                results.Add(result);

                try
                {
                    RunItem(
                        settings,
                        item,
                        result,
                        usedNames,
                        bindingNames,
                        (message, itemProgress) =>
                        {
                            var totalProgress = (index + Mathf.Clamp01(itemProgress)) / enabledItems.Count;
                            return progressCallback?.Invoke(
                                $"[{index + 1}/{enabledItems.Count}] {message}",
                                totalProgress) == true;
                        });
                }
                catch (OperationCanceledException)
                {
                    throw;
                }
                catch (Exception exception)
                {
                    result.Stage = MocapPipelineStage.Failed;
                    result.Error = exception.Message;
                    Debug.LogException(exception);
                    if (!settings.ContinueOnError)
                        throw;
                }
            }

            progressCallback?.Invoke("Mocap to Biped FBX 파이프라인 완료", 1f);
            return results;
        }

        private static void RunItem(
            MocapPipelineSettings settings,
            MocapPipelineItem item,
            MocapPipelineResult result,
            ISet<string> usedNames,
            IReadOnlyDictionary<string, string> bindingNames,
            Func<string, float, bool> progressCallback)
        {
            var sourcePath = AssetDatabase.GetAssetPath(item.SourceFbx);
            AnimationClip sourceClip;
            string fallbackOutputName;
            if (MocapPipelineSourceUtility.TryGetStandaloneAnimationClip(item.SourceFbx, out sourceClip))
            {
                result.Stage = MocapPipelineStage.Binding;
                ThrowIfCancelled(progressCallback, $"{sourceClip.name}: Anim 직접 입력", 0.10f);
                fallbackOutputName = Path.GetFileNameWithoutExtension(sourcePath);
            }
            else
            {
                if (!MocapPipelineSourceUtility.IsFbxModel(item.SourceFbx))
                    throw new InvalidOperationException($"{item.SourceFbx?.name}: 지원되는 FBX 또는 Anim 에셋이 아닙니다.");

                result.Stage = MocapPipelineStage.BackingUpSource;
                ThrowIfCancelled(progressCallback, $"{item.SourceFbx.name}: 원본 백업", 0.01f);
                result.SourceBackupPath = OptiTrackMotionBindingService.EnsureSourceBackup(
                    sourcePath,
                    out var backupCreated);
                result.SourceBackupCreated = backupCreated;

                result.Stage = MocapPipelineStage.Binding;
                ThrowIfCancelled(progressCallback, $"{item.SourceFbx.name}: OptiTrack 바인딩", 0.05f);
                bindingNames.TryGetValue(sourcePath, out var plannedBindingName);
                result.Binding = OptiTrackMotionBindingService.Process(
                    sourcePath,
                    settings.ExistingBindingPolicy,
                    plannedBindingName);
                if (!result.Binding.Succeeded || result.Binding.AnimationClip == null)
                    throw new InvalidOperationException(result.Binding.Note ?? "OptiTrack 바인딩에 실패했습니다.");

                sourceClip = result.Binding.AnimationClip;
                fallbackOutputName = result.Binding.AnimationName;
            }

            var outputName = MocapPipelineOutputNaming.MakeUnique(
                item, fallbackOutputName, sourcePath, usedNames);
            ValidateRange(item, sourceClip);

            ForearmHingeBakeResult hingeResult = null;
            try
            {
                result.Stage = MocapPipelineStage.HingeBake;
                hingeResult = ForearmHingeBakeService.BakeEditMode(
                    settings.TargetAnimator,
                    sourceClip,
                    new ForearmHingeBakeSettings
                    {
                        SampleRate = settings.SampleRate,
                        HingeAxis = settings.HingeAxis
                    },
                    (message, progress) =>
                        progressCallback?.Invoke(message, Mathf.Lerp(0.15f, 0.55f, progress)) == true);

                result.Stage = MocapPipelineStage.ExportingFbx;
                var outputPath = Path.Combine(
                    Path.GetFullPath(settings.FbxOutputDirectory),
                    outputName + ".fbx");
                result.FbxExport = BipedFbxExportService.Export(
                    new BipedFbxExportSettings
                    {
                        TargetRoot = settings.TargetAnimator.gameObject,
                        Clip = hingeResult.Clip,
                        OutputPath = outputPath,
                        StartTime = item.StartTime,
                        Duration = item.Duration > 0f ? item.Duration : hingeResult.Clip.length - item.StartTime,
                        FrameRate = settings.SampleRate,
                        RecordBlendShapes = settings.RecordBlendShapes,
                        ClampedTangents = settings.ClampedTangents,
                        Compression = settings.Compression,
                        ExportGeometry = settings.ExportGeometry,
                        ExportUnrendered = settings.ExportUnrendered,
                        // Maya-compatible naming replaces spaces with underscores.
                        // Preserve the Biped hierarchy names for the final Max FBX.
                        UseCompatibleNames = false,
                        KeepInstances = settings.KeepInstances,
                        EmbedTextures = settings.EmbedTextures,
                        CreateBackup = settings.CreateFbxBackup
                    },
                    (message, progress) =>
                        progressCallback?.Invoke(message, Mathf.Lerp(0.58f, 1f, progress)) == true);
            }
            finally
            {
                if (hingeResult?.Clip != null)
                    Object.DestroyImmediate(hingeResult.Clip);
            }

            result.Stage = MocapPipelineStage.Completed;
            Debug.Log(
                $"[Mocap Pipeline] Source: {sourcePath} | Backup: {result.SourceBackupPath ?? "N/A"} | " +
                $"{sourceClip.name} -> {result.FbxExport.OutputPath}");
        }

        private static void ValidateSettings(
            MocapPipelineSettings settings,
            IReadOnlyList<MocapPipelineItem> items)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (settings.HingeBakeMode != MocapHingeBakeMode.EditMode)
                throw new InvalidOperationException(
                    "동기 파이프라인은 Edit Mode Bake 전용입니다. Play Mode runner를 사용하세요.");
            if (settings.TargetAnimator == null)
                throw new InvalidOperationException("대상 Biped Animator를 지정하세요.");
            if (settings.TargetAnimator.avatar == null ||
                !settings.TargetAnimator.avatar.isValid ||
                !settings.TargetAnimator.avatar.isHuman)
                throw new InvalidOperationException("대상 Biped에 유효한 Humanoid Avatar가 필요합니다.");
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

        private static void ThrowIfCancelled(
            Func<string, float, bool> callback,
            string message,
            float progress)
        {
            if (callback?.Invoke(message, progress) == true)
                throw new OperationCanceledException("Mocap 파이프라인이 취소되었습니다.");
        }
    }

    /// <summary>
    /// Output FBX naming, shared by the Edit Mode pipeline and the Play Mode runner
    /// so both disambiguate identically.
    /// </summary>
    public static class MocapPipelineOutputNaming
    {
        public static string MakeUnique(
            MocapPipelineItem item,
            string fallbackName,
            string sourcePath,
            ISet<string> usedNames)
        {
            var requested = Sanitize(
                string.IsNullOrWhiteSpace(item.OutputName) ? fallbackName : item.OutputName);
            requested = string.IsNullOrWhiteSpace(requested) ? "Motion" : requested;

            if (usedNames.Add(requested))
                return requested;

            // Two items want the same output file. Prefer naming them after their
            // source files (the actor number for per-actor exports) over an opaque
            // "_2", so the finished FBX still says which capture it came from.
            var sourceName = Sanitize(Path.GetFileNameWithoutExtension(sourcePath ?? string.Empty));
            if (!string.IsNullOrEmpty(sourceName))
            {
                var qualified = OptiTrackMotionBindingService.AppendSourceName(requested, sourceName);
                if (!string.Equals(qualified, requested, StringComparison.Ordinal) &&
                    usedNames.Add(qualified))
                    return qualified;
            }

            var candidate = requested;
            var suffix = 2;
            while (!usedNames.Add(candidate))
                candidate = $"{requested}_{suffix++}";
            return candidate;
        }

        private static string Sanitize(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
                name = name.Replace(invalidCharacter, '_');
            return name.Trim();
        }
    }

    internal static class MocapPipelineSourceUtility
    {
        public static bool IsSupported(Object candidate)
        {
            return IsFbxModel(candidate) || TryGetStandaloneAnimationClip(candidate, out _);
        }

        public static bool IsFbxModel(Object candidate)
        {
            if (candidate == null)
                return false;
            var path = AssetDatabase.GetAssetPath(candidate);
            return !string.IsNullOrEmpty(path) &&
                   path.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) &&
                   AssetImporter.GetAtPath(path) is ModelImporter;
        }

        public static bool TryGetStandaloneAnimationClip(Object candidate, out AnimationClip clip)
        {
            clip = candidate as AnimationClip;
            if (clip == null)
                return false;
            var path = AssetDatabase.GetAssetPath(clip);
            return !string.IsNullOrEmpty(path) &&
                   path.EndsWith(".anim", StringComparison.OrdinalIgnoreCase);
        }
    }
}
