using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor.Tests
{
    public class FacialAnimationMirrorTests
    {
        [TestCase("eyeBlinkLeft", "eyeBlinkRight")]
        [TestCase("Smile_L", "Smile_R")]
        [TestCase("Smile.R", "Smile.L")]
        [TestCase("L_Smile", "R_Smile")]
        [TestCase("mouthSmileL", "mouthSmileR")]
        [TestCase("eye_left", "eye_right")]
        [TestCase("BRIGHT", "BRIGHT")]
        [TestCase("jawOpen", "jawOpen")]
        public void NamesAreReversible(string name, string expected)
        {
            Assert.AreEqual(expected, FacialAnimationMirror.FlipName(name));
            Assert.AreEqual(name, FacialAnimationMirror.FlipName(expected));
        }

        [Test]
        public void SwapsPairsMovesOneSidedAndPreservesOtherPathsAndMetadata()
        {
            var source = new AnimationClip { frameRate = 60, wrapMode = WrapMode.Loop };
            AnimationClip result = null;
            AnimationClip twice = null;
            try
            {
                var left = EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.eyeBlinkLeft");
                var right = left; right.propertyName = "blendShape.eyeBlinkRight";
                var single = left; single.propertyName = "blendShape.Smile_L";
                var other = left; other.path = "Other";
                var curve = new AnimationCurve(new Keyframe(0, 12, 3, 4, .2f, .3f), new Keyframe(1, 80, 5, 6));
                curve.preWrapMode = WrapMode.PingPong;
                AnimationUtility.SetEditorCurve(source, left, curve);
                AnimationUtility.SetEditorCurve(source, right, AnimationCurve.Linear(0, 90, 1, 20));
                AnimationUtility.SetEditorCurve(source, single, curve);
                AnimationUtility.SetEditorCurve(source, other, curve);
                AnimationUtility.SetAnimationEvents(source, new[] { new AnimationEvent { time = .5f, functionName = "TestEvent" } });
                result = FacialAnimationMirror.CreateFlippedClip(source, new[] { "Face" }, out var count);
                Assert.AreEqual(3, count);
                Assert.AreEqual(12, AnimationUtility.GetEditorCurve(result, right).keys[0].value);
                Assert.AreEqual(90, AnimationUtility.GetEditorCurve(result, left).keys[0].value);
                Assert.IsNull(AnimationUtility.GetEditorCurve(result, single));
                single.propertyName = "blendShape.Smile_R";
                Assert.AreEqual(12, AnimationUtility.GetEditorCurve(result, single).keys[0].value);
                Assert.AreEqual(12, AnimationUtility.GetEditorCurve(result, other).keys[0].value);
                Assert.AreEqual(60, result.frameRate);
                Assert.AreEqual("TestEvent", AnimationUtility.GetAnimationEvents(result).Single().functionName);
                twice = FacialAnimationMirror.CreateFlippedClip(result, new[] { "Face" }, out _);
                foreach (var binding in AnimationUtility.GetCurveBindings(source))
                {
                    var original = AnimationUtility.GetEditorCurve(source, binding);
                    var restored = AnimationUtility.GetEditorCurve(twice, binding);
                    CollectionAssert.AreEqual(original.keys, restored.keys);
                    Assert.AreEqual(original.preWrapMode, restored.preWrapMode);
                    Assert.AreEqual(original.postWrapMode, restored.postWrapMode);
                }
                Assert.AreEqual(12, AnimationUtility.GetEditorCurve(source, left).keys[0].value);
            }
            finally
            {
                Object.DestroyImmediate(source);
                if (result) Object.DestroyImmediate(result);
                if (twice) Object.DestroyImmediate(twice);
            }
        }
    }
}
