using System.Reflection;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor.Tests
{
    internal sealed class Unity7CompatibilityTests
    {
        private static void Invoke(BlendShapeLookAt gaze, string method) =>
            typeof(BlendShapeLookAt).GetMethod(method, BindingFlags.Instance | BindingFlags.NonPublic)
                .Invoke(gaze, null);

        [Test]
        public void LookAtToleratesRendererWithoutMesh()
        {
            var root = new GameObject("MissingMeshProbe");
            try
            {
                var renderer = root.AddComponent<SkinnedMeshRenderer>();
                var gaze = root.AddComponent<BlendShapeLookAt>();
                gaze.faceMesh = renderer;
                Assert.DoesNotThrow(() => Invoke(gaze, "CacheIndices"));
                Assert.DoesNotThrow(() => Invoke(gaze, "LateUpdate"));
                gaze.active = false;
                Assert.DoesNotThrow(() => Invoke(gaze, "LateUpdate"));
            }
            finally { Object.DestroyImmediate(root); }
        }

        [Test]
        public void LookAtRefreshesIndicesWhenMeshChanges()
        {
            var root = new GameObject("MeshSwapProbe");
            var first = CreateMesh(false);
            var second = CreateMesh(true);
            try
            {
                var renderer = root.AddComponent<SkinnedMeshRenderer>();
                renderer.sharedMesh = first;
                var gaze = root.AddComponent<BlendShapeLookAt>();
                gaze.faceMesh = renderer;
                Invoke(gaze, "CacheIndices");
                renderer.sharedMesh = second;
                renderer.SetBlendShapeWeight(0, 77);
                renderer.SetBlendShapeWeight(1, 50);
                gaze.active = false;
                Invoke(gaze, "LateUpdate");
                Assert.That(renderer.GetBlendShapeWeight(0), Is.EqualTo(77));
                Assert.That(renderer.GetBlendShapeWeight(1), Is.Zero);
                renderer.sharedMesh = null;
                Assert.DoesNotThrow(() => Invoke(gaze, "LateUpdate"));
            }
            finally
            {
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(first);
                Object.DestroyImmediate(second);
            }
        }

        [Test]
        public void EntityIdPersistenceRoundTripsWithSupportedApi()
        {
            var root = new GameObject("EntityIdProbe");
            try
            {
                var stored = EntityId.ToULong(root.GetEntityId());
                Assert.That(UnityEditor.EditorUtility.EntityIdToObject(EntityId.FromULong(stored)),
                    Is.SameAs(root));
            }
            finally { Object.DestroyImmediate(root); }
        }

        private static Mesh CreateMesh(bool includeUnrelated)
        {
            var mesh = new Mesh { vertices = new[] { Vector3.zero, Vector3.right, Vector3.up } };
            var deltas = new Vector3[3];
            if (includeUnrelated)
                mesh.AddBlendShapeFrame("Unrelated", 100, deltas, deltas, deltas);
            mesh.AddBlendShapeFrame("eyeLookUpLeft", 100, deltas, deltas, deltas);
            return mesh;
        }

        [Test]
        public void MaterialCollectorFindsTextureWithoutReadingNonTextureProperties()
        {
            var root = new GameObject("ShaderPropertiesProbe");
            var shader = Shader.Find("Universal Render Pipeline/Lit");
            Assert.That(shader, Is.Not.Null);
            var material = new Material(shader);
            var texture = new Texture2D(2, 2);
            var window = ScriptableObject.CreateInstance<MaterialAndTextureCollectorWindow>();
            var flags = BindingFlags.Instance | BindingFlags.NonPublic;
            try
            {
                material.SetTexture("_BaseMap", texture);
                root.AddComponent<MeshRenderer>().sharedMaterial = material;
                typeof(MaterialAndTextureCollectorWindow).GetField("targetPrefab", flags).SetValue(window, root);
                typeof(MaterialAndTextureCollectorWindow).GetMethod("CollectDuplicates", flags).Invoke(window, null);
                var collected = (System.Collections.Generic.HashSet<Texture>)typeof(MaterialAndTextureCollectorWindow)
                    .GetField("collectedTextures", flags).GetValue(window);
                Assert.That(collected, Does.Contain(texture));
            }
            finally
            {
                Object.DestroyImmediate(window);
                Object.DestroyImmediate(root);
                Object.DestroyImmediate(material);
                Object.DestroyImmediate(texture);
            }
        }

        [Test]
        public void FbxOptionsResolveFromCurrentlyLoadedAssemblies()
        {
            var options = MocapFbxExporterCompat.BuildOptions(false, false, false, true, true);
            Assert.That(options, Is.Not.Null);
            Assert.That(options.GetType().FullName,
                Is.EqualTo("UnityEditor.Formats.Fbx.Exporter.ExportModelOptions"));
        }

        [Test]
        public void CompositionOverlayRendersIntoOutputAndStopsAfterClosing()
        {
            var scene = UnityEditor.SceneManagement.EditorSceneManager.NewPreviewScene();
            var root = new GameObject("OverlayProbe");
            UnityEngine.SceneManagement.SceneManager.MoveGameObjectToScene(root, scene);
            CameraCompositionWindow window = null;
            var textures = new System.Collections.Generic.List<Texture2D>();
            var path = System.IO.Path.GetFullPath("Temp/OverlayProbe_" + System.Guid.NewGuid().ToString("N") + ".png");
            try
            {
                var camera = root.AddComponent<Camera>();
                camera.enabled = false;
                camera.clearFlags = CameraClearFlags.SolidColor;
                camera.backgroundColor = Color.black;
                camera.cullingMask = 0;
                var capture = typeof(MainCameraScreenshotCapture).GetMethod("CaptureCamera",
                    BindingFlags.NonPublic | BindingFlags.Static);
                for (var step = 0; step < 3; step++)
                {
                    if (step == 1)
                    {
                        window = ScriptableObject.CreateInstance<CameraCompositionWindow>();
                        typeof(CameraCompositionWindow).GetField("_targetCamera",
                            BindingFlags.Instance | BindingFlags.NonPublic).SetValue(window, camera);
                    }
                    if (step == 2)
                    {
                        Object.DestroyImmediate(window);
                        window = null;
                    }
                    capture.Invoke(null, new object[] { camera, 128, 128, path });
                    var texture = new Texture2D(2, 2);
                    textures.Add(texture);
                    ImageConversion.LoadImage(texture, System.IO.File.ReadAllBytes(path));
                }
                var baseline = textures[0].GetPixels32();
                var overlay = textures[1].GetPixels32();
                var closed = textures[2].GetPixels32();
                var changed = 0;
                for (var index = 0; index < baseline.Length; index++)
                {
                    if (!baseline[index].Equals(overlay[index])) changed++;
                    Assert.That(closed[index], Is.EqualTo(baseline[index]), "Overlay remained after closing");
                }
                Assert.That(changed, Is.GreaterThan(0), "Overlay did not reach the camera output");
            }
            finally
            {
                if (window != null) Object.DestroyImmediate(window);
                foreach (var texture in textures) Object.DestroyImmediate(texture);
                Object.DestroyImmediate(root);
                UnityEditor.SceneManagement.EditorSceneManager.ClosePreviewScene(scene);
                if (System.IO.File.Exists(path)) System.IO.File.Delete(path);
            }
        }
    }
}
