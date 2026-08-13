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
        Overwrite,

        /// <summary>
        /// Keeps every source: when the target name is already taken by a foreign
        /// asset, a numeric suffix is appended instead of deleting anything.
        /// Only leftovers that belong to the source itself are replaced.
        /// </summary>
        Disambiguate
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

        /// <param name="desiredAnimationName">
        /// Overrides the name derived from the FBX take. Pass the value produced by
        /// <see cref="PlanAnimationNames"/> when processing a batch, so that several
        /// sources sharing one take name (e.g. per-actor OptiTrack exports) do not
        /// fight over the same target file.
        /// </param>
        public static OptiTrackMotionBindingResult Process(
            string sourcePath,
            ExistingMotionAssetPolicy existingAssetPolicy = ExistingMotionAssetPolicy.Fail,
            string desiredAnimationName = null)
        {
            var result = new OptiTrackMotionBindingResult { SourcePath = sourcePath };
            var importer = AssetImporter.GetAtPath(sourcePath) as ModelImporter;
            if (importer == null)
                return Fail(result, $"{sourcePath}: ModelImporter 아님");

            // A "_T" file is this tool's own T-pose asset; binding it would rename the
            // avatar the real motion points at. Refuse instead of destroying it.
            if (IsTPoseAsset(sourcePath))
                return Fail(result, $"{sourcePath}: _T(T 포즈) 파일은 바인딩 대상이 아닙니다. 목록에서 제외하세요.");

            var animationName = SanitizeFileName(
                string.IsNullOrWhiteSpace(desiredAnimationName)
                    ? ResolveAnimationName(importer)
                    : desiredAnimationName.Trim());
            if (string.IsNullOrEmpty(animationName))
                return Fail(result, $"{sourcePath}: 애니메이션 이름을 찾을 수 없음");

            var directory = Path.GetDirectoryName(sourcePath)?.Replace('\\', '/');
            if (string.IsNullOrEmpty(directory))
                return Fail(result, $"{sourcePath}: 상위 폴더를 찾을 수 없음");

            if (!ResolveFreeTargetName(
                    sourcePath,
                    directory,
                    animationName,
                    existingAssetPolicy,
                    out animationName,
                    out var motionPath,
                    out var tPosePath,
                    out var renameNote,
                    out var renameError))
                return Fail(result, renameError);

            result.AnimationName = animationName;
            result.TPosePath = tPosePath;
            AppendNote(result, renameNote);

            if (!PathsEqual(sourcePath, motionPath))
            {
                if (!PrepareDestination(motionPath, existingAssetPolicy, out var error, out var overwriteNote))
                    return Fail(result, error);
                AppendNote(result, overwriteNote);

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

            if (!PrepareDestination(tPosePath, existingAssetPolicy, out var destinationError, out var tPoseOverwriteNote))
                return Fail(result, destinationError);
            AppendNote(result, tPoseOverwriteNote);
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
                AppendNote(result, $"{sourcePath}: T 아바타 자동 동기화 실패 - Rig 탭에서 Update를 눌러주세요.");
            }

            result.AnimationClip = AssetDatabase.LoadAllAssetsAtPath(sourcePath)
                .OfType<AnimationClip>()
                .FirstOrDefault(clip => !clip.name.StartsWith("__preview__", StringComparison.OrdinalIgnoreCase));
            if (result.AnimationClip == null)
                return Fail(result, $"{sourcePath}: 바인딩 후 AnimationClip을 찾을 수 없음");

            result.Succeeded = true;
            return result;
        }

        /// <summary>
        /// Decides the target file name for every source up front, so that a whole
        /// batch can be bound without any source clobbering another's output.
        /// <para>
        /// The binding pipeline names its output after the FBX take, not after the
        /// source file. Per-actor OptiTrack exports of one take (001.fbx, 002.fbx…)
        /// therefore all resolve to the same take name; sources that collide this way
        /// get their original file name (= the actor number) appended so each one
        /// survives as its own motion.
        /// </para>
        /// </summary>
        /// <returns>Source asset path → animation name. Sources whose take name cannot
        /// be resolved are omitted; <see cref="Process"/> reports those individually.</returns>
        public static Dictionary<string, string> PlanAnimationNames(
            IEnumerable<string> sourcePaths,
            out List<string> notes)
        {
            notes = new List<string>();
            var plan = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (sourcePaths == null)
                return plan;

            // Group by (folder, take name) — a collision only matters inside one folder.
            var groups = new Dictionary<string, List<KeyValuePair<string, string>>>(StringComparer.OrdinalIgnoreCase);
            var order = new List<string>();
            foreach (var sourcePath in sourcePaths)
            {
                if (string.IsNullOrEmpty(sourcePath) || plan.ContainsKey(sourcePath))
                    continue;
                if (IsTPoseAsset(sourcePath))
                    continue;
                if (!(AssetImporter.GetAtPath(sourcePath) is ModelImporter importer))
                    continue;

                var takeName = SanitizeFileName(ResolveAnimationName(importer));
                if (string.IsNullOrEmpty(takeName))
                    continue;

                plan[sourcePath] = takeName;
                var key = DirectoryOf(sourcePath) + "|" + takeName;
                if (!groups.TryGetValue(key, out var group))
                {
                    groups[key] = group = new List<KeyValuePair<string, string>>();
                    order.Add(key);
                }
                group.Add(new KeyValuePair<string, string>(sourcePath, takeName));
            }

            // Reserve the unambiguous names first so a disambiguated name never
            // steals a name another source would have kept as-is.
            var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var key in order)
            {
                if (groups[key].Count > 1)
                    continue;
                var entry = groups[key][0];
                plan[entry.Key] = Reserve(DirectoryOf(entry.Key), entry.Value, reserved);
            }

            foreach (var key in order)
            {
                var group = groups[key];
                if (group.Count <= 1)
                    continue;

                foreach (var entry in group)
                {
                    var directory = DirectoryOf(entry.Key);
                    var takeName = entry.Value;
                    var candidate = Reserve(
                        directory,
                        AppendSourceName(takeName, Path.GetFileNameWithoutExtension(entry.Key)),
                        reserved);
                    plan[entry.Key] = candidate;

                    if (!string.Equals(candidate, takeName, StringComparison.Ordinal))
                        notes.Add($"{entry.Key}: 클립 이름 '{takeName}'이(가) {group.Count}개 파일에서 겹쳐 '{candidate}'(으)로 저장합니다.");
                }
            }

            return plan;
        }

        /// <summary>
        /// Picks a name whose motion file and "_T" file are both free, or already
        /// belong to this source (a re-run of a previously bound file). Only applies
        /// to <see cref="ExistingMotionAssetPolicy.Disambiguate"/>; the other policies
        /// keep their existing fail/overwrite behaviour in <see cref="PrepareDestination"/>.
        /// </summary>
        private static bool ResolveFreeTargetName(
            string sourcePath,
            string directory,
            string baseName,
            ExistingMotionAssetPolicy policy,
            out string resolvedName,
            out string motionPath,
            out string tPosePath,
            out string note,
            out string error)
        {
            resolvedName = baseName;
            motionPath = $"{directory}/{baseName}.fbx";
            tPosePath = $"{directory}/{baseName}_T.fbx";
            note = null;
            error = null;

            if (policy != ExistingMotionAssetPolicy.Disambiguate)
                return true;

            var candidate = baseName;
            var suffix = 2;
            while (true)
            {
                motionPath = $"{directory}/{candidate}.fbx";
                tPosePath = $"{directory}/{candidate}_T.fbx";

                // Owning the motion name means this file was already bound under it,
                // so the "_T" beside it is our own leftover and may be replaced.
                var ownsName = PathsEqual(sourcePath, motionPath);
                if (ownsName ||
                    (AssetDatabase.LoadMainAssetAtPath(motionPath) == null &&
                     AssetDatabase.LoadMainAssetAtPath(tPosePath) == null))
                    break;

                if (suffix > 999)
                {
                    error = $"{sourcePath}: '{baseName}' 이름으로 사용할 수 있는 빈 자리를 찾지 못했습니다.";
                    return false;
                }

                candidate = $"{baseName}_{suffix++}";
            }

            resolvedName = candidate;
            if (!string.Equals(candidate, baseName, StringComparison.Ordinal))
                note = $"{sourcePath}: '{baseName}' 이름이 이미 사용 중이라 '{candidate}'(으)로 저장했습니다.";
            return true;
        }

        private static bool PrepareDestination(
            string destinationPath,
            ExistingMotionAssetPolicy policy,
            out string error,
            out string note)
        {
            error = null;
            note = null;
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

            // Disambiguate only ever reaches this point for the source's own leftovers.
            if (policy == ExistingMotionAssetPolicy.Overwrite)
                note = $"{destinationPath}: 기존 에셋을 삭제하고 덮어썼습니다.";
            return true;
        }

        private static string Reserve(string directory, string candidate, HashSet<string> reserved)
        {
            var baseName = candidate;
            var suffix = 2;
            while (!reserved.Add(directory + "|" + candidate))
                candidate = $"{baseName}_{suffix++}";
            return candidate;
        }

        private static string AppendSourceName(string takeName, string sourceName)
        {
            sourceName = SanitizeFileName(sourceName);
            if (string.IsNullOrEmpty(sourceName))
                return takeName;
            if (string.Equals(takeName, sourceName, StringComparison.OrdinalIgnoreCase))
                return takeName;
            // Already disambiguated by an earlier run ("드립_003" + "드립_003_001") —
            // keep the current name so re-running the tool stays idempotent.
            if (sourceName.StartsWith(takeName + "_", StringComparison.OrdinalIgnoreCase))
                return sourceName;
            if (takeName.EndsWith("_" + sourceName, StringComparison.OrdinalIgnoreCase))
                return takeName;
            return takeName + "_" + sourceName;
        }

        /// <summary>True for the "_T" T-pose copies this pipeline generates.</summary>
        public static bool IsTPoseAsset(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;
            var fileName = Path.GetFileNameWithoutExtension(assetPath);
            return !string.IsNullOrEmpty(fileName)
                && fileName.EndsWith("_T", StringComparison.OrdinalIgnoreCase);
        }

        private static string DirectoryOf(string assetPath)
        {
            return Path.GetDirectoryName(assetPath)?.Replace('\\', '/') ?? string.Empty;
        }

        private static void AppendNote(OptiTrackMotionBindingResult result, string note)
        {
            if (string.IsNullOrEmpty(note))
                return;
            result.Note = string.IsNullOrEmpty(result.Note) ? note : result.Note + "\n" + note;
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
            AppendNote(result, note);
            return result;
        }
    }
}
