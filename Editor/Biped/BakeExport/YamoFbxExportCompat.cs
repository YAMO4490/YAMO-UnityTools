// ----------------------------------------------------------------------------
// YamoFbxExportCompat.cs
//
// FBX Exporter (com.unity.formats.fbx) 의 ModelExporter / 옵션 API 를
// reflection 으로 호출하기 위한 호환 레이어.
//
// 왜 reflection 인가?
//   - 정식 UPM 패키지(5.x): ExportModelOptions 가 public.
//   - 정식 UPM 패키지(4.x): ExportModelOptions 자체가 없음. 옵션은 internal
//     ExportModelSettingsSerialize (IExportOptions 구현) 로만 가능하고,
//     이를 받는 ExportObjects 오버로드도 internal.
//   - 임베디드(asmdef 없이 Assets/.../com.unity.formats.fbx@<hash>/ 로 풀어놓은 형태):
//     모든 코드가 default 어셈블리(Assembly-CSharp-Editor)로 들어감.
//
// YAMO 는 패키지(Packages/com.yamo.unitytools/) 형태이므로 default 어셈블리를
// 직접 참조할 수 없고, 정식 UPM 패키지를 references 에 넣으면 임베디드 환경에서
// 그 asmdef 가 없어 컴파일 실패. → 어떤 환경에서도 자기 완결적으로 동작하도록
// 컴파일 타임 의존을 두지 않고 런타임 reflection 으로 호출한다.
//
// 동작 시나리오:
//   - v5+:  ExportModelOptions (public) 사용
//           → ExportObject(string, Object, ExportModelOptions)
//   - v4:   ExportModelSettingsSerialize (internal) + IExportOptions (internal) 사용
//           → internal ExportObjects(string, Object[], IExportOptions, Dictionary<,>)
//           이 경로 덕분에 v4 에서도 UseMayaCompatibleNames=false 가 적용되어
//           FBX 노드 이름이 원본 그대로 유지됨 (공백/특수문자가 _ 로 치환되지 않음).
//   - 옵션 객체 생성 실패: 옵션 없이 simple ExportObject(string, Object) 로 fallback
//                          (이 경우 프로젝트 기본 설정이 적용되어 이름 치환이
//                           발생할 수 있으니 경고 로그)
//   - 미설치: ModelExporter 부재 → 에러 로그 + null 반환
// ----------------------------------------------------------------------------

using System;
using System.Linq;
using System.Reflection;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    internal static class YamoFbxExportCompat
    {
        private const string ModelExporterFullName  = "UnityEditor.Formats.Fbx.Exporter.ModelExporter";
        private const string OptionsTypeFullName_V5 = "UnityEditor.Formats.Fbx.Exporter.ExportModelOptions";
        private const string OptionsTypeFullName_V4 = "UnityEditor.Formats.Fbx.Exporter.ExportModelSettingsSerialize";
        private const string IExportOptionsFullName = "UnityEditor.Formats.Fbx.Exporter.IExportOptions";

        private static readonly Type _modelExporterType;

        // v5 path: public ExportObject(string, Object, ExportModelOptions)
        private static readonly Type _optionsTypeV5;
        private static readonly MethodInfo _exportWithOptionsV5;

        // v4 path: internal ExportObjects(string, Object[], IExportOptions, Dictionary<GameObject, IExportData>)
        private static readonly Type _optionsTypeV4;
        private static readonly Type _iExportOptionsType;
        private static readonly MethodInfo _exportObjectsV4;

        // 공통 fallback: public ExportObject(string, Object)
        private static readonly MethodInfo _exportSimple;

        private static readonly bool _supportsOptionsV5;
        private static readonly bool _supportsOptionsV4;
        private static readonly string _initLog;

        static YamoFbxExportCompat()
        {
            _modelExporterType = FindType(ModelExporterFullName);
            if (_modelExporterType == null)
            {
                _initLog = $"[YAMO.FbxCompat] {ModelExporterFullName} 을 찾지 못함 — FBX Exporter 가 설치되지 않음.";
                return;
            }

            // 공통: simple 오버로드 — 모든 버전에 존재
            _exportSimple = _modelExporterType.GetMethod(
                "ExportObject",
                BindingFlags.Public | BindingFlags.Static,
                null,
                new[] { typeof(string), typeof(UnityEngine.Object) },
                null);

            // v5 path
            _optionsTypeV5 = FindType(OptionsTypeFullName_V5);
            if (_optionsTypeV5 != null)
            {
                _exportWithOptionsV5 = _modelExporterType.GetMethod(
                    "ExportObject",
                    BindingFlags.Public | BindingFlags.Static,
                    null,
                    new[] { typeof(string), typeof(UnityEngine.Object), _optionsTypeV5 },
                    null);
            }
            _supportsOptionsV5 = _optionsTypeV5 != null && _exportWithOptionsV5 != null;

            // v4 path
            _optionsTypeV4      = FindType(OptionsTypeFullName_V4);
            _iExportOptionsType = FindType(IExportOptionsFullName);
            if (_optionsTypeV4 != null && _iExportOptionsType != null)
            {
                // ExportObjects (plural) — internal static, 4-arg overload.
                // 첫 3개 파라미터 타입까지만 체크 (4번째는 Dictionary<GameObject, IExportData>
                // 인데 IExportData 가 internal 이라 정확한 generic 타입 매칭이 번거롭고,
                // 4-arg 오버로드는 v4 에서 ExportObjects 라는 이름으로 유일하므로 충분).
                _exportObjectsV4 = _modelExporterType
                    .GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
                    .FirstOrDefault(m =>
                    {
                        if (m.Name != "ExportObjects") return false;
                        var ps = m.GetParameters();
                        return ps.Length == 4
                            && ps[0].ParameterType == typeof(string)
                            && ps[1].ParameterType == typeof(UnityEngine.Object[])
                            && ps[2].ParameterType == _iExportOptionsType;
                    });
            }
            _supportsOptionsV4 = _optionsTypeV4 != null && _exportObjectsV4 != null;

            _initLog = $"[YAMO.FbxCompat] ModelExporter=OK, " +
                       $"V5Options={(_supportsOptionsV5 ? "OK" : "MISSING")}, " +
                       $"V4Options={(_supportsOptionsV4 ? "OK" : "MISSING")}, " +
                       $"SimpleExport={(_exportSimple != null ? "OK" : "MISSING")}";
        }

        /// <summary>FBX Exporter 가 환경에 존재해서 ExportObject 호출 자체가 가능한가.</summary>
        public static bool ModelExporterAvailable => _modelExporterType != null;

        /// <summary>v4/v5 중 하나라도 옵션 전달이 가능한가.</summary>
        public static bool SupportsOptions => _supportsOptionsV5 || _supportsOptionsV4;

        /// <summary>초기화 로그(디버그/UI 표시용).</summary>
        public static string InitLog => _initLog;

        /// <summary>
        /// AvatarBakePipeline 에서 쓰는 표준 옵션을 만든다:
        ///   ExportFormat = Binary
        ///   UseMayaCompatibleNames = useMayaCompatibleNames 인자 그대로
        ///     - true  (기본·안전): FBX 노드명 sanitization 활성화. Unity 임포터의
        ///                          특수문자 처리 이슈를 회피.
        ///     - false (실험적):    FBX 노드명 원본 유지. 외부 FBX 편집 호환성을
        ///                          위해 사용. 일부 환경에서 mesh-node 매핑이
        ///                          꼬이는 사례가 보고됨.
        /// 환경에 맞춰 v5 (ExportModelOptions) 또는 v4 (ExportModelSettingsSerialize)
        /// 인스턴스를 만들어 반환. 둘 다 안 되면 null.
        /// </summary>
        public static object BuildBinaryExportOptions(bool useMayaCompatibleNames)
        {
            // v5: public 프로퍼티 setter 로 직접 설정
            if (_supportsOptionsV5)
            {
                var opts = Activator.CreateInstance(_optionsTypeV5);
                SetEnumProp(_optionsTypeV5, opts, "ExportFormat", 1); // 0=ASCII, 1=Binary
                SetBoolProp(_optionsTypeV5, opts, "UseMayaCompatibleNames", useMayaCompatibleNames);
                return opts;
            }

            // v4: SetXxx() 메서드 호출 (필드는 private, 프로퍼티는 read-only)
            if (_supportsOptionsV4)
            {
                var opts = Activator.CreateInstance(_optionsTypeV4);

                var setMaya = _optionsTypeV4.GetMethod(
                    "SetUseMayaCompatibleNames",
                    BindingFlags.Public | BindingFlags.Instance);
                if (setMaya != null)
                {
                    setMaya.Invoke(opts, new object[] { useMayaCompatibleNames });
                }

                var setFmt = _optionsTypeV4.GetMethod(
                    "SetExportFormat",
                    BindingFlags.Public | BindingFlags.Instance);
                if (setFmt != null)
                {
                    var fmtParamType = setFmt.GetParameters()[0].ParameterType;
                    setFmt.Invoke(opts, new[] { Enum.ToObject(fmtParamType, 1) }); // Binary
                }
                return opts;
            }

            return null;
        }

        /// <summary>
        /// 하위 호환용: 기존 호출자(BuildBinaryNoMayaCompatOptions)가 있다면 그대로 동작.
        /// 새 코드는 BuildBinaryExportOptions(bool) 를 직접 호출할 것.
        /// </summary>
        public static object BuildBinaryNoMayaCompatOptions()
            => BuildBinaryExportOptions(useMayaCompatibleNames: false);

        /// <summary>
        /// ModelExporter 의 reflection 래퍼.
        /// options 타입에 따라 v5/v4 경로를 자동 선택. 어느 쪽도 적용 불가하면
        /// 옵션 없이 simple ExportObject 로 fallback (이 경우 이름 치환 위험 경고).
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
                if (options != null)
                {
                    // v5
                    if (_supportsOptionsV5 && _optionsTypeV5.IsInstanceOfType(options))
                    {
                        return _exportWithOptionsV5.Invoke(null, new[] { filePath, obj, options }) as string;
                    }
                    // v4
                    if (_supportsOptionsV4 && _optionsTypeV4.IsInstanceOfType(options))
                    {
                        var args = new object[] { filePath, new[] { obj }, options, null };
                        return _exportObjectsV4.Invoke(null, args) as string;
                    }
                    Debug.LogWarning("[YAMO.FbxCompat] 옵션 객체 타입을 인식하지 못함 — 옵션 없이 export 합니다 " +
                                     "(이름 치환·ASCII 포맷 가능).");
                }
                else
                {
                    Debug.LogWarning("[YAMO.FbxCompat] options 가 null — 옵션 없이 export 합니다 " +
                                     "(프로젝트 기본 설정 적용, 이름 치환 가능).");
                }

                if (_exportSimple == null)
                {
                    Debug.LogError("[YAMO.FbxCompat] ModelExporter.ExportObject(string, UnityEngine.Object) 오버로드를 찾지 못함.");
                    return null;
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

        private static bool SetEnumProp(Type type, object target, string propName, int value)
        {
            var prop = type.GetProperty(propName);
            if (prop == null) return false;
            prop.SetValue(target, Enum.ToObject(prop.PropertyType, value));
            return true;
        }

        private static bool SetBoolProp(Type type, object target, string propName, bool value)
        {
            var prop = type.GetProperty(propName);
            if (prop == null) return false;
            prop.SetValue(target, value);
            return true;
        }
    }
}
