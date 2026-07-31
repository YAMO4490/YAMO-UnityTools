using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    public enum ExistingMotionAssetPolicy
    {
        Fail,
        Overwrite
    }

    public sealed class OptiTrackMotionBindingResult
    {
        public bool Succeeded { get; internal set; }
        public string SourcePath { get; internal set; }
        public string MotionPath { get; internal set; }
        public string TPosePath { get; internal set; }
        public string AnimationName { get; internal set; }
        public AnimationClip AnimationClip { get; internal set; }
        public string Note { get; internal set; }
    }

    /// <summary>
    /// Reusable OptiTrack FBX binding pipeline used by both the legacy setup window
    /// and higher-level mocap batch workflows.
    /// </summary>
    public static class OptiTrackMotionBindingService
    {
        private const string SpineBone = "_Spine1";
        private const string ChestBone = "_Spine3";
        private const string UpperChestBone = "_Spine4";
        private const string SourceBackupMarker = "YAMO_MOCAP_SOURCE_BACKUP=";

        private static MethodInfo setupHumanSkeleton;
        private static bool setupHumanSkeletonResolved;
        private static MethodInfo copyHumanDescription;
        private static bool copyHumanDescriptionResolved;

        /// <summary>
        /// Creates an untouched copy beside the source before the binding pipeline
        /// renames or changes its importer. An existing backup is deliberately kept.
        /// </summary>
        public static string EnsureSourceBackup(string sourcePath, out bool created)
        {
            created = false;
            if (!(AssetImporter.GetAtPath(sourcePath) is ModelImporter importer))
                throw new InvalidOperationException($"{sourcePath}: 백업할 ModelImporter FBX를 찾을 수 없습니다.");

            var recordedBackupPath = ReadSourceBackupPath(importer);
            if (!string.IsNullOrEmpty(recordedBackupPath) &&
                AssetDatabase.LoadMainAssetAtPath(recordedBackupPath) != null)
                return recordedBackupPath;

            var directory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/');
            var fileName = Path.GetFileNameWithoutExtension(sourcePath);
            if (string.IsNullOrEmpty(directory) || string.IsNullOrEmpty(fileName))
                throw new InvalidOperationException($"{sourcePath}: 백업 경로를 만들 수 없습니다.");

            var backupPath = $"{directory}/{fileName}_Backup.fbx";
            if (AssetDatabase.LoadMainAssetAtPath(backupPath) != null)
            {
                WriteSourceBackupPath(importer, backupPath);
                return backupPath;
            }

            if (!AssetDatabase.CopyAsset(sourcePath, backupPath))
                throw new InvalidOperationException($"{sourcePath}: 원본 백업 생성에 실패했습니다 ({backupPath}).");

            WriteSourceBackupPath(importer, backupPath);
            created = true;
            return backupPath;
        }

        private static string ReadSourceBackupPath(AssetImporter importer)
        {
            var lines = (importer.userData ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries);
            foreach (var line in lines)
            {
                if (line.StartsWith(SourceBackupMarker, StringComparison.Ordinal))
                    return line.Substring(SourceBackupMarker.Length).Trim();
            }
            return null;
        }

        private static void WriteSourceBackupPath(AssetImporter importer, string backupPath)
        {
            var lines = (importer.userData ?? string.Empty)
                .Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(line => !line.StartsWith(SourceBackupMarker, StringComparison.Ordinal))
                .ToList();
            lines.Add(SourceBackupMarker + backupPath);
            importer.userData = string.Join("\n", lines);
            importer.SaveAndReimport();
        }

        public static OptiTrackMotionBindingResult Process(
            string sourcePath,
            ExistingMotionAssetPolicy existingAssetPolicy = ExistingMotionAssetPolicy.Fail)
        {
            var result = new OptiTrackMotionBindingResult { SourcePath = sourcePath };
            var importer = AssetImporter.GetAtPath(sourcePath) as ModelImporter;
            if (importer == null)
                return Fail(result, $"{sourcePath}: ModelImporter 아님");

            var animationName = SanitizeFileName(ResolveAnimationName(importer));
            if (string.IsNullOrEmpty(animationName))
                return Fail(result, $"{sourcePath}: 애니메이션 이름을 찾을 수 없음");

            result.AnimationName = animationName;
            var directory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(directory))
                return Fail(result, $"{sourcePath}: 상위 폴더를 찾을 수 없음");

            var motionPath = $"{directory}/{animationName}.fbx";
            if (!PathsEqual(sourcePath, motionPath))
            {
                if (!PrepareDestination(motionPath, existingAssetPolicy, out var error))
                    return Fail(result, error);

                error = AssetDatabase.RenameAsset(sourcePath, animationName);
                if (!string.IsNullOrEmpty(error))
                    return Fail(result, $"{sourcePath}: 리네임 실패 - {error}");

                sourcePath = motionPath;
                importer = AssetImporter.GetAtPath(sourcePath) as ModelImporter;
                if (importer == null)
                    return Fail(result, $"{sourcePath}: 리네임 후 ModelImporter를 찾을 수 없음");
            }

            result.MotionPath = sourcePath;

            var clips = importer.clipAnimations;
            if (clips == null || clips.Length == 0)
                clips = importer.defaultClipAnimations;

            var tPosePath = $"{directory}/{animationName}_T.fbx";
            result.TPosePath = tPosePath;
            if (!PrepareDestination(tPosePath, existingAssetPolicy, out var destinationError))
                return Fail(result, destinationError);
            if (!AssetDatabase.CopyAsset(sourcePath, tPosePath))
                return Fail(result, $"{sourcePath}: _T 복사 실패");

            if (!BuildTPoseAvatar(tPosePath, out var tPoseAvatar, out var note))
                return Fail(result, note);

            importer.animationType = ModelImporterAnimationType.Human;
            importer.importAnimation = true;
            importer.avatarSetup = ModelImporterAvatarSetup.CopyFromOther;
            importer.sourceAvatar = tPoseAvatar;
            importer.animationCompression = ModelImporterAnimationCompression.Off;

            if (clips != null && clips.Length > 0)
            {
                if (clips.Length == 1)
                    clips[0].name = animationName;

                foreach (var clip in clips)
                {
                    clip.lockRootRotation = true;
                    clip.keepOriginalOrientation = true;
                    clip.lockRootHeightY = true;
                    clip.keepOriginalPositionY = true;
                    clip.lockRootPositionXZ = true;
                    clip.keepOriginalPositionXZ = true;
                }

                importer.clipAnimations = clips;
            }

            importer.SaveAndReimport();

            var tPoseImporter = AssetImporter.GetAtPath(tPosePath) as ModelImporter;
            if (tPoseImporter != null && TryCopyHumanDescription(tPoseImporter, importer))
            {
                importer.SaveAndReimport();
            }
            else
            {
                result.Note = $"{sourcePath}: T 아바타 자동 동기화 실패 - Rig 탭에서 Update를 눌러주세요.";
            }

            result.AnimationClip = AssetDatabase.LoadAllAssetsAtPath(sourcePath)
                .OfType<AnimationClip>()
                .FirstOrDefault(clip => !clip.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase));
            if (result.AnimationClip == null)
                return Fail(result, $"{sourcePath}: 바인딩 후 AnimationClip을 찾을 수 없음");

            result.Succeeded = true;
            return result;
        }

        private static bool PrepareDestination(
            string destinationPath,
            ExistingMotionAssetPolicy policy,
            out string error)
        {
            error = null;
            if (AssetDatabase.LoadMainAssetAtPath(destinationPath) == null)
                return true;

            if (policy == ExistingMotionAssetPolicy.Fail)
            {
                error = $"{destinationPath}: 동일 이름의 에셋이 이미 존재함";
                return false;
            }

            if (!AssetDatabase.DeleteAsset(destinationPath))
            {
                error = $"{destinationPath}: 기존 에셋 삭제 실패";
                return false;
            }

            return true;
        }

        private static bool BuildTPoseAvatar(string tPosePath, out Avatar avatar, out string note)
        {
            avatar = null;
            note = null;
            var importer = AssetImporter.GetAtPath(tPosePath) as ModelImporter;
            if (importer == null)
            {
                note = $"{tPosePath}: T ModelImporter 아님";
                return false;
            }

            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = false;
            importer.SaveAndReimport();

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(tPosePath);
            if (model == null)
            {
                note = $"{tPosePath}: T 모델 로드 실패";
                return false;
            }

            if (!TryCaptureHumanoid(model, out var human, out var skeleton, out var translationDof))
            {
                note = $"{tPosePath}: 휴머노이드 매핑 캡처 실패 (AvatarSetupTool 접근 불가)";
                return false;
            }

            string prefix = null;
            foreach (var humanBone in human)
            {
                if (humanBone.humanName == "Hips" &&
                    !string.IsNullOrEmpty(humanBone.boneName) &&
                    humanBone.boneName.EndsWith("_Hips", StringComparison.Ordinal))
                {
                    prefix = humanBone.boneName.Substring(0, humanBone.boneName.Length - "_Hips".Length);
                    break;
                }
            }

            if (string.IsNullOrEmpty(prefix))
            {
                note = $"{tPosePath}: Hips 본에서 접두사 탐지 실패";
                return false;
            }

            var remapped = new List<HumanBone>(human.Length);
            foreach (var humanBone in human)
            {
                if (humanBone.humanName == "LeftEye" ||
                    humanBone.humanName == "RightEye" ||
                    humanBone.humanName == "Jaw")
                    continue;

                var remappedBone = humanBone;
                if (humanBone.humanName == "Spine")
                    remappedBone.boneName = prefix + SpineBone;
                else if (humanBone.humanName == "Chest")
                    remappedBone.boneName = prefix + ChestBone;
                else if (humanBone.humanName == "UpperChest")
                    remappedBone.boneName = prefix + UpperChestBone;
                remapped.Add(remappedBone);
            }

            var description = importer.humanDescription;
            description.human = remapped.ToArray();
            description.skeleton = skeleton;
            description.hasTranslationDoF = translationDof;
            importer.humanDescription = description;
            importer.SaveAndReimport();

            avatar = AssetDatabase.LoadAllAssetsAtPath(tPosePath).OfType<Avatar>().FirstOrDefault();
            if (avatar == null || !avatar.isValid)
            {
                note = $"{tPosePath}: T 아바타 생성 실패/무효";
                return false;
            }

            return true;
        }

        private static bool TryCaptureHumanoid(
            GameObject model,
            out HumanBone[] human,
            out SkeletonBone[] skeleton,
            out bool hasTranslationDof)
        {
            human = null;
            skeleton = null;
            hasTranslationDof = false;

            if (!setupHumanSkeletonResolved)
            {
                setupHumanSkeletonResolved = true;
                var avatarSetupTool = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.AvatarSetupTool");
                setupHumanSkeleton = avatarSetupTool?.GetMethod(
                    "SetupHumanSkeleton",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }

            if (setupHumanSkeleton == null)
                return false;

            var arguments = new object[] { model, null, null, false };
            setupHumanSkeleton.Invoke(null, arguments);
            human = arguments[1] as HumanBone[];
            skeleton = arguments[2] as SkeletonBone[];
            hasTranslationDof = arguments[3] is bool value && value;
            return human != null && human.Length > 0 && skeleton != null && skeleton.Length > 0;
        }

        private static bool TryCopyHumanDescription(ModelImporter source, ModelImporter destination)
        {
            if (!copyHumanDescriptionResolved)
            {
                copyHumanDescriptionResolved = true;
                var rigEditor = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.ModelImporterRigEditor");
                copyHumanDescription = rigEditor?.GetMethod(
                    "CopyHumanDescriptionToDestination",
                    BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Static);
            }

            if (copyHumanDescription == null)
                return false;

            var sourceObject = new SerializedObject(source);
            var destinationObject = new SerializedObject(destination);
            copyHumanDescription.Invoke(null, new object[] { sourceObject, destinationObject });
            destinationObject.ApplyModifiedProperties();
            return true;
        }

        private static string ResolveAnimationName(ModelImporter importer)
        {
            string rawName = null;
            var clips = importer.clipAnimations;
            if (clips != null && clips.Length > 0)
                rawName = clips[0].name;

            if (string.IsNullOrEmpty(rawName))
            {
                clips = importer.defaultClipAnimations;
                if (clips != null && clips.Length > 0)
                    rawName = clips[0].name;
            }

            if (string.IsNullOrEmpty(rawName))
                return null;

            if (rawName.Length >= 4 &&
                rawName.Substring(rawName.Length - 4).Equals("_FBX", StringComparison.OrdinalIgnoreCase))
                rawName = rawName.Substring(0, rawName.Length - 4);

            return rawName.Trim();
        }

        private static string SanitizeFileName(string name)
        {
            if (string.IsNullOrEmpty(name))
                return name;
            foreach (var character in Path.GetInvalidFileNameChars())
                name = name.Replace(character, '_');
            return name.Trim();
        }

        private static bool PathsEqual(string first, string second)
        {
            return string.Equals(
                first.Replace('\\', '/'),
                second.Replace('\\', '/'),
                StringComparison.OrdinalIgnoreCase);
        }

        private static OptiTrackMotionBindingResult Fail(
            OptiTrackMotionBindingResult result,
            string note)
        {
            result.Succeeded = false;
            result.Note = note;
            return result;
        }
    }
}
