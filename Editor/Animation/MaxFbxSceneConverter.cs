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
            bool embedTextures = false)
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

            InspectAndValidate(destinationPath, report);
            return report;
        }

        private static void InspectAndValidate(string path, MaxFbxConversionReport report)
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
