using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using Autodesk.Fbx;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor.Tests
{
    internal sealed class MocapPipelineServicesTests
    {
        [Test]
        public void PipelineDefaultsUseSixtyFramesPerSecond()
        {
            Assert.That(new ForearmHingeBakeSettings().SampleRate, Is.EqualTo(60));
            Assert.That(new BipedFbxExportSettings().FrameRate, Is.EqualTo(60f));
            Assert.That(new BipedFbxExportSettings().UseCompatibleNames, Is.False);
            Assert.That(new MocapPipelineSettings().SampleRate, Is.EqualTo(60));
            Assert.That(new MocapPipelineSettings().HingeBakeMode, Is.EqualTo(MocapHingeBakeMode.PlayMode));
        }

        [Test]
        public void PlayModeResultBuildsInMemoryGenericTransformClip()
        {
            var path = Path.GetFullPath(Path.Combine(
                "Temp",
                $"YamoPlayModeHinge_{Guid.NewGuid():N}.bin"));
            AnimationClip clip = null;
            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                using (var writer = new BinaryWriter(File.Open(path, FileMode.Create)))
                {
                    writer.Write(2); // frames
                    writer.Write(1); // bones
                    writer.Write("Bone With Spaces");
                    writer.Write(0f); writer.Write(0f); writer.Write(0f); writer.Write(1f);
                    writer.Write(0f); writer.Write(0f); writer.Write(0f);
                    writer.Write(0f); writer.Write(0f); writer.Write(0.1f); writer.Write(0.9949874f);
                    writer.Write(1f); writer.Write(2f); writer.Write(3f);
                }

                var result = ForearmHingeBakeService.LoadPlayModeResult(path, 60, "PlayModeResult");
                clip = result.Clip;

                Assert.That(result.FrameCount, Is.EqualTo(2));
                Assert.That(result.BoneCount, Is.EqualTo(1));
                Assert.That(clip.name, Is.EqualTo("PlayModeResult"));
                Assert.That(clip.frameRate, Is.EqualTo(60f));
                var positionBinding = AnimationUtility.GetCurveBindings(clip)
                    .Single(binding =>
                        binding.path == "Bone With Spaces" &&
                        binding.type == typeof(Transform) &&
                        binding.propertyName.EndsWith("Position.x", StringComparison.OrdinalIgnoreCase));
                var positionCurve = AnimationUtility.GetEditorCurve(clip, positionBinding);
                Assert.That(positionCurve, Is.Not.Null);
                Assert.That(positionCurve.length, Is.EqualTo(2));
                Assert.That(positionCurve.keys[1].value, Is.EqualTo(1f));
            }
            finally
            {
                if (clip != null)
                    Object.DestroyImmediate(clip);
                DeleteIfPresent(path);
            }
        }

        [Test]
        public void FinalSamplingCloneRemovesAvatarWithoutChangingSourceAnimator()
        {
            var root = new GameObject("GenericSamplingSource");
            var skeletonRoot = new GameObject("SkeletonRoot");
            skeletonRoot.transform.SetParent(root.transform, false);
            var animator = root.AddComponent<Animator>();
            var avatar = AvatarBuilder.BuildGenericAvatar(root, string.Empty);
            animator.avatar = avatar;
            animator.applyRootMotion = true;
            GameObject clone = null;

            try
            {
                var method = typeof(BipedFbxExportService).GetMethod(
                    "CreateSamplingClone",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(method, Is.Not.Null);

                clone = (GameObject)method.Invoke(null, new object[] { root });
                var cloneAnimator = clone.GetComponent<Animator>();

                Assert.That(cloneAnimator, Is.Not.Null);
                Assert.That(cloneAnimator.avatar, Is.Null);
                Assert.That(cloneAnimator.applyRootMotion, Is.False);
                Assert.That(cloneAnimator.cullingMode, Is.EqualTo(AnimatorCullingMode.AlwaysAnimate));
                Assert.That(animator.avatar, Is.SameAs(avatar));
                Assert.That(animator.applyRootMotion, Is.True);
            }
            finally
            {
                if (clone != null)
                    Object.DestroyImmediate(clone);
                Object.DestroyImmediate(root);
                if (avatar != null)
                    Object.DestroyImmediate(avatar);
            }
        }

        [Test]
        public void SourceBackupPreservesFirstFbxCopyAndUsesBackupSuffix()
        {
            var token = Guid.NewGuid().ToString("N");
            var folderName = $"__YamoMocapBackupTest_{token}";
            var assetDirectory = $"Assets/{folderName}";
            var sourcePath = $"{assetDirectory}/OriginalCapture.fbx";
            var expectedBackupPath = $"{assetDirectory}/OriginalCapture_Backup.fbx";
            var root = new GameObject("BackupSourceRoot");
            var bone = new GameObject("BackupSourceBone");
            bone.transform.SetParent(root.transform, false);

            try
            {
                Assert.That(AssetDatabase.CreateFolder("Assets", folderName), Is.Not.Empty);
                var options = MocapFbxExporterCompat.BuildOptions(
                    useMayaCompatibleNames: true,
                    exportGeometry: true,
                    animateSkinnedMesh: false,
                    exportUnrendered: true,
                    keepInstances: true);
                var exportedPath = MocapFbxExporterCompat.ExportObject(sourcePath, root, options);
                Assert.That(exportedPath, Is.Not.Null.And.Not.Empty);
                AssetDatabase.ImportAsset(sourcePath, ImportAssetOptions.ForceSynchronousImport);

                var originalBytes = File.ReadAllBytes(sourcePath);
                var backupPath = OptiTrackMotionBindingService.EnsureSourceBackup(
                    sourcePath,
                    out var created);

                Assert.That(created, Is.True);
                Assert.That(backupPath, Is.EqualTo(expectedBackupPath));
                Assert.That(File.Exists(backupPath), Is.True);
                CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(backupPath));

                var reusedPath = OptiTrackMotionBindingService.EnsureSourceBackup(
                    sourcePath,
                    out var createdAgain);
                Assert.That(createdAgain, Is.False);
                Assert.That(reusedPath, Is.EqualTo(expectedBackupPath));
                CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(backupPath));

                var renameError = AssetDatabase.RenameAsset(sourcePath, "RenamedMotion");
                Assert.That(renameError, Is.Empty);
                var renamedSourcePath = $"{assetDirectory}/RenamedMotion.fbx";
                var backupAfterRename = OptiTrackMotionBindingService.EnsureSourceBackup(
                    renamedSourcePath,
                    out var createdAfterRename);
                Assert.That(createdAfterRename, Is.False);
                Assert.That(backupAfterRename, Is.EqualTo(expectedBackupPath));
                Assert.That(File.Exists($"{assetDirectory}/RenamedMotion_Backup.fbx"), Is.False);
                CollectionAssert.AreEqual(originalBytes, File.ReadAllBytes(backupPath));
            }
            finally
            {
                Object.DestroyImmediate(root);
                AssetDatabase.DeleteAsset(assetDirectory);
            }
        }

        [Test]
        public void PlanAnimationNamesKeepsEveryActorWhenTakeNamesCollide()
        {
            // Per-actor OptiTrack exports of one take share the take name, so naming
            // the output after the take alone made the second file overwrite the first.
            var token = Guid.NewGuid().ToString("N");
            var folderName = $"__YamoOptiNamePlanTest_{token}";
            var assetDirectory = $"Assets/{folderName}";
            const string takeName = "Drip_003";
            var root = new GameObject("NamePlanRoot");
            var bone = new GameObject("NamePlanBone");
            bone.transform.SetParent(root.transform, false);

            try
            {
                Assert.That(AssetDatabase.CreateFolder("Assets", folderName), Is.Not.Empty);
                var actorOne = ExportTakeFbx(assetDirectory, "001", takeName, root);
                var actorTwo = ExportTakeFbx(assetDirectory, "002", takeName, root);
                var soloTake = ExportTakeFbx(assetDirectory, "003", "Solo_007", root);

                var plan = OptiTrackMotionBindingService.PlanAnimationNames(
                    new[] { actorOne, actorTwo, soloTake },
                    out var notes);

                Assert.That(plan[actorOne], Is.EqualTo($"{takeName}_001"));
                Assert.That(plan[actorTwo], Is.EqualTo($"{takeName}_002"));
                Assert.That(plan[soloTake], Is.EqualTo("Solo_007"), "충돌하지 않는 파일은 테이크 이름을 그대로 유지해야 합니다.");
                Assert.That(notes.Count, Is.EqualTo(2));

                // Re-running the tool on the already-disambiguated files must be a no-op.
                Assert.That(AssetDatabase.RenameAsset(actorOne, plan[actorOne]), Is.Empty);
                Assert.That(AssetDatabase.RenameAsset(actorTwo, plan[actorTwo]), Is.Empty);
                var boundOne = $"{assetDirectory}/{takeName}_001.fbx";
                var boundTwo = $"{assetDirectory}/{takeName}_002.fbx";

                var replan = OptiTrackMotionBindingService.PlanAnimationNames(
                    new[] { boundOne, boundTwo },
                    out _);

                Assert.That(replan[boundOne], Is.EqualTo($"{takeName}_001"));
                Assert.That(replan[boundTwo], Is.EqualTo($"{takeName}_002"));
            }
            finally
            {
                Object.DestroyImmediate(root);
                AssetDatabase.DeleteAsset(assetDirectory);
            }
        }

        [Test]
        public void OutputNamesQualifyCollisionsWithTheSourceFileName()
        {
            var usedNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // Distinct names are left alone.
            Assert.That(
                MocapPipelineOutputNaming.MakeUnique(
                    new MocapPipelineItem(), "Drip_003_001", "Assets/Mocap/001.fbx", usedNames),
                Is.EqualTo("Drip_003_001"));

            // A second item wanting the same output is named after its source file,
            // not an opaque "_2".
            var explicitName = new MocapPipelineItem { OutputName = "Drip_003_001" };
            Assert.That(
                MocapPipelineOutputNaming.MakeUnique(
                    explicitName, "ignored", "Assets/Mocap/002.fbx", usedNames),
                Is.EqualTo("Drip_003_001_002"));

            // Only when the source name adds nothing does the numeric suffix apply.
            Assert.That(
                MocapPipelineOutputNaming.MakeUnique(
                    new MocapPipelineItem(), "Drip_003_001", "Assets/Mocap/001.fbx", usedNames),
                Is.EqualTo("Drip_003_001_2"));

            // Empty output names fall back rather than producing ".fbx".
            Assert.That(
                MocapPipelineOutputNaming.MakeUnique(
                    new MocapPipelineItem(), "   ", null, usedNames),
                Is.EqualTo("Motion"));
        }

        [Test]
        public void PlanAnimationNamesSkipsGeneratedTPoseAssets()
        {
            Assert.That(OptiTrackMotionBindingService.IsTPoseAsset("Assets/Motion/Drip_003_T.fbx"), Is.True);
            Assert.That(OptiTrackMotionBindingService.IsTPoseAsset("Assets/Motion/Drip_003.fbx"), Is.False);
        }

        // Exports a placeholder FBX and stamps the take name onto its clip list, which
        // is what the binding pipeline reads to decide the output file name.
        private static string ExportTakeFbx(
            string assetDirectory,
            string fileName,
            string takeName,
            GameObject root)
        {
            var assetPath = $"{assetDirectory}/{fileName}.fbx";
            var options = MocapFbxExporterCompat.BuildOptions(
                useMayaCompatibleNames: true,
                exportGeometry: true,
                animateSkinnedMesh: false,
                exportUnrendered: true,
                keepInstances: true);
            Assert.That(MocapFbxExporterCompat.ExportObject(assetPath, root, options), Is.Not.Null.And.Not.Empty);
            AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);

            var importer = (ModelImporter)AssetImporter.GetAtPath(assetPath);
            importer.animationType = ModelImporterAnimationType.Generic;
            importer.importAnimation = true;
            importer.clipAnimations = new[]
            {
                new ModelImporterClipAnimation
                {
                    name = takeName + "_FBX",
                    takeName = takeName,
                    firstFrame = 0f,
                    lastFrame = 1f
                }
            };
            importer.SaveAndReimport();
            return assetPath;
        }

        [Test]
        public void QueueFolderDiscoveryIncludesStandaloneAnimAndHonorsSubfolderOption()
        {
            var token = Guid.NewGuid().ToString("N");
            var folderName = $"__YamoMocapQueueTest_{token}";
            var assetDirectory = $"Assets/{folderName}";
            var nestedDirectory = $"{assetDirectory}/Nested";
            var directPath = $"{assetDirectory}/Direct.anim";
            var nestedPath = $"{nestedDirectory}/Nested.anim";
            var window = ScriptableObject.CreateInstance<MocapToBipedFbxPipelineWindow>();

            try
            {
                Assert.That(AssetDatabase.CreateFolder("Assets", folderName), Is.Not.Empty);
                Assert.That(AssetDatabase.CreateFolder(assetDirectory, "Nested"), Is.Not.Empty);
                AssetDatabase.CreateAsset(new AnimationClip(), directPath);
                AssetDatabase.CreateAsset(new AnimationClip(), nestedPath);
                AssetDatabase.SaveAssets();

                var windowType = typeof(MocapToBipedFbxPipelineWindow);
                var includeField = windowType.GetField(
                    "includeSubfolders",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                var discoverMethod = windowType.GetMethod(
                    "AddMotionPathsFromFolders",
                    BindingFlags.Instance | BindingFlags.NonPublic);
                Assert.That(includeField, Is.Not.Null);
                Assert.That(discoverMethod, Is.Not.Null);

                includeField.SetValue(window, false);
                var directOnly = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                discoverMethod.Invoke(window, new object[] { new[] { assetDirectory }, directOnly });
                CollectionAssert.AreEquivalent(new[] { directPath }, directOnly);

                includeField.SetValue(window, true);
                var recursive = new SortedSet<string>(StringComparer.OrdinalIgnoreCase);
                discoverMethod.Invoke(window, new object[] { new[] { assetDirectory }, recursive });
                CollectionAssert.AreEquivalent(new[] { directPath, nestedPath }, recursive);
            }
            finally
            {
                Object.DestroyImmediate(window);
                AssetDatabase.DeleteAsset(assetDirectory);
            }
        }

        [Test]
        public void MaxConversionPreservesNestedBoneLocalAxes()
        {
            var token = Guid.NewGuid().ToString("N");
            var sourcePath = Path.GetFullPath(Path.Combine("Temp", $"YamoPackageAxis_{token}.unity.fbx"));
            var destinationPath = Path.GetFullPath(Path.Combine("Temp", $"YamoPackageAxis_{token}.max.fbx"));
            var root = new GameObject("PackageAxisRoot");
            var parent = new GameObject("PackageAxisParent");
            var child = new GameObject("PackageAxisChild");

            try
            {
                parent.transform.SetParent(root.transform, false);
                child.transform.SetParent(parent.transform, false);
                parent.transform.localPosition = new Vector3(0.25f, 1.5f, -0.75f);
                parent.transform.localRotation = Quaternion.Euler(17f, -33f, 48f);
                child.transform.localPosition = new Vector3(-0.5f, 0.8f, 1.25f);
                child.transform.localRotation = Quaternion.Euler(-29f, 61f, 12f);

                var options = MocapFbxExporterCompat.BuildOptions(
                    useMayaCompatibleNames: true,
                    exportGeometry: true,
                    animateSkinnedMesh: false,
                    exportUnrendered: true,
                    keepInstances: true);
                var exportedPath = MocapFbxExporterCompat.ExportObject(sourcePath, root, options);

                Assert.That(exportedPath, Is.Not.Null.And.Not.Empty);
                var sourceMatrix = ReadLocalMatrix(exportedPath, child.name);
                var report = MaxFbxSceneConverter.Convert(exportedPath, destinationPath);
                var maxMatrix = ReadLocalMatrix(destinationPath, child.name);

                Assert.That(report.AxisConversion, Does.StartWith("Root only"));
                Assert.That(report.ResultAxis, Does.Contain("eZAxis"));
                AssertMatricesEqual(sourceMatrix, maxMatrix, 0.000001d);
            }
            finally
            {
                Object.DestroyImmediate(root);
                DeleteIfPresent(sourcePath);
                DeleteIfPresent(destinationPath);
            }
        }

        [Test]
        public void BipedFbxExportServiceExportsSixtyFpsAnimation()
        {
            var token = Guid.NewGuid().ToString("N");
            var outputDirectory = Path.GetFullPath(Path.Combine("Temp", $"YamoPackageExport_{token}"));
            var outputPath = Path.Combine(outputDirectory, "SixtyFps.fbx");
            var root = new GameObject("PackageExportRoot");
            var bone = new GameObject("Bone With Spaces");
            bone.transform.SetParent(root.transform, false);
            root.AddComponent<Animator>();
            var clip = new AnimationClip { name = "SixtyFpsSource", frameRate = 60f };
            clip.SetCurve(
                "Bone With Spaces",
                typeof(Transform),
                "localPosition.x",
                AnimationCurve.Linear(0f, 0f, 1f, 1f));

            try
            {
                var result = BipedFbxExportService.Export(new BipedFbxExportSettings
                {
                    TargetRoot = root,
                    Clip = clip,
                    OutputPath = outputPath,
                    StartTime = 0f,
                    Duration = 1f,
                    FrameRate = 60f,
                    RecordBlendShapes = false,
                    ExportGeometry = false,
                    CreateBackup = false
                });

                Assert.That(File.Exists(outputPath), Is.True);
                Assert.That(result.SampleCount, Is.EqualTo(61));
                Assert.That(result.Conversion.ResultAxis, Does.Contain("eZAxis"));
                Assert.That(result.Conversion.AxisConversion, Does.StartWith("Root only"));
                Assert.That(FbxContainsNode(outputPath, "Bone With Spaces"), Is.True);
                Assert.That(FbxContainsNode(outputPath, "Bone_With_Spaces"), Is.False);
            }
            finally
            {
                Object.DestroyImmediate(clip);
                Object.DestroyImmediate(root);
                if (Directory.Exists(outputDirectory))
                    Directory.Delete(outputDirectory, true);
            }
        }

        private static double[] ReadLocalMatrix(string path, string nodeName)
        {
            using (var manager = FbxManager.Create())
            {
                var settings = FbxIOSettings.Create(manager, Globals.IOSROOT);
                manager.SetIOSettings(settings);
                using (var scene = FbxScene.Create(manager, "YamoPackageAxisInspection"))
                using (var importer = FbxImporter.Create(manager, "YamoPackageAxisImporter"))
                {
                    Assert.That(importer.Initialize(path, -1, settings), Is.True, importer.GetStatus().GetErrorString());
                    Assert.That(importer.Import(scene), Is.True, importer.GetStatus().GetErrorString());
                    var node = scene.GetRootNode().FindChild(nodeName, true);
                    Assert.That(node, Is.Not.Null);

                    using (var matrix = node.EvaluateLocalTransform())
                    {
                        var values = new double[16];
                        for (var row = 0; row < 4; row++)
                        {
                            var vector = matrix.GetRow(row);
                            for (var column = 0; column < 4; column++)
                                values[row * 4 + column] = vector[column];
                        }
                        return values;
                    }
                }
            }
        }

        private static bool FbxContainsNode(string path, string nodeName)
        {
            using (var manager = FbxManager.Create())
            {
                var settings = FbxIOSettings.Create(manager, Globals.IOSROOT);
                manager.SetIOSettings(settings);
                using (var scene = FbxScene.Create(manager, "YamoPackageNameInspection"))
                using (var importer = FbxImporter.Create(manager, "YamoPackageNameImporter"))
                {
                    Assert.That(importer.Initialize(path, -1, settings), Is.True, importer.GetStatus().GetErrorString());
                    Assert.That(importer.Import(scene), Is.True, importer.GetStatus().GetErrorString());
                    return scene.GetRootNode().FindChild(nodeName, true) != null;
                }
            }
        }

        private static void AssertMatricesEqual(double[] expected, double[] actual, double tolerance)
        {
            Assert.That(actual, Has.Length.EqualTo(expected.Length));
            for (var index = 0; index < expected.Length; index++)
                Assert.That(actual[index], Is.EqualTo(expected[index]).Within(tolerance));
        }

        private static void DeleteIfPresent(string path)
        {
            if (File.Exists(path))
                File.Delete(path);
        }
    }
}
