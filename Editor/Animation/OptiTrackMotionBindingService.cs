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

    /// <summary>Skeleton naming convention of a motion FBX.</summary>
    public enum MocapSourceFormat
    {
        /// <summary>
        /// OptiTrack Motive export: every bone carries the actor prefix
        /// ("001_Hips", "001_Spine1"…) and the take name identifies the motion.
        /// </summary>
        OptiTrack,

        /// <summary>
        /// MMRP (Mingle Motion Replayer) export: bones already use the Unity
        /// humanoid standard names ("Hips", "LeftUpperArm", "LeftHandIndex1"…)
        /// without any prefix, the take is always "Take 001", and the file
        /// name identifies the motion.
        /// </summary>
        MMRP
    }

    public static class MocapSourceFormatExtensions
    {
        public static string GetLabel(this MocapSourceFormat format)
        {
            return format == MocapSourceFormat.MMRP ? "MMRP" : "OptiTrack";
        }
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
    /// Reusable motion FBX binding pipeline (OptiTrack and MMRP skeletons) used by
    /// the mocap window's binding-only tool and the batch pipeline workflows.
    /// </summary>
    public static class OptiTrackMotionBindingService
    {
        private const string SpineBone = "_Spine";
        private const string ChestBone = "_Spine1";
        private const string SourceBackupMarker = "YAMO_MOCAP_SOURCE_BACKUP=";

        private static readonly KeyValuePair<string, string>[] OptiTrackHumanBoneSuffixes =
        {
            new KeyValuePair<string, string>("Hips", "_Hips"),
            new KeyValuePair<string, string>("LeftUpperLeg", "_LeftUpLeg"),
            new KeyValuePair<string, string>("RightUpperLeg", "_RightUpLeg"),
            new KeyValuePair<string, string>("LeftLowerLeg", "_LeftLeg"),
            new KeyValuePair<string, string>("RightLowerLeg", "_RightLeg"),
            new KeyValuePair<string, string>("LeftFoot", "_LeftFoot"),
            new KeyValuePair<string, string>("RightFoot", "_RightFoot"),
            new KeyValuePair<string, string>("Spine", SpineBone),
            new KeyValuePair<string, string>("Chest", ChestBone),
            new KeyValuePair<string, string>("Neck", "_Neck"),
            new KeyValuePair<string, string>("Head", "_Head"),
            new KeyValuePair<string, string>("LeftShoulder", "_LeftShoulder"),
            new KeyValuePair<string, string>("RightShoulder", "_RightShoulder"),
            new KeyValuePair<string, string>("LeftUpperArm", "_LeftArm"),
            new KeyValuePair<string, string>("RightUpperArm", "_RightArm"),
            new KeyValuePair<string, string>("LeftLowerArm", "_LeftForeArm"),
            new KeyValuePair<string, string>("RightLowerArm", "_RightForeArm"),
            new KeyValuePair<string, string>("LeftHand", "_LeftHand"),
            new KeyValuePair<string, string>("RightHand", "_RightHand"),
            new KeyValuePair<string, string>("LeftToes", "_LeftToeBase"),
            new KeyValuePair<string, string>("RightToes", "_RightToeBase"),
            new KeyValuePair<string, string>("Left Thumb Proximal", "_LeftHandThumb1"),
            new KeyValuePair<string, string>("Left Thumb Intermediate", "_LeftHandThumb2"),
            new KeyValuePair<string, string>("Left Thumb Distal", "_LeftHandThumb3"),
            new KeyValuePair<string, string>("Left Index Proximal", "_LeftHandIndex1"),
            new KeyValuePair<string, string>("Left Index Intermediate", "_LeftHandIndex2"),
            new KeyValuePair<string, string>("Left Index Distal", "_LeftHandIndex3"),
            new KeyValuePair<string, string>("Left Middle Proximal", "_LeftHandMiddle1"),
            new KeyValuePair<string, string>("Left Middle Intermediate", "_LeftHandMiddle2"),
            new KeyValuePair<string, string>("Left Middle Distal", "_LeftHandMiddle3"),
            new KeyValuePair<string, string>("Left Ring Proximal", "_LeftHandRing1"),
            new KeyValuePair<string, string>("Left Ring Intermediate", "_LeftHandRing2"),
            new KeyValuePair<string, string>("Left Ring Distal", "_LeftHandRing3"),
            new KeyValuePair<string, string>("Left Little Proximal", "_LeftHandPinky1"),
            new KeyValuePair<string, string>("Left Little Intermediate", "_LeftHandPinky2"),
            new KeyValuePair<string, string>("Left Little Distal", "_LeftHandPinky3"),
            new KeyValuePair<string, string>("Right Thumb Proximal", "_RightHandThumb1"),
            new KeyValuePair<string, string>("Right Thumb Intermediate", "_RightHandThumb2"),
            new KeyValuePair<string, string>("Right Thumb Distal", "_RightHandThumb3"),
            new KeyValuePair<string, string>("Right Index Proximal", "_RightHandIndex1"),
            new KeyValuePair<string, string>("Right Index Intermediate", "_RightHandIndex2"),
            new KeyValuePair<string, string>("Right Index Distal", "_RightHandIndex3"),
            new KeyValuePair<string, string>("Right Middle Proximal", "_RightHandMiddle1"),
            new KeyValuePair<string, string>("Right Middle Intermediate", "_RightHandMiddle2"),
            new KeyValuePair<string, string>("Right Middle Distal", "_RightHandMiddle3"),
            new KeyValuePair<string, string>("Right Ring Proximal", "_RightHandRing1"),
            new KeyValuePair<string, string>("Right Ring Intermediate", "_RightHandRing2"),
            new KeyValuePair<string, string>("Right Ring Distal", "_RightHandRing3"),
            new KeyValuePair<string, string>("Right Little Proximal", "_RightHandPinky1"),
            new KeyValuePair<string, string>("Right Little Intermediate", "_RightHandPinky2"),
            new KeyValuePair<string, string>("Right Little Distal", "_RightHandPinky3")
        };

        /// <summary>Humanoid slots the Biped pipeline never maps, in either format.</summary>
        private static readonly string[] ExcludedHumanBones = { "LeftEye", "RightEye", "Jaw", "UpperChest" };

        /// <summary>Human name → acceptable bone names for Unity-standard skeletons (MMRP).</summary>
        private static readonly KeyValuePair<string, string[]>[] StandardHumanBoneNames = BuildStandardHumanBoneNames();

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
        /// <param name="format">
        /// Skeleton convention of the source. A source whose skeleton clearly belongs
        /// to the other format is refused before anything is renamed or copied.
        /// </param>
        public static OptiTrackMotionBindingResult Process(
            string sourcePath,
            ExistingMotionAssetPolicy existingAssetPolicy = ExistingMotionAssetPolicy.Fail,
            string desiredAnimationName = null,
            MocapSourceFormat format = MocapSourceFormat.OptiTrack)
        {
            var result = new OptiTrackMotionBindingResult { SourcePath = sourcePath };
            var importer = AssetImporter.GetAtPath(sourcePath) as ModelImporter;
            if (importer == null)
                return Fail(result, $"{sourcePath}: ModelImporter 아님");

            // A "_T" file is this tool's own T-pose asset; binding it would rename the
            // avatar the real motion points at. Refuse instead of destroying it.
            if (IsTPoseAsset(sourcePath))
                return Fail(result, $"{sourcePath}: _T(T 포즈) 파일은 바인딩 대상이 아닙니다. 목록에서 제외하세요.");

            var formatError = ValidateSourceFormat(sourcePath, format);
            if (formatError != null)
                return Fail(result, formatError);

            var animationName = SanitizeFileName(
                string.IsNullOrWhiteSpace(desiredAnimationName)
                    ? ResolveAnimationName(importer, sourcePath, format)
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

            if (!BuildTPoseAvatar(tPosePath, format, out var tPoseAvatar, out var note))
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
                    FbxAnimationSetupService.ApplyRootBakeDefaults(clip);

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
            return PlanAnimationNames(sourcePaths, MocapSourceFormat.OptiTrack, out notes);
        }

        /// <param name="format">
        /// Decides where the base name comes from: the FBX take for OptiTrack, the
        /// file name for MMRP (whose take is always "Take 001").
        /// </param>
        public static Dictionary<string, string> PlanAnimationNames(
            IEnumerable<string> sourcePaths,
            MocapSourceFormat format,
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

                var takeName = SanitizeFileName(ResolveAnimationName(importer, sourcePath, format));
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

        /// <summary>
        /// The shared disambiguation rule: qualify a colliding name with the source
        /// file name (the actor number for per-actor OptiTrack exports), unless the
        /// name already carries it. Returns the name unchanged when it adds nothing.
        /// </summary>
        public static string AppendSourceName(string takeName, string sourceName)
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
            return FileNameEndsWith(assetPath, "_T");
        }

        /// <summary>True for the "_Backup" source copies this pipeline generates.</summary>
        public static bool IsBackupAsset(string assetPath)
        {
            return FileNameEndsWith(assetPath, "_Backup");
        }

        /// <summary>
        /// True for any file this pipeline produced beside a source ("_T", "_Backup").
        /// Folder scans skip these so a re-run never binds its own leftovers.
        /// </summary>
        public static bool IsGeneratedAsset(string assetPath)
        {
            return IsTPoseAsset(assetPath) || IsBackupAsset(assetPath);
        }

        private static bool FileNameEndsWith(string assetPath, string suffix)
        {
            if (string.IsNullOrEmpty(assetPath))
                return false;
            var fileName = Path.GetFileNameWithoutExtension(assetPath);
            return !string.IsNullOrEmpty(fileName)
                && fileName.EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// Guesses the skeleton convention of a model asset from its bone names.
        /// Returns null when the asset cannot be loaded or matches neither format.
        /// </summary>
        public static MocapSourceFormat? DetectSourceFormat(string assetPath)
        {
            if (string.IsNullOrEmpty(assetPath) || !(AssetImporter.GetAtPath(assetPath) is ModelImporter))
                return null;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            return model == null ? null : DetectSourceFormat(model.transform);
        }

        /// <summary>
        /// OptiTrack when any bone ends with "_Hips", MMRP when a bone is named
        /// exactly "Hips", otherwise null.
        /// </summary>
        public static MocapSourceFormat? DetectSourceFormat(Transform root)
        {
            if (root == null)
                return null;
            var transforms = root.GetComponentsInChildren<Transform>(true);
            if (transforms.Any(transform => transform.name.EndsWith("_Hips", StringComparison.OrdinalIgnoreCase)))
                return MocapSourceFormat.OptiTrack;
            if (transforms.Any(transform => string.Equals(transform.name, "Hips", StringComparison.OrdinalIgnoreCase)))
                return MocapSourceFormat.MMRP;
            return null;
        }

        /// <summary>
        /// Error text when the source's skeleton clearly belongs to the other format,
        /// otherwise null. Callers check this before creating any backup or copy so a
        /// wrong-tab mistake leaves no leftovers behind.
        /// </summary>
        public static string ValidateSourceFormat(string sourcePath, MocapSourceFormat format)
        {
            var detected = DetectSourceFormat(sourcePath);
            if (!detected.HasValue || detected.Value == format)
                return null;
            return $"{sourcePath}: {detected.Value.GetLabel()} 형식 스켈레톤입니다. " +
                   $"{format.GetLabel()} 탭이 아니라 {detected.Value.GetLabel()} 탭에서 처리하세요.";
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

        private static bool BuildTPoseAvatar(
            string tPosePath,
            MocapSourceFormat format,
            out Avatar avatar,
            out string note)
        {
            avatar = null;
            note = null;
            var importer = AssetImporter.GetAtPath(tPosePath) as ModelImporter;
            if (importer == null)
            {
                note = $"{tPosePath}: T ModelImporter 아님";
                return false;
            }

            // A copied motion FBX can inherit an invalid Humanoid description.
            // Import it as Generic first so the complete transform hierarchy is
            // available before constructing the new avatar mapping.
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.importAnimation = false;
            importer.SaveAndReimport();

            var model = AssetDatabase.LoadAssetAtPath<GameObject>(tPosePath);
            if (model == null)
            {
                note = $"{tPosePath}: T 모델 로드 실패";
                return false;
            }

            HumanBone[] human;
            SkeletonBone[] skeleton;
            var translationDof = false;
            string prefix = null;
            if (format == MocapSourceFormat.MMRP)
            {
                // MMRP already uses the Unity humanoid names, so the exact-name table
                // is authoritative; AvatarSetupTool only backs it up.
                if (!TryBuildStandardHumanoid(model, out human, out skeleton, out var mappingError) &&
                    !TryCaptureHumanoid(model, out human, out skeleton, out translationDof))
                {
                    note = $"{tPosePath}: 휴머노이드 매핑 캡처 실패 ({mappingError}; AvatarSetupTool 결과 없음)";
                    return false;
                }
            }
            else
            {
                if (!TryBuildOptiTrackHumanoid(
                        model,
                        out human,
                        out skeleton,
                        out translationDof,
                        out var mappingError) &&
                    !TryCaptureHumanoid(model, out human, out skeleton, out translationDof))
                {
                    note = $"{tPosePath}: 휴머노이드 매핑 캡처 실패 ({mappingError}; AvatarSetupTool 결과 없음)";
                    return false;
                }

                prefix = FindOptiTrackPrefix(human);
                if (string.IsNullOrEmpty(prefix))
                {
                    note = $"{tPosePath}: Hips 본에서 접두사 탐지 실패 " +
                           "(OptiTrack 형식은 '001_Hips'처럼 접두사가 필요합니다. MMRP 파일이면 MMRP 탭을 사용하세요.)";
                    return false;
                }
            }

            var description = importer.humanDescription;
            description.human = RemapHumanBones(human, format, prefix);
            description.skeleton = skeleton;
            description.hasTranslationDoF = translationDof;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.importAnimation = false;
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

        private static string FindOptiTrackPrefix(IEnumerable<HumanBone> human)
        {
            foreach (var humanBone in human)
            {
                if (humanBone.humanName == "Hips" &&
                    !string.IsNullOrEmpty(humanBone.boneName) &&
                    humanBone.boneName.EndsWith("_Hips", StringComparison.Ordinal))
                    return humanBone.boneName.Substring(0, humanBone.boneName.Length - "_Hips".Length);
            }
            return null;
        }

        /// <summary>
        /// Drops the mappings the Biped pipeline never wants (eyes, jaw, upper chest)
        /// and, for OptiTrack, forces the spine chain onto the prefixed Spine/Spine1
        /// bones because the automatic mapping lands one bone too high.
        /// </summary>
        public static HumanBone[] RemapHumanBones(
            IEnumerable<HumanBone> human,
            MocapSourceFormat format,
            string optiTrackPrefix)
        {
            var remapped = new List<HumanBone>();
            foreach (var humanBone in human)
            {
                if (Array.IndexOf(ExcludedHumanBones, humanBone.humanName) >= 0)
                    continue;

                // AvatarSetupTool happily latches humanoid slots onto MMRP helper nodes
                // (e.g. RightEye → MMRP_Deform_Palette_106); never keep those.
                if (format == MocapSourceFormat.MMRP && IsMmrpHelperBone(humanBone.boneName))
                    continue;

                var remappedBone = humanBone;
                if (format == MocapSourceFormat.OptiTrack && !string.IsNullOrEmpty(optiTrackPrefix))
                {
                    if (humanBone.humanName == "Spine")
                        remappedBone.boneName = optiTrackPrefix + SpineBone;
                    else if (humanBone.humanName == "Chest")
                        remappedBone.boneName = optiTrackPrefix + ChestBone;
                }
                remapped.Add(remappedBone);
            }
            return remapped.ToArray();
        }

        private static bool IsMmrpHelperBone(string boneName)
        {
            return !string.IsNullOrEmpty(boneName) &&
                   boneName.StartsWith("MMRP_", StringComparison.OrdinalIgnoreCase);
        }

        private static KeyValuePair<string, string[]>[] BuildStandardHumanBoneNames()
        {
            var table = new List<KeyValuePair<string, string[]>>();
            foreach (var name in new[]
                     {
                         "Hips", "Spine", "Chest", "Neck", "Head",
                         "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
                         "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand",
                         "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "LeftToes",
                         "RightUpperLeg", "RightLowerLeg", "RightFoot", "RightToes"
                     })
            {
                table.Add(new KeyValuePair<string, string[]>(name, new[] { name }));
            }

            // Fingers follow the Mecanim "LeftHandIndex1" convention. MMRP names the
            // little finger "Little"; other exporters use "Pinky", accept both.
            var phalanges = new[] { "Proximal", "Intermediate", "Distal" };
            foreach (var side in new[] { "Left", "Right" })
            {
                foreach (var finger in new[] { "Thumb", "Index", "Middle", "Ring", "Little" })
                {
                    for (var index = 0; index < phalanges.Length; index++)
                    {
                        var candidates = finger == "Little"
                            ? new[] { $"{side}HandLittle{index + 1}", $"{side}HandPinky{index + 1}" }
                            : new[] { $"{side}Hand{finger}{index + 1}" };
                        table.Add(new KeyValuePair<string, string[]>(
                            $"{side} {finger} {phalanges[index]}",
                            candidates));
                    }
                }
            }

            return table.ToArray();
        }

        /// <summary>
        /// Exact-name humanoid mapping for skeletons that already use the Unity
        /// standard bone names (MMRP exports). Eyes, jaw and upper chest are never
        /// mapped, matching <see cref="RemapHumanBones"/>.
        /// </summary>
        private static bool TryBuildStandardHumanoid(
            GameObject model,
            out HumanBone[] human,
            out SkeletonBone[] skeleton,
            out string error)
        {
            human = null;
            skeleton = null;
            error = null;
            if (model == null)
            {
                error = "모델 없음";
                return false;
            }

            var transforms = model.GetComponentsInChildren<Transform>(true);
            var transformsByName = IndexByName(transforms);
            if (!transformsByName.ContainsKey("Hips"))
            {
                error = "Hips 본 없음";
                return false;
            }

            var mapped = new List<HumanBone>(StandardHumanBoneNames.Length);
            var missingRequired = new List<string>();
            foreach (var pair in StandardHumanBoneNames)
            {
                Transform transform = null;
                foreach (var candidate in pair.Value)
                {
                    if (transformsByName.TryGetValue(candidate, out transform))
                        break;
                }

                if (transform == null)
                {
                    if (IsRequiredHumanBone(pair.Key))
                        missingRequired.Add($"{pair.Key} ({pair.Value[0]})");
                    continue;
                }

                mapped.Add(new HumanBone
                {
                    humanName = pair.Key,
                    boneName = transform.name,
                    limit = new HumanLimit { useDefaultValues = true }
                });
            }

            if (missingRequired.Count > 0)
            {
                error = "필수 본 누락: " + string.Join(", ", missingRequired);
                return false;
            }

            human = mapped.ToArray();
            skeleton = BuildSkeleton(transforms);
            return human.Length > 0 && skeleton.Length > 0;
        }

        private static Dictionary<string, Transform> IndexByName(IEnumerable<Transform> transforms)
        {
            var transformsByName = new Dictionary<string, Transform>(StringComparer.OrdinalIgnoreCase);
            foreach (var transform in transforms)
            {
                if (!transformsByName.ContainsKey(transform.name))
                    transformsByName.Add(transform.name, transform);
            }
            return transformsByName;
        }

        private static SkeletonBone[] BuildSkeleton(IEnumerable<Transform> transforms)
        {
            return transforms.Select(transform => new SkeletonBone
            {
                name = transform.name,
                position = transform.localPosition,
                rotation = transform.localRotation,
                scale = transform.localScale
            }).ToArray();
        }

        private static bool TryBuildOptiTrackHumanoid(
            GameObject model,
            out HumanBone[] human,
            out SkeletonBone[] skeleton,
            out bool hasTranslationDof,
            out string error)
        {
            human = null;
            skeleton = null;
            hasTranslationDof = false;
            error = null;
            if (model == null)
            {
                error = "모델 없음";
                return false;
            }

            var transforms = model.GetComponentsInChildren<Transform>(true);
            var hipsCandidates = transforms
                .Where(transform => transform.name.EndsWith("_Hips", StringComparison.OrdinalIgnoreCase))
                .ToArray();
            if (hipsCandidates.Length != 1)
            {
                error = $"_Hips 본 후보 {hipsCandidates.Length}개";
                return false;
            }

            var hipsName = hipsCandidates[0].name;
            var prefix = hipsName.Substring(0, hipsName.Length - "_Hips".Length);
            var transformsByName = IndexByName(transforms);

            var mapped = new List<HumanBone>(OptiTrackHumanBoneSuffixes.Length);
            var missingRequired = new List<string>();
            foreach (var pair in OptiTrackHumanBoneSuffixes)
            {
                var boneName = prefix + pair.Value;
                if (!transformsByName.TryGetValue(boneName, out var transform))
                {
                    if (IsRequiredHumanBone(pair.Key))
                        missingRequired.Add($"{pair.Key} ({boneName})");
                    continue;
                }

                mapped.Add(new HumanBone
                {
                    humanName = pair.Key,
                    boneName = transform.name,
                    limit = new HumanLimit { useDefaultValues = true }
                });
            }

            if (missingRequired.Count > 0)
            {
                error = "필수 본 누락: " + string.Join(", ", missingRequired);
                return false;
            }

            human = mapped.ToArray();
            skeleton = BuildSkeleton(transforms);
            return human.Length > 0 && skeleton.Length > 0;
        }

        private static bool IsRequiredHumanBone(string humanName)
        {
            var boneNames = HumanTrait.BoneName;
            for (var index = 0; index < boneNames.Length; index++)
            {
                if (boneNames[index] == humanName)
                    return HumanTrait.RequiredBone(index);
            }
            return false;
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

        private static string ResolveAnimationName(
            ModelImporter importer,
            string sourcePath,
            MocapSourceFormat format)
        {
            // MMRP exports always carry the generic "Take 001" take, so the file
            // name is the only meaningful identity of the motion.
            if (format == MocapSourceFormat.MMRP)
                return Path.GetFileNameWithoutExtension(sourcePath)?.Trim();

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
