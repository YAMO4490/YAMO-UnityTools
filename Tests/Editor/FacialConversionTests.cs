using System.IO;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor.Tests
{
    public sealed class FacialConversionTests
    {
        [TestCase(false, 1)]
        [TestCase(true, 52)]
        public void ArkitExtractionKeepsValuesAndTargetsFace(bool fillMissing, int expectedCount)
        {
            var source = new AnimationClip();
            AnimationClip result = null;
            try
            {
                AnimationUtility.SetEditorCurve(source,
                    EditorCurveBinding.FloatCurve("OriginalMesh", typeof(SkinnedMeshRenderer), "blendShape.eyeBlinkLeft"),
                    AnimationCurve.Linear(0, 0, 1, 75));
                result = YAMO.Editor.FacialFbxToAnimation.Extract(source, "OriginalMesh", fillMissing, out var missing);
                var bindings = AnimationUtility.GetCurveBindings(result);
                Assert.That(bindings.Length, Is.EqualTo(expectedCount));
                Assert.That(missing.Length, Is.EqualTo(51));
                Assert.That(bindings.All(b => b.path == "Face"), Is.True);
                var blink = bindings.Single(b => b.propertyName == "blendShape.eyeBlinkLeft");
                Assert.That(AnimationUtility.GetEditorCurve(result, blink).Evaluate(1), Is.EqualTo(75).Within(0.001));
            }
            finally { Object.DestroyImmediate(source); if (result) Object.DestroyImmediate(result); }
        }

        [Test]
        public void CsvRejectsNonIncreasingTimestamps()
        {
            var path = Path.GetTempFileName();
            try
            {
                File.WriteAllText(path, "time_s,eyeBlinkLeft\n1,0\n1,0.5\n");
                Assert.Throws<InvalidDataException>(() => YAMO.Editor.FacialCsvToAnimation.Convert(path));
            }
            finally { File.Delete(path); }
        }
    }
}
