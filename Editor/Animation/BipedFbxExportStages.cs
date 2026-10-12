using System;
using System.Collections;
using System.IO;
using UnityEngine;
using UnityEditor;
using UnityEditor.Animations;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor
{
    internal static class BipedFbxExportStages
    {
        public static IEnumerator Export(BipedFbxExportSettings settings, Func<string, float, bool> progress,
            Action<BipedFbxExportResult> completed)
        {
            BipedFbxExportService.Validate(settings);
            var output = Path.GetFullPath(settings.OutputPath);
            Directory.CreateDirectory(Path.GetDirectoryName(output));
            var token = Guid.NewGuid().ToString("N");
            var unityPath = Path.Combine(Path.GetDirectoryName(output), $".{Path.GetFileNameWithoutExtension(output)}.{token}.unity.fbx");
            var maxPath = Path.Combine(Path.GetDirectoryName(output), $".{Path.GetFileNameWithoutExtension(output)}.{token}.max.fbx");
            GameObject clone = null; AnimationClip clip = null; AnimatorController controller = null;
            var samples = Mathf.CeilToInt(settings.Duration * settings.FrameRate) + 1;
            var refreshPaused = false;
            try
            {
                Check(progress, "FBX 복제본 준비", 0.05f);
                clone = BipedFbxExportService.CreateSamplingClone(settings.TargetRoot,
                    settings.ExportGeometry || settings.RecordBlendShapes || !settings.ExportUnrendered);
                // Recorder output already contains evaluated Generic bone transforms.
                // The normal queue needs no second GameObjectRecorder over the full avatar.
                if (!settings.RecordBlendShapes && !settings.ClampedTangents &&
                    settings.Compression == MotionFbxCurveCompression.Disabled && settings.StartTime == 0f &&
                    Mathf.Abs(settings.Duration - settings.Clip.length) < 0.0001f &&
                    Mathf.Abs(settings.FrameRate - settings.Clip.frameRate) < 0.0001f && !settings.Clip.isHumanMotion)
                {
                    clip = settings.Clip; settings.Clip = null;
                }
                else
                {
                    clip = BipedFbxExportService.BakeClip(clone, settings, progress, out samples);
                    Object.DestroyImmediate(settings.Clip); settings.Clip = null;
                }
                yield return null;
                controller = BipedFbxExportService.AttachSingleClipController(clone, clip);
                Check(progress, "Unity FBX 출력", 0.35f);
                var keysEstimate = checked((long)samples * Math.Max(1, UnityEditor.AnimationUtility.GetCurveBindings(clip).Length) * 32 * 3);
                MocapMemoryWork.RequireHeadroom("Unity FBX 출력", keysEstimate);
                var options = MocapFbxExporterCompat.BuildOptions(settings.UseCompatibleNames, settings.ExportGeometry,
                    settings.RecordBlendShapes, settings.ExportUnrendered, settings.KeepInstances);
                AssetDatabase.DisallowAutoRefresh(); refreshPaused = true;
                var exported = MocapFbxExporterCompat.ExportObject(unityPath, clone, options);
                if (string.IsNullOrEmpty(exported) || !File.Exists(exported)) throw new IOException("Unity FBX 출력 파일이 없습니다.");
                // Release managed/Unity animation representations BEFORE creating the SDK scene.
                BipedFbxExportService.DestroyTransientController(controller); controller = null;
                Object.DestroyImmediate(clone); clone = null;
                Object.DestroyImmediate(clip); clip = null;
                yield return MocapMemoryWork.Reclaim("Unity FBX 출력 후 정리");
                Check(progress, "3ds Max Z-up 변환", 0.75f);
                MocapMemoryWork.RequireHeadroom("FBX SDK 변환", Math.Max(keysEstimate, new FileInfo(exported).Length * 12));
                var conversion = MaxFbxSceneConverter.Convert(exported, maxPath, settings.EmbedTextures);
                yield return MocapMemoryWork.Reclaim("FBX SDK 변환 후 정리");
                Check(progress, "최종 FBX 저장", 0.95f);
                var assetRoot = Path.GetFullPath(Application.dataPath) + Path.DirectorySeparatorChar;
                var importOutput = output.StartsWith(assetRoot, StringComparison.OrdinalIgnoreCase);
                if (importOutput)
                    MocapMemoryWork.RequireHeadroom("출력 FBX 가져오기", checked(new FileInfo(maxPath).Length * 12));
                var backup = BipedFbxExportService.ReplaceTarget(maxPath, output, settings.CreateBackup);
                completed(new BipedFbxExportResult { OutputPath = output, BackupPath = backup, SampleCount = samples, Conversion = conversion });
                // Import the current result only after the SDK scene has been disposed.
                // Avoid an asset import overlapping native FBX export/conversion.
                if (importOutput)
                {
                    // Commit and import form one phase; cancellation is honored before commit.
                    var assetPath = "Assets/" + output.Substring(assetRoot.Length).Replace('\\', '/');
                    AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                }
                AssetDatabase.AllowAutoRefresh(); refreshPaused = false;
                yield return MocapMemoryWork.Reclaim("출력 에셋 가져오기 후 정리");
            }
            finally
            {
                if (controller != null) BipedFbxExportService.DestroyTransientController(controller);
                if (clone != null) Object.DestroyImmediate(clone);
                if (clip != null) Object.DestroyImmediate(clip);
                BipedFbxExportService.DeleteTemporaryFile(unityPath);
                BipedFbxExportService.DeleteTemporaryFile(maxPath);
                if (refreshPaused) AssetDatabase.AllowAutoRefresh();
            }
        }
        private static void Check(Func<string, float, bool> progress, string message, float value)
        {
            if (progress?.Invoke(message, value) == true) throw new OperationCanceledException("FBX 출력이 취소되었습니다.");
        }
    }
}
