using System.Linq;
using NUnit.Framework;
using UnityEngine;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor.Tests
{
    public class FbxMaterialSlotCheckerTests
    {
        private GameObject root;
        private SkinnedMeshRenderer renderer;
        private Mesh mesh;
        private Material first;
        private Material second;

        [SetUp]
        public void SetUp()
        {
            root = new GameObject("Avatar");
            var child = new GameObject("HairFront");
            child.transform.SetParent(root.transform, false);
            renderer = child.AddComponent<SkinnedMeshRenderer>();
            mesh = new Mesh { name = "HairFront" };
            mesh.vertices = new[] { Vector3.zero, Vector3.right, Vector3.up };
            mesh.triangles = new[] { 0, 1, 2 };
            renderer.sharedMesh = mesh;
            var shader = Shader.Find("Hidden/InternalErrorShader");
            first = new Material(shader) { name = "Hair" };
            second = new Material(shader) { name = "HairLayer" };
            renderer.sharedMaterials = new[] { first };
        }

        [TearDown]
        public void TearDown()
        {
            Object.DestroyImmediate(root);
            Object.DestroyImmediate(mesh);
            Object.DestroyImmediate(first);
            Object.DestroyImmediate(second);
        }

        [Test]
        public void MatchingSlotsPassWithoutChangingSource()
        {
            var report = FbxMaterialSlotChecker.Scan(root);
            Assert.That(report.RendererCount, Is.EqualTo(1));
            Assert.That(report.Issues, Is.Empty);
            Assert.That(renderer.sharedMesh, Is.SameAs(mesh));
            Assert.That(renderer.sharedMaterials, Is.EqualTo(new[] { first }));
        }

        [Test]
        public void HairFrontExtraLayerIsFoundOnInactiveChildAndOffersBothSolutions()
        {
            renderer.sharedMaterials = new[] { first, second };
            renderer.gameObject.SetActive(false);
            var report = FbxMaterialSlotChecker.Scan(root);
            var issue = report.Issues.Single();
            Assert.That(report.HasErrors, Is.True);
            Assert.That(issue.Kind, Is.EqualTo(FbxMaterialSlotChecker.IssueKind.SlotCountMismatch));
            Assert.That(issue.Path, Is.EqualTo("Avatar/HairFront"));
            Assert.That(issue.Renderer, Is.SameAs(renderer));
            Assert.That(issue.MaterialCount, Is.EqualTo(2));
            Assert.That(issue.SubMeshCount, Is.EqualTo(1));
            Assert.That(issue.Solution, Does.Contain("초과 슬롯을 제거").And.Contain("추가 서브메시로 복제"));
            Assert.That(renderer.gameObject.activeSelf, Is.False);
            Assert.That(renderer.sharedMaterials, Is.EqualTo(new[] { first, second }));
            Assert.That(mesh.subMeshCount, Is.EqualTo(1));
        }

        [Test]
        public void MissingMaterialSlotsBlockBake()
        {
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 1);
            var report = FbxMaterialSlotChecker.Scan(root);
            Assert.That(report.HasErrors, Is.True);
            Assert.That(report.Issues.Single().MaterialCount, Is.EqualTo(1));
            Assert.That(report.Issues.Single().SubMeshCount, Is.EqualTo(2));
        }

        [Test]
        public void StaticMeshRendererIsAlsoChecked()
        {
            Object.DestroyImmediate(renderer);
            var filter = root.AddComponent<MeshFilter>();
            filter.sharedMesh = mesh;
            var staticRenderer = root.AddComponent<MeshRenderer>();
            staticRenderer.sharedMaterials = new[] { first, second };
            var report = FbxMaterialSlotChecker.Scan(root);
            Assert.That(report.HasErrors, Is.True);
            Assert.That(report.Issues.Single().Renderer, Is.SameAs(staticRenderer));
            Assert.That(report.Issues.Single().Path, Is.EqualTo("Avatar"));
        }

        [Test]
        public void EmptySubMeshIsWarningEvenWhenSlotCountsMatch()
        {
            mesh.subMeshCount = 2;
            mesh.SetTriangles(new int[0], 1);
            renderer.sharedMaterials = new[] { first, second };
            var report = FbxMaterialSlotChecker.Scan(root);
            Assert.That(report.HasErrors, Is.False);
            Assert.That(report.Issues.Single().Kind, Is.EqualTo(FbxMaterialSlotChecker.IssueKind.EmptySubMesh));
        }

        [Test]
        public void NullAndRepeatedMaterialsAreWarnings()
        {
            mesh.subMeshCount = 3;
            mesh.SetTriangles(new[] { 0, 1, 2 }, 1);
            mesh.SetTriangles(new[] { 0, 1, 2 }, 2);
            renderer.sharedMaterials = new[] { first, first, null };
            var report = FbxMaterialSlotChecker.Scan(root);
            Assert.That(report.HasErrors, Is.False);
            Assert.That(report.Issues.Select(i => i.Kind), Is.EquivalentTo(new[]
            {
                FbxMaterialSlotChecker.IssueKind.RepeatedMaterial,
                FbxMaterialSlotChecker.IssueKind.NullMaterial,
            }));
        }

        [Test]
        public void ScanDoesNotNeedReadableMeshData()
        {
            mesh.UploadMeshData(true);
            Assert.That(mesh.isReadable, Is.False);
            Assert.That(FbxMaterialSlotChecker.Scan(root).Issues, Is.Empty);
            Assert.That(mesh.isReadable, Is.False);
        }

        [Test]
        public void MissingMeshIsReportedAndNonMeshRenderersAreIgnored()
        {
            renderer.sharedMesh = null;
            root.AddComponent<LineRenderer>();
            var report = FbxMaterialSlotChecker.Scan(root);
            Assert.That(report.RendererCount, Is.EqualTo(1));
            Assert.That(report.Issues.Single().Kind, Is.EqualTo(FbxMaterialSlotChecker.IssueKind.MissingMesh));
            Assert.That(report.HasErrors, Is.False);
            Assert.That(FbxMaterialSlotChecker.Scan(null).RendererCount, Is.Zero);
        }
    }
}