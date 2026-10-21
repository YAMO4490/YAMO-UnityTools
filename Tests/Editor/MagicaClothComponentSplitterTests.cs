using System;
using System.Linq;
using NUnit.Framework;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor.Tests
{
    public class MagicaClothComponentSplitterTests
    {
        private Scene scene;
        private Type clothType;
        private GameObject root;

        [SetUp]
        public void SetUp()
        {
            clothType = AppDomain.CurrentDomain.GetAssemblies()
                .Select(a => a.GetType("MagicaCloth2.MagicaCloth", false)).FirstOrDefault(t => t != null);
            if (clothType == null) Assert.Ignore("MagicaCloth2 is optional.");
            scene = EditorSceneManager.NewPreviewScene();
            root = Create("Avatar");
        }

        [TearDown]
        public void TearDown()
        {
            if (scene.IsValid()) EditorSceneManager.ClosePreviewScene(scene);
        }

        private GameObject Create(string name, Transform parent = null)
        {
            var go = new GameObject(name);
            SceneManager.MoveGameObjectToScene(go, scene);
            if (parent != null) go.transform.SetParent(parent, false);
            return go;
        }

        private void SetReference(Component component, string path, Object value)
        {
            var so = new SerializedObject(component);
            var property = so.FindProperty(path);
            Assert.That(property, Is.Not.Null, path);
            property.objectReferenceValue = value;
            so.ApplyModifiedPropertiesWithoutUndo();
        }

        [Test]
        public void SplitInactiveStacksPreservesTransformsReferencesAndSupportsUndoRedo()
        {
            var host = Create("Cloth", root.transform);
            host.transform.localPosition = new Vector3(1, 2, 3);
            host.transform.localRotation = Quaternion.Euler(15, 30, 45);
            host.transform.localScale = new Vector3(2, 3, 4);
            host.SetActive(false);
            var first = host.AddComponent(clothType);
            var second = host.AddComponent(clothType);
            var external = Create("External").AddComponent(clothType);
            SetReference(external, "serializeData.selfCollisionConstraint.syncPartner", second);
            Create("Cloth_02", root.transform); // Must not overwrite or reuse this object.

            var result = MagicaClothComponentSplitter.Split(root);
            Assert.That(result.CreatedObjects, Is.EqualTo(1));
            var splitHost = root.transform.Find("Cloth_03");
            Assert.That(splitHost, Is.Not.Null);
            Assert.That(splitHost.localPosition, Is.EqualTo(host.transform.localPosition));
            Assert.That(Quaternion.Angle(splitHost.localRotation, host.transform.localRotation), Is.LessThan(0.001f));
            Assert.That(splitHost.localScale, Is.EqualTo(host.transform.localScale));
            Assert.That(splitHost.gameObject.activeSelf, Is.False);
            Assert.That(host.GetComponents(clothType), Is.EqualTo(new[] { first }));
            Assert.That(new SerializedObject(external).FindProperty("serializeData.selfCollisionConstraint.syncPartner").objectReferenceValue,
                Is.EqualTo(splitHost.GetComponent(clothType)));
            Undo.FlushUndoRecordObjects();
            Undo.PerformUndo();
            Assert.That(host.GetComponents(clothType).Length, Is.EqualTo(2));
            Assert.That(root.transform.Find("Cloth_03"), Is.Null);
            Undo.PerformRedo();
            Assert.That(MagicaClothComponentSplitter.Scan(root).Stacks, Is.Empty);
            Assert.That(root.transform.Find("Cloth_03").GetComponents(clothType).Length, Is.EqualTo(1));
        }

        [Test]
        public void HostUsedAsRootBoneRemainsASimulationInput()
        {
            var host = Create("Bone", root.transform);
            host.AddComponent(clothType);
            var second = host.AddComponent(clothType);
            var so = new SerializedObject(second);
            var roots = so.FindProperty("serializeData.rootBones");
            roots.arraySize = 1;
            roots.GetArrayElementAtIndex(0).objectReferenceValue = host.transform;
            so.ApplyModifiedPropertiesWithoutUndo();
            MagicaClothComponentSplitter.Split(root);
            var copy = root.transform.Find("Bone_02").GetComponent(clothType);
            Assert.That(new SerializedObject(copy).FindProperty("serializeData.rootBones").GetArrayElementAtIndex(0).objectReferenceValue,
                Is.EqualTo(host.transform));
        }

        [Test]
        public void RootLevelStacksSplitIntoSameSceneAndSecondRunIsANoOp()
        {
            root.AddComponent(clothType);
            root.AddComponent(clothType);
            root.AddComponent(clothType);
            Assert.That(MagicaClothComponentSplitter.Split(root).CreatedObjects, Is.EqualTo(2));
            Assert.That(scene.GetRootGameObjects().Length, Is.EqualTo(3));
            Assert.That(scene.GetRootGameObjects().All(go => go.GetComponents(clothType).Length == 1), Is.True);
            Assert.That(MagicaClothComponentSplitter.Split(root).CreatedObjects, Is.Zero);
        }

        [Test]
        public void NullTargetIsRejected()
        {
            Assert.That(MagicaClothComponentSplitter.Scan(null).Error, Is.Not.Empty);
            Assert.Throws<InvalidOperationException>(() => MagicaClothComponentSplitter.Split(null));
        }
    }
}
