using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// Reflection facade for the incompatible public APIs of FBX Exporter 4.x and 5.x.
    /// </summary>
    public static class MocapFbxExporterCompat
    {
        private const string ModelExporterName = "UnityEditor.Formats.Fbx.Exporter.ModelExporter";
        private const string OptionsV5Name = "UnityEditor.Formats.Fbx.Exporter.ExportModelOptions";
        private const string OptionsV4Name = "UnityEditor.Formats.Fbx.Exporter.ExportModelSettingsSerialize";
        private const string OptionsInterfaceName = "UnityEditor.Formats.Fbx.Exporter.IExportOptions";

        private static readonly Type ModelExporterType = FindType(ModelExporterName);
        private static readonly Type OptionsV5Type = FindType(OptionsV5Name);
        private static readonly Type OptionsV4Type = FindType(OptionsV4Name);
        private static readonly Type OptionsInterfaceType = FindType(OptionsInterfaceName);
        private static readonly MethodInfo ExportV5 = FindExportV5();
        private static readonly MethodInfo ExportV4 = FindExportV4();
        private static readonly MethodInfo ExportSimple = FindSimpleExport();

        public static object BuildOptions(
            bool useMayaCompatibleNames,
            bool exportGeometry,
            bool animateSkinnedMesh,
            bool exportUnrendered,
            bool keepInstances)
        {
            if (OptionsV5Type != null && ExportV5 != null)
            {
                var options = Activator.CreateInstance(OptionsV5Type);
                SetEnumProperty(options, "ExportFormat", 1);
                SetEnumProperty(options, "ModelAnimIncludeOption", exportGeometry ? 2 : 1);
                SetEnumProperty(options, "LODExportType", 0);
                SetEnumProperty(options, "ObjectPosition", 1);
                SetBoolProperty(options, "UseMayaCompatibleNames", useMayaCompatibleNames);
                SetBoolProperty(options, "AnimateSkinnedMesh", animateSkinnedMesh);
                SetBoolProperty(options, "ExportUnrendered", exportUnrendered);
                SetBoolProperty(options, "KeepInstances", keepInstances);
                SetBoolProperty(options, "EmbedTextures", false);
                SetBoolProperty(options, "PreserveImportSettings", false);
                return options;
            }

            if (OptionsV4Type != null && ExportV4 != null)
            {
                var options = Activator.CreateInstance(OptionsV4Type);
                InvokeEnumSetter(options, "SetExportFormat", 1);
                InvokeEnumSetter(options, "SetModelAnimIncludeOption", exportGeometry ? 2 : 1);
                InvokeEnumSetter(options, "SetLODExportType", 0);
                InvokeEnumSetter(options, "SetObjectPosition", 1);
                InvokeBoolSetter(options, "SetUseMayaCompatibleNames", useMayaCompatibleNames);
                InvokeBoolSetter(options, "SetAnimatedSkinnedMesh", animateSkinnedMesh);
                InvokeBoolSetter(options, "SetExportUnredererd", exportUnrendered);
                InvokeBoolSetter(options, "SetPreserveImportSettings", false);
                return options;
            }

            return null;
        }

        public static string ExportObject(string path, UnityEngine.Object target, object options)
        {
            if (ModelExporterType == null)
                throw new InvalidOperationException("Unity FBX Exporter가 설치되어 있지 않습니다.");

            try
            {
                if (options != null && OptionsV5Type != null && OptionsV5Type.IsInstanceOfType(options))
                    return ExportV5?.Invoke(null, new[] { path, target, options }) as string;

                if (options != null && OptionsV4Type != null && OptionsV4Type.IsInstanceOfType(options))
                    return ExportV4?.Invoke(null, new object[] { path, new[] { target }, options, null }) as string;

                if (ExportSimple == null)
                    throw new MissingMethodException(ModelExporterName, "ExportObject");
                Debug.LogWarning("[Mocap Pipeline] FBX 버전별 옵션 API를 찾지 못해 기본 ExportObject를 사용합니다.");
                return ExportSimple.Invoke(null, new object[] { path, target }) as string;
            }
            catch (TargetInvocationException exception)
            {
                throw new InvalidOperationException(
                    "Unity FBX Exporter 호출에 실패했습니다.",
                    exception.InnerException ?? exception);
            }
        }

        private static MethodInfo FindExportV5()
        {
            return ModelExporterType?.GetMethod(
                "ExportObject",
                BindingFlags.Public | BindingFlags.Static,
                null,
                OptionsV5Type == null
                    ? Type.EmptyTypes
                    : new[] { typeof(string), typeof(UnityEngine.Object), OptionsV5Type },
                null);
        }

        private static MethodInfo FindExportV4()
        {
            if (ModelExporterType == null || OptionsInterfaceType == null)
                return null;
            return ModelExporterType
                .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                .FirstOrDefault(method =>
                {
                    if (method.Name != "ExportObjects") return false;
                    var parameters = method.GetParameters();
                    return parameters.Length == 4 &&
                           parameters[0].ParameterType == typeof(string) &&
                           parameters[1].ParameterType == typeof(UnityEngine.Object[]) &&
                           parameters[2].ParameterType == OptionsInterfaceType;
                });
        }

        private static MethodInfo FindSimpleExport()
        {
            return ModelExporterType?.GetMethod(
                "ExportObject",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(UnityEngine.Object) },
                null);
        }

        private static Type FindType(string fullName)
        {
            foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
            {
                try
                {
                    var type = assembly.GetType(fullName, false);
                    if (type != null) return type;
                }
                catch
                {
                    // Ignore assemblies that cannot enumerate a requested type.
                }
            }
            return null;
        }

        private static void SetEnumProperty(object target, string name, int value)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property?.CanWrite == true)
                property.SetValue(target, Enum.ToObject(property.PropertyType, value));
        }

        private static void SetBoolProperty(object target, string name, bool value)
        {
            var property = target.GetType().GetProperty(name, BindingFlags.Public | BindingFlags.Instance);
            if (property?.CanWrite == true)
                property.SetValue(target, value);
        }

        private static void InvokeEnumSetter(object target, string name, int value)
        {
            var method = target.GetType().GetMethod(name, BindingFlags.Public | BindingFlags.Instance);
            if (method == null) return;
            var parameterType = method.GetParameters()[0].ParameterType;
            method.Invoke(target, new[] { Enum.ToObject(parameterType, value) });
        }

        private static void InvokeBoolSetter(object target, string name, bool value)
        {
            target.GetType()
                .GetMethod(name, BindingFlags.Public | BindingFlags.Instance)
                ?.Invoke(target, new object[] { value });
        }
    }
}
