using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor
{
    /// <summary>베이크 FBX의 임포트 설정, 원본 머티리얼 연결 및 Biped 전용 Avatar 생성.</summary>
    public static class AvatarBakeFbxSetup
    {
        public static string GetAvatarFbxPath(string fbxPath)
        {
            return Path.ChangeExtension(fbxPath, null) + "_Avatar.fbx";
        }

        public static bool HasBipedSkeleton(GameObject source)
        {
            return source != null && source.GetComponentsInChildren<Transform>(true)
                .Any(t => IsBipedName(t.name));
        }

        private static bool IsBipedName(string name)
        {
            return name.Equals("Bip001", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Bip001 ", StringComparison.OrdinalIgnoreCase)
                || name.StartsWith("Bip001_", StringComparison.OrdinalIgnoreCase);
        }

        public static Avatar ExportBipedAvatar(GameObject normalized, GameObject source,
            string avatarFbxPath, object exportOptions, IMigrationLog log)
        {
            var keep = new HashSet<Transform> { normalized.transform };
            foreach (var t in normalized.GetComponentsInChildren<Transform>(true).Where(t => IsBipedName(t.name)))
            {
                for (var p = t; p != null; p = p.parent)
                {
                    keep.Add(p);
                    if (p == normalized.transform) break;
                }
            }

            GameObject skeleton = null;
            try
            {
                skeleton = CopySkeleton(normalized.transform, null, keep).gameObject;
                var absolutePath = Path.Combine(Path.GetDirectoryName(Application.dataPath), avatarFbxPath);
                if (string.IsNullOrEmpty(YamoFbxExportCompat.ExportObject(absolutePath, skeleton, exportOptions)))
                    throw new InvalidOperationException("Bip001 Avatar FBX export failed: " + avatarFbxPath);
            }
            finally
            {
                if (skeleton != null) Object.DestroyImmediate(skeleton);
            }

            AssetDatabase.ImportAsset(avatarFbxPath, ImportAssetOptions.ForceSynchronousImport);
            var importer = GetImporter(avatarFbxPath);
            SetMeshOptions(importer);
            importer.materialImportMode = ModelImporterMaterialImportMode.None;
            importer.animationType = ModelImporterAnimationType.Human;
            importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
            importer.sourceAvatar = null;
            importer.optimizeGameObjects = false;

            var imported = AssetDatabase.LoadAssetAtPath<GameObject>(avatarFbxPath);
            importer.humanDescription = BuildHumanDescription(source, imported);
            importer.SaveAndReimport();

            var avatar = AssetDatabase.LoadAllAssetsAtPath(avatarFbxPath).OfType<Avatar>()
                .FirstOrDefault(a => a.isValid && a.isHuman);
            if (avatar == null)
                throw new InvalidOperationException("Bip001 FBX에서 유효한 Humanoid Avatar를 생성하지 못했습니다: " + avatarFbxPath);
            log.Info("Humanoid Avatar created from Bip001 skeleton: " + avatarFbxPath);
            return avatar;
        }

        // Transform만 복사하여 Bip001 아래에 붙은 메시/컴포넌트도 Avatar FBX에 섞이지 않는다.
        private static Transform CopySkeleton(Transform source, Transform parent, HashSet<Transform> keep)
        {
            var copy = new GameObject(source.name).transform;
            copy.SetParent(parent, false);
            copy.localPosition = source.localPosition;
            copy.localRotation = source.localRotation;
            copy.localScale = source.localScale;
            foreach (Transform child in source)
                if (keep.Contains(child)) CopySkeleton(child, copy, keep);
            return copy;
        }

        private static HumanDescription BuildHumanDescription(GameObject source, GameObject skeleton)
        {
            if (skeleton == null) throw new InvalidOperationException("Avatar skeleton FBX could not be loaded.");
            var transforms = skeleton.GetComponentsInChildren<Transform>(true);
            var names = new HashSet<string>(transforms.Select(t => t.name));
            var mapped = new Dictionary<HumanBodyBones, string>();
            var animator = source.GetComponent<Animator>();
            bool hasHuman = animator != null && animator.avatar != null && animator.avatar.isValid && animator.avatar.isHuman;

            // 기존 Humanoid의 Bip001 매핑을 우선 보존한다 (Hips가 Bip001인 리그도 지원).
            if (hasHuman)
            {
                foreach (HumanBodyBones bone in Enum.GetValues(typeof(HumanBodyBones)))
                {
                    if (bone == HumanBodyBones.LastBone) continue;
                    var t = animator.GetBoneTransform(bone);
                    if (t != null && IsBipedName(t.name) && names.Contains(t.name))
                        mapped[bone] = t.name;
                }
            }
            foreach (var pair in AvatarBakePreUtilities.BipedNameToHumanBone)
                if (names.Contains(pair.Key) && !mapped.ContainsKey(pair.Value))
                    mapped[pair.Value] = pair.Key;

            for (int i = 0; i < HumanTrait.BoneCount; i++)
                if (HumanTrait.RequiredBone(i) && !mapped.ContainsKey((HumanBodyBones)i))
                    throw new InvalidOperationException("Bip001 Humanoid 필수 본을 찾지 못했습니다: " + HumanTrait.BoneName[i]);

            var description = hasHuman ? animator.avatar.humanDescription : new HumanDescription
            {
                upperArmTwist = 0.5f, lowerArmTwist = 0.5f,
                upperLegTwist = 0.5f, lowerLegTwist = 0.5f,
                armStretch = 0.05f, legStretch = 0.05f,
            };
            var previousLimits = (description.human ?? new HumanBone[0])
                .GroupBy(b => b.humanName).ToDictionary(g => g.Key, g => g.First().limit);
            description.human = mapped.OrderBy(p => (int)p.Key).Select(pair => new HumanBone
            {
                boneName = pair.Value,
                humanName = HumanTrait.BoneName[(int)pair.Key],
                limit = previousLimits.TryGetValue(HumanTrait.BoneName[(int)pair.Key], out var limit)
                    ? limit : new HumanLimit { useDefaultValues = true },
            }).ToArray();
            description.skeleton = transforms.Select(t => new SkeletonBone
            {
                name = t.name, position = t.localPosition, rotation = t.localRotation, scale = t.localScale,
            }).ToArray();
            return description;
        }

        public static void ConfigureMainFbx(string fbxPath, AvatarMode mode, GameObject source,
            Avatar sourceAvatar, IMigrationLog log)
        {
            var importer = GetImporter(fbxPath);
            SetMeshOptions(importer);
            importer.animationType = sourceAvatar != null || mode == AvatarMode.Humanoid
                ? ModelImporterAnimationType.Human : ModelImporterAnimationType.Generic;
            importer.avatarSetup = sourceAvatar != null
                ? ModelImporterAvatarSetup.CopyFromOther : ModelImporterAvatarSetup.CreateFromThisModel;
            importer.sourceAvatar = sourceAvatar;
            importer.optimizeGameObjects = false;
            importer.materialImportMode = ModelImporterMaterialImportMode.ImportStandard;
            importer.materialLocation = ModelImporterMaterialLocation.InPrefab;

            // 이전 실행의 매핑이 FBX 안의 슬롯 이름을 가리지 않도록 먼저 해제한다.
            foreach (var key in importer.GetExternalObjectMap().Keys.Where(k => k.type == typeof(Material)).ToArray())
                importer.RemoveRemap(key);
            importer.SaveAndReimport();

            var originals = source.GetComponentsInChildren<Renderer>(true)
                .GroupBy(r => r.name).ToDictionary(g => g.Key, g => g.ToArray());
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(fbxPath);
            var remaps = new Dictionary<AssetImporter.SourceAssetIdentifier, Material>();
            foreach (var renderer in model.GetComponentsInChildren<Renderer>(true))
            {
                if (!originals.TryGetValue(renderer.name, out var matches) || matches.Length != 1)
                    throw new InvalidOperationException("원본 렌더러를 특정할 수 없어 머티리얼을 매핑하지 못했습니다: " + renderer.name);
                var originalMaterials = matches[0].sharedMaterials;
                var importedMaterials = renderer.sharedMaterials;
                if (originalMaterials.Length != importedMaterials.Length)
                    throw new InvalidOperationException("FBX/원본 머티리얼 슬롯 수가 다릅니다: " + renderer.name);
                for (int i = 0; i < importedMaterials.Length; i++)
                {
                    var original = originalMaterials[i];
                    var imported = importedMaterials[i];
                    if (original == null) continue;
                    if (imported == null || !AssetDatabase.Contains(original))
                        throw new InvalidOperationException("머티리얼을 자산으로 저장한 뒤 다시 베이크하세요: " + original.name);
                    var key = new AssetImporter.SourceAssetIdentifier(typeof(Material), imported.name);
                    if (remaps.TryGetValue(key, out var previous) && previous != original)
                        throw new InvalidOperationException("서로 다른 원본 머티리얼이 같은 FBX 슬롯 이름을 사용합니다: " + imported.name);
                    remaps[key] = original;
                }
            }

            importer = GetImporter(fbxPath);
            foreach (var pair in remaps) importer.AddRemap(pair.Key, pair.Value);
            importer.SaveAndReimport();
            log.Info($"FBX configured: Read/Write + Legacy Blend Shape Normals ON; {remaps.Count} material(s) remapped.");
            if (sourceAvatar != null)
            {
                // Copy From Other 설정이어도 임포트된 모델에는 Animator가 없을 수 있다.
                // FBX의 외부 참조를 확인하고, 최종 프리팹의 Animator는 파이프라인에서 보장한다.
                if (GetImporter(fbxPath).sourceAvatar != sourceAvatar || !sourceAvatar.isValid || !sourceAvatar.isHuman)
                    throw new InvalidOperationException("본체 FBX가 유효한 Bip001 Avatar를 참조하지 않습니다: " + fbxPath);
                log.Info("Main FBX uses Avatar from: " + AssetDatabase.GetAssetPath(sourceAvatar));
            }
        }

        private static ModelImporter GetImporter(string path)
        {
            var importer = AssetImporter.GetAtPath(path) as ModelImporter;
            if (importer == null) throw new InvalidOperationException("ModelImporter not found: " + path);
            return importer;
        }

        private static void SetMeshOptions(ModelImporter importer)
        {
            importer.isReadable = true;
            importer.importBlendShapes = true;
            // Unity 2021에서는 이 옵션의 C# 프로퍼티가 internal이므로 Inspector와 같은
            // 직렬화 속성을 사용한다.
            var serialized = new SerializedObject(importer);
            var legacyNormals = serialized.FindProperty("legacyComputeAllNormalsFromSmoothingGroupsWhenMeshHasBlendShapes");
            if (legacyNormals == null)
                throw new InvalidOperationException("Legacy Blend Shape Normals option is unavailable: " + importer.assetPath);
            legacyNormals.boolValue = true;
            serialized.ApplyModifiedPropertiesWithoutUndo();
        }
    }
}
