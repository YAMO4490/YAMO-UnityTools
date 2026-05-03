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
using UnityEditor.Formats.Fbx.Exporter;
using UnityEngine;

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
            var log = opt.Log ?? new DebugMigrationLog("[AvatarBake] ");

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

                // ExportModelOptions 를 명시 지정:
                //   - UseMayaCompatibleNames = false : "Bangs.1" 같은 이름의 점(.)이 _ 로
                //                                      치환되는 문제 방지
                //   - ExportFormat = Binary          : 바이너리 FBX (파일 크기/호환성)
                var exportOptions = new ExportModelOptions
                {
                    ExportFormat = ExportFormat.Binary,
                    UseMayaCompatibleNames = false,
                };
                var written = ModelExporter.ExportObject(fbxAbsolute, normalized, exportOptions);
                if (string.IsNullOrEmpty(written))
                {
                    log.Error("ModelExporter.ExportObject returned null.");
                    return false;
                }

                // 정규화 임시 GO 즉시 정리
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
                log.Info("Pipeline complete. Snapshot and target instance left in scene for review.");
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
                // 정규화 임시는 무조건 정리. snapshot/targetInstance 는 사용자 검수용으로 보존.
                if (normalized != null) Object.DestroyImmediate(normalized);
                EditorUtility.ClearProgressBar();
            }
        }

        // ============================================================
        // 내부 헬퍼
        // ============================================================

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
