using System.Collections;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEngine;
using UnityEngine.TestTools;

namespace YAMO.UnityTools.Editor.Tests
{
    public sealed class YamoFacialRecorderTests
    {
        private static SkinnedMeshRenderer MakeFace(Transform root, string name)
        {
            var go = new GameObject(name);
            go.transform.SetParent(root, false);
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up }, triangles = new[] { 0, 1, 2 } };
            var delta = new[] { Vector3.forward, Vector3.zero, Vector3.zero };
            mesh.AddBlendShapeFrame("eyeBlinkLeft", 100f, delta, null, null);
            mesh.AddBlendShapeFrame("eyeBlinkRight", 100f, delta, null, null);
            var renderer = go.AddComponent<SkinnedMeshRenderer>();
            renderer.sharedMesh = mesh;
            return renderer;
        }

        [Test]
        public void PacketAcceptsBothDelimitersAndIgnoresMalformedOrNonFiniteValues()
        {
            Assert.That(YamoFacialPacket.TryParse("eyeBlink_L&25|eyeBlink_R-75|bad-NaN|junk= head#1,2,3", out var packet), Is.True);
            Assert.That(packet.Weights["eyeBlinkLeft"], Is.EqualTo(25f));
            Assert.That(packet.Weights["eyeBlinkRight"], Is.EqualTo(75f));
            Assert.That(packet.Weights.ContainsKey("bad"), Is.False);
            Assert.That(YamoFacialPacket.TryParse("broken|bad-Infinity", out _), Is.False);
        }

        [Test]
        public void PacketParsesRotationAndClampsWeightsUsingInvariantCulture()
        {
            var culture = System.Threading.Thread.CurrentThread.CurrentCulture;
            try
            {
                System.Threading.Thread.CurrentThread.CurrentCulture = new System.Globalization.CultureInfo("fr-FR");
                Assert.That(YamoFacialPacket.TryParse("jawOpen&150|mouthSmile_L&12.5=head#1.5,2,3|leftEye#4,5,6", out var packet), Is.True);
                Assert.That(packet.Weights["jawOpen"], Is.EqualTo(100f));
                Assert.That(packet.Weights["mouthSmileLeft"], Is.EqualTo(12.5f));
                Assert.That(packet.Rotations["head"], Is.EqualTo(new Vector3(1.5f, 2, 3)));
            }
            finally { System.Threading.Thread.CurrentThread.CurrentCulture = culture; }
        }

        [Test]
        public void SwitchingRoutesOnlyToSelectedMeshesAndInvalidSlotKeepsCurrentTarget()
        {
            var host = new GameObject("RecorderTest");
            Mesh meshA = null, meshB = null;
            try
            {
                var rootA = new GameObject("A").transform; rootA.SetParent(host.transform);
                var rootB = new GameObject("B").transform; rootB.SetParent(host.transform);
                var a = MakeFace(rootA, "Face"); meshA = a.sharedMesh;
                var b = MakeFace(rootB, "Face"); meshB = b.sharedMesh;
                var recorder = host.AddComponent<YamoFacialRecorder>();
                recorder.Mirror = false;
                recorder.Targets.Add(new YamoFacialTarget { Root = rootA, FaceMeshes = new[] { a } });
                recorder.Targets.Add(new YamoFacialTarget { Root = rootB, FaceMeshes = new[] { b } });
                Assert.That(recorder.SelectTarget(0), Is.True);
                recorder.ApplyPacket("eyeBlink_L&35");
                Assert.That(a.GetBlendShapeWeight(0), Is.EqualTo(35));
                Assert.That(b.GetBlendShapeWeight(0), Is.Zero);
                Assert.That(recorder.SelectTarget(1), Is.True);
                recorder.ApplyPacket("eyeBlink_L&65");
                Assert.That(a.GetBlendShapeWeight(0), Is.EqualTo(35));
                Assert.That(b.GetBlendShapeWeight(0), Is.EqualTo(65));
                Assert.That(recorder.SelectTarget(99), Is.False);
                Assert.That(recorder.ActiveTargetIndex, Is.EqualTo(1));
                recorder.Mirror = true;
                recorder.ApplyPacket("eyeBlink_L&90");
                Assert.That(b.GetBlendShapeWeight(1), Is.EqualTo(90));
            }
            finally
            {
                Object.DestroyImmediate(host);
                if (meshA != null) Object.DestroyImmediate(meshA);
                if (meshB != null) Object.DestroyImmediate(meshB);
            }
        }

        [Test]
        public void ValidationRejectsMeshesOutsideRootAndAmbiguousSiblingPaths()
        {
            var root = new GameObject("Root");
            var outside = new GameObject("Outside");
            Mesh mesh = null;
            try
            {
                var face = MakeFace(outside.transform, "Face"); mesh = face.sharedMesh;
                var slot = new YamoFacialTarget { Root = root.transform, FaceMeshes = new[] { face } };
                Assert.That(YamoFacialRecorder.ValidateTarget(slot, out _), Is.False);
                face.transform.SetParent(root.transform);
                Assert.That(YamoFacialRecorder.ValidateTarget(slot, out _), Is.True);
                new GameObject("Face").transform.SetParent(root.transform);
                Assert.That(YamoFacialRecorder.ValidateTarget(slot, out _), Is.False);
            }
            finally { Object.DestroyImmediate(root); Object.DestroyImmediate(outside); if (mesh != null) Object.DestroyImmediate(mesh); }
        }

        [UnityTest]
        public IEnumerator PlayModeSwitchSavesSeparateFacialOnlyClipsWithoutAvatar()
        {
            SessionState.SetString("YAMO.FacialRecorder.PlayTest", "running");
            yield return new EnterPlayMode();
            var host = new GameObject("YamoFacialIntegrationTest");
            host.SetActive(false);
            var output = "Assets/Codex/2026-11-28/FacialRecorderTests/" + System.Guid.NewGuid().ToString("N");
            Mesh meshA = null, meshB = null;
            try
            {
                var rootA = new GameObject("A").transform; rootA.SetParent(host.transform);
                var rootB = new GameObject("B").transform; rootB.SetParent(host.transform);
                var a = MakeFace(rootA, "Face"); meshA = a.sharedMesh;
                var b = MakeFace(rootB, "Face"); meshB = b.sharedMesh;
                var recorder = host.AddComponent<YamoFacialRecorder>();
                recorder.EnableHotkeys = false;
                recorder.ShowOverlay = false;
                recorder.OutputFolder = output;
                recorder.Mirror = false;
                recorder.Targets.Add(new YamoFacialTarget { Label = "A", Root = rootA, FaceMeshes = new[] { a } });
                recorder.Targets.Add(new YamoFacialTarget { Label = "B", Root = rootB, FaceMeshes = new[] { b } });
                // Exercise the actual socket worker and main-thread packet handoff.
                using (var probe = new System.Net.Sockets.UdpClient(0))
                    recorder.Port = ((System.Net.IPEndPoint)probe.Client.LocalEndPoint).Port;
                recorder.DeviceAddress = "127.0.0.1";
                host.SetActive(true);
                Assert.That(recorder.ActiveTargetIndex, Is.EqualTo(0));
                Assert.That(recorder.IsReceiving, Is.True, recorder.Status);
                using (var sender = new System.Net.Sockets.UdpClient())
                {
                    var bytes = System.Text.Encoding.UTF8.GetBytes("eyeBlink_L&23");
                    sender.Send(bytes, bytes.Length, "127.0.0.1", recorder.Port);
                }
                var receiveDeadline = Time.realtimeSinceStartup + 2f;
                while (!recorder.HasRecentPacket && Time.realtimeSinceStartup < receiveDeadline) yield return null;
                Assert.That(a.GetBlendShapeWeight(0), Is.EqualTo(23f));
                Assert.That(recorder.HasRecentPacket, Is.True);
                recorder.Disconnect();
                Assert.That(recorder.IsReceiving, Is.False);
                Assert.That(recorder.StartRecording(), Is.True);
                recorder.ApplyPacket("eyeBlink_L&70");
                recorder.ShowOverlay = true;
                System.IO.Directory.CreateDirectory("Codex/2026-11-28/FacialRecorderTests");
                yield return null;
                ScreenCapture.CaptureScreenshot("Codex/2026-11-28/FacialRecorderTests/recording-gameview.png");
                yield return null;
                Assert.That(recorder.SelectTarget(1), Is.True);
                var pathA = recorder.LastSavedPath;
                Assert.That(recorder.IsRecording, Is.True);
                recorder.ApplyPacket("eyeBlink_L&40");
                yield return null;
                Assert.That(recorder.StopRecording(), Is.True);
                var pathB = recorder.LastSavedPath;
                Assert.That(pathA, Is.Not.EqualTo(pathB));
                var clipA = AssetDatabase.LoadAssetAtPath<AnimationClip>(pathA);
                var clipB = AssetDatabase.LoadAssetAtPath<AnimationClip>(pathB);
                foreach (var clip in new[] { clipA, clipB })
                {
                    Assert.That(clip, Is.Not.Null);
                    var bindings = AnimationUtility.GetCurveBindings(clip);
                    Assert.That(bindings.Length, Is.EqualTo(2));
                    Assert.That(bindings.All(c => c.type == typeof(SkinnedMeshRenderer) && c.path == "Face" && c.propertyName.StartsWith("blendShape.")), Is.True);
                    Assert.That(clip.length, Is.GreaterThan(0));
                }
                var binding = EditorCurveBinding.FloatCurve("Face", typeof(SkinnedMeshRenderer), "blendShape.eyeBlinkLeft");
                Assert.That(AnimationUtility.GetEditorCurve(clipA, binding).Evaluate(clipA.length), Is.EqualTo(70f).Within(.01f));
                Assert.That(AnimationUtility.GetEditorCurve(clipB, binding).Evaluate(clipB.length), Is.EqualTo(40f).Within(.01f));
                Assert.That(recorder.StartRecording(), Is.True);
                yield return null;
                recorder.enabled = false;
                Assert.That(recorder.IsReceiving, Is.False);
                Assert.That(AssetDatabase.LoadAssetAtPath<AnimationClip>(recorder.LastSavedPath), Is.Not.Null);
                Assert.That(recorder.IsRecording, Is.False);
                SessionState.SetString("YAMO.FacialRecorder.PlayTest", "PASS: UDP loopback, target switch, separate clips, facial-only bindings, final values, save on disable");
                // Retain test artifacts under the project's Codex folder for review.
                Debug.Log("YAMO Facial test clips: " + output);
            }
            finally
            {
                Object.DestroyImmediate(host);
                if (meshA != null) Object.DestroyImmediate(meshA);
                if (meshB != null) Object.DestroyImmediate(meshB);
            }
            yield return new ExitPlayMode();
        }
    }
}
