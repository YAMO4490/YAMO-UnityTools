using System;
using System.IO;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor
{
    public enum MotionFbxCurveCompression
    {
        Disabled,
        Lossless,
        Lossy
    }

    public sealed class BipedFbxExportSettings
    {
        public GameObject TargetRoot { get; set; }
        public AnimationClip Clip { get; set; }
        public string OutputPath { get; set; }
        public float StartTime { get; set; }
        public float Duration { get; set; }
        public float FrameRate { get; set; } = 60f;
        public bool RecordBlendShapes { get; set; } = true;
        public bool ClampedTangents { get; set; } = true;
        public MotionFbxCurveCompression Compression { get; set; } = MotionFbxCurveCompression.Disabled;
        public bool ExportGeometry { get; set; }
        public bool ExportUnrendered { get; set; } = true;
        public bool UseCompatibleNames { get; set; }
        public bool KeepInstances { get; set; } = true;
        public bool EmbedTextures { get; set; }
        public bool CreateBackup { get; set; } = true;
    }

    public sealed class BipedFbxExportResult
    {
        public string OutputPath { get; internal set; }
        public string BackupPath { get; internal set; }
        public int SampleCount { get; internal set; }
        public MaxFbxConversionReport Conversion { get; internal set; }
    }

    public static class BipedFbxExportService
    {
        public static BipedFbxExportResult Export(
            BipedFbxExportSettings settings,
            Func<string, float, bool> progressCallback = null)
        {
            Validate(settings);

            var outputPath = Path.GetFullPath(settings.OutputPath);
            var outputDirectory = Path.GetDirectoryName(outputPath);
            if (string.IsNullOrEmpty(outputDirectory))
                throw new InvalidOperationException("FBX 출력 폴더를 확인할 수 없습니다.");
            Directory.CreateDirectory(outputDirectory);

            var token = Guid.NewGuid().ToString("N");
            var name = Path.GetFileNameWithoutExtension(outputPath);
            var unityTemporaryPath = Path.Combine(outputDirectory, $".{name}.{token}.unity.fbx");
            var maxTemporaryPath = Path.Combine(outputDirectory, $".{name}.{token}.max.fbx");

            GameObject clone = null;
            AnimationClip bakedClip = null;
            AnimatorController controller = null;
            try
            {
                ThrowIfCancelled(progressCallback, "Biped 복제본 준비", 0.02f);
                clone = CreateSamplingClone(settings.TargetRoot);
                bakedClip = BakeClip(
                    clone,
                    settings,
                    (message, progress) =>
                        progressCallback?.Invoke(message, Mathf.Lerp(0.05f, 0.68f, progress)) == true,
                    out var sampleCount);
                bakedClip.name = SanitizeFileName(settings.Clip.name);

                ThrowIfCancelled(progressCallback, "FBX 애니메이션 구성", 0.7f);
                controller = AttachSingleClipController(clone, bakedClip);
                var exportOptions = MocapFbxExporterCompat.BuildOptions(
                    settings.UseCompatibleNames,
                    settings.ExportGeometry,
                    settings.RecordBlendShapes,
                    settings.ExportUnrendered,
                    settings.KeepInstances);

                var exportedPath = MocapFbxExporterCompat.ExportObject(
                    unityTemporaryPath,
                    clone,
                    exportOptions);
                if (string.IsNullOrEmpty(exportedPath) || !File.Exists(exportedPath))
                    throw new InvalidOperationException("Unity FBX Exporter가 임시 FBX를 생성하지 못했습니다.");

                ThrowIfCancelled(progressCallback, "3ds Max Z-up 변환", 0.82f);
                var conversion = MaxFbxSceneConverter.Convert(
                    exportedPath,
                    maxTemporaryPath,
                    settings.EmbedTextures);

                ThrowIfCancelled(progressCallback, "최종 FBX 저장", 0.96f);
                var backupPath = ReplaceTarget(maxTemporaryPath, outputPath, settings.CreateBackup);
                progressCallback?.Invoke("FBX Export 완료", 1f);

                return new BipedFbxExportResult
                {
                    OutputPath = outputPath,
                    BackupPath = backupPath,
                    SampleCount = sampleCount,
                    Conversion = conversion
                };
            }
            finally
            {
                if (controller != null)
                    Object.DestroyImmediate(controller);
                if (bakedClip != null)
                    Object.DestroyImmediate(bakedClip);
                if (clone != null)
                    Object.DestroyImmediate(clone);

                DeleteTemporaryFile(unityTemporaryPath);
                DeleteTemporaryFile(maxTemporaryPath);
            }
        }

        public static AnimationClip BakeClip(
            GameObject samplingRoot,
            BipedFbxExportSettings settings,
            Func<string, float, bool> progressCallback,
            out int sampleCount)
        {
            if (AnimationMode.InAnimationMode())
                throw new InvalidOperationException("Animation Preview가 활성화되어 있습니다. Preview를 끈 뒤 다시 실행하세요.");

            var recorder = new GameObjectRecorder(samplingRoot);
            recorder.BindComponentsOfType(samplingRoot, typeof(Transform), true);
            if (settings.RecordBlendShapes)
                recorder.BindComponentsOfType(samplingRoot, typeof(SkinnedMeshRenderer), true);

            var duration = EffectiveDuration(settings);
            var endTime = settings.StartTime + duration;
            var frameCount = Mathf.Max(1, Mathf.CeilToInt(duration * settings.FrameRate));
            var previousTime = settings.StartTime;
            sampleCount = frameCount + 1;

            AnimationMode.StartAnimationMode();
            try
            {
                for (var frame = 0; frame <= frameCount; frame++)
                {
                    var sourceTime = frame == frameCount
                        ? endTime
                        : Mathf.Min(settings.StartTime + frame / settings.FrameRate, endTime);

                    AnimationMode.BeginSampling();
                    try
                    {
                        AnimationMode.SampleAnimationClip(samplingRoot, settings.Clip, sourceTime);
                    }
                    finally
                    {
                        AnimationMode.EndSampling();
                    }

                    recorder.TakeSnapshot(frame == 0 ? 0f : sourceTime - previousTime);
                    previousTime = sourceTime;
                    if (progressCallback?.Invoke(
                            $"{settings.Clip.name}: {sourceTime:0.###}s 샘플링",
                            frame / (float)frameCount) == true)
                        throw new OperationCanceledException("FBX 애니메이션 베이크가 취소되었습니다.");
                }
            }
            finally
            {
                AnimationMode.StopAnimationMode();
            }

            var clip = new AnimationClip { frameRate = settings.FrameRate };
            recorder.SaveToClip(clip, settings.FrameRate, GetCurveFilterOptions(settings.Compression));
            if (settings.ClampedTangents)
                ApplyClampedTangents(clip);
            return clip;
        }

        private static GameObject CreateSamplingClone(GameObject source)
        {
            var clone = Object.Instantiate(source);
            clone.name = source.name;
            clone.transform.SetParent(null, true);
            clone.SetActive(true);
            foreach (var transform in clone.GetComponentsInChildren<Transform>(true))
                transform.gameObject.hideFlags = HideFlags.HideAndDontSave;
            foreach (var childAnimator in clone.GetComponentsInChildren<Animator>(true))
            {
                childAnimator.runtimeAnimatorController = null;
                // Forearm Hinge 결과는 Humanoid muscle clip이 아니라 Biped 경로에
                // 직접 기록된 Transform 커브다. Avatar를 유지하면 최종 재샘플링과
                // FBX Export 단계에서 Humanoid로 다시 해석되어 포즈가 무너질 수 있다.
                // 원본이 아닌 이 임시 복제본에서만 Generic 상태로 전환한다.
                childAnimator.avatar = null;
                childAnimator.applyRootMotion = false;
                childAnimator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                childAnimator.enabled = true;
            }
            return clone;
        }

        private static AnimatorController AttachSingleClipController(GameObject root, AnimationClip clip)
        {
            var animator = root.GetComponent<Animator>();
            if (animator == null)
                throw new InvalidOperationException("Biped 루트에 Animator가 없습니다.");
            if (animator.avatar != null)
                throw new InvalidOperationException("최종 FBX 복제본의 Animator는 Generic 상태여야 합니다.");

            var controller = new AnimatorController { name = "YAMO_MocapFbx_TemporaryController" };
            controller.AddLayer("Base Layer");
            controller.AddMotion(clip);
            animator.runtimeAnimatorController = controller;
            return controller;
        }

        private static CurveFilterOptions GetCurveFilterOptions(MotionFbxCurveCompression compression)
        {
            switch (compression)
            {
                case MotionFbxCurveCompression.Lossy:
                    return new CurveFilterOptions
                    {
                        keyframeReduction = true,
                        positionError = 0.5f,
                        rotationError = 0.5f,
                        scaleError = 0.5f,
                        floatError = 0.5f
                    };
                case MotionFbxCurveCompression.Lossless:
                    return new CurveFilterOptions { keyframeReduction = true };
                default:
                    return new CurveFilterOptions { keyframeReduction = false };
            }
        }

        private static void ApplyClampedTangents(AnimationClip clip)
        {
            foreach (var binding in AnimationUtility.GetCurveBindings(clip))
            {
                var curve = AnimationUtility.GetEditorCurve(clip, binding);
                for (var index = 0; index < curve.length; index++)
                {
                    AnimationUtility.SetKeyLeftTangentMode(curve, index, AnimationUtility.TangentMode.ClampedAuto);
                    AnimationUtility.SetKeyRightTangentMode(curve, index, AnimationUtility.TangentMode.ClampedAuto);
                }
                AnimationUtility.SetEditorCurve(clip, binding, curve);
            }
        }

        private static void Validate(BipedFbxExportSettings settings)
        {
            if (settings == null)
                throw new ArgumentNullException(nameof(settings));
            if (settings.TargetRoot == null || settings.TargetRoot.GetComponent<Animator>() == null)
                throw new InvalidOperationException("Animator가 있는 Biped 루트를 지정하세요.");
            if (settings.Clip == null)
                throw new InvalidOperationException("AnimationClip을 지정하세요.");
            if (settings.FrameRate <= 0f || float.IsNaN(settings.FrameRate) || float.IsInfinity(settings.FrameRate))
                throw new InvalidOperationException("FPS는 0보다 큰 유한한 값이어야 합니다.");
            if (settings.StartTime < 0f || settings.StartTime >= settings.Clip.length)
                throw new InvalidOperationException("시작 시간이 AnimationClip 범위를 벗어났습니다.");
            if (EffectiveDuration(settings) <= 0f ||
                settings.StartTime + EffectiveDuration(settings) > settings.Clip.length + 0.0001f)
                throw new InvalidOperationException("출력 구간이 AnimationClip 범위를 벗어났습니다.");
            if (string.IsNullOrWhiteSpace(settings.OutputPath) ||
                !settings.OutputPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException(".fbx 출력 경로를 지정하세요.");
        }

        private static float EffectiveDuration(BipedFbxExportSettings settings)
        {
            return settings.Duration > 0f
                ? settings.Duration
                : settings.Clip.length - settings.StartTime;
        }

        private static string SanitizeFileName(string value)
        {
            foreach (var invalidCharacter in Path.GetInvalidFileNameChars())
                value = value.Replace(invalidCharacter, '_');
            return string.IsNullOrWhiteSpace(value) ? "Animation" : value.Trim();
        }

        private static string ReplaceTarget(string sourcePath, string targetPath, bool keepBackup)
        {
            if (!File.Exists(targetPath))
            {
                File.Move(sourcePath, targetPath);
                return null;
            }

            string backupPath = null;
            if (keepBackup)
            {
                var timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                backupPath = $"{targetPath}.{timestamp}.bak";
                var suffix = 1;
                while (File.Exists(backupPath))
                    backupPath = $"{targetPath}.{timestamp}_{suffix++}.bak";
            }

            File.Replace(sourcePath, targetPath, backupPath);
            return backupPath;
        }

        private static void ThrowIfCancelled(
            Func<string, float, bool> callback,
            string message,
            float progress)
        {
            if (callback?.Invoke(message, progress) == true)
                throw new OperationCanceledException("FBX Export가 취소되었습니다.");
        }

        private static void DeleteTemporaryFile(string path)
        {
            if (!string.IsNullOrEmpty(path) && File.Exists(path))
                File.Delete(path);
        }
    }
}
