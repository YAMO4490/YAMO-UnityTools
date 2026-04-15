// ----------------------------------------------------------------------------
// YamoDependencyDetector.cs
//
// YAMO Unity Tools의 외부 패키지 의존성(MagicaCloth2 / VRM)을 자동으로 감지하고,
// Scripting Define Symbols에 다음 심볼을 자동 주입/제거하는 에디터 전용 스크립트.
//
//   - MagicaCloth2 감지됨  →  YAMO_HAS_MAGICACLOTH 활성
//   - VRM (UniVRM 0.x) 감지됨 →  YAMO_HAS_VRM 활성
//
// 이 방식의 장점:
//   1. asmdef의 "references"에 하드 의존성을 넣지 않음 → 패키지가 없어도 YAMO
//      어셈블리 자체는 항상 컴파일됨.
//   2. 설치 형태(UPM, .unitypackage, Assets/External 수동 복사)와 무관하게 동작.
//      오직 "런타임에 해당 어셈블리가 로드되어 있는가"만 본다.
//   3. 사용자가 수동으로 Scripting Define Symbols를 건드릴 필요가 없다.
//
// 감지되는 어셈블리 이름:
//   - MagicaClothV2       (Assets/External/MagicaCloth2/MagicaCloth2.asmdef의 name)
//   - VRM                 (UniVRM 0.x의 런타임 어셈블리 이름)
//
// 다른 패키지를 추가로 감지하고 싶으면 아래 Detectors 배열에 항목을 추가만 하면 됨.
// ----------------------------------------------------------------------------

using System.Collections.Generic;
using System.Linq;
using UnityEditor;
using UnityEditor.Build;

namespace YAMO.UnityTools.Editor.Internal
{
    [InitializeOnLoad]
    internal static class YamoDependencyDetector
    {
        // (어셈블리 이름, 주입할 define 심볼) 쌍. 여기에 추가하면 감지 대상이 늘어남.
        private static readonly (string assembly, string define)[] Detectors = new[]
        {
            ("MagicaClothV2", "YAMO_HAS_MAGICACLOTH"),
            ("VRM",           "YAMO_HAS_VRM"),
        };

        static YamoDependencyDetector()
        {
            // Unity 에디터 로드/스크립트 리컴파일 직후 1회 실행.
            // 에디터 작업 중 PlayerSettings API를 바로 호출해도 안전하지만,
            // 간혹 초기화 타이밍 이슈가 있어 delayCall로 안전하게 미룬다.
            EditorApplication.delayCall += Sync;
        }

        private static void Sync()
        {
            var loadedAssemblies = System.AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetName().Name)
                .ToHashSet();

            var target = NamedBuildTarget.FromBuildTargetGroup(
                EditorUserBuildSettings.selectedBuildTargetGroup);

            PlayerSettings.GetScriptingDefineSymbols(target, out string[] currentArr);
            var defines = new HashSet<string>(currentArr);
            bool changed = false;

            foreach (var (assembly, define) in Detectors)
            {
                bool present = loadedAssemblies.Contains(assembly);
                bool hasDefine = defines.Contains(define);

                if (present && !hasDefine)
                {
                    defines.Add(define);
                    changed = true;
                }
                else if (!present && hasDefine)
                {
                    defines.Remove(define);
                    changed = true;
                }
            }

            if (changed)
            {
                PlayerSettings.SetScriptingDefineSymbols(target, defines.ToArray());
                UnityEngine.Debug.Log(
                    "[YAMO] Scripting Define Symbols 업데이트: " +
                    string.Join(", ", Detectors.Select(d =>
                        $"{d.define}={(loadedAssemblies.Contains(d.assembly) ? "ON" : "OFF")}")));
            }
        }
    }
}
