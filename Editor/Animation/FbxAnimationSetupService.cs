using System.IO;
using UnityEditor;

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
