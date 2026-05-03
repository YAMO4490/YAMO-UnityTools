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
    /// SkinnedMeshRenderer 의 BlendShape(셰이프키) 중 선택한 항목을 삭제한
    /// 새 메시(*_shapekeydeleted.asset)를 생성·할당하는 에디터 윈도우.
    /// Shift+클릭으로 범위 선택 지원.
    /// </summary>
    public class ShapeKeyDeleter : EditorWindow
    {
        private SkinnedMeshRenderer renderer;

        private List<string> shapeKeyNames;
        private bool[] selectedShapeKeys;
        private bool isOpenedBlendShape = true;
        private Vector2 shapeKeyScrollPos = Vector2.zero;

        private int lastSelectedIndex = -1;

        [MenuItem("Tools/YAMO/Mesh/ShapeKey Deleter")]
        private static void Open()
        {
            GetWindow<ShapeKeyDeleter>("ShapeKey Deleter");
        }

        private void OnEnable()
        {
            renderer = null;
            shapeKeyNames = null;
            selectedShapeKeys = null;
            isOpenedBlendShape = true;
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

                        Event e = Event.current;

                        for (int i = 0; i < shapeKeyNames.Count; i++)
                        {
                            bool originalValue = selectedShapeKeys[i];
                            bool newValue = EditorGUILayout.ToggleLeft(shapeKeyNames[i], selectedShapeKeys[i]);

                            // Shift 클릭 범위 선택
                            if (e.shift && lastSelectedIndex != -1 && newValue != originalValue)
                            {
                                int startIndex = Mathf.Min(lastSelectedIndex, i);
                                int endIndex = Mathf.Max(lastSelectedIndex, i);
                                for (int j = startIndex; j <= endIndex; j++)
                                {
                                    selectedShapeKeys[j] = true;
                                }
                            }

                            if (newValue != originalValue)
                            {
                                lastSelectedIndex = i;
                            }

                            selectedShapeKeys[i] = newValue;
                        }
                    }
                }
            }

            using (new EditorGUI.DisabledScope(renderer == null || (selectedShapeKeys != null && selectedShapeKeys.All(x => !x))))
            {
                if (GUILayout.Button("Delete ShapeKeys"))
                {
                    var selectedBlendShapeIndexs = selectedShapeKeys
                        .Select((isSelect, index) => new { Index = index, Value = isSelect })
                        .Where(x => x.Value)
                        .Select(x => x.Index)
                        .ToArray();

                    DeleteShapeKey(renderer, selectedBlendShapeIndexs);

                    shapeKeyNames = GetBlendShapeListFromRenderer(renderer);
                    selectedShapeKeys = new bool[shapeKeyNames.Count];
                }
            }
        }

        private bool DeleteShapeKey(SkinnedMeshRenderer renderer, int[] selectedShapeKeyIndexs)
        {
            var mesh = renderer.sharedMesh;
            if (mesh == null) return false;

            var meshCustom = Instantiate(mesh);
            meshCustom.ClearBlendShapes();

            int frameIndex = 0;
            for (int blendShapeIndex = 0; blendShapeIndex < mesh.blendShapeCount; blendShapeIndex++)
            {
                var deltaVertices = new Vector3[mesh.vertexCount];
                var deltaNormals = new Vector3[mesh.vertexCount];
                var deltaTangents = new Vector3[mesh.vertexCount];
                mesh.GetBlendShapeFrameVertices(blendShapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);
                float weight = mesh.GetBlendShapeFrameWeight(blendShapeIndex, frameIndex);

                if (!selectedShapeKeyIndexs.Contains(blendShapeIndex))
                {
                    string shapeKeyName = mesh.GetBlendShapeName(blendShapeIndex);
                    meshCustom.AddBlendShapeFrame(shapeKeyName, weight, deltaVertices, deltaNormals, deltaTangents);
                }
            }

            Undo.RecordObject(renderer, "Renderer " + renderer.name);
            renderer.sharedMesh = meshCustom;

            var path = Path.GetDirectoryName(AssetDatabase.GetAssetPath(mesh)) + "/" + mesh.name + "_shapekeydeleted.asset";
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
