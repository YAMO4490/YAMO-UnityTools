using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Autodesk.Fbx;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor.Tests
{
    internal sealed class MocapTemporaryTargetTests
    {
        private static Type TargetType => typeof(MocapToBipedFbxPipeline).Assembly
            .GetType("YAMO.UnityTools.Editor.MocapTemporaryTarget", true);

        [TestCase(false)]
        [TestCase(true)]
        public void SkeletonCopyRemovesMeshAndKeepsBoneRestTransformAndAncestors(bool unicodeSource)
        {
            var work = Path.Combine("Codex", DateTime.Now.ToString("yyyy-MM-dd"), "MocapTargetTests", Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(work);
            var input = Path.GetFullPath(Path.Combine(work, "Input.fbx"));
            var output = Path.GetFullPath(Path.Combine(work, "Skeleton.fbx"));
            using (var manager = FbxManager.Create())
            {
                var io = FbxIOSettings.Create(manager, Globals.IOSROOT);
                manager.SetIOSettings(io);
                using (var scene = FbxScene.Create(manager, "Fixture"))
                using (var writer = FbxExporter.Create(manager, "Fixture writer"))
                {
                    var parent = FbxNode.Create(scene, "Ancestor");
                    scene.GetRootNode().AddChild(parent);
                    var bone = FbxNode.Create(scene, "MappedBone");
                    bone.SetNodeAttribute(FbxSkeleton.Create(scene, "Bone attribute"));
                    bone.LclRotation.Set(new FbxDouble3(12, 23, 34));
                    parent.AddChild(bone);
                    var meshNode = FbxNode.Create(scene, "MeshNode");
                    meshNode.SetNodeAttribute(FbxMesh.Create(scene, "Mesh"));
                    scene.GetRootNode().AddChild(meshNode);
                    Assert.That(writer.Initialize(input, -1, io) && writer.Export(scene), Is.True);
                }
                if (unicodeSource)
                {
                    var unicodeInput = Path.Combine(work, "한글_대상_아바타.fbx");
                    File.Copy(input, unicodeInput);
                    input = Path.GetFullPath(unicodeInput);
                }
                TargetType.GetMethod("WriteSkeletonCopy", BindingFlags.Static | BindingFlags.NonPublic)
                    .Invoke(null, new object[] { input, output, new HashSet<string> { "MappedBone" } });
                using (var scene = FbxScene.Create(manager, "Result"))
                using (var reader = FbxImporter.Create(manager, "Result reader"))
                {
                    Assert.That(reader.Initialize(output, -1, io) && reader.Import(scene), Is.True);
                    var root = scene.GetRootNode();
                    Assert.That(root.GetChildCount(), Is.EqualTo(1));
                    var parent = root.GetChild(0);
                    Assert.That(parent.GetName(), Is.EqualTo("Ancestor"));
                    var bone = parent.GetChild(0);
                    Assert.That(bone.GetName(), Is.EqualTo("MappedBone"));
                    Assert.That(bone.GetSkeleton(), Is.Not.Null);
                    var rotation = bone.LclRotation.Get();
                    Assert.That(rotation[0], Is.EqualTo(12).Within(.001));
                    Assert.That(rotation[1], Is.EqualTo(23).Within(.001));
                    Assert.That(rotation[2], Is.EqualTo(34).Within(.001));
                }
            }
        }

        [Test]
        public void ResetPoseUsesPrefabPoseAndRequiredApisExist()
        {
            var prefab = new GameObject("Root");
            var child = new GameObject("Bone");
            child.transform.SetParent(prefab.transform, false);
            child.transform.localRotation = Quaternion.Euler(11, 22, 33);
            var instance = Object.Instantiate(prefab);
            try
            {
                instance.transform.GetChild(0).localRotation = Quaternion.Euler(70, 80, 90);
                // Confirm the actual API required by Reset Pose exists in this Editor.
                var tool = typeof(UnityEditor.Editor).Assembly.GetType("UnityEditor.AvatarSetupTool", true);
                var copy = tool.GetMethod("CopyPose", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(GameObject), typeof(GameObject) }, null);
                Assert.That(copy, Is.Not.Null);
                copy.Invoke(null, new object[] { instance, prefab });
                Assert.That(Quaternion.Angle(instance.transform.GetChild(0).localRotation,
                    child.transform.localRotation), Is.LessThan(.001f));
                Assert.That(tool.GetMethod("TransferPoseToDescription", BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(SerializedProperty), typeof(Transform) }, null), Is.Not.Null);
            }
            finally { Object.DestroyImmediate(instance); Object.DestroyImmediate(prefab); }
        }
    }
}
