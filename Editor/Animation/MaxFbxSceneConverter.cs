using System;
using System.IO;
using Autodesk.Fbx;

namespace YAMO.UnityTools.Editor
{
    public sealed class MaxFbxConversionReport
    {
        public string SourceAxis { get; internal set; }
        public string SourceUnit { get; internal set; }
        public string AxisConversion { get; internal set; }
        public string ResultAxis { get; internal set; }
        public string ResultUnit { get; internal set; }
    }

    /// <summary>
    /// Converts Unity's right-handed Maya Y-up FBX to 3ds Max Z-up while preserving
    /// descendant bone local axes. A deep conversion is used only for left-handed input.
    /// </summary>
    public static class MaxFbxSceneConverter
    {
        public static MaxFbxConversionReport Convert(
            string sourcePath,
            string destinationPath,
            bool embedTextures = false,
            bool bipedBonesOnly = false)
        {
            if (string.IsNullOrWhiteSpace(sourcePath))
                throw new ArgumentException("Source FBX path is empty.", nameof(sourcePath));
            if (string.IsNullOrWhiteSpace(destinationPath))
                throw new ArgumentException("Destination FBX path is empty.", nameof(destinationPath));
            if (!File.Exists(sourcePath))
                throw new FileNotFoundException("Source FBX file was not found.", sourcePath);

            var report = new MaxFbxConversionReport();
            using (var manager = FbxManager.Create())
            {
                if (manager == null)
                    throw new InvalidOperationException("Failed to create the Autodesk FBX manager.");

                var settings = FbxIOSettings.Create(manager, Globals.IOSROOT);
                manager.SetIOSettings(settings);
                ConfigureImport(settings);

                using (var scene = FbxScene.Create(manager, "YamoMaxCompatibleScene"))
                using (var importer = FbxImporter.Create(manager, "YamoUnityFbxImporter"))
                {
                    if (!importer.Initialize(sourcePath, -1, settings))
                        throw CreateSdkException("Failed to initialize the FBX importer", importer.GetStatus());
                    if (!importer.Import(scene))
                        throw CreateSdkException("Failed to import the Unity FBX", importer.GetStatus());

                    if (bipedBonesOnly)
                        MarkBipedSkeleton(scene);

                    var globalSettings = scene.GetGlobalSettings();
                    var requiresHandednessConversion = false;
                    using (var sourceAxis = globalSettings.GetAxisSystem())
                    using (var sourceUnit = globalSettings.GetSystemUnit())
                    {
                        report.SourceAxis = DescribeAxis(sourceAxis);
                        report.SourceUnit = DescribeUnit(sourceUnit);
                        requiresHandednessConversion =
                            sourceAxis.GetCoorSystem() != FbxAxisSystem.ECoordSystem.eRightHanded;
                    }

                    FbxSystemUnit.cm.ConvertScene(scene);
                    if (requiresHandednessConversion)
                    {
                        FbxAxisSystem.Max.DeepConvertScene(scene);
                        report.AxisConversion = "Deep (handedness change)";
                    }
                    else
                    {
                        FbxAxisSystem.Max.ConvertScene(scene);
                        report.AxisConversion = "Root only (preserve bone local axes)";
                    }

                    if (bipedBonesOnly)
                        BakeBipedRootRotation(scene);

                    ConfigureExport(settings, embedTextures);
                    using (var exporter = FbxExporter.Create(manager, "YamoMaxFbxExporter"))
                    {
                        if (!exporter.Initialize(destinationPath, -1, settings))
                            throw CreateSdkException("Failed to initialize the FBX exporter", exporter.GetStatus());
                        if (!exporter.Export(scene))
                            throw CreateSdkException("Failed to write the Max-compatible FBX", exporter.GetStatus());
                    }
                }
            }

            InspectAndValidate(destinationPath, report, bipedBonesOnly);
            return report;
        }

        private static void BakeBipedRootRotation(FbxScene scene)
        {
            var root = scene.GetRootNode().GetChild(0);
            var stack = scene.GetCurrentAnimationStack();
            if (stack == null || stack.GetMemberCount() != 1)
                throw new InvalidDataException("Biped FBX requires one animation layer.");

            var layer = stack.GetAnimLayerMember(0);
            var curves = new[]
            {
                root.LclRotation.GetCurve(layer, "X", false),
                root.LclRotation.GetCurve(layer, "Y", false),
                root.LclRotation.GetCurve(layer, "Z", false)
            };
            if (curves[0] == null || curves[1] == null || curves[2] == null)
                throw new InvalidDataException("Biped root rotation requires three sampled curves.");

            var count = curves[0].KeyGetCount();
            if (count == 0 || curves[1].KeyGetCount() != count || curves[2].KeyGetCount() != count)
                throw new InvalidDataException("Biped root rotation curves must use matching sample times.");

            var rotations = new FbxVector4[count];
            for (var index = 0; index < count; index++)
            {
                using (var time = curves[0].KeyGetTime(index))
                using (var yTime = curves[1].KeyGetTime(index))
                using (var zTime = curves[2].KeyGetTime(index))
                {
                    if (time.GetSecondDouble() != yTime.GetSecondDouble() || time.GetSecondDouble() != zTime.GetSecondDouble())
                        throw new InvalidDataException("Biped root rotation curves must use matching sample times.");
                    using (var transform = root.EvaluateLocalTransform(time))
                        rotations[index] = transform.GetR();
                }
            }

            // Max's animation update onto an existing Biped misinterprets the
            // root-only axis correction when it is stored as FBX PreRotation.
            // Preserve the evaluated Z-up pose in ordinary XYZ Euler keys.
            var pivot = FbxNode.EPivotSet.eSourcePivot;
            root.SetPreRotation(pivot, new FbxVector4(0, 0, 0));
            root.SetPostRotation(pivot, new FbxVector4(0, 0, 0));
            root.SetRotationOrder(pivot, FbxEuler.EOrder.eOrderXYZ);
            root.LclRotation.Set(new FbxDouble3(rotations[0][0], rotations[0][1], rotations[0][2]));
            for (var axis = 0; axis < curves.Length; axis++)
            {
                var curve = curves[axis];
                curve.KeyModifyBegin();
                try
                {
                    var previous = (float)rotations[0][axis];
                    for (var index = 0; index < count; index++)
                    {
                        var value = previous + UnityEngine.Mathf.DeltaAngle(previous, (float)rotations[index][axis]);
                        curve.KeySetValue(index, value);
                        previous = value;
                    }
                }
                finally
                {
                    curve.KeyModifyEnd();
                }
            }
        }
        private static void MarkBipedSkeleton(FbxScene scene)
        {
            var root = scene.GetRootNode();
            if (root.GetChildCount() != 1 || root.GetChild(0).GetName() != "Bip001")
                throw new InvalidDataException("Bone-only FBX must contain a single Bip001 root.");
            MarkBipedNode(scene, root.GetChild(0), true);
        }

        private static void MarkBipedNode(FbxScene scene, FbxNode node, bool root)
        {
            if ((!root && !node.GetName().StartsWith("Bip001 ", StringComparison.Ordinal)) || node.GetMesh() != null)
                throw new InvalidDataException("Unexpected non-Biped FBX node: " + node.GetName());
            if (node.GetSkeleton() == null)
            {
                var skeleton = FbxSkeleton.Create(scene, node.GetName());
                skeleton.SetSkeletonType(root ? FbxSkeleton.EType.eRoot : FbxSkeleton.EType.eLimbNode);
                node.SetNodeAttribute(skeleton);
            }
            for (var index = 0; index < node.GetChildCount(); index++)
                MarkBipedNode(scene, node.GetChild(index), false);
        }

        private static void ValidateBipedNode(FbxNode node)
        {
            if (node.GetSkeleton() == null || node.GetMesh() != null ||
                (node.GetName() != "Bip001" && !node.GetName().StartsWith("Bip001 ", StringComparison.Ordinal)))
                throw new InvalidDataException("Invalid Biped skeleton node in exported FBX: " + node.GetName());
            for (var index = 0; index < node.GetChildCount(); index++)
                ValidateBipedNode(node.GetChild(index));
        }
        private static void InspectAndValidate(string path, MaxFbxConversionReport report, bool bipedBonesOnly)
        {
            using (var manager = FbxManager.Create())
            {
                var settings = FbxIOSettings.Create(manager, Globals.IOSROOT);
                manager.SetIOSettings(settings);
                ConfigureImport(settings);

                using (var scene = FbxScene.Create(manager, "YamoFbxValidationScene"))
                using (var importer = FbxImporter.Create(manager, "YamoFbxValidationImporter"))
                {
                    if (!importer.Initialize(path, -1, settings) || !importer.Import(scene))
                        throw CreateSdkException("The exported FBX could not be read back", importer.GetStatus());

                    if (bipedBonesOnly)
                    {
                        var root = scene.GetRootNode();
                        if (root.GetChildCount() != 1 || root.GetChild(0).GetName() != "Bip001")
                            throw new InvalidDataException("Unexpected FBX root after Biped export.");
                        ValidateBipedNode(root.GetChild(0));
                    }

                    var globalSettings = scene.GetGlobalSettings();
                    using (var axis = globalSettings.GetAxisSystem())
                    using (var unit = globalSettings.GetSystemUnit())
                    {
                        report.ResultAxis = DescribeAxis(axis);
                        report.ResultUnit = DescribeUnit(unit);
                        if (axis != FbxAxisSystem.Max)
                            throw new InvalidDataException($"FBX validation failed: expected Max axis, got {report.ResultAxis}.");

                        const double tolerance = 0.000001d;
                        if (Math.Abs(unit.GetScaleFactor() - FbxSystemUnit.cm.GetScaleFactor()) > tolerance)
                            throw new InvalidDataException($"FBX validation failed: expected centimeters, got {report.ResultUnit}.");
                    }
                }
            }
        }

        private static void ConfigureImport(FbxIOSettings settings)
        {
            settings.SetBoolProp(Globals.IMP_FBX_GLOBAL_SETTINGS, true);
            settings.SetBoolProp(Globals.IMP_FBX_MATERIAL, true);
            settings.SetBoolProp(Globals.IMP_FBX_TEXTURE, true);
            settings.SetBoolProp(Globals.IMP_FBX_ANIMATION, true);
            settings.SetBoolProp(Globals.IMP_FBX_EXTRACT_EMBEDDED_DATA, false);
        }

        private static void ConfigureExport(FbxIOSettings settings, bool embedTextures)
        {
            settings.SetBoolProp(Globals.EXP_FBX_GLOBAL_SETTINGS, true);
            settings.SetBoolProp(Globals.EXP_FBX_MATERIAL, true);
            settings.SetBoolProp(Globals.EXP_FBX_TEXTURE, true);
            settings.SetBoolProp(Globals.EXP_FBX_ANIMATION, true);
            settings.SetBoolProp(Globals.EXP_FBX_EMBEDDED, embedTextures);
        }

        private static Exception CreateSdkException(string message, FbxStatus status)
        {
            return new InvalidOperationException($"{message}: {status.GetCode()} - {status.GetErrorString()}");
        }

        private static string DescribeAxis(FbxAxisSystem axis)
        {
            return $"Up={axis.GetUpVector()}, Front={axis.GetFrontVector()}, Handedness={axis.GetCoorSystem()}";
        }

        private static string DescribeUnit(FbxSystemUnit unit)
        {
            return $"{unit.GetScaleFactorAsString(true)} (scale {unit.GetScaleFactor():0.######})";
        }
    }
}
