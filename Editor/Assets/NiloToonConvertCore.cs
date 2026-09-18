// Convert NiloToon — Material And Texture Tool 의 섹션 4 가 호출하는 정적 코어.
//
// 절차:
//   1) 아바타가 참조하는 머티리얼을 Assets/MaterialsBackup/<아바타>_<시각>/ 에 그대로 백업 (복구용)
//   2) 루트에 NiloToonPerCharacterRenderController 를 붙이고 "Auto setup this character" 실행
//      (NiloToon 이 lilToon/MToon/URP Lit 등을 NiloToon_Character 로 변환)
//   3) 변환된 머티리얼 정리
//      - "켜져 있지만 텍스처 맵이 하나도 없는" 기능 그룹을 끔 (자동 변환의 버그성 잔여)
//      - Generic Reflection 은 항상 끔
//      - Smoothness/Roughness 그룹은 끌 수 없는 그룹이라 셰이더 기본값으로 초기화
//
// NiloToon 어셈블리를 하드 참조하지 않도록 (asmdef references: []) 리플렉션으로만 접근한다.
//
// 백업은 나중에 다른 폴더(예: 아바타 폴더)로 옮겨질 수 있으므로 경로에 의존하지 않는다.
//   - 백업 폴더를 만들 때 그 안에 내용 기록 TSV(BackupMapFileName)를 함께 남긴다
//   - 복구는 사용자가 백업 "폴더"를 직접 고르면 그 안의 TSV 를 읽어 처리한다 (LoadBackupSet)
//   - 원본/백업 머티리얼 식별 : GUID (Unity 안에서 이동·이름변경해도 유지).
//     폴백 — 백업: 선택한 폴더 안의 파일명 / 원본: 기록 당시 경로
//
// NiloToon 이 MinimumNiloToonVersion 미만이거나 미설치면 기능 자체를 노출하지 않는다 (IsSupported).

using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    public static class NiloToonConvertCore
    {
        public const string BackupRootFolder = "Assets/MaterialsBackup";   // 생성 위치일 뿐, 이후 이동돼도 무방
        public const string BackupMapFileName = "_YAMO_MaterialBackupMap.tsv";
        public const string NiloToonCharacterShaderName = "Universal Render Pipeline/NiloToon/NiloToon_Character";

        /// 이 기능을 개발·검증한 NiloToon 버전. 이보다 낮으면 Auto setup API 시그니처와
        /// NiloToon_Character 프로퍼티/키워드 이름이 다를 수 있어 기능을 노출하지 않는다.
        public const string MinimumNiloToonVersion = "0.17.14";

        private const string NiloPackageName        = "com.kuroneko-shaderlab.nilotoon-urp";
        private const string NiloEditorAssemblyName = "NiloToon.NiloToonURP.Editor";
        private const string NiloControllerTypeName = "NiloToon.NiloToonURP.NiloToonPerCharacterRenderController";
        private const string NiloAutoSetupTypeName  = "NiloToon.NiloToonURP.NiloToonEditorPerCharacterRenderControllerCustomEditor";

        private const char Tab = '\t';

        // ------------------------------------------------------------
        // 기능 그룹 정의
        // ------------------------------------------------------------

        /// 토글 + 키워드 + "이 중 하나라도 있으면 정상" 으로 보는 텍스처 슬롯들.
        private class Feature
        {
            public string Label;
            public string Toggle;
            public string Keyword;
            public string[] Textures;   // null = 조건 없이 항상 끔
            // 텍스처가 없어도 이 색이 검정이 아니면 "색만으로 동작하는 정상 기능"으로 보고 유지한다.
            // (예: lilToon 에서 맵 없이 색만 준 Emission — 헤어핀의 청록 발광. 끄면 색이 사라진다)
            public string KeepIfColorNotBlack;
        }

        private static List<Feature> _features;
        private static List<Feature> Features => _features ??= BuildFeatures();

        private static List<Feature> BuildFeatures()
        {
            var list = new List<Feature>();

            // Base Map Stacking Layer 1~10 : Layer Map / Mask Map 둘 다 없으면 끔
            for (int i = 1; i <= 10; i++)
            {
                list.Add(new Feature
                {
                    Label    = $"Base Map Stacking Layer {i}",
                    Toggle   = $"_BaseMapStackingLayer{i}Enable",
                    Keyword  = $"_BASEMAP_STACKING_LAYER{i}",
                    Textures = new[] { $"_BaseMapStackingLayer{i}Tex", $"_BaseMapStackingLayer{i}MaskTex" },
                });
            }

            // MatCap 계열 전부 : 판정은 MatCap 맵 자체 (맵 없이 마스크만 있는 MatCap 은 무의미)
            for (int i = 1; i <= 2; i++)
            {
                list.Add(new Feature
                {
                    Label    = $"MatCap{i}",
                    Toggle   = $"_UseGenericMatCap{i}",
                    Keyword  = $"_GENERIC_MATCAP{i}",
                    Textures = new[] { $"_GenericMatCap{i}Map" },
                });
            }
            list.Add(new Feature { Label = "MatCap (Color Replace)",            Toggle = "_UseMatCapAlphaBlend", Keyword = "_MATCAP_BLEND",     Textures = new[] { "_MatCapAlphaBlendMap" } });
            list.Add(new Feature { Label = "MatCap (Shadow)",                   Toggle = "_UseMatCapOcclusion",  Keyword = "_MATCAP_OCCLUSION", Textures = new[] { "_MatCapOcclusionMap" } });
            list.Add(new Feature { Label = "MatCap (Add/Specular/RimLight)",    Toggle = "_UseMatCapAdditive",   Keyword = "_MATCAP_ADD",       Textures = new[] { "_MatCapAdditiveMap" } });

            list.Add(new Feature { Label = "Emission",               Toggle = "_UseEmission",  Keyword = "_EMISSION",           Textures = new[] { "_EmissionMap" }, KeepIfColorNotBlack = "_EmissionColor" });
            list.Add(new Feature { Label = "Normal Map",             Toggle = "_UseNormalMap", Keyword = "_NORMALMAP",          Textures = new[] { "_BumpMap" } });
            list.Add(new Feature { Label = "Specular Highlights",    Toggle = "_UseSpecular",  Keyword = "_SPECULARHIGHLIGHTS", Textures = new[] { "_SpecularMap", "_SpecularColorTintMap" } });
            list.Add(new Feature { Label = "Occlusion Map (Shadow)", Toggle = "_UseOcclusion", Keyword = "_OCCLUSIONMAP",       Textures = new[] { "_OcclusionMap" } });

            // 항상 끔
            list.Add(new Feature { Label = "Generic Reflection", Toggle = "_UseGenericReflection", Keyword = "_GENERIC_REFLECTION", Textures = null });

            return list;
        }

        /// Smoothness/Roughness 그룹 — 끌 수 없으므로 셰이더 기본값으로 되돌릴 프로퍼티들.
        private static readonly string[] SmoothnessProperties =
        {
            "_Smoothness",
            "_UseSmoothnessMap",
            "_SmoothnessMap",
            "_SmoothnessMapChannelMask",
            "_SmoothnessMapInputIsRoughnessMap",
            "_SmoothnessMapRemapMinMaxSlider",
            "_SmoothnessMapRemapStart",
            "_SmoothnessMapRemapEnd",
        };
        private const string SmoothnessMapKeyword = "_SMOOTHNESSMAP";

        // ------------------------------------------------------------
        // 결과 / 데이터
        // ------------------------------------------------------------

        public class Result
        {
            public bool Success;
            public string Error;
            public string BackupFolder;
            public int BackedUpCount;
            public int ConvertedCount;                 // 변환 후 NiloToon_Character 인 머티리얼 수
            public List<string> NotConverted = new List<string>();   // 변환 후에도 NiloToon 이 아닌 머티리얼
            public List<string> Skipped = new List<string>();        // 에셋이 아니라 건드리지 않은 머티리얼
            public List<string> Changes = new List<string>();        // 정리 내역 (사람이 읽는 로그)
            public bool AlreadyNiloToon;                              // 전부 이미 NiloToon 이라 아무것도 하지 않음
            public List<string> UntouchedNilo = new List<string>();  // 원래 NiloToon 이라 정리에서 제외한 머티리얼
            public int BakedCount;                                    // lilToon 메인 컬러를 텍스처에 구운 머티리얼 수
            public List<string> Bakes = new List<string>();          // 굽기 내역

            public string ToText()
            {
                var sb = new StringBuilder();
                if (!Success) sb.AppendLine("❌ " + Error);
                if (AlreadyNiloToon)
                {
                    sb.AppendLine($"머티리얼 {ConvertedCount}개가 이미 전부 NiloToon 입니다. 변환 단계가 필요 없어 아무것도 변경하지 않았습니다 (백업 없음).");
                    return sb.ToString();
                }
                if (UntouchedNilo.Count > 0) sb.AppendLine($"원래 NiloToon 이라 그대로 둠: {UntouchedNilo.Count}개 ({string.Join(", ", UntouchedNilo)})");
                if (!string.IsNullOrEmpty(BackupFolder)) sb.AppendLine($"백업: {BackedUpCount}개 → {BackupFolder}");
                if (Bakes.Count > 0)
                {
                    sb.AppendLine($"lilToon 색 굽기: {BakedCount}개");
                    foreach (var b in Bakes) sb.AppendLine("  " + b);
                }
                sb.AppendLine($"NiloToon 머티리얼: {ConvertedCount}개");
                if (NotConverted.Count > 0) sb.AppendLine("⚠ 변환되지 않음: " + string.Join(", ", NotConverted));
                if (Skipped.Count > 0) sb.AppendLine("⚠ 에셋이 아니라 제외: " + string.Join(", ", Skipped));
                sb.AppendLine($"정리 내역: {Changes.Count}건");
                foreach (var c in Changes) sb.AppendLine("  " + c);
                return sb.ToString();
            }
        }

        public class BackupSet
        {
            public string Folder;        // 현재 위치 (이동됐으면 이동된 위치)
            public string MapPath;
            public string AvatarName;
            public string Created;       // 맵에 기록된 생성 시각 (폴더 이동과 무관)
            public int Count;
            public string DisplayName => $"{AvatarName} | {Created} | {Count}개 | {Folder}";
        }

        private struct MapRow
        {
            public string OriginalGuid, BackupGuid, BackupFileName, OriginalPath;
        }

        // ------------------------------------------------------------
        // 지원 여부 (NiloToon 설치 + 버전)
        // ------------------------------------------------------------

        public static bool IsNiloToonInstalled => FindType(NiloAutoSetupTypeName) != null;

        /// 설치된 NiloToon 버전 문자열 (package.json). 못 찾으면 null. 도메인 리로드까지 캐시.
        public static string InstalledNiloToonVersion
        {
            get
            {
                if (!_versionResolved) { _installedVersion = ResolveNiloToonVersion(); _versionResolved = true; }
                return _installedVersion;
            }
        }
        private static bool _versionResolved;
        private static string _installedVersion;

        /// NiloToon 이 설치돼 있고 버전이 MinimumNiloToonVersion 이상일 때만 true. UI 노출 조건.
        public static bool IsSupported => IsNiloToonInstalled && IsVersionSupported(InstalledNiloToonVersion);

        /// 버전 문자열이 MinimumNiloToonVersion 이상인지. 파싱 불가(null 포함)는 미지원으로 본다.
        public static bool IsVersionSupported(string versionText)
        {
            return TryParseVersion(versionText, out var installed)
                && TryParseVersion(MinimumNiloToonVersion, out var minimum)
                && installed >= minimum;
        }

        // ------------------------------------------------------------
        // 공개 API
        // ------------------------------------------------------------

        /// <summary>
        /// 백업 → (lilToon 색 굽기) → Auto setup(변환) → 정리 를 한 번에 수행.
        /// bakeLilToonMainColor: lilToon 머티리얼의 메인 컬러/HSV·Gamma/그라디언트를 텍스처에 구운 뒤 변환한다.
        /// lilToon 과 NiloToon 은 메인 컬러를 다르게 해석하므로(sRGB vs [HDR] 리니어) 굽지 않으면 색이 달라진다.
        /// </summary>
        public static Result ConvertAvatar(GameObject root, bool bakeLilToonMainColor = true)
        {
            var result = new Result();
            if (root == null) { result.Error = "타겟 오브젝트가 없습니다."; return result; }
            if (EditorUtility.IsPersistent(root))
            {
                result.Error = "씬에 배치된 오브젝트를 지정하세요. (프리팹 에셋에는 NiloToon Auto setup 을 실행할 수 없습니다)";
                return result;
            }
            if (!IsNiloToonInstalled) { result.Error = "NiloToonURP 를 찾을 수 없습니다."; return result; }
            if (!IsSupported)
            {
                result.Error = $"NiloToon {MinimumNiloToonVersion} 이상이 필요합니다. (설치됨: {InstalledNiloToonVersion ?? "버전 확인 불가"})";
                return result;
            }

            var materials = CollectMaterialAssets(root, result.Skipped);
            if (materials.Count == 0) { result.Error = "변환할 머티리얼 에셋이 없습니다."; return result; }

            // 이미 NiloToon 인 머티리얼은 변환 대상이 아니다. 전부 NiloToon 이면 이 단계 자체가 불필요 —
            // 백업도 만들지 않고, (손으로 조정했을) 값을 정리 단계가 초기화하지 않도록 아무것도 건드리지 않는다.
            var niloShader = Shader.Find(NiloToonCharacterShaderName);
            var alreadyNilo = new HashSet<Material>(materials.Where(m => m.shader == niloShader));
            if (alreadyNilo.Count == materials.Count)
            {
                result.Success = true;
                result.AlreadyNiloToon = true;
                result.ConvertedCount = materials.Count;
                return result;
            }

            // 1) 백업 — 실패하면 변환하지 않는다
            result.BackupFolder = BackupMaterials(materials, root.name, out result.BackedUpCount);
            if (result.BackedUpCount != materials.Count)
            {
                result.Error = $"백업 실패 ({result.BackedUpCount}/{materials.Count}). 변환을 중단했습니다.";
                return result;
            }

            // 1.5) lilToon 메인 컬러 굽기 — 반드시 백업 後·변환 前. (백업본은 굽기 전 상태와 원본 텍스처를 그대로 가리킨다)
            if (bakeLilToonMainColor)
                result.BakedCount = LilToonMainColorBaker.BakeAll(materials.Where(m => !alreadyNilo.Contains(m)), result.Bakes);

            // 2) NiloToon Auto setup (확인 다이얼로그 없이, 머티리얼 변환 포함)
            if (!RunNiloAutoSetup(root, out string setupError)) { result.Error = setupError; return result; }

            // 3) 정리 — 이번에 새로 변환된 머티리얼만. 원래부터 NiloToon 이던 것은 자동 변환의 잔여가 없고
            //    사용자가 조정한 값일 수 있으므로 건드리지 않는다 (필요하면 CleanupOnly 를 명시적으로 실행).
            foreach (var mat in materials)
            {
                if (mat.shader != niloShader) { result.NotConverted.Add($"{mat.name} ({mat.shader.name})"); continue; }
                result.ConvertedCount++;
                if (alreadyNilo.Contains(mat)) { result.UntouchedNilo.Add(mat.name); continue; }
                CleanupMaterial(mat, result.Changes);
            }
            AssetDatabase.SaveAssets();

            result.Success = true;
            return result;
        }

        /// <summary>타겟의 머티리얼(.mat 에셋)이 전부 이미 NiloToon_Character 인지. true 면 변환 단계 불필요.</summary>
        public static bool IsAlreadyNiloToon(GameObject root)
        {
            if (root == null) return false;
            var materials = CollectMaterialAssets(root, null);
            if (materials.Count == 0) return false;
            var niloShader = Shader.Find(NiloToonCharacterShaderName);
            return materials.All(m => m.shader == niloShader);
        }

        /// <summary>이미 NiloToon 인 머티리얼만 정리 (백업/변환 없이). 사용자가 명시적으로 요청할 때만.</summary>
        public static Result CleanupOnly(GameObject root)
        {
            var result = new Result();
            if (root == null) { result.Error = "타겟 오브젝트가 없습니다."; return result; }
            if (!IsSupported)
            {
                result.Error = $"NiloToon {MinimumNiloToonVersion} 이상이 필요합니다. (설치됨: {InstalledNiloToonVersion ?? "없음"})";
                return result;
            }

            var niloShader = Shader.Find(NiloToonCharacterShaderName);
            foreach (var mat in CollectMaterialAssets(root, result.Skipped))
            {
                if (mat.shader != niloShader) { result.NotConverted.Add($"{mat.name} ({mat.shader.name})"); continue; }
                result.ConvertedCount++;
                CleanupMaterial(mat, result.Changes);
            }
            AssetDatabase.SaveAssets();
            result.Success = true;
            return result;
        }

        /// <summary>
        /// 사용자가 고른 백업 폴더("Assets/..." 경로) 안의 맵 TSV 를 읽어 백업 세트를 만든다.
        /// 폴더에 맵 파일이 없으면 null (error 에 사유).
        /// </summary>
        public static BackupSet LoadBackupSet(string folder, out string error)
        {
            error = null;
            if (string.IsNullOrEmpty(folder)) { error = "백업 폴더를 선택하세요."; return null; }
            folder = folder.Replace('\\', '/').TrimEnd('/');
            if (!AssetDatabase.IsValidFolder(folder)) { error = "프로젝트 안의 폴더가 아닙니다: " + folder; return null; }

            string mapPath = folder + "/" + BackupMapFileName;
            if (!File.Exists(mapPath))
            {
                error = $"이 폴더에 백업 기록 파일({BackupMapFileName})이 없습니다. Convert NiloToon 이 만든 백업 폴더를 선택하세요.";
                return null;
            }

            var set = new BackupSet { MapPath = mapPath, Folder = folder, AvatarName = "?", Created = "" };
            foreach (var line in File.ReadAllLines(mapPath))
            {
                if (line.StartsWith("#avatar" + Tab))  set.AvatarName = line.Substring("#avatar".Length + 1);
                if (line.StartsWith("#created" + Tab)) set.Created    = line.Substring("#created".Length + 1);
            }
            set.Count = ReadMapRows(mapPath).Count;
            return set;
        }

        /// <summary>
        /// 백업 세트의 머티리얼 내용을 원본 머티리얼에 덮어써 복구한다. 원본의 GUID(참조)는 유지된다.
        /// 백업은 선택된 폴더에서, 원본은 GUID 로 찾으므로 둘 다 어디로 옮겨졌든 동작한다. 복구된 수를 반환.
        /// </summary>
        public static int RestoreFromBackup(BackupSet set, List<string> log = null)
        {
            if (set == null || !File.Exists(set.MapPath)) { log?.Add("백업 맵 파일이 없습니다."); return 0; }

            int restored = 0;
            foreach (var row in ReadMapRows(set.MapPath))
            {
                var original = ResolveMaterial(row.OriginalGuid, row.OriginalPath);
                // 백업: 사용자가 고른 폴더 안의 파일이 우선 (폴더를 복제해 GUID 가 달라졌어도 동작) → 없으면 GUID
                var backup = AssetDatabase.LoadAssetAtPath<Material>(set.Folder + "/" + row.BackupFileName)
                             ?? ResolveMaterial(row.BackupGuid, null);

                if (backup == null)   { log?.Add($"건너뜀(백업 없음): {row.BackupFileName}"); continue; }
                if (original == null) { log?.Add($"건너뜀(원본 머티리얼을 찾을 수 없음): {row.OriginalPath}"); continue; }
                if (original == backup) { log?.Add($"건너뜀(원본과 백업이 같은 에셋): {row.BackupFileName}"); continue; }

                Undo.RecordObject(original, "Restore Material From Backup");
                string name = original.name;
                EditorUtility.CopySerialized(backup, original);
                original.name = name;   // CopySerialized 가 이름까지 덮어쓰므로 파일명과 일치하게 되돌림
                EditorUtility.SetDirty(original);
                restored++;
            }
            AssetDatabase.SaveAssets();
            return restored;
        }

        // ------------------------------------------------------------
        // 내부
        // ------------------------------------------------------------

        private static List<Material> CollectMaterialAssets(GameObject root, List<string> skipped)
        {
            var set = new HashSet<Material>();
            var list = new List<Material>();
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                foreach (var mat in renderer.sharedMaterials)
                {
                    if (mat == null || !set.Add(mat)) continue;
                    string path = AssetDatabase.GetAssetPath(mat);
                    // .mat 에셋만 대상. FBX 임베디드/패키지/런타임 인스턴스는 백업·수정 대상이 아님
                    if (path.StartsWith("Assets/") && path.EndsWith(".mat")) list.Add(mat);
                    else skipped?.Add(mat.name);
                }
            }
            return list;
        }

        private static string BackupMaterials(List<Material> materials, string avatarName, out int count)
        {
            count = 0;
            if (!AssetDatabase.IsValidFolder(BackupRootFolder))
                AssetDatabase.CreateFolder("Assets", Path.GetFileName(BackupRootFolder));

            string folderName = SanitizeFileName(avatarName) + "_" + System.DateTime.Now.ToString("yyyyMMdd_HHmmss");
            string folder = AssetDatabase.GUIDToAssetPath(AssetDatabase.CreateFolder(BackupRootFolder, folderName));

            var map = new StringBuilder();
            map.AppendLine("# YAMO material backup map v2 - this folder may be moved as a whole (restore is GUID based).");
            map.AppendLine("# columns: originalGuid<TAB>backupGuid<TAB>backupFileName<TAB>originalPath(at backup time, fallback only)");
            map.Append("#avatar").Append(Tab).AppendLine(avatarName);
            map.Append("#created").Append(Tab).AppendLine(System.DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
            foreach (var mat in materials)
            {
                string srcPath = AssetDatabase.GetAssetPath(mat);
                string dstPath = AssetDatabase.GenerateUniqueAssetPath(folder + "/" + Path.GetFileName(srcPath));
                if (!AssetDatabase.CopyAsset(srcPath, dstPath))
                {
                    Debug.LogError($"[ConvertNiloToon] 머티리얼 백업 실패: {srcPath}", mat);
                    continue;
                }
                map.Append(AssetDatabase.AssetPathToGUID(srcPath)).Append(Tab)
                   .Append(AssetDatabase.AssetPathToGUID(dstPath)).Append(Tab)
                   .Append(Path.GetFileName(dstPath)).Append(Tab).AppendLine(srcPath);
                count++;
            }
            File.WriteAllText(Path.Combine(folder, BackupMapFileName), map.ToString(), new UTF8Encoding(false));
            AssetDatabase.Refresh();
            return folder;
        }

        private static List<MapRow> ReadMapRows(string mapPath)
        {
            var rows = new List<MapRow>();
            foreach (var line in File.ReadAllLines(mapPath))
            {
                if (line.Length == 0 || line[0] == '#') continue;
                var c = line.Split(Tab);
                if (c.Length < 4) continue;
                rows.Add(new MapRow { OriginalGuid = c[0], BackupGuid = c[1], BackupFileName = c[2], OriginalPath = c[3] });
            }
            return rows;
        }

        /// GUID 우선, 실패하면 경로로 폴백.
        private static Material ResolveMaterial(string guid, string fallbackPath)
        {
            string path = AssetDatabase.GUIDToAssetPath(guid);
            var mat = string.IsNullOrEmpty(path) ? null : AssetDatabase.LoadAssetAtPath<Material>(path);
            if (mat == null && !string.IsNullOrEmpty(fallbackPath))
                mat = AssetDatabase.LoadAssetAtPath<Material>(fallbackPath);
            return mat;
        }

        private static bool RunNiloAutoSetup(GameObject root, out string error)
        {
            error = null;
            var setupType = FindType(NiloAutoSetupTypeName);
            var method = setupType?.GetMethod("AutoSetupCharacterGameObject",
                System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public,
                null, new[] { typeof(GameObject), typeof(bool), typeof(bool) }, null);
            if (method == null)
            {
                error = "NiloToon 의 AutoSetupCharacterGameObject(GameObject, bool, bool) 를 찾을 수 없습니다. (NiloToon 버전 확인 필요)";
                return false;
            }

            // Auto setup 이 컨트롤러를 AddComponent 하므로 Undo 에 잡히도록 먼저 직접 붙인다
            var controllerType = FindType(NiloControllerTypeName);
            if (controllerType != null && root.GetComponent(controllerType) == null)
                Undo.AddComponent(root, controllerType);

            try
            {
                // shouldPromptMaterialEditConfirmation=false, shouldEditMaterialWhenConfirmationSkipped=true
                method.Invoke(null, new object[] { root, false, true });
            }
            catch (System.Exception ex)
            {
                error = "NiloToon Auto setup 중 예외: " + (ex.InnerException ?? ex).Message;
                Debug.LogException(ex.InnerException ?? ex);
                return false;
            }
            return true;
        }

        private static void CleanupMaterial(Material mat, List<string> changes)
        {
            bool dirty = false;

            foreach (var f in Features)
            {
                if (!mat.HasProperty(f.Toggle)) continue;
                bool enabled = mat.GetFloat(f.Toggle) > 0.5f || mat.IsKeywordEnabled(f.Keyword);
                if (!enabled) continue;

                if (f.Textures != null && f.Textures.Any(t => mat.HasProperty(t) && mat.GetTexture(t) != null)) continue;

                if (f.KeepIfColorNotBlack != null && mat.HasProperty(f.KeepIfColorNotBlack))
                {
                    Color c = mat.GetColor(f.KeepIfColorNotBlack);
                    if (Mathf.Max(c.r, Mathf.Max(c.g, c.b)) > 0.001f) continue;   // 색만으로 동작 중 — 유지
                }

                mat.SetFloat(f.Toggle, 0f);
                mat.DisableKeyword(f.Keyword);
                dirty = true;
                changes?.Add($"{mat.name}: {f.Label} OFF" + (f.Textures == null ? " (항상 비활성)" : " (텍스처 없음)"));
            }

            if (ResetSmoothnessGroup(mat))
            {
                dirty = true;
                changes?.Add($"{mat.name}: Smoothness/Roughness 기본값으로 초기화");
            }

            if (dirty) EditorUtility.SetDirty(mat);
        }

        /// Smoothness 그룹을 셰이더에 선언된 기본값으로 되돌린다. 바뀐 게 있으면 true.
        private static bool ResetSmoothnessGroup(Material mat)
        {
            var shader = mat.shader;
            bool changed = false;

            foreach (var prop in SmoothnessProperties)
            {
                int idx = shader.FindPropertyIndex(prop);
                if (idx < 0) continue;

                switch (shader.GetPropertyType(idx))
                {
                    case UnityEngine.Rendering.ShaderPropertyType.Float:
                    case UnityEngine.Rendering.ShaderPropertyType.Range:
                        float f = shader.GetPropertyDefaultFloatValue(idx);
                        if (!Mathf.Approximately(mat.GetFloat(prop), f)) { mat.SetFloat(prop, f); changed = true; }
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Vector:
                    case UnityEngine.Rendering.ShaderPropertyType.Color:
                        Vector4 v = shader.GetPropertyDefaultVectorValue(idx);
                        if (mat.GetVector(prop) != v) { mat.SetVector(prop, v); changed = true; }
                        break;
                    case UnityEngine.Rendering.ShaderPropertyType.Texture:
                        if (mat.GetTexture(prop) != null) { mat.SetTexture(prop, null); changed = true; }
                        break;
                }
            }

            if (mat.IsKeywordEnabled(SmoothnessMapKeyword)) { mat.DisableKeyword(SmoothnessMapKeyword); changed = true; }
            return changed;
        }

        /// NiloToon 의 package.json 에서 version 을 읽는다. Packages/ 설치와 Assets/ 임베드 모두 지원.
        private static string ResolveNiloToonVersion()
        {
            // 1) 정식 패키지로 설치된 경우
            var setupType = FindType(NiloAutoSetupTypeName);
            if (setupType != null)
            {
                var info = UnityEditor.PackageManager.PackageInfo.FindForAssembly(setupType.Assembly);
                if (info != null && info.name == NiloPackageName) return info.version;
            }

            // 2) Assets/ 아래 임베드 : asmdef 위치에서 위로 올라가며 NiloToon 의 package.json 탐색
            string asmdef = UnityEditor.Compilation.CompilationPipeline.GetAssemblyDefinitionFilePathFromAssemblyName(NiloEditorAssemblyName);
            string dir = string.IsNullOrEmpty(asmdef) ? null : Path.GetDirectoryName(asmdef);
            while (!string.IsNullOrEmpty(dir))
            {
                string json = Path.Combine(dir, "package.json");
                if (File.Exists(json))
                {
                    string text = File.ReadAllText(json);
                    if (text.Contains(NiloPackageName))
                    {
                        var m = Regex.Match(text, "\"version\"\\s*:\\s*\"([^\"]+)\"");
                        return m.Success ? m.Groups[1].Value : null;
                    }
                }
                dir = Path.GetDirectoryName(dir);
            }
            return null;
        }

        /// "0.17.14", "0.17.14-preview.1" 등을 System.Version 으로.
        private static bool TryParseVersion(string text, out System.Version version)
        {
            version = null;
            if (string.IsNullOrEmpty(text)) return false;
            var m = Regex.Match(text, "^\\d+(\\.\\d+){1,3}");
            return m.Success && System.Version.TryParse(m.Value, out version);
        }

        private static System.Type FindType(string fullName)
        {
            foreach (var asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                var t = asm.GetType(fullName);
                if (t != null) return t;
            }
            return null;
        }

        private static string SanitizeFileName(string name)
        {
            foreach (var c in Path.GetInvalidFileNameChars()) name = name.Replace(c, '_');
            return name;
        }
    }
}
