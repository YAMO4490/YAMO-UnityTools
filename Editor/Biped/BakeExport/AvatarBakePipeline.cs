// 이 파일은 MagicaCloth2 + VRM(UniVRM 0.x) 둘 다 설치된 환경에서만 컴파일됩니다.
// 상위 폴더의 YAMO.UnityTools.Biped.Editor.asmdef 가
// defineConstraints = [YAMO_HAS_MAGICACLOTH, YAMO_HAS_VRM] 로 게이팅합니다.
//
// 본 파일의 역할:
//   "Snapshot → Activate-All → Bake → Export FBX → Import → Instantiate
//    → Migrate → Save Prefab" 풀 파이프라인을 정적 메서드로 제공.
//
// 마이그레이션 본체는 YAMO.UnityTools.Editor.AvatarMigrationCore 가 담당하고,
// 본 파일은 베이크/임포트/프리팹 저장 + 오케스트레이션을 수행합니다.

using System.Collections.Generic;
using System.IO;
using System.Linq;
using UniHumanoid;
using UnityEditor;
using UnityEngine;
// FBX Exporter 호출은 YamoFbxExportCompat (reflection) 를 통해 수행한다.
// 정식 UPM 패키지 / 임베디드(asmdef 없는 형태) / 미설치 환경 모두에서 자기 완결적으로
// 동작하기 위해 컴파일 타임 의존을 제거.

namespace YAMO.UnityTools.Editor
{
    public enum AvatarMode
    {
        Auto,
        Humanoid,
        Generic,
    }

    /// <summary>
    /// AvatarBakePipeline.Run 에 전달하는 옵션. 기본값은 일반적 사용 시나리오에 맞춰져 있습니다.
    /// </summary>
    public class AvatarBakeOptions
    {
        // ---- 입력/출력 ----
        public GameObject Source;
        public string FbxProjectPath;       // "Assets/..." 형태(프로젝트 상대경로)
        public string PrefabProjectPath;    // "Assets/..." 형태

        // ---- Avatar 모드 ----
        public AvatarMode AvatarMode = AvatarMode.Auto;
        public bool ForceTPose = false;

        // ---- 회전 보존 ----
        public bool PreserveAllRotations = true;        // 기본 ON: 모든 transform 의 world rotation 보존
        public string PreserveRotationSubstring = "";   // 위 옵션이 false 일 때만 사용.
                                                        // 비어 있으면 모두 zero (원본 BoneNormalizer 동작)
                                                        // 채우면 (예: "Bip001") 그 substring 을 포함한
                                                        // transform 만 보존, 나머지는 zero.

        // ---- 마이그레이션 카테고리 ----
        public bool MigrateActiveStates = true;
        public bool MigrateBlendShapes  = true;
        public bool MigratePhysics      = true;  // Colliders + MagicaCloth + VRMSpringBone
        public bool MigrateConstraints  = true;

        // ---- 파이프라인 옵션 ----
        public bool ValidateUniqueNames     = true;   // pre-flight: source 트리에 중복 이름 검사
        public bool ZeroBlendShapesBeforeBake = true; // 베이크 전 source 의 모든 BlendShape weight 를 0으로
                                                      // 초기화. BoneNormalizer 의 BakeMesh 가 현재 포즈를
                                                      // rest 로 굽기 때문에, 이 단계 없이 베이크하면
                                                      // 적용된 셰이프(예: 눈 감기 100)가 결과물에서 죽음.
        public bool RestoreSourceAfterBake  = true;   // 베이크 직후 source 의 on/off 와 BlendShape weight 를
                                                      // snapshot 기준으로 되돌림 (사용자 원본 보호)
        public bool UpdateWhenOffscreenInPrefab = true; // Prefab 의 모든 SkinnedMeshRenderer 에
                                                        // updateWhenOffscreen = true 적용
        public bool MaterialImportNone      = false;  // ModelImporter.materialImportMode = None
                                                      // 기본 false: Unity 기본 동작으로 슬롯 갯수/이름 보존,
                                                      // 외부 머티리얼 도구가 슬롯 이름 매칭으로 머티리얼 부착.
                                                      // true 로 하면 슬롯이 빈 상태로 임포트되며 슬롯 이름이
                                                      // 사라질 가능성이 있음.

        public bool VerboseDiagnostics = false;
        // 각 단계에서 SkinnedMeshRenderer 인벤토리 (GO 경로, sharedMesh 이름·vertex count·instance ID)
        // 를 로그로 출력. 메시 매핑이 꼬이는 문제를 진단할 때 사용.

        public string LogFilePath = null;
        // 비어 있지 않으면 파이프라인의 모든 log.Info/Warning/Error 가 콘솔/윈도우 외에
        // 이 경로의 텍스트 파일에도 함께 기록됨. 절대경로 또는 프로젝트 루트 기준 상대경로.
        // 부모 디렉터리는 필요 시 자동 생성. UTF-8 로 기록 (한글 OK).

        // 참고: 이전 버전에 있던 PreserveOriginalNamesInFbx 옵션은 표준 동작으로 흡수됨.
        // FBX export 는 항상 UseMayaCompatibleNames=false 로 진행 — 노드/머티리얼 이름의
        // 원본을 유지해 외부 편집·후속 머티리얼 매칭이 깨지지 않게 한다. step 8.5 가
        // NAME 기반 매칭이라 자식 재정렬에도 안전하므로 더 이상 옵션화할 이유가 없음.

        // ---- 콜백 ----
        public IMigrationLog Log;  // null 이면 DebugMigrationLog 사용
    }

    public static class AvatarBakePipeline
    {
        /// <summary>
        /// 풀 파이프라인 실행. 성공 시 true.
        /// snapshot, target instance 는 성공 후에도 씬에 남겨 두어 사용자가 직접 비교/정리합니다.
        /// </summary>
        public static bool Run(AvatarBakeOptions opt)
        {
            var baseLog = opt.Log ?? new DebugMigrationLog("[AvatarBake] ");
            // LogFilePath 가 설정되어 있으면 파일로도 함께 기록 (Dispose 는 finally 에서)
            TeeMigrationLog teeLog = null;
            IMigrationLog log;
            if (!string.IsNullOrEmpty(opt.LogFilePath))
            {
                var resolvedPath = ResolveLogFilePath(opt.LogFilePath);
                teeLog = new TeeMigrationLog(baseLog, resolvedPath);
                log = teeLog;
                log.Info($"Pipeline log will be written to: {resolvedPath}");
            }
            else
            {
                log = baseLog;
            }

            try
            {
            return RunInternal(opt, log);
            }
            finally
            {
                teeLog?.Dispose();
            }
        }

        private static bool RunInternal(AvatarBakeOptions opt, IMigrationLog log)
        {
            // ---------------- 입력 검증 ----------------
            if (opt.Source == null) { log.Error("Source GameObject is null."); return false; }
            if (string.IsNullOrEmpty(opt.FbxProjectPath))    { log.Error("FBX path is empty.");    return false; }
            if (string.IsNullOrEmpty(opt.PrefabProjectPath)) { log.Error("Prefab path is empty."); return false; }
            if (!opt.FbxProjectPath.StartsWith("Assets/"))    { log.Error("FBX path must be under Assets/."); return false; }
            if (!opt.PrefabProjectPath.StartsWith("Assets/")) { log.Error("Prefab path must be under Assets/."); return false; }

            // ---------------- Pre-flight ----------------
            if (opt.ValidateUniqueNames
                && !AvatarMigrationCore.ValidateNoDuplicateNames(opt.Source.transform, log))
            {
                return false;
            }

            GameObject snapshot = null;
            GameObject normalized = null;
            GameObject targetInstance = null;

            const string PB_TITLE = "Avatar Bake & Prefab";

            try
            {
                // ---------------- 0) Diagnostic: source 인벤토리 ----------------
                if (opt.VerboseDiagnostics)
                {
                    LogSmrInventory("[DIAG @0 source]", opt.Source, log);
                }

                // ---------------- 1) Snapshot ----------------
                EditorUtility.DisplayProgressBar(PB_TITLE, "Creating snapshot...", 0.05f);
                log.Info("Creating snapshot of source (preserves original on/off + data)...");
                snapshot = Object.Instantiate(opt.Source);
                snapshot.name = opt.Source.name + "__OriginalState";
                snapshot.transform.SetParent(opt.Source.transform.parent, true);

                // prefab 인스턴스라면 snapshot 을 unpack 해서 prefab 변경점 누수 방지
                if (PrefabUtility.IsPartOfPrefabInstance(snapshot))
                {
                    PrefabUtility.UnpackPrefabInstance(snapshot,
                        PrefabUnpackMode.OutermostRoot,
                        InteractionMode.AutomatedAction);
                }

                // ---------------- 2) Activate-All on live source ----------------
                EditorUtility.DisplayProgressBar(PB_TITLE, "Activating all GameObjects...", 0.10f);
                log.Info("Activating all GameObjects on source for full bake coverage...");
                ActivateAllRecursive(opt.Source.transform);

                // ---------------- 2.5) Zero BlendShape weights on source ----------------
                // BoneNormalizer 의 BakeMesh 가 현재 BlendShape 포즈를 rest 로 굽기 때문에,
                // 이걸 0 으로 초기화하지 않으면 적용 중인 셰이프(예: 눈 감기 100)가 결과물에서
                // 죽음. snapshot 은 원본 weight 를 보존하고 있으므로 마이그레이션에서 복원.
                if (opt.ZeroBlendShapesBeforeBake)
                {
                    log.Info("Zeroing BlendShape weights on source before bake...");
                    ZeroAllBlendShapes(opt.Source);
                }

                // ---------------- 3) Resolve Avatar mode + T-Pose ----------------
                var resolvedMode = ResolveAvatarMode(opt.Source, opt.AvatarMode);
                if (resolvedMode == AvatarMode.Humanoid && opt.ForceTPose)
                {
                    TryEnforceTPose(opt.Source, log);
                }

                // ---------------- 4) Bake ----------------
                EditorUtility.DisplayProgressBar(PB_TITLE, "Baking (BoneNormalizer)...", 0.20f);
                log.Info($"Baking via YamoBoneNormalizer (mode: {resolvedMode})...");
                YamoBoneNormalizer.CreateAvatarFunc createAvatar = resolvedMode == AvatarMode.Humanoid
                    ? (YamoBoneNormalizer.CreateAvatarFunc)BuildHumanoidAvatar
                    : BuildGenericAvatar;

                var normalizeOptions = BuildNormalizeOptions(opt, log);
                var (n, _) = YamoBoneNormalizer.Execute(opt.Source, createAvatar, normalizeOptions);
                normalized = n;
                normalized.transform.position = Vector3.zero; // FBX 로컬 좌표를 깔끔하게

                // ---------------- 5) Export FBX ----------------
                EditorUtility.DisplayProgressBar(PB_TITLE, "Exporting FBX...", 0.55f);
                var fbxAbsolute = ProjectRelativeToAbsolute(opt.FbxProjectPath);
                log.Info($"Exporting FBX: {opt.FbxProjectPath}");
                EnsureProjectFolder(opt.FbxProjectPath);
                EnsureDirectory(Path.GetDirectoryName(fbxAbsolute));

                // FBX 옵션을 명시 지정 (reflection 경유):
                //   - UseMayaCompatibleNames = false : FBX 노드명·머티리얼명에 원본을 유지.
                //       (점·공백 등 비-영숫자 문자가 _ 로 치환되지 않음.)
                //       Unity 임포트 후 자식이 알파벳순 재정렬되더라도 step 8.5 가 NAME 기반
                //       매칭으로 prefab 인스턴스 이름을 source 기준으로 복원하므로 안전.
                //   - ExportFormat = Binary          : 바이너리 FBX (파일 크기/호환성)
                // v5+ : ExportModelOptions (public) 사용
                // v4   : ExportModelSettingsSerialize (internal) + internal ExportObjects 호출
                // 둘 다 실패 시 옵션 없는 fallback 으로 동작 (이 경우 이름 치환 발생 가능,
                // 경고 로그가 남음).
                var exportOptions = YamoFbxExportCompat.BuildBinaryExportOptions(useMayaCompatibleNames: false);
                var written = YamoFbxExportCompat.ExportObject(fbxAbsolute, normalized, exportOptions);
                if (string.IsNullOrEmpty(written))
                {
                    log.Error("YamoFbxExportCompat.ExportObject returned null.");
                    return false;
                }

                // 정규화 임시 GO 즉시 정리
                if (opt.VerboseDiagnostics)
                {
                    LogSmrInventory("[DIAG @5 normalized (post-bake, pre-export-cleanup)]", normalized, log);
                }
                Object.DestroyImmediate(normalized);
                normalized = null;

                // ---------------- 6) (옵션) source 복원 ----------------
                if (opt.RestoreSourceAfterBake)
                {
                    EditorUtility.DisplayProgressBar(PB_TITLE, "Restoring source from snapshot...", 0.78f);
                    log.Info("Restoring source on/off + BlendShape weights from snapshot...");
                    RestoreActiveStatesFromSnapshot(opt.Source.transform, snapshot.transform);
                    RestoreBlendShapeWeightsFromSnapshot(opt.Source, snapshot);
                }

                // ---------------- 7) Import + ModelImporter 설정 ----------------
                EditorUtility.DisplayProgressBar(PB_TITLE, "Importing FBX...", 0.82f);
                AssetDatabase.ImportAsset(opt.FbxProjectPath, ImportAssetOptions.ForceSynchronousImport);
                ConfigureModelImporter(opt.FbxProjectPath, resolvedMode, opt.MaterialImportNone, log);

                // ---------------- 8) Instantiate FBX ----------------
                EditorUtility.DisplayProgressBar(PB_TITLE, "Instantiating prefab...", 0.88f);
                var fbxAsset = AssetDatabase.LoadMainAssetAtPath(opt.FbxProjectPath) as GameObject;
                if (fbxAsset == null)
                {
                    log.Error($"Failed to load FBX asset at {opt.FbxProjectPath}.");
                    return false;
                }
                targetInstance = (GameObject)PrefabUtility.InstantiatePrefab(fbxAsset);
                if (targetInstance == null)
                {
                    log.Error("PrefabUtility.InstantiatePrefab returned null.");
                    return false;
                }
                targetInstance.name = Path.GetFileNameWithoutExtension(opt.PrefabProjectPath);
                // source 옆에 같은 부모 아래 배치
                targetInstance.transform.SetParent(opt.Source.transform.parent, true);

                if (opt.VerboseDiagnostics)
                {
                    LogFbxSubAssets("[DIAG @8a fbx asset sub-assets]", opt.FbxProjectPath, log);
                    LogSmrInventory("[DIAG @8b targetInstance (just instantiated)]", targetInstance, log);
                }

                // ---------------- 8.5) FBX 이름 치환 복원 ----------------
                // FBX export 가 항상 UseMayaCompatibleNames=false 로 동작하므로 FBX 자체에는
                // 원본 이름이 유지되고, 정상 경로에서는 count=0 (no-op) 가 된다.
                // 다만 (a) 동일 부모 아래 동명의 형제가 있어 GetUniqueFbxNodeName 이 "_N"
                // 접미사를 붙인 경우, (b) FBX Exporter 의 reflection 이 실패해 simple
                // fallback 으로 export 된 경우 등에 일부 GameObject 이름이 sanitize/치환
                // 상태일 수 있다. NAME 기반 매처가 자식 재정렬에 안전하게 원본 이름 복원.
                var namesRestored = RestoreOriginalNamesFromSource(
                    opt.Source.transform, targetInstance.transform);
                if (namesRestored > 0)
                {
                    log.Info($"Restored {namesRestored} GameObject names on prefab instance " +
                             "(unique-suffix or fallback sanitization detected).");
                }

                // ---------------- 9) Bone map: snapshot → target ----------------
                EditorUtility.DisplayProgressBar(PB_TITLE, "Migrating data (snapshot → prefab)...", 0.92f);
                var boneMap = AvatarMigrationCore.BuildBoneMap(snapshot, targetInstance, log);

                // ---------------- 10) Migration (snapshot 을 source-of-truth 로) ----------------
                if (opt.MigrateActiveStates)
                {
                    AvatarMigrationCore.MigrateActiveStates(boneMap, log);
                }
                if (opt.MigrateBlendShapes)
                {
                    AvatarMigrationCore.MigrateBlendShapes(snapshot, targetInstance, log);
                }
                if (opt.MigratePhysics)
                {
                    AvatarMigrationCore.MigrateColliders(snapshot.transform, targetInstance.transform, boneMap, log);
                    AvatarMigrationCore.MigrateMagicaCloth(snapshot.transform, targetInstance.transform, boneMap, log);
                    AvatarMigrationCore.MigrateVRMSpringBone(snapshot.transform, targetInstance.transform, boneMap, log);
                }
                if (opt.MigrateConstraints)
                {
                    AvatarMigrationCore.MigrateConstraints(snapshot.transform, boneMap, log);
                }

                // ---------------- 10.5) (옵션) updateWhenOffscreen ----------------
                if (opt.UpdateWhenOffscreenInPrefab)
                {
                    log.Info("Setting updateWhenOffscreen = true on all SkinnedMeshRenderers...");
                    EnableUpdateWhenOffscreenAll(targetInstance);
                }

                // ---------------- 11) Save Prefab (덮어쓰기) ----------------
                EditorUtility.DisplayProgressBar(PB_TITLE, "Saving prefab...", 0.97f);
                EnsureProjectFolder(opt.PrefabProjectPath);
                EnsureDirectory(Path.GetDirectoryName(ProjectRelativeToAbsolute(opt.PrefabProjectPath)));
                var savedPrefab = PrefabUtility.SaveAsPrefabAsset(targetInstance, opt.PrefabProjectPath);
                if (savedPrefab == null)
                {
                    log.Error($"Failed to save prefab at {opt.PrefabProjectPath}.");
                    return false;
                }

                AssetDatabase.SaveAssets();
                log.Info($"Prefab saved: {opt.PrefabProjectPath}");

                if (opt.VerboseDiagnostics)
                {
                    LogSmrInventory("[DIAG @11a targetInstance (pre-cleanup, post-save)]", targetInstance, log);
                    var savedAsset = AssetDatabase.LoadMainAssetAtPath(opt.PrefabProjectPath) as GameObject;
                    if (savedAsset != null)
                    {
                        LogSmrInventory("[DIAG @11b saved prefab asset]", savedAsset, log);
                    }
                }

                // ---------------- 12) 임시 GameObject 정리 ----------------
                // 성공 시 snapshot 과 targetInstance 는 더 이상 필요 없으므로 씬에서 제거.
                // (실패 시에는 finally 블록을 거쳐도 정리하지 않아, 사용자가 디버깅할 수 있게 남겨 둠)
                Object.DestroyImmediate(snapshot);
                snapshot = null;
                Object.DestroyImmediate(targetInstance);
                targetInstance = null;

                log.Info("Pipeline complete.");
                return true;
            }
            catch (System.Exception e)
            {
                log.Error($"Pipeline failed: {e.Message}");
                Debug.LogException(e);
                return false;
            }
            finally
            {
                // 정규화 임시는 항상 정리.
                // snapshot/targetInstance 는 성공 경로에서 step 12 가 정리.
                // 실패 경로에서는 사용자 디버깅을 위해 의도적으로 남겨 둔다.
                if (normalized != null) Object.DestroyImmediate(normalized);
                EditorUtility.ClearProgressBar();
            }
        }

        // ============================================================
        // 내부 헬퍼
        // ============================================================

        /// <summary>
        /// 진단용: 한 GameObject 트리 안의 SkinnedMeshRenderer 인벤토리를 로그로 출력.
        /// 각 줄: 경로 | sharedMesh 이름 | 정점 수 | mesh instance ID | sub-mesh 수 | material 수
        /// 메시 매핑 꼬임 디버깅 시 단계별 호출하여 어디서 sharedMesh 가 다른 mesh 자산으로
        /// 바뀌는지 (또는 같은 mesh 자산이 여러 GO 에서 공유되는지) 추적.
        /// </summary>
        private static void LogSmrInventory(string prefix, GameObject root, IMigrationLog log)
        {
            if (root == null) { log.Info($"{prefix} (root is null)"); return; }
            var smrs = root.GetComponentsInChildren<SkinnedMeshRenderer>(true);
            log.Info($"{prefix} root='{root.name}' SMR count={smrs.Length}");
            foreach (var smr in smrs)
            {
                var path = GetRelativePath(smr.transform, root.transform);
                var mesh = smr.sharedMesh;
                if (mesh == null)
                {
                    log.Info($"  - {path} | sharedMesh=NULL | mat={smr.sharedMaterials?.Length ?? 0}");
                    continue;
                }
                int meshId = mesh.GetInstanceID();
                log.Info($"  - {path} | sharedMesh='{mesh.name}' | verts={mesh.vertexCount} | " +
                         $"submeshes={mesh.subMeshCount} | meshID={meshId} | mat={smr.sharedMaterials?.Length ?? 0}");
            }
            // 동일 mesh 가 여러 SMR 에 공유되고 있는지 별도 표기
            var grouped = smrs.Where(s => s.sharedMesh != null)
                              .GroupBy(s => s.sharedMesh.GetInstanceID())
                              .Where(g => g.Count() > 1)
                              .ToList();
            if (grouped.Count > 0)
            {
                log.Warning($"{prefix} 동일 sharedMesh 를 공유하는 SMR 그룹 {grouped.Count} 건 발견:");
                foreach (var g in grouped)
                {
                    var paths = string.Join(", ", g.Select(s => GetRelativePath(s.transform, root.transform)));
                    log.Warning($"  meshID={g.Key} ('{g.First().sharedMesh.name}') ← {paths}");
                }
            }
        }

        /// <summary>
        /// 진단용: FBX 자산이 임포트된 후 sub-asset 들 (mesh, material 등) 의 목록을 로그로 출력.
        /// </summary>
        private static void LogFbxSubAssets(string prefix, string fbxProjectPath, IMigrationLog log)
        {
            var assets = AssetDatabase.LoadAllAssetsAtPath(fbxProjectPath);
            if (assets == null || assets.Length == 0)
            {
                log.Warning($"{prefix} no sub-assets found at {fbxProjectPath}");
                return;
            }
            var meshes = assets.OfType<Mesh>().ToList();
            log.Info($"{prefix} path={fbxProjectPath} — total sub-assets={assets.Length}, meshes={meshes.Count}");
            foreach (var m in meshes)
            {
                log.Info($"  mesh: name='{m.name}' verts={m.vertexCount} submeshes={m.subMeshCount} " +
                         $"meshID={m.GetInstanceID()}");
            }
        }

        private static string GetRelativePath(Transform t, Transform root)
        {
            if (t == root) return "<root>";
            var stack = new System.Collections.Generic.Stack<string>();
            var cur = t;
            while (cur != null && cur != root)
            {
                stack.Push(cur.name);
                cur = cur.parent;
            }
            return string.Join("/", stack);
        }

        /// <summary>
        /// FBX Exporter 가 sanitize 한 target 이름을 source 기준으로 복원.
        /// Unity FBX 임포터는 자식 GameObject 순서를 알파벳순으로 재정렬하기 때문에
        /// child-index lockstep 매칭은 안전하지 않다 (source[Face, Body] vs target[Body, Face]
        /// 처럼 순서가 어긋나면 잘못된 GO 에 source 이름이 붙어 mesh 매핑이 통째로 시프트됨).
        /// 따라서 NAME 기반 매칭을 한다:
        ///   Pass 1) target.name == source.name 인 쌍 (sanitize 가 필요 없는 정상 케이스)
        ///   Pass 2) Maya compat sanitize (비-영숫자 → '_') 이후 같은 이름인 쌍
        ///   Pass 3) target 끝에 "_N"(N=숫자) 형태로 GetUniqueFbxNodeName 접미사가 붙은 케이스
        /// 매칭된 쌍에 대해서만 rename 수행 후 자식으로 재귀.
        /// </summary>
        private static int RestoreOriginalNamesFromSource(Transform source, Transform target)
        {
            int count = 0;
            RestoreNamesByMatch(source, target, ref count);
            return count;
        }

        private static void RestoreNamesByMatch(Transform source, Transform target, ref int count)
        {
            var srcList = new System.Collections.Generic.List<Transform>(source.childCount);
            foreach (Transform c in source) srcList.Add(c);
            var tgtList = new System.Collections.Generic.List<Transform>(target.childCount);
            foreach (Transform c in target) tgtList.Add(c);

            var pairs = new System.Collections.Generic.List<(Transform src, Transform tgt)>();
            var consumedSrc = new System.Collections.Generic.HashSet<Transform>();
            var pairedTgt = new System.Collections.Generic.HashSet<Transform>();

            // Pass 1: 정확히 같은 이름
            foreach (var tgt in tgtList)
            {
                Transform match = null;
                foreach (var src in srcList)
                {
                    if (consumedSrc.Contains(src)) continue;
                    if (src.name == tgt.name) { match = src; break; }
                }
                if (match != null)
                {
                    consumedSrc.Add(match);
                    pairedTgt.Add(tgt);
                    pairs.Add((match, tgt));
                }
            }

            // Pass 2: source 의 sanitize 형태 == target 이름
            foreach (var tgt in tgtList)
            {
                if (pairedTgt.Contains(tgt)) continue;
                Transform match = null;
                foreach (var src in srcList)
                {
                    if (consumedSrc.Contains(src)) continue;
                    if (SanitizeMayaCompat(src.name) == tgt.name) { match = src; break; }
                }
                if (match != null)
                {
                    consumedSrc.Add(match);
                    pairedTgt.Add(tgt);
                    pairs.Add((match, tgt));
                }
            }

            // Pass 3: target 이름이 sanitize + "_N" 접미사 (FBX exporter 의 GetUniqueFbxNodeName)
            foreach (var tgt in tgtList)
            {
                if (pairedTgt.Contains(tgt)) continue;
                var stripped = StripUniqueSuffix(tgt.name);
                if (stripped == null) continue;
                Transform match = null;
                foreach (var src in srcList)
                {
                    if (consumedSrc.Contains(src)) continue;
                    if (SanitizeMayaCompat(src.name) == stripped) { match = src; break; }
                }
                if (match != null)
                {
                    consumedSrc.Add(match);
                    pairedTgt.Add(tgt);
                    pairs.Add((match, tgt));
                }
            }

            // 매칭된 쌍만 rename + 재귀
            foreach (var (src, tgt) in pairs)
            {
                if (tgt.name != src.name)
                {
                    tgt.gameObject.name = src.name;
                    count++;
                }
                RestoreNamesByMatch(src, tgt, ref count);
            }
        }

        /// <summary>
        /// Maya compatible sanitization: 비-영숫자(언더바 제외 X — 모두 '_' 로) → '_'.
        /// 첫 글자가 숫자면 앞에 '_' prepend. UnityEditor.Formats.Fbx.Exporter.ModelExporter.
        /// ConvertToMayaCompatibleName 의 단순화 버전 (diacritics/namespace 처리는 생략).
        /// </summary>
        private static string SanitizeMayaCompat(string name)
        {
            if (string.IsNullOrEmpty(name)) return "_";
            var sb = new System.Text.StringBuilder(name.Length + 1);
            if (char.IsDigit(name[0])) sb.Append('_');
            foreach (var c in name)
            {
                sb.Append(char.IsLetterOrDigit(c) ? c : '_');
            }
            return sb.ToString();
        }

        /// <summary>
        /// 끝에 "_<digits>" 가 붙어 있으면 그 부분을 잘라낸 문자열 반환.
        /// 그렇지 않으면 null.
        /// </summary>
        private static string StripUniqueSuffix(string name)
        {
            if (string.IsNullOrEmpty(name)) return null;
            int idx = name.LastIndexOf('_');
            if (idx <= 0 || idx >= name.Length - 1) return null;
            for (int i = idx + 1; i < name.Length; i++)
            {
                if (!char.IsDigit(name[i])) return null;
            }
            return name.Substring(0, idx);
        }

        private static void ActivateAllRecursive(Transform t)
        {
            if (!t.gameObject.activeSelf) t.gameObject.SetActive(true);
            for (int i = 0; i < t.childCount; i++)
            {
                ActivateAllRecursive(t.GetChild(i));
            }
        }

        /// <summary>
        /// snapshot 과 source 는 Object.Instantiate 직후엔 동일한 child 순서/구조를 가지므로,
        /// 동일 인덱스로 lockstep 순회하면서 activeSelf 를 복원합니다.
        /// (베이크 후 source 트리 구조가 변하지 않아야 정확히 일치)
        /// </summary>
        private static void RestoreActiveStatesFromSnapshot(Transform source, Transform snapshot)
        {
            source.gameObject.SetActive(snapshot.gameObject.activeSelf);
            int n = Mathf.Min(source.childCount, snapshot.childCount);
            for (int i = 0; i < n; i++)
            {
                RestoreActiveStatesFromSnapshot(source.GetChild(i), snapshot.GetChild(i));
            }
        }

        private static void ZeroAllBlendShapes(GameObject root)
        {
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (smr.sharedMesh == null) continue;
                int n = smr.sharedMesh.blendShapeCount;
                for (int i = 0; i < n; i++)
                {
                    smr.SetBlendShapeWeight(i, 0f);
                }
            }
        }

        /// <summary>
        /// snapshot 의 SkinnedMeshRenderer (이름 기준 매칭) 의 weight 를
        /// source 의 동명 renderer 에 그대로 복사합니다. (mesh 가 공유되므로 인덱스 일치)
        /// </summary>
        private static void RestoreBlendShapeWeightsFromSnapshot(GameObject source, GameObject snapshot)
        {
            var snapMap = new Dictionary<string, SkinnedMeshRenderer>();
            foreach (var s in snapshot.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!snapMap.ContainsKey(s.name)) snapMap[s.name] = s;
            }
            foreach (var srcSmr in source.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                if (!snapMap.TryGetValue(srcSmr.name, out var snapSmr)) continue;
                if (srcSmr.sharedMesh == null) continue;
                int n = srcSmr.sharedMesh.blendShapeCount;
                for (int i = 0; i < n; i++)
                {
                    srcSmr.SetBlendShapeWeight(i, snapSmr.GetBlendShapeWeight(i));
                }
            }
        }

        private static void EnableUpdateWhenOffscreenAll(GameObject root)
        {
            foreach (var smr in root.GetComponentsInChildren<SkinnedMeshRenderer>(true))
            {
                smr.updateWhenOffscreen = true;
            }
        }

        /// <summary>
        /// AvatarBakeOptions 의 회전 보존 설정을 NormalizeOptions 로 변환.
        /// </summary>
        private static NormalizeOptions BuildNormalizeOptions(AvatarBakeOptions opt, IMigrationLog log)
        {
            var result = new NormalizeOptions();

            if (opt.PreserveAllRotations)
            {
                result.PreserveAllRotations = true;
                log.Info("Rotation preservation: ALL transforms.");
            }
            else if (!string.IsNullOrEmpty(opt.PreserveRotationSubstring))
            {
                var needle = opt.PreserveRotationSubstring;
                result.RotationFilter = t => t != null && t.name.IndexOf(needle, System.StringComparison.Ordinal) >= 0;
                log.Info($"Rotation preservation: by name substring \"{needle}\".");
            }
            else
            {
                log.Info("Rotation preservation: none (all rotations zeroed).");
            }

            return result;
        }

        private static AvatarMode ResolveAvatarMode(GameObject go, AvatarMode requested)
        {
            if (requested != AvatarMode.Auto) return requested;
            var animator = go.GetComponent<Animator>();
            if (animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman)
                return AvatarMode.Humanoid;
            return AvatarMode.Generic;
        }

        private static void TryEnforceTPose(GameObject go, IMigrationLog log)
        {
            var animator = go.GetComponent<Animator>();
            if (animator == null || animator.avatar == null || !animator.avatar.isHuman) return;

            var hips = animator.GetBoneTransform(HumanBodyBones.Hips);
            if (hips == null) return;

            var p = hips.position;
            var r = hips.rotation;
            try
            {
                HumanPoseTransfer.SetTPose(animator.avatar, go.transform);
            }
            catch (System.Exception e)
            {
                log.Warning($"T-Pose enforcement failed: {e.Message}");
            }
            finally
            {
                hips.position = p;
                hips.rotation = r;
            }
        }

        // BoneNormalizer 의 CreateAvatarFunc(원본GO, 정규화GO, boneMap) → Avatar
        private static Avatar BuildHumanoidAvatar(GameObject src, GameObject dst, Dictionary<Transform, Transform> boneMap)
        {
            var srcAnimator = src.GetComponent<Animator>();
            if (srcAnimator == null || srcAnimator.avatar == null || !srcAnimator.avatar.isHuman)
            {
                return BuildGenericAvatar(src, dst, boneMap);
            }

            var humanBoneMap = System.Enum.GetValues(typeof(HumanBodyBones))
                .Cast<HumanBodyBones>()
                .Where(x => x != HumanBodyBones.LastBone)
                .Select(x => new { Key = x, Value = srcAnimator.GetBoneTransform(x) })
                .Where(x => x.Value != null && boneMap.ContainsKey(x.Value))
                .ToDictionary(x => x.Key, x => boneMap[x.Value]);

            if (dst.GetComponent<Animator>() == null) dst.AddComponent<Animator>();

            var desc = AvatarDescription.Create();
            desc.SetHumanBones(humanBoneMap);
            return desc.CreateAvatar(dst.transform);
        }

        private static Avatar BuildGenericAvatar(GameObject src, GameObject dst, Dictionary<Transform, Transform> boneMap)
        {
            if (dst.GetComponent<Animator>() == null) dst.AddComponent<Animator>();
            return AvatarBuilder.BuildGenericAvatar(dst, "");
        }

        private static void ConfigureModelImporter(string fbxProjectPath, AvatarMode mode, bool materialNone, IMigrationLog log)
        {
            var importer = AssetImporter.GetAtPath(fbxProjectPath) as ModelImporter;
            if (importer == null)
            {
                log.Warning($"ModelImporter not found at {fbxProjectPath}; skipping configure.");
                return;
            }

            importer.animationType = (mode == AvatarMode.Humanoid)
                ? ModelImporterAnimationType.Human
                : ModelImporterAnimationType.Generic;
            importer.importBlendShapes = true;

            // materialImportMode 는 명시적 요청 시에만 None 으로. 기본값은 Unity 기본 동작
            // (슬롯 갯수/이름 보존, 머티리얼 자동 부착 시도)을 유지.
            if (materialNone)
            {
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
            }
            importer.SaveAndReimport();
        }

        // ---- path utilities ----

        /// <summary>
        /// LogFilePath 옵션을 절대 경로로 변환.
        /// - 절대 경로면 그대로
        /// - 상대 경로면 프로젝트 루트(Assets 의 부모) 기준으로 해석
        /// </summary>
        private static string ResolveLogFilePath(string logPath)
        {
            if (string.IsNullOrEmpty(logPath)) return null;
            if (Path.IsPathRooted(logPath)) return logPath.Replace('\\', '/');
            var dataPath = Application.dataPath.Replace('\\', '/');
            var projectRoot = dataPath.Substring(0, dataPath.Length - "Assets".Length);
            return (projectRoot + logPath.Replace('\\', '/')).Replace("//", "/");
        }

        private static string ProjectRelativeToAbsolute(string projectRel)
        {
            if (string.IsNullOrEmpty(projectRel)) return null;
            var dataPath = Application.dataPath.Replace('\\', '/');
            // dataPath = "<projectRoot>/Assets"
            var projectRoot = dataPath.Substring(0, dataPath.Length - "Assets".Length);
            return projectRoot + projectRel.Replace('\\', '/');
        }

        private static void EnsureDirectory(string dir)
        {
            if (!string.IsNullOrEmpty(dir) && !Directory.Exists(dir))
            {
                Directory.CreateDirectory(dir);
            }
        }

        /// <summary>
        /// "Assets/Foo/Bar/file.fbx" 같은 프로젝트 상대 경로를 받아 부모 폴더가
        /// AssetDatabase 에 인식된 폴더로 존재하도록 보장합니다.
        /// 누락된 중간 폴더는 AssetDatabase.CreateFolder 로 차례로 생성.
        /// </summary>
        private static void EnsureProjectFolder(string projectRelativeFilePath)
        {
            if (string.IsNullOrEmpty(projectRelativeFilePath)) return;
            var dir = Path.GetDirectoryName(projectRelativeFilePath);
            if (string.IsNullOrEmpty(dir)) return;
            dir = dir.Replace('\\', '/');
            if (dir == "Assets" || AssetDatabase.IsValidFolder(dir)) return;

            var parts = dir.Split('/');
            if (parts.Length < 2 || parts[0] != "Assets") return;

            var current = parts[0];
            for (int i = 1; i < parts.Length; i++)
            {
                var next = current + "/" + parts[i];
                if (!AssetDatabase.IsValidFolder(next))
                {
                    AssetDatabase.CreateFolder(current, parts[i]);
                }
                current = next;
            }
        }
    }
}
