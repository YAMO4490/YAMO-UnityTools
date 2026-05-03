// ----------------------------------------------------------------------------
// YamoFbxExportCompat.cs
//
// FBX Exporter (com.unity.formats.fbx) 의 ModelExporter / ExportModelOptions
// 를 reflection 으로 호출하기 위한 호환 레이어.
//
// 왜 reflection 인가?
//   - 정식 UPM 패키지(4.x): asmdef = "Unity.Formats.Fbx.Editor"
//   - 정식 UPM 패키지(5.x): asmdef = "Unity.Formats.Fbx.Editor", ExportModelOptions public
//   - 임베디드(asmdef 없이 Assets/.../com.unity.formats.fbx@<hash>/ 형태로 풀어놓음):
//     모든 코드가 default 어셈블리(Assembly-CSharp-Editor)로 들어감.
//
// YAMO 는 패키지(Packages/com.yamo.unitytools/) 형태이므로 default 어셈블리를
// 직접 참조할 수 없고, 정식 UPM 패키지를 references 에 넣으면 임베디드 환경에서
// 그 asmdef 가 없어 컴파일 실패. → 어떤 환경에서도 자기 완결적으로 동작하도록
// 컴파일 타임 의존을 두지 않고 런타임 reflection 으로 호출한다.
//
// 동작 시나리오:
//   - v5+ 정식 패키지/임베디드: ExportModelOptions 사용 (UseMayaCompatibleNames=false,
//                               ExportFormat=Binary)
//   - v4 정식 패키지            : 옵션 미지원 → 옵션 없이 ExportObject 호출 (기능 손실
//                               경고 1회 로그)
//   - FBX Exporter 미설치       : ModelExporter 자체 부재 → ExportObject 호출 시
//                               에러 로그 후 null 반환
// ----------------------------------------------------------------------------

using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    internal static class YamoFbxExportCompat
    {
        private const string ModelExporterFullName = "UnityEditor.Formats.Fbx.Exporter.ModelExporter";
        private const string OptionsTypeFullName  = "UnityEditor.Formats.Fbx.Exporter.ExportModelOptions";

        private static readonly Type _modelExporterType;
        private static readonly Type _optionsType;
        private static readonly MethodInfo _exportWithOptions;
        private static readonly MethodInfo _exportSimple;
        private static readonly bool _supportsOptions;
        private static readonly string _initLog;

        static YamoFbxExportCompat()
        {
            _modelExporterType = FindType(ModelExporterFullName);
            if (_modelExporterType == null)
            {
                _initLog = $"[YAMO.FbxCompat] {ModelExporterFullName} 을 찾지 못함 — FBX Exporter 가 설치되지 않음.";
                return;
            }

            _optionsType = FindType(OptionsTypeFullName);

            // (string path, UnityEngine.Object obj) 오버로드 — 모든 버전에 존재
            _exportSimple = _modelExporterType.GetMethod(
                "ExportObject",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(UnityEngine.Object) },
                null);

            // (string path, UnityEngine.Object obj, ExportModelOptions options) — v5+ 만 존재
            if (_optionsType != null)
            {
                _exportWithOptions = _modelExporterType.GetMethod(
                    "ExportObject",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(UnityEngine.Object), _optionsType },
                    null);
            }

            _supportsOptions = _optionsType != null && _exportWithOptions != null;
            _initLog = $"[YAMO.FbxCompat] ModelExporter=OK, Options={(_optionsType != null ? "OK" : "MISSING")}, " +
                       $"ExportWithOptions={(_exportWithOptions != null ? "OK" : "MISSING")}, " +
                       $"SupportsOptions={_supportsOptions}";
        }

        /// <summary>FBX Exporter 가 환경에 존재해서 ExportObject 호출 자체가 가능한가.</summary>
        public static bool ModelExporterAvailable => _modelExporterType != null;

        /// <summary>v5+ 의 ExportModelOptions API 를 사용할 수 있는가.</summary>
        public static bool SupportsOptions => _supportsOptions;

        /// <summary>초기화 로그(디버그/UI 표시용).</summary>
        public static string InitLog => _initLog;

        /// <summary>
        /// AvatarBakePipeline 에서 쓰는 표준 옵션:
        ///   ExportFormat = Binary, UseMayaCompatibleNames = false.
        /// v5+ 환경에서만 옵션 객체를 생성하며, v4/미설치 환경에서는 null 을 반환한다.
        /// </summary>
        public static object BuildBinaryNoMayaCompatOptions()
        {
            if (!_supportsOptions) return null;

            var opts = Activator.CreateInstance(_optionsType);
            SetEnumProp(opts, "ExportFormat", 1); // 0=ASCII, 1=Binary
            SetBoolProp(opts, "UseMayaCompatibleNames", false);
            return opts;
        }

        /// <summary>
        /// ModelExporter.ExportObject 의 reflection 래퍼.
        /// options 가 null 이거나 v4 환경이면 옵션 없는 오버로드로 fallback.
        /// FBX Exporter 자체가 없으면 null 을 반환하고 에러 로그를 남긴다.
        /// </summary>
        public static string ExportObject(string filePath, UnityEngine.Object obj, object options)
        {
            if (_modelExporterType == null)
            {
                Debug.LogError("[YAMO.FbxCompat] FBX Exporter 가 설치되어 있지 않습니다. " +
                               "Package Manager 또는 Assets/External/FbxExportTool/ 의 임베디드 패키지를 확인하세요.");
                return null;
            }

            try
            {
                if (_supportsOptions && options != null)
                {
                    return _exportWithOptions.Invoke(null, new[] { filePath, obj, options }) as string;
                }

                if (_exportSimple == null)
                {
                    Debug.LogError("[YAMO.FbxCompat] ModelExporter.ExportObject(string, UnityEngine.Object) 오버로드를 찾지 못함.");
                    return null;
                }

                if (options != null && !_supportsOptions)
                {
                    Debug.LogWarning("[YAMO.FbxCompat] 현재 FBX Exporter 가 v4 수준이라 ExportModelOptions 미지원. " +
                                     "옵션 없이 export 합니다(이름 dot 치환·ASCII 포맷 가능).");
                }

                return _exportSimple.Invoke(null, new object[] { filePath, obj }) as string;
            }
            catch (TargetInvocationException tie)
            {
                Debug.LogError($"[YAMO.FbxCompat] ExportObject 호출 중 예외: {tie.InnerException?.Message ?? tie.Message}");
                return null;
            }
            catch (Exception ex)
            {
                Debug.LogError($"[YAMO.FbxCompat] ExportObject 호출 실패: {ex.Message}");
                return null;
            }
        }

        // ──────── 내부 유틸 ────────

        private static Type FindType(string fullName)
        {
            // 모든 로드된 어셈블리에서 검색 — UPM 패키지(asmdef) / 임베디드(default 어셈블리)
            // 어디에 있어도 잡힌다.
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t;
                try { t = asm.GetType(fullName, false); }
                catch { continue; }
                if (t != null) return t;
            }
            return null;
        }

        private static bool SetEnumProp(object target, string propName, int value)
        {
            var prop = _optionsType.GetProperty(propName);
            if (prop == null) return false;
            prop.SetValue(target, Enum.ToObject(prop.PropertyType, value));
            return true;
        }

        private static bool SetBoolProp(object target, string propName, bool value)
        {
            var prop = _optionsType.GetProperty(propName);
            if (prop == null) return false;
            prop.SetValue(target, value);
            return true;
        }
    }
}
