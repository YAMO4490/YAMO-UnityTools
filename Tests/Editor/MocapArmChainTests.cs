using System;
using NUnit.Framework;

namespace YAMO.UnityTools.Editor.Tests
{
    internal sealed class MocapArmChainTests
    {
        [Test]
        public void MmrpAlternateNamesRetainAllEightChainSlots()
        {
            var names = new[] { "Hip", "Ab", "Spine2", "Spine4", "LShoulder", "LUArm", "LFArm", "LHand",
                "RShoulder", "RUArm", "RFArm", "RHand" };
            CollectionAssert.AreEqual(new[] { "LShoulder", "LUArm", "LFArm", "LHand", "RShoulder", "RUArm", "RFArm", "RHand" },
                MocapArmChainPipeline.ResolveRawArmBones(names, MocapSourceFormat.MMRP));
        }

        [Test]
        public void OptitrackUsesTheActorsActualPrefix()
        {
            var names = new[] { "Artist_007_Hips", "Artist_007_LeftShoulder", "Artist_007_LeftArm", "Artist_007_LeftForeArm",
                "Artist_007_LeftHand", "Artist_007_RightShoulder", "Artist_007_RightArm", "Artist_007_RightForeArm", "Artist_007_RightHand" };
            var mapped = MocapArmChainPipeline.ResolveRawArmBones(names, MocapSourceFormat.OptiTrack);
            Assert.That(mapped[0], Is.EqualTo("Artist_007_LeftShoulder"));
            Assert.That(mapped[6], Is.EqualTo("Artist_007_RightForeArm"));
        }

        [Test]
        public void MultipleOptitrackActorsAreRejectedInsteadOfMixingArms()
        {
            Assert.Throws<InvalidOperationException>(() => MocapArmChainPipeline.ResolveRawArmBones(
                new[] { "001_Hips", "002_Hips", "001_LeftShoulder", "002_RightHand" }, MocapSourceFormat.OptiTrack));
        }

        [Test]
        public void MmrpConflictingAliasesAreRejected()
        {
            Assert.Throws<InvalidOperationException>(() => MocapArmChainPipeline.ResolveRawArmBones(
                new[] { "LeftShoulder", "LShoulder", "LUArm", "LFArm", "LHand", "RShoulder", "RUArm", "RFArm", "RHand" }, MocapSourceFormat.MMRP));
        }
    }
}
