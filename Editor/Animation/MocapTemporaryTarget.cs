using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using Autodesk.Fbx;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor
{
    /// <summary>Owns only the explicitly named per-run FBX and its scene instance.</summary>
    internal static class MocapTemporaryTarget
    {
        internal static string NewAssetPath() =>
            $"Assets/Codex/{DateTime.Now:yyyy-MM-dd}/MocapTarget/{Guid.NewGuid():N}/Target.fbx";

        internal static Animator Create(Animator source, string assetPath)
        {
            if (source == null || source.avatar == null || !source.avatar.isHuman || !source.avatar.isValid)
                throw new InvalidOperationException("유효한 대상 Humanoid Animator가 필요합니다.");
            var sourcePath = AssetDatabase.GetAssetPath(source.avatar);
            var originalImporter = AssetImporter.GetAtPath(sourcePath) as ModelImporter;
            if (originalImporter == null || !sourcePath.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("임시 대상 생성에는 FBX 기반 Avatar가 필요합니다.");
            var description = source.avatar.humanDescription;
            var model = AssetDatabase.LoadAssetAtPath<GameObject>(sourcePath);
            var boneNames = new HashSet<string>(description.human.Select(b => b.boneName), StringComparer.Ordinal);
            foreach (var skin in model.GetComponentsInChildren<SkinnedMeshRenderer>(true))
                foreach (var bone in skin.bones)
                    if (bone != null) boneNames.Add(bone.name);
            GameObject instance = null;
            try
            {
                EnsureFolder(Path.GetDirectoryName(assetPath).Replace('\\', '/'));
                WriteSkeletonCopy(sourcePath, assetPath, boneNames);
                AssetDatabase.ImportAsset(assetPath, ImportAssetOptions.ForceSynchronousImport);
                var importer = (ModelImporter)AssetImporter.GetAtPath(assetPath);
                // Preserve coordinate conversion, scaling and hierarchy import options.
                EditorJsonUtility.FromJsonOverwrite(EditorJsonUtility.ToJson(originalImporter), importer);
                importer.animationType = ModelImporterAnimationType.Generic;
                importer.avatarSetup = ModelImporterAvatarSetup.NoAvatar;
                importer.sourceAvatar = null;
                importer.importAnimation = false;
                importer.importCameras = false;
                importer.importLights = false;
                importer.optimizeGameObjects = false;
                importer.materialImportMode = ModelImporterMaterialImportMode.None;
                importer.SaveAndReimport();

                var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(assetPath);
                // The Generic importer can omit mapped bones from its HumanDescription.
                // Use the imported model's default pose and complete transform hierarchy.
                description.skeleton = prefab.GetComponentsInChildren<Transform>(true)
                    .Select(t => new SkeletonBone
                    {
                        name = t.name,
                        position = t.localPosition,
                        rotation = t.localRotation,
                        scale = t.localScale
                    }).ToArray();
                description.lowerArmTwist = 1f;
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                // Switching Generic -> Human can populate Unity's automatic mapping.
                // Restore the source mapping only after the rig mode is configured.
                importer.humanDescription = description;
                using (var serialized = new SerializedObject(importer))
                {
                    // Explicit source mappings must not be replaced by Unity's
                    // automatic Biped mapping (Bip001 -> Bip001 Pelvis for Hips).
                    var autoMapping = serialized.FindProperty("m_AutoGenerateAvatarMappingIfUnspecified");
                    if (autoMapping == null)
                        throw new NotSupportedException("Avatar 자동 매핑 제어 필드를 찾지 못했습니다.");
                    autoMapping.boolValue = false;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                importer.SaveAndReimport();

                var avatar = AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Avatar>().Single();
                if (!avatar.isValid || !avatar.isHuman || !Mathf.Approximately(avatar.humanDescription.lowerArmTwist, 1f))
                    throw new InvalidOperationException("임시 대상 Avatar 생성/Twist 검증 실패");
                var actualMapping = avatar.humanDescription.human.ToDictionary(b => b.humanName, b => b.boneName);
                var mismatches = description.human
                    .Where(b => !actualMapping.TryGetValue(b.humanName, out var name) || name != b.boneName)
                    .Select(b => b.humanName + ": '" + b.boneName + "' -> '" +
                        (actualMapping.TryGetValue(b.humanName, out var name) ? name : "<missing>") + "'").ToArray();
                if (mismatches.Length != 0)
                    throw new InvalidOperationException("임시 대상 Avatar의 원본 매핑 보존 검증 실패\n" + string.Join("\n", mismatches));
                instance = Object.Instantiate(AssetDatabase.LoadAssetAtPath<GameObject>(assetPath));
                instance.name = source.gameObject.name;
                if (instance.GetComponentsInChildren<Renderer>(true).Length != 0 ||
                    AssetDatabase.LoadAllAssetsAtPath(assetPath).OfType<Mesh>().Any())
                    throw new InvalidOperationException("임시 대상 FBX에 메시가 남아 있습니다.");
                var animator = instance.GetComponent<Animator>() ?? instance.AddComponent<Animator>();
                animator.avatar = avatar;
                animator.runtimeAnimatorController = null;
                animator.applyRootMotion = source.applyRootMotion;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.Rebind();
                return animator;
            }
            catch
            {
                Delete(assetPath, instance == null ? null : instance.GetComponent<Animator>());
                if (instance != null) Object.DestroyImmediate(instance);
                throw;
            }
        }

        internal static void Delete(string assetPath, Animator animator)
        {
            if (string.IsNullOrEmpty(assetPath)) return;
            if (!assetPath.StartsWith("Assets/Codex/", StringComparison.Ordinal) ||
                !assetPath.Contains("/MocapTarget/") || !assetPath.EndsWith("/Target.fbx", StringComparison.Ordinal) ||
                assetPath.Contains(".."))
                throw new InvalidOperationException("임시 대상 FBX 삭제 범위 오류");
            if (animator != null) Object.DestroyImmediate(animator.gameObject);
            if ((!string.IsNullOrEmpty(AssetDatabase.AssetPathToGUID(assetPath)) || File.Exists(assetPath)) && !AssetDatabase.DeleteAsset(assetPath))
                Debug.LogError("임시 대상 FBX 삭제 실패: " + assetPath);
        }

        private static void WriteSkeletonCopy(string source, string destination, HashSet<string> boneNames)
        {
            var readPath = Path.GetFullPath(source);
            // The native FBX binding cannot reliably open Unicode paths on Windows.
            // Stage only this input outside Assets; never move/reimport the original.
            if (readPath.Any(c => c > 127))
            {
                var work = Path.GetFullPath(Path.Combine("Codex", DateTime.Now.ToString("yyyy-MM-dd"),
                    "MocapTarget", Guid.NewGuid().ToString("N")));
                if (work.Any(c => c > 127))
                    throw new IOException("FBX SDK용 영문 임시 경로를 만들 수 없습니다. 프로젝트 상위 경로를 확인하세요: " + work);
                Directory.CreateDirectory(work);
                readPath = Path.Combine(work, "Source.fbx");
                File.Copy(Path.GetFullPath(source), readPath);
            }
            using (var manager = FbxManager.Create())
            {
                var io = FbxIOSettings.Create(manager, Globals.IOSROOT);
                manager.SetIOSettings(io);
                io.SetBoolProp(Globals.IMP_FBX_ANIMATION, false);
                io.SetBoolProp(Globals.EXP_FBX_ANIMATION, false);
                io.SetBoolProp(Globals.EXP_FBX_MATERIAL, false);
                io.SetBoolProp(Globals.EXP_FBX_TEXTURE, false);
                using (var scene = FbxScene.Create(manager, "Temporary mocap target"))
                using (var reader = FbxImporter.Create(manager, "Read target FBX"))
                using (var writer = FbxExporter.Create(manager, "Write skeleton FBX"))
                {
                    if (!reader.Initialize(readPath, -1, io))
                        throw new IOException("대상 FBX 읽기 초기화 실패: " + source + " — " + reader.GetStatus().GetErrorString());
                    if (!reader.Import(scene))
                        throw new IOException("대상 FBX 읽기 실패: " + source + " — " + reader.GetStatus().GetErrorString());
                    Prune(scene.GetRootNode(), boneNames, true);
                    if (!writer.Initialize(Path.GetFullPath(destination), -1, io) || !writer.Export(scene))
                        throw new IOException("본 전용 대상 FBX 추출 실패: " + destination + " — " + writer.GetStatus().GetErrorString());
                }
            }
        }

        private static bool Prune(FbxNode node, HashSet<string> boneNames, bool root = false)
        {
            var keep = root || node.GetSkeleton() != null || boneNames.Contains(node.GetName());
            for (var i = node.GetChildCount() - 1; i >= 0; i--)
            {
                var child = node.GetChild(i);
                if (Prune(child, boneNames)) keep = true;
                else { node.RemoveChild(child); child.Destroy(); }
            }
            if (node.GetSkeleton() == null)
            {
                var attribute = node.GetNodeAttribute();
                // The C# FBX binding rejects SetNodeAttribute(null). Destroy
                // disconnects the attribute from its owner without that call.
                if (attribute != null) attribute.Destroy();
            }
            return keep;
        }

        private static void EnsureFolder(string path)
        {
            if (AssetDatabase.IsValidFolder(path)) return;
            var parent = Path.GetDirectoryName(path).Replace('\\', '/');
            EnsureFolder(parent);
            AssetDatabase.CreateFolder(parent, Path.GetFileName(path));
        }
    }
}
