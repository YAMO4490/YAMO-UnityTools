using System;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor.Tests
{
    public class BlendShapeCurveRemapperTests
    {
        private const string FacePath = "Avatar/Face";
        private const string AccessoryPath = "Avatar/Glasses";
        private const string LeftProperty = "blendShape.eyeBlinkLeft";
        private const string RightProperty = "blendShape.eyeBlinkRight";
        private const string SmileProperty = "blendShape.mouthSmileLeft";

        private static readonly EditorCurveBinding FaceLeftBinding =
            CreateBlendShapeBinding(FacePath, LeftProperty);
        private static readonly EditorCurveBinding FaceRightBinding =
            CreateBlendShapeBinding(FacePath, RightProperty);
        private static readonly EditorCurveBinding AccessoryLeftBinding =
            CreateBlendShapeBinding(AccessoryPath, LeftProperty);

        [TestCase(0f, 0f)]
        [TestCase(5f, 0f)]
        [TestCase(10f, 0f)]
        [TestCase(11f, 0.2666667f)]
        [TestCase(25f, 4f)]
        [TestCase(50f, 10.6666667f)]
        [TestCase(75f, 17.3333333f)]
        [TestCase(84f, 19.7333333f)]
        [TestCase(85f, 100f)]
        [TestCase(100f, 100f)]
        public void RemapValueUsesInclusivePlateausAndCompressedMiddle(
            float input,
            float expected)
        {
            float actual = BlendShapeCurveRemapper.RemapValue(
                input,
                BlendShapeRemapSettings.Default);
            Assert.That(actual, Is.EqualTo(expected).Within(0.0001f));
        }

        [Test]
        public void RemapValueSupportsCustomThresholdsAndOutputs()
        {
            var settings = new BlendShapeRemapSettings
            {
                LowerThreshold = 20f,
                UpperThreshold = 80f,
                LowerOutput = 2f,
                MiddleMaximumOutput = 32f,
                UpperOutput = 95f
            };

            Assert.That(BlendShapeCurveRemapper.RemapValue(20f, settings), Is.EqualTo(2f));
            Assert.That(
                BlendShapeCurveRemapper.RemapValue(50f, settings),
                Is.EqualTo(17f).Within(0.0001f));
            Assert.That(BlendShapeCurveRemapper.RemapValue(80f, settings), Is.EqualTo(95f));
        }

        [Test]
        public void InvalidThresholdOrderIsRejected()
        {
            BlendShapeRemapSettings settings = BlendShapeRemapSettings.Default;
            settings.UpperThreshold = settings.LowerThreshold;

            Assert.That(
                BlendShapeCurveRemapper.GetSettingsValidationError(settings),
                Is.Not.Null.And.Not.Empty);
            Assert.Throws<ArgumentException>(
                () => BlendShapeCurveRemapper.RemapValue(50f, settings));
        }

        [Test]
        public void DiscoverMeshTracksGroupsExactPathsAndSortsProperties()
        {
            var clip = new AnimationClip();

            try
            {
                AnimationUtility.SetEditorCurve(
                    clip,
                    CreateBlendShapeBinding(FacePath, RightProperty),
                    AnimationCurve.Linear(0f, 0f, 1f, 100f));
                AnimationUtility.SetEditorCurve(
                    clip,
                    CreateBlendShapeBinding(FacePath, LeftProperty),
                    AnimationCurve.Linear(0f, 0f, 1f, 100f));
                AnimationUtility.SetEditorCurve(
                    clip,
                    CreateBlendShapeBinding(AccessoryPath, LeftProperty),
                    AnimationCurve.Linear(0f, 0f, 1f, 100f));
                AnimationUtility.SetEditorCurve(
                    clip,
                    EditorCurveBinding.FloatCurve(
                        FacePath,
                        typeof(SkinnedMeshRenderer),
                        "m_Enabled"),
                    AnimationCurve.Constant(0f, 1f, 1f));
                AnimationUtility.SetEditorCurve(
                    clip,
                    EditorCurveBinding.FloatCurve(
                        FacePath,
                        typeof(Transform),
                        "blendShape.notActuallyABlendShape"),
                    AnimationCurve.Constant(0f, 1f, 1f));

                var tracks = BlendShapeCurveRemapper.DiscoverMeshTracks(clip);

                Assert.That(tracks, Has.Count.EqualTo(2));
                Assert.That(tracks[0].MeshPath, Is.EqualTo(FacePath));
                Assert.That(
                    tracks[0].PropertyNames,
                    Is.EqualTo(new[] { LeftProperty, RightProperty }));
                Assert.That(tracks[1].MeshPath, Is.EqualTo(AccessoryPath));
                Assert.That(tracks[1].PropertyNames, Is.EqualTo(new[] { LeftProperty }));
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void AnalyzeClipMatchesExactMeshPathAndPropertyPair()
        {
            var clip = new AnimationClip();

            try
            {
                AnimationUtility.SetEditorCurve(
                    clip,
                    FaceLeftBinding,
                    AnimationCurve.Linear(0f, 0f, 1f, 100f));
                AnimationUtility.SetEditorCurve(
                    clip,
                    AccessoryLeftBinding,
                    AnimationCurve.Linear(0f, 25f, 1f, 75f));
                AnimationUtility.SetEditorCurve(
                    clip,
                    CreateBlendShapeBinding(FacePath, LeftProperty + "Extra"),
                    AnimationCurve.Linear(0f, 0f, 1f, 100f));

                BlendShapeRemapReport report = BlendShapeCurveRemapper.AnalyzeClip(
                    clip,
                    BlendShapeRemapSettings.Default,
                    new[] { new BlendShapeCurveTarget(FacePath, LeftProperty) });

                Assert.That(report.MatchedCurveCount, Is.EqualTo(1));
                Assert.That(report.TotalKeyCount, Is.EqualTo(2));
                Assert.That(report.Curves[0].MeshPath, Is.EqualTo(FacePath));
                Assert.That(report.Curves[0].PropertyName, Is.EqualTo(LeftProperty));
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void EmptyTargetSelectionIsRejected()
        {
            var clip = new AnimationClip();

            try
            {
                Assert.Throws<ArgumentException>(
                    () => BlendShapeCurveRemapper.AnalyzeClip(
                        clip,
                        BlendShapeRemapSettings.Default,
                        new BlendShapeCurveTarget[0]));
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void ProcessClipChangesMultiplePropertiesOnlyOnSelectedMesh()
        {
            var clip = new AnimationClip();
            var sourceValues = new[] { 0f, 5f, 10f, 50f, 84f, 85f, 100f };
            var expectedValues = new[] { 0f, 0f, 0f, 10.6666667f, 19.7333333f, 100f, 100f };
            var faceLeftCurve = CreateWeightedFreeCurve(sourceValues);
            var faceRightCurve = CreateWeightedFreeCurve(sourceValues);
            var accessoryCurve = AnimationCurve.Linear(0f, 25f, 1f, 75f);
            var smileBinding = CreateBlendShapeBinding(FacePath, SmileProperty);
            var smileCurve = AnimationCurve.Linear(0f, 30f, 1f, 60f);

            faceLeftCurve.preWrapMode = WrapMode.Loop;
            faceLeftCurve.postWrapMode = WrapMode.PingPong;

            try
            {
                AnimationUtility.SetEditorCurve(clip, FaceLeftBinding, faceLeftCurve);
                AnimationUtility.SetEditorCurve(clip, FaceRightBinding, faceRightCurve);
                AnimationUtility.SetEditorCurve(clip, AccessoryLeftBinding, accessoryCurve);
                AnimationUtility.SetEditorCurve(clip, smileBinding, smileCurve);

                BlendShapeRemapReport report = BlendShapeCurveRemapper.ProcessClip(
                    clip,
                    BlendShapeRemapSettings.Default,
                    new[]
                    {
                        new BlendShapeCurveTarget(FacePath, LeftProperty),
                        new BlendShapeCurveTarget(FacePath, RightProperty)
                    });

                Assert.That(report.MatchedCurveCount, Is.EqualTo(2));
                Assert.That(report.TotalKeyCount, Is.EqualTo(sourceValues.Length * 2));

                AssertProcessedLinearCurve(
                    AnimationUtility.GetEditorCurve(clip, FaceLeftBinding),
                    expectedValues);
                AssertProcessedLinearCurve(
                    AnimationUtility.GetEditorCurve(clip, FaceRightBinding),
                    expectedValues);

                AnimationCurve processedLeft =
                    AnimationUtility.GetEditorCurve(clip, FaceLeftBinding);
                Assert.That(processedLeft.preWrapMode, Is.EqualTo(WrapMode.Loop));
                Assert.That(processedLeft.postWrapMode, Is.EqualTo(WrapMode.PingPong));

                AnimationCurve untouchedAccessory =
                    AnimationUtility.GetEditorCurve(clip, AccessoryLeftBinding);
                Assert.That(untouchedAccessory.keys[0].value, Is.EqualTo(25f));
                Assert.That(untouchedAccessory.keys[1].value, Is.EqualTo(75f));

                AnimationCurve untouchedSmile =
                    AnimationUtility.GetEditorCurve(clip, smileBinding);
                Assert.That(untouchedSmile.keys[0].value, Is.EqualTo(30f));
                Assert.That(untouchedSmile.keys[1].value, Is.EqualTo(60f));
            }
            finally
            {
                Object.DestroyImmediate(clip);
            }
        }

        [Test]
        public void ProcessedCopyPreservesSourceAndClipMetadata()
        {
            string folderName = "__YamoBlendShapeRemapperTest_" + Guid.NewGuid().ToString("N");
            string folderPath = "Assets/" + folderName;
            string sourcePath = folderPath + "/Source.anim";
            string outputPath = folderPath + "/Source_BlendShapeRemapped.anim";

            Assert.That(AssetDatabase.CreateFolder("Assets", folderName), Is.Not.Empty);

            try
            {
                var source = new AnimationClip
                {
                    name = "Source",
                    frameRate = 60f,
                    wrapMode = WrapMode.Loop
                };
                AnimationUtility.SetEditorCurve(
                    source,
                    FaceLeftBinding,
                    new AnimationCurve(
                        new Keyframe(0f, 0f),
                        new Keyframe(1f, 50f),
                        new Keyframe(2f, 100f)));
                AnimationUtility.SetAnimationEvents(
                    source,
                    new[] { new AnimationEvent { time = 1f, functionName = "BlinkMarker" } });
                AssetDatabase.CreateAsset(source, sourcePath);
                AssetDatabase.SaveAssetIfDirty(source);

                BlendShapeRemapReport report;
                AnimationClip copy = BlendShapeCurveRemapper.CreateProcessedCopyAsset(
                    source,
                    outputPath,
                    BlendShapeRemapSettings.Default,
                    new[] { new BlendShapeCurveTarget(FacePath, LeftProperty) },
                    out report);

                Assert.That(report.MatchedCurveCount, Is.EqualTo(1));
                Assert.That(AssetDatabase.GetAssetPath(copy), Is.EqualTo(outputPath));
                Assert.That(copy.frameRate, Is.EqualTo(60f));
                Assert.That(AnimationUtility.GetAnimationEvents(copy), Has.Length.EqualTo(1));
                Assert.That(
                    AnimationUtility.GetAnimationEvents(copy)[0].functionName,
                    Is.EqualTo("BlinkMarker"));

                AnimationCurve sourceCurve =
                    AnimationUtility.GetEditorCurve(source, FaceLeftBinding);
                AnimationCurve copyCurve =
                    AnimationUtility.GetEditorCurve(copy, FaceLeftBinding);
                Assert.That(sourceCurve.keys[1].value, Is.EqualTo(50f));
                Assert.That(
                    copyCurve.keys[1].value,
                    Is.EqualTo(10.6666667f).Within(0.0001f));
            }
            finally
            {
                AssetDatabase.DeleteAsset(folderPath);
            }
        }

        private static EditorCurveBinding CreateBlendShapeBinding(
            string path,
            string propertyName)
        {
            return EditorCurveBinding.FloatCurve(
                path,
                typeof(SkinnedMeshRenderer),
                propertyName);
        }

        private static AnimationCurve CreateWeightedFreeCurve(float[] values)
        {
            var keys = new Keyframe[values.Length];
            for (int index = 0; index < values.Length; index++)
            {
                keys[index] = new Keyframe(
                    index,
                    values[index],
                    777f,
                    -555f,
                    0.25f,
                    0.75f)
                {
                    weightedMode = WeightedMode.Both
                };
            }

            var curve = new AnimationCurve(keys);
            for (int index = 0; index < curve.length; index++)
            {
                AnimationUtility.SetKeyBroken(curve, index, true);
                AnimationUtility.SetKeyLeftTangentMode(
                    curve,
                    index,
                    AnimationUtility.TangentMode.Free);
                AnimationUtility.SetKeyRightTangentMode(
                    curve,
                    index,
                    AnimationUtility.TangentMode.Free);
            }

            return curve;
        }

        private static void AssertProcessedLinearCurve(
            AnimationCurve processed,
            float[] expectedValues)
        {
            Assert.That(processed.length, Is.EqualTo(expectedValues.Length));

            for (int index = 0; index < processed.length; index++)
            {
                Keyframe key = processed.keys[index];
                Assert.That(key.time, Is.EqualTo(index));
                Assert.That(
                    key.value,
                    Is.EqualTo(expectedValues[index]).Within(0.0001f));
                Assert.That(key.weightedMode, Is.EqualTo(WeightedMode.None));
                Assert.That(
                    AnimationUtility.GetKeyLeftTangentMode(processed, index),
                    Is.EqualTo(AnimationUtility.TangentMode.Linear));
                Assert.That(
                    AnimationUtility.GetKeyRightTangentMode(processed, index),
                    Is.EqualTo(AnimationUtility.TangentMode.Linear));
                Assert.That(AnimationUtility.GetKeyBroken(processed, index), Is.False);
            }
        }
    }
}
