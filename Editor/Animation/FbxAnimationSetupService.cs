using System;
using System.IO;
using System.Linq;
using System.Reflection;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// Batch import settings for animation FBX files: compression off, a single
    /// clip renamed after the file, and every root transform baked into the pose
    /// with "Based Upon = Original". Shared by the FBX 애니메이션 설정 tab and the
    /// motion binding pipeline so both write identical clip settings.
    /// </summary>
    public static class FbxAnimationSetupService
    {
        /// <summary>Configure a Biped animation, then apply the shared clip defaults.</summary>
        public static void ApplyBiped(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null || !assetPath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("FBX 모델을 선택해 주세요.");

            // Configure > Pose > Reset copies the imported prefab pose, rather than
            // enforcing a T-pose or zeroing the bone rotations. Capture it before reimport.
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
            GameObject instance = null;
            SkeletonBone[] resetPose;
            try
            {
                instance = UnityEngine.Object.Instantiate(model);
                instance.hideFlags = HideFlags.HideAndDontSave;
                AnimatorUtility.DeoptimizeTransformHierarchy(instance);
                var transforms = instance.GetComponentsInChildren<Transform>(true);
                var roots = transforms.Where(t => t.name == "Bip001").ToArray();
                if (roots.Length != 1 || !transforms.Any(t => t.name == "Bip001 Pelvis" && t.parent == roots[0]))
                    throw new InvalidOperationException("Bip001 → Bip001 Pelvis 구조의 Biped FBX가 아닙니다.");
                instance.name = model.name;
                // Use Unity's own pose serializer, including skeleton parent names.
                var getSkeleton = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.AvatarSetupTool")
                    ?.GetMethod("GetSkeletonBones", BindingFlags.Static | BindingFlags.Public,
                        null, new[] { typeof(Transform) }, null);
                if (getSkeleton == null)
                    throw new NotSupportedException("Unity의 Avatar 포즈 저장 API를 찾지 못했습니다.");
                resetPose = (SkeletonBone[])getSkeleton.Invoke(null, new object[] { instance.transform });
            }
            finally
            {
                if (instance != null) UnityEngine.Object.DestroyImmediate(instance);
            }

            var previous = EditorJsonUtility.ToJson(importer);
            try
            {
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                importer.sourceAvatar = null;
                importer.importAnimation = true;
                importer.SaveAndReimport();
                importer = (ModelImporter)AssetImporter.GetAtPath(assetPath);
                var description = importer.humanDescription;
                var human = description.human;
                var hips = Array.FindIndex(human, bone => bone.humanName == "Hips");
                if (hips < 0)
                    throw new InvalidOperationException("자동 Humanoid 매핑에서 Hips를 찾지 못했습니다.");
                human[hips].boneName = "Bip001";
                description.human = human;
                description.skeleton = resetPose;
                importer.humanDescription = description;
                using (var serialized = new SerializedObject(importer))
                {
                    var autoMapping = serialized.FindProperty("m_AutoGenerateAvatarMappingIfUnspecified");
                    if (autoMapping == null)
                        throw new NotSupportedException("Avatar 자동 매핑 제어 필드를 찾지 못했습니다.");
                    autoMapping.boolValue = false;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                importer.SaveAndReimport();
                var avatar = AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Avatar>().FirstOrDefault();
                if (avatar == null || !avatar.isValid || !avatar.isHuman ||
                    !avatar.humanDescription.human.Any(b => b.humanName == "Hips" && b.boneName == "Bip001"))
                    throw new InvalidOperationException("Humanoid Avatar 또는 Hips 매핑 검증에 실패했습니다.");
                Apply(assetPath);
            }
            catch
            {
                importer = (ModelImporter)AssetImporter.GetAtPath(assetPath);
                EditorJsonUtility.FromJsonOverwrite(previous, importer);
                importer.SaveAndReimport();
                throw;
            }
        }

        public static void ApplyRootBakeDefaults(ModelImporterClipAnimation clip)
        {
            if (clip == null)
                return;

            clip.lockRootRotation = true;
            clip.keepOriginalOrientation = true;

            clip.lockRootHeightY = true;
            clip.keepOriginalPositionY = true;

            clip.lockRootPositionXZ = true;
            clip.keepOriginalPositionXZ = true;
        }

        /// <summary>
        /// Applies the settings to one FBX and reimports it.
        /// Returns false when the asset is not a model FBX.
        /// </summary>
        public static bool Apply(string assetPath)
        {
            var importer = AssetImporter.GetAtPath(assetPath) as ModelImporter;
            if (importer == null)
                return false;

            importer.animationCompression = ModelImporterAnimationCompression.Off;

            // If no custom clips are defined yet, seed from the auto-generated defaults.
            var clips = importer.clipAnimations;
            if (clips == null || clips.Length == 0)
                clips = importer.defaultClipAnimations;

            if (clips != null && clips.Length > 0)
            {
                // Rename single-clip FBX to file name; multi-clip keeps existing names.
                if (clips.Length == 1)
                    clips[0].name = Path.GetFileNameWithoutExtension(assetPath);

                foreach (var clip in clips)
                    ApplyRootBakeDefaults(clip);

                importer.clipAnimations = clips;
            }

            importer.SaveAndReimport();
            return true;
        }
    }
}
