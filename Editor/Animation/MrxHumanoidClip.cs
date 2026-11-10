using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    internal static class MrxHumanoidClip
    {
        internal static AnimationClip Build(string path, string output, string name)
        {

            var frames = File.ReadLines(path).Select(JObject.Parse)
                .OrderBy(frame => (long)frame["timeNs"])
                .GroupBy(frame => (float)((long)frame["timeNs"] / 1e9))
                .Select(group => (time: group.Key, frame: group.Last())).ToArray();
            if (frames.Length < 2)
                throw new InvalidDataException("Humanoid 모션에 필요한 MRX 포즈가 부족합니다.");

            var first = (JArray)frames[0].frame["subjects"];
            if (first.Count != 1)
                throw new InvalidDataException("현재는 MRX 파일당 스켈레톤 하나를 지원합니다.");

            var subjectId = (int)first[0]["id"];
            var names = first[0]["seg_names"].Values<string>().ToArray();
            var parents = first[0]["parents"].Values<int>().ToArray();
            if (parents.Length != names.Length || parents.Where((parent, index) =>
                parent < 0 || parent > names.Length || parent == index + 1).Any())
                throw new InvalidDataException("MRX 스켈레톤 부모 인덱스가 잘못되었습니다.");

            var reference = ReadBindPose();

            var root = new GameObject("MRX Source") { hideFlags = HideFlags.HideAndDontSave };
            AnimationClip motion = null;
            AnimatorController controller = null;
            try
            {
                var bones = names.Select(name =>
                {
                    var bone = new GameObject(name).transform;
                    bone.SetParent(root.transform, false);
                    return bone;
                }).ToArray();
                for (var index = 0; index < names.Length; index++)
                {
                    var parent = parents[index];
                    bones[index].SetParent(parent == 0 ? root.transform : bones[parent - 1], false);
                    var bind = reference[names[index]] ??
                        throw new InvalidDataException($"MMRP 기준 자세에 '{names[index]}' 본이 없습니다.");
                    var direction = Position(bind["t"]);
                    bones[index].localPosition = parent == 0 ? Vector3.zero :
                        direction.normalized * Position(first[0]["local"][index]).magnitude;
                    bones[index].localRotation = BindRotation(bind);
                }

                if (!OptiTrackMotionBindingService.TryBuildStandardHumanoid(root, out var human, out var skeleton, out var error))
                    throw new InvalidDataException(error);
                foreach (var bone in bones)
                {
                    var rest = Array.Find(skeleton, item => item.name == bone.name);
                    bone.localPosition = rest.position;
                }

                var bindings = new System.Collections.Generic.List<EditorCurveBinding>();
                var curves = new System.Collections.Generic.List<AnimationCurve>();
                var samples = frames.Select(entry =>
                {
                    var subject = entry.frame["subjects"].SingleOrDefault(item => (int)item["id"] == subjectId);
                    if (subject == null || !JToken.DeepEquals(subject["seg_names"], first[0]["seg_names"]) ||
                        !JToken.DeepEquals(subject["parents"], first[0]["parents"]))
                        throw new InvalidDataException("녹화 도중 MRX 스켈레톤 정의가 변경되었습니다.");
                    return (entry.time, local: (JArray)subject["local"]);
                }).ToArray();
                var properties = new[] { "m_LocalPosition.x", "m_LocalPosition.y", "m_LocalPosition.z",
                    "m_LocalRotation.x", "m_LocalRotation.y", "m_LocalRotation.z", "m_LocalRotation.w" };
                for (var bone = 0; bone < bones.Length; bone++)
                {
                    var bonePath = AnimationUtility.CalculateTransformPath(bones[bone], root.transform);
                    for (var channel = 0; channel < properties.Length; channel++)
                    {
                        var sign = channel == 0 || channel == 4 || channel == 5 ? -1 : 1;
                        var keys = samples.Select(sample => new Keyframe(sample.time,
                            sign * (float)sample.local[bone][channel])).ToArray();
                        bindings.Add(EditorCurveBinding.FloatCurve(bonePath, typeof(Transform), properties[channel]));
                        curves.Add(LinearCurve(keys));
                    }
                }

                motion = new AnimationClip { name = "MRX Motion", frameRate = 60 };
                AnimationUtility.SetEditorCurves(motion, bindings.ToArray(), curves.ToArray());
                motion.EnsureQuaternionContinuity();
                controller = new AnimatorController();
                controller.AddLayer("Base Layer");
                controller.AddMotion(motion);
                root.AddComponent<Animator>().runtimeAnimatorController = controller;

                var work = Path.GetDirectoryName(path);
                var staged = Path.Combine(work, "Source.fbx");
                var options = MocapFbxExporterCompat.BuildOptions(false, false, false, true, true);
                if (string.IsNullOrEmpty(MocapFbxExporterCompat.ExportObject(staged, root, options)) || !File.Exists(staged))
                    throw new IOException("MRX 원본 FBX를 생성하지 못했습니다.");

                var rawPath = AssetDatabase.GenerateUniqueAssetPath($"{output}/{name}_Raw.fbx");
                var humanPath = AssetDatabase.GenerateUniqueAssetPath($"{output}/{name}_Humanoid.fbx");
                File.Copy(staged, rawPath);
                File.Copy(staged, humanPath);
                AssetDatabase.ImportAsset(rawPath, ImportAssetOptions.ForceSynchronousImport);
                AssetDatabase.ImportAsset(humanPath, ImportAssetOptions.ForceSynchronousImport);
                var rawImporter = (ModelImporter)AssetImporter.GetAtPath(rawPath);
                Configure(rawImporter);
                rawImporter.animationType = ModelImporterAnimationType.Generic;
                rawImporter.SaveAndReimport();

                var importer = (ModelImporter)AssetImporter.GetAtPath(humanPath);
                Configure(importer);
                importer.animationType = ModelImporterAnimationType.Human;
                importer.avatarSetup = ModelImporterAvatarSetup.CreateFromThisModel;
                var description = importer.humanDescription;
                description.human = human;
                description.skeleton = skeleton;
                importer.humanDescription = description;
                var serialized = new SerializedObject(importer);
                var automatic = serialized.FindProperty("m_AutoGenerateAvatarMappingIfUnspecified");
                if (automatic != null)
                {
                    automatic.boolValue = false;
                    serialized.ApplyModifiedPropertiesWithoutUndo();
                }
                ApplyRootBakeDefaults(importer);
                importer.userData = MrxTimelineSource.Prefix + AssetDatabase.AssetPathToGUID(rawPath);
                importer.SaveAndReimport();

                var result = AssetDatabase.LoadAllAssetsAtPath(humanPath).OfType<AnimationClip>()
                    .SingleOrDefault(clip => clip.humanMotion && !clip.name.StartsWith("__preview__", StringComparison.Ordinal));
                if (result == null)
                    throw new InvalidDataException("MRX FBX에서 Humanoid 클립을 임포트하지 못했습니다.");
                return result;
            }
            finally
            {
                if (controller != null) UnityEngine.Object.DestroyImmediate(controller);
                if (motion != null) UnityEngine.Object.DestroyImmediate(motion);
                UnityEngine.Object.DestroyImmediate(root);
            }
        }

        internal static JToken ReadBindPose() => JObject.Parse(File.ReadAllText(MrxRuntime.BindPosePath()))["bones"];

        internal static Quaternion BindRotation(JToken bind)
        {
            var rotation = bind["r"].Values<float>().ToArray();
            // Mirror the Y-up MMRP reference across X, as for NatNet samples.
            return Quaternion.LookRotation(
                new Vector3(-rotation[2], rotation[5], rotation[8]),
                new Vector3(-rotation[1], rotation[4], rotation[7]));
        }
        internal static void ApplyRootBakeDefaults(ModelImporter importer)
        {
            var clips = importer.clipAnimations;
            if (clips.Length == 0)
                clips = importer.defaultClipAnimations;
            foreach (var clip in clips)
                FbxAnimationSetupService.ApplyRootBakeDefaults(clip);
            importer.clipAnimations = clips;
        }
        private static void Configure(ModelImporter importer)
        {
            importer.importAnimation = true;
            importer.animationCompression = ModelImporterAnimationCompression.Off;
            importer.optimizeGameObjects = false;
        }
        private static Vector3 Position(JToken values) =>
            new Vector3(-(float)values[0], (float)values[1], (float)values[2]);

        private static AnimationCurve LinearCurve(Keyframe[] keys)
        {
            for (var index = 1; index < keys.Length; index++)
            {
                var slope = (keys[index].value - keys[index - 1].value) /
                            (keys[index].time - keys[index - 1].time);
                keys[index - 1].outTangent = slope;
                keys[index].inTangent = slope;
            }
            return new AnimationCurve(keys);
        }
    }
}
