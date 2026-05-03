using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEngine;

// Originally from VRCDeveloperTool by gatosyocora (MIT License)
// https://github.com/gatosyocora/VRCDeveloperTool
// Refactored for YAMO Unity Tools.

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// 여러 BlendShape(셰이프키)의 정점 변위를 합산하여 하나의 새로운 셰이프키로 합성한
    /// 메시(*_custom.asset)를 생성하는 에디터 윈도우.
    /// </summary>
    public class ShapeKeyMixer : EditorWindow
    {
        private SkinnedMeshRenderer renderer;

        private List<string> shapeKeyNames;
        private bool[] selectedShapeKeys;
        private bool isOpenedBlendShape = true;
        private string combinedShapeKeyName = "";
        private bool deleteOriginShapeKey = true;
        private Vector2 shapeKeyScrollPos = Vector2.zero;

        [MenuItem("Tools/YAMO/Mesh/ShapeKey Mixer")]
        private static void Open()
        {
            GetWindow<ShapeKeyMixer>("ShapeKey Mixer");
        }

        private void OnEnable()
        {
            renderer = null;
            shapeKeyNames = null;
            selectedShapeKeys = null;
            isOpenedBlendShape = true;
            combinedShapeKeyName = "";
            shapeKeyScrollPos = Vector2.zero;
        }

        private void OnGUI()
        {
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                renderer = EditorGUILayout.ObjectField(
                                "SkinnedMeshRenderer",
                                renderer,
                                typeof(SkinnedMeshRenderer),
                                true
                            ) as SkinnedMeshRenderer;

                if (check.changed)
                {
                    if (renderer != null)
                    {
                        shapeKeyNames = GetBlendShapeListFromRenderer(renderer);
                        selectedShapeKeys = new bool[shapeKeyNames.Count];
                    }
                }
            }

            if (shapeKeyNames != null)
            {
                isOpenedBlendShape = EditorGUILayout.Foldout(isOpenedBlendShape, "Shape Keys");
                if (isOpenedBlendShape)
                {
                    using (new EditorGUI.IndentLevelScope())
                    using (var scroll = new EditorGUILayout.ScrollViewScope(shapeKeyScrollPos, GUI.skin.box))
                    {
                        shapeKeyScrollPos = scroll.scrollPosition;
                        for (int i = 0; i < shapeKeyNames.Count; i++)
                        {
                            selectedShapeKeys[i] = EditorGUILayout.ToggleLeft(shapeKeyNames[i], selectedShapeKeys[i]);
                        }
                    }
                }
                deleteOriginShapeKey = EditorGUILayout.Toggle("Delete Origin ShapeKey", deleteOriginShapeKey);
                combinedShapeKeyName = EditorGUILayout.TextField("Mixed ShapeKey Name", combinedShapeKeyName);
            }

            using (new EditorGUI.DisabledScope(renderer == null || combinedShapeKeyName == "" || (selectedShapeKeys != null && selectedShapeKeys.Sum(x => x ? 1 : 0) <= 1)))
            {
                if (GUILayout.Button("Mix ShapeKeys"))
                {
                    if (selectedShapeKeys.Sum(x => x ? 1 : 0) > 1)
                    {
                        var selectedBlendShapeIndexs = selectedShapeKeys
                            .Select((isSelect, index) => new { Index = index, Value = isSelect })
                            .Where(x => x.Value)
                            .Select(x => x.Index)
                            .ToArray();

                        MixShapeKey(renderer, selectedBlendShapeIndexs, combinedShapeKeyName, deleteOriginShapeKey);
                    }

                    shapeKeyNames = GetBlendShapeListFromRenderer(renderer);
                    selectedShapeKeys = new bool[shapeKeyNames.Count];
                }
            }
        }

        private bool MixShapeKey(SkinnedMeshRenderer renderer, int[] selectedShapeKeyIndexs, string combinedBlendShapeName, bool deleteOriginShapeKey)
        {
            var mesh = renderer.sharedMesh;
            if (mesh == null) return false;

            var meshCustom = Instantiate(mesh);
            meshCustom.ClearBlendShapes();

            int frameIndex = 0;

            var combinedDeltaVertices = new Vector3[mesh.vertexCount];
            var combinedDeltaNormals = new Vector3[mesh.vertexCount];
            var combinedDeltaTangents = new Vector3[mesh.vertexCount];
            float combinedWeight = 0;

            for (int blendShapeIndex = 0; blendShapeIndex < mesh.blendShapeCount; blendShapeIndex++)
            {
                var deltaVertices = new Vector3[mesh.vertexCount];
                var deltaNormals = new Vector3[mesh.vertexCount];
                var deltaTangents = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(blendShapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);
                float weight = mesh.GetBlendShapeFrameWeight(blendShapeIndex, frameIndex);

                if (selectedShapeKeyIndexs.Contains(blendShapeIndex))
                {
                    for (int i = 0; i < mesh.vertexCount; i++)
                    {
                        combinedDeltaVertices[i] += deltaVertices[i];
                        combinedDeltaNormals[i] += deltaNormals[i];
                        combinedDeltaTangents[i] += deltaTangents[i];
                        combinedWeight = Mathf.Max(combinedWeight, weight);
                    }

                    if (!deleteOriginShapeKey)
                    {
                        string shapeKeyName = mesh.GetBlendShapeName(blendShapeIndex);
                        meshCustom.AddBlendShapeFrame(shapeKeyName, weight, deltaVertices, deltaNormals, deltaTangents);
                    }
                }
                else
                {
                    string shapeKeyName = mesh.GetBlendShapeName(blendShapeIndex);
                    meshCustom.AddBlendShapeFrame(shapeKeyName, weight, deltaVertices, deltaNormals, deltaTangents);
                }
            }

            if (selectedShapeKeyIndexs.Length > 0)
                meshCustom.AddBlendShapeFrame(combinedBlendShapeName, combinedWeight, combinedDeltaVertices, combinedDeltaNormals, combinedDeltaTangents);

            Undo.RecordObject(renderer, "Renderer " + renderer.name);
            renderer.sharedMesh = meshCustom;

            var path = Path.GetDirectoryName(AssetDatabase.GetAssetPath(mesh)) + "/" + mesh.name + "_custom.asset";
            AssetDatabase.CreateAsset(meshCustom, AssetDatabase.GenerateUniqueAssetPath(path));
            AssetDatabase.SaveAssets();

            return true;
        }

        private List<string> GetBlendShapeListFromRenderer(SkinnedMeshRenderer renderer)
        {
            var names = new List<string>();
            var mesh = renderer.sharedMesh;

            if (mesh != null)
                for (int i = 0; i < mesh.blendShapeCount; i++)
                    names.Add(mesh.GetBlendShapeName(i));

            return names;
        }
    }
}
