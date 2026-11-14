using System.Linq;
using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor.Tests
{
    /// <summary>
    /// MMRP (Mingle Motion Replayer) exports use the Unity humanoid bone names
    /// without an actor prefix; these tests pin the second source format the
    /// binding service accepts beside OptiTrack.
    /// </summary>
    internal sealed class MocapSourceFormatTests
    {
        private static readonly string[] StandardBodyBones =
        {
            "Hips", "Spine", "Chest", "UpperChest", "Neck", "Head",
            "LeftShoulder", "LeftUpperArm", "LeftLowerArm", "LeftHand",
            "RightShoulder", "RightUpperArm", "RightLowerArm", "RightHand",
            "LeftUpperLeg", "LeftLowerLeg", "LeftFoot", "LeftToes",
            "RightUpperLeg", "RightLowerLeg", "RightFoot", "RightToes"
        };

        [Test]
        public void PipelineSettingsDefaultToOptiTrack()
        {
            Assert.That(new MocapPipelineSettings().SourceFormat, Is.EqualTo(MocapSourceFormat.OptiTrack));
            Assert.That(MocapSourceFormat.OptiTrack.GetLabel(), Is.EqualTo("OptiTrack"));
            Assert.That(MocapSourceFormat.MMRP.GetLabel(), Is.EqualTo("MMRP"));
        }

        [Test]
        public void StandardHumanoidMappingUsesUnityBoneNamesWithoutPrefix()
        {
            var root = new GameObject("MmrpMappingRoot");
            try
            {
                foreach (var name in StandardBodyBones
                             .Concat(new[]
                             {
                                 "LeftHandThumb1", "LeftHandIndex1", "LeftHandLittle1",
                                 "RightHandThumb1", "RightHandIndex1", "RightHandPinky1",
                                 "MMRP_Deform_Palette_001", "MMRP_ControlHelper_005"
                             }))
                {
                    new GameObject(name).transform.SetParent(root.transform, false);
                }

                var method = typeof(OptiTrackMotionBindingService).GetMethod(
                    "TryBuildStandardHumanoid",
                    BindingFlags.NonPublic | BindingFlags.Static);
                Assert.That(method, Is.Not.Null);

                var arguments = new object[] { root, null, null, null };
                Assert.That((bool)method.Invoke(null, arguments), Is.True, arguments[3] as string);
                var human = (HumanBone[])arguments[1];
                var skeleton = (SkeletonBone[])arguments[2];

                Assert.That(human.Single(bone => bone.humanName == "Hips").boneName, Is.EqualTo("Hips"));
                Assert.That(human.Single(bone => bone.humanName == "Chest").boneName, Is.EqualTo("Chest"));
                Assert.That(human.Single(bone => bone.humanName == "LeftUpperArm").boneName, Is.EqualTo("LeftUpperArm"));
                Assert.That(human.Single(bone => bone.humanName == "Left Thumb Proximal").boneName, Is.EqualTo("LeftHandThumb1"));
                Assert.That(human.Single(bone => bone.humanName == "Left Little Proximal").boneName, Is.EqualTo("LeftHandLittle1"));
                Assert.That(human.Single(bone => bone.humanName == "Right Little Proximal").boneName, Is.EqualTo("RightHandPinky1"));
                Assert.That(human.Any(bone => bone.humanName == "UpperChest"), Is.False, "UpperChest is never mapped");
                Assert.That(human.Any(bone => bone.humanName.EndsWith("Eye") || bone.humanName == "Jaw"), Is.False);
                Assert.That(human.Any(bone => bone.boneName.StartsWith("MMRP_")), Is.False);
                Assert.That(skeleton.Length, Is.EqualTo(root.GetComponentsInChildren<Transform>(true).Length));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void StandardHumanoidMappingReportsMissingRequiredBones()
        {
            var root = new GameObject("MmrpMissingRoot");
            try
            {
                new GameObject("Hips").transform.SetParent(root.transform, false);
                new GameObject("Spine").transform.SetParent(root.transform, false);

                var method = typeof(OptiTrackMotionBindingService).GetMethod(
                    "TryBuildStandardHumanoid",
                    BindingFlags.NonPublic | BindingFlags.Static);
                var arguments = new object[] { root, null, null, null };
                Assert.That((bool)method.Invoke(null, arguments), Is.False);
                Assert.That(arguments[3] as string, Does.Contain("Head"));
            }
            finally
            {
                Object.DestroyImmediate(root);
            }
        }

        [Test]
        public void HipOriginSkeletonUsesSpine2AndGroundedReferenceHeight()
        {
            var root = new GameObject("HipOrigin");
            try
            {
                foreach (var name in StandardBodyBones)
                {
                    var bone = new GameObject(name == "Hips" ? "Hip" : name == "Spine" ? "Spine2" : name);
                    bone.transform.SetParent(root.transform, false);
                    if (name.EndsWith("Foot"))
                        bone.transform.localPosition = Vector3.down * 0.8f;
                }
                new GameObject("Ab").transform.SetParent(root.transform, false);
                new GameObject("Spine4").transform.SetParent(root.transform, false);
                var method = typeof(OptiTrackMotionBindingService).GetMethod(
                    "TryBuildStandardHumanoid", BindingFlags.NonPublic | BindingFlags.Static);
                var arguments = new object[] { root, null, null, null };
                Assert.That((bool)method.Invoke(null, arguments), Is.True);
                Assert.That(((HumanBone[])arguments[1]).Single(b => b.humanName == "Spine").boneName,
                    Is.EqualTo("Spine2"));
                Assert.That(((HumanBone[])arguments[1]).Single(b => b.humanName == "Chest").boneName,
                    Is.EqualTo("Spine4"));
                Assert.That(((HumanBone[])arguments[1]).Any(b => b.humanName == "UpperChest"), Is.False);
                Assert.That(((SkeletonBone[])arguments[2]).Single(b => b.name == "Hip").position.y,
                    Is.EqualTo(0.8f).Within(0.00001f));
                Assert.That(root.transform.Find("Hip").localPosition, Is.EqualTo(Vector3.zero));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void RemapDropsEyesJawUpperChestAndMmrpHelperNodes()
        {
            var captured = new[]
            {
                Bone("Hips", "Hips"),
                Bone("Spine", "Spine"),
                Bone("UpperChest", "UpperChest"),
                Bone("Jaw", "Jaw"),
                Bone("LeftEye", "LeftEye"),
                // AvatarSetupTool latched RightEye onto an MMRP helper node.
                Bone("RightEye", "MMRP_Deform_Palette_106"),
                Bone("Head", "Head")
            };

            var remapped = OptiTrackMotionBindingService.RemapHumanBones(captured, MocapSourceFormat.MMRP, null);

            CollectionAssert.AreEquivalent(
                new[] { "Hips", "Spine", "Head" },
                remapped.Select(bone => bone.humanName));
            Assert.That(remapped.Single(bone => bone.humanName == "Spine").boneName, Is.EqualTo("Spine"));
        }

        [Test]
        public void RemapForcesOptiTrackSpineChainOntoPrefixedBones()
        {
            var captured = new[]
            {
                Bone("Hips", "001_Hips"),
                Bone("Spine", "001_Spine1"),
                Bone("Chest", "001_Spine2"),
                Bone("UpperChest", "001_Spine4"),
                Bone("LeftEye", "001_LeftEye")
            };

            var remapped = OptiTrackMotionBindingService.RemapHumanBones(captured, MocapSourceFormat.OptiTrack, "001");

            Assert.That(remapped.Single(bone => bone.humanName == "Spine").boneName, Is.EqualTo("001_Spine1"));
            Assert.That(remapped.Single(bone => bone.humanName == "Chest").boneName, Is.EqualTo("001_Spine3"));
            Assert.That(remapped.Any(bone => bone.humanName == "UpperChest"), Is.False);
            Assert.That(remapped.Any(bone => bone.humanName == "LeftEye"), Is.False);
        }

        [Test]
        public void DetectSourceFormatDistinguishesOptiTrackFromMmrp()
        {
            var optiTrack = new GameObject("OptiTrackRoot");
            var mmrp = new GameObject("MmrpRoot");
            var unknown = new GameObject("UnknownRoot");
            try
            {
                new GameObject("001_Hips").transform.SetParent(optiTrack.transform, false);
                var mmrpRoot = new GameObject("Root");
                mmrpRoot.transform.SetParent(mmrp.transform, false);
                new GameObject("Hips").transform.SetParent(mmrpRoot.transform, false);
                new GameObject("Pelvis").transform.SetParent(unknown.transform, false);

                Assert.That(
                    OptiTrackMotionBindingService.DetectSourceFormat(optiTrack.transform),
                    Is.EqualTo(MocapSourceFormat.OptiTrack));
                Assert.That(
                    OptiTrackMotionBindingService.DetectSourceFormat(mmrp.transform),
                    Is.EqualTo(MocapSourceFormat.MMRP));
                Assert.That(OptiTrackMotionBindingService.DetectSourceFormat(unknown.transform), Is.Null);
                Assert.That(OptiTrackMotionBindingService.DetectSourceFormat((Transform)null), Is.Null);
            }
            finally
            {
                Object.DestroyImmediate(optiTrack);
                Object.DestroyImmediate(mmrp);
                Object.DestroyImmediate(unknown);
            }
        }

        [Test]
        public void DetectAlternateMmrpRequiresFullHipAbSpineChain()
        {
            var root = new GameObject("AlternateMmrp");
            try
            {
                foreach (var name in new[] { "Hip", "Ab", "Spine2" })
                    new GameObject(name).transform.SetParent(root.transform, false);
                Assert.That(OptiTrackMotionBindingService.DetectSourceFormat(root.transform), Is.Null);
                new GameObject("Spine4").transform.SetParent(root.transform, false);
                Assert.That(OptiTrackMotionBindingService.DetectSourceFormat(root.transform),
                    Is.EqualTo(MocapSourceFormat.MMRP));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void GeneratedAssetsAreRecognizedBySuffix()
        {
            Assert.That(OptiTrackMotionBindingService.IsBackupAsset("Assets/Motion/Drip_003_Backup.fbx"), Is.True);
            Assert.That(OptiTrackMotionBindingService.IsBackupAsset("Assets/Motion/Drip_003.fbx"), Is.False);
            Assert.That(OptiTrackMotionBindingService.IsGeneratedAsset("Assets/Motion/Drip_003_T.fbx"), Is.True);
            Assert.That(OptiTrackMotionBindingService.IsGeneratedAsset("Assets/Motion/Drip_003_Backup.fbx"), Is.True);
            Assert.That(OptiTrackMotionBindingService.IsGeneratedAsset("Assets/Motion/Drip_003.fbx"), Is.False);
        }

        [Test]
        public void HumanoidCaptureFallbackDoesNotInvokeNativePointerOverload()
        {
            var model = new GameObject("UnsupportedSkeleton");
            try
            {
                var method = typeof(OptiTrackMotionBindingService).GetMethod(
                    "TryCaptureHumanoid", BindingFlags.NonPublic | BindingFlags.Static);
                var arguments = new object[] { model, null, null, false };
                Assert.DoesNotThrow(() => method.Invoke(null, arguments));
            }
            finally
            {
                Object.DestroyImmediate(model);
            }
        }

        [Test]
        public void RecordedFixedBoneTranslationSurvivesClipRebuild()
        {
            var method = typeof(ForearmHingeBakeService).GetMethod(
                "BuildClipFromRecordedPaths", BindingFlags.NonPublic | BindingFlags.Static);
            var clip = (AnimationClip)method.Invoke(null, new object[]
            {
                "FixedPosition", 30, new[] { "Bip001" },
                new[] { new[] { Quaternion.identity, Quaternion.identity } },
                new[] { new[] { new Vector3(1, 2, 3), new Vector3(1, 2, 3) } }, 2
            });
            var root = new GameObject("Playback");
            var bone = new GameObject("Bip001");
            bone.transform.SetParent(root.transform, false);
            try
            {
                bone.transform.localPosition = new Vector3(20, 30, 40);
                clip.SampleAnimation(root, 0);
                Assert.That(Vector3.Distance(bone.transform.localPosition, new Vector3(1, 2, 3)),
                    Is.LessThan(0.00001f));
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(clip);
            }
        }

        private static HumanBone Bone(string humanName, string boneName)
        {
            return new HumanBone
            {
                humanName = humanName,
                boneName = boneName,
                limit = new HumanLimit { useDefaultValues = true }
            };
        }
    }
}
