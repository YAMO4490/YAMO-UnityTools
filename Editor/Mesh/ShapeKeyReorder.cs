using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditorInternal;
using UnityEngine;

// Originally from VRCDeveloperTool by gatosyocora (MIT License)
// https://github.com/gatosyocora/VRCDeveloperTool
// Refactored for YAMO Unity Tools.

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// SkinnedMeshRenderer 메시의 BlendShape(셰이프키) 순서를 드래그로 재정렬하고
    /// 정렬된 새 메시(*_reorderd.asset)를 생성하는 에디터 윈도우.
    /// 자동 정렬: UnSort / VRChat Default / A-Z / Z-A.
    /// </summary>
    public class ShapeKeyReorder : EditorWindow
    {
        private ReorderableList blendShapeReorderableList;
        private SkinnedMeshRenderer renderer;
        private Vector2 scrollPos = Vector2.zero;

        public class BlendShape
        {
            public int index;
            public string name;

            public BlendShape(int index, string name)
            {
                this.index = index;
                this.name = name;
            }
        }

        [MenuItem("Tools/YAMO/Mesh/ShapeKey Reorder")]
        private static void Open()
        {
            GetWindow<ShapeKeyReorder>("ShapeKey Reorder");
        }

        private void OnEnable()
        {
            renderer = null;
            blendShapeReorderableList = null;
        }

        private void OnGUI()
        {
            using (var check = new EditorGUI.ChangeCheckScope())
            {
                renderer = EditorGUILayout.ObjectField(
                                "Renderer",
                                renderer,
                                typeof(SkinnedMeshRenderer),
                                true
                           ) as SkinnedMeshRenderer;

                if (check.changed)
                {
                    if (renderer != null)
                    {
                        var blendShapePairList = new List<BlendShape>();
                        var mesh = renderer.sharedMesh;
                        if (mesh != null)
                        {
                            int blendShapeCount = mesh.blendShapeCount;
                            for (int i = 0; i < blendShapeCount; i++)
                            {
                                blendShapePairList.Add(new BlendShape(i, mesh.GetBlendShapeName(i)));
                            }
                        }
                        blendShapeReorderableList = InitializeReorderableList(blendShapePairList);
                    }
                    else
                    {
                        blendShapeReorderableList = null;
                    }
                }
            }

            if (blendShapeReorderableList != null)
            {
                using (var scroll = new EditorGUILayout.ScrollViewScope(scrollPos))
                {
                    scrollPos = scroll.scrollPosition;
                    blendShapeReorderableList.DoLayoutList();
                }
            }

            using (new EditorGUI.DisabledGroupScope(renderer == null))
            {
                EditorGUILayout.LabelField("Auto Sort");
                using (new EditorGUILayout.HorizontalScope())
                {
                    if (GUILayout.Button("UnSort"))
                    {
                        blendShapeReorderableList.list = blendShapeReorderableList.list
                            .Cast<BlendShape>()
                            .OrderBy(x => x.index)
                            .ToList();
                    }

                    if (GUILayout.Button("VRChat Default"))
                    {
                        blendShapeReorderableList.list = SortByVRChatDefault(
                            blendShapeReorderableList.list as List<BlendShape>);
                    }

                    if (GUILayout.Button("A-Z"))
                    {
                        blendShapeReorderableList.list = blendShapeReorderableList.list
                            .Cast<BlendShape>()
                            .OrderBy(x => x.name)
                            .ToList();
                    }

                    if (GUILayout.Button("Z-A"))
                    {
                        blendShapeReorderableList.list = blendShapeReorderableList.list
                            .Cast<BlendShape>()
                            .OrderByDescending(x => x.name)
                            .ToList();
                    }
                }

                EditorGUILayout.Space();

                if (GUILayout.Button("Change ShapeKey order"))
                {
                    CreateNewShapeKeyNameMesh(renderer, blendShapeReorderableList.list as List<BlendShape>);
                }
            }
        }

        private bool CreateNewShapeKeyNameMesh(SkinnedMeshRenderer renderer, List<BlendShape> reorderdBlendShapeList)
        {
            var mesh = renderer.sharedMesh;
            if (mesh == null) return false;
            if (reorderdBlendShapeList.Count != mesh.blendShapeCount) return false;

            var meshCustom = Object.Instantiate(mesh);
            meshCustom.ClearBlendShapes();

            int frameIndex = 0;
            for (int i = 0; i < reorderdBlendShapeList.Count; i++)
            {
                var deltaVertices = new Vector3[mesh.vertexCount];
                var deltaNormals = new Vector3[mesh.vertexCount];
                var deltaTangents = new Vector3[mesh.vertexCount];

                int blendShapeIndex = reorderdBlendShapeList[i].index;

                mesh.GetBlendShapeFrameVertices(blendShapeIndex, frameIndex, deltaVertices, deltaNormals, deltaTangents);
                float weight = mesh.GetBlendShapeFrameWeight(blendShapeIndex, frameIndex);
                string shapeKeyName = reorderdBlendShapeList[i].name;

                meshCustom.AddBlendShapeFrame(shapeKeyName, weight, deltaVertices, deltaNormals, deltaTangents);
            }

            Undo.RecordObject(renderer, "Renderer " + renderer.name);
            renderer.sharedMesh = meshCustom;

            var path = Path.GetDirectoryName(AssetDatabase.GetAssetPath(mesh)) + "/" + mesh.name + "_reorderd.asset";
            AssetDatabase.CreateAsset(meshCustom, AssetDatabase.GenerateUniqueAssetPath(path));
            AssetDatabase.SaveAssets();

            return true;
        }

        // VRChat 표준 셰이프키(blink, lowerlid)를 앞쪽으로 끌어오는 정렬
        private List<BlendShape> SortByVRChatDefault(List<BlendShape> list)
        {
            var vrcBlendShapes = new[]
            {
                "vrc.blink_left",
                "vrc.blink_right",
                "vrc.lowerlid_left",
                "vrc.lowerlid_right",
            };

            var newList = new List<BlendShape>();
            for (int i = 0; i < vrcBlendShapes.Length; i++)
            {
                int index = list.Select(x => x.name).ToList().IndexOf(vrcBlendShapes[i]);
                if (index == -1) continue;

                var blendShape = list[index];
                list.RemoveAt(index);
                newList.Add(blendShape);
            }

            newList.AddRange(list);
            return newList;
        }

        private ReorderableList InitializeReorderableList(List<BlendShape> list)
        {
            var reorderableList = new ReorderableList(list, typeof(BlendShape));
            reorderableList.drawHeaderCallback = rect => EditorGUI.LabelField(rect, "BlendShape");
            reorderableList.drawElementCallback = (rect, index, isActive, isFocused) =>
            {
                var item = reorderableList.list[index] as BlendShape;
                rect.height = EditorGUIUtility.singleLineHeight;
                EditorGUI.LabelField(rect, item.name);
            };
            reorderableList.displayAdd = false;
            reorderableList.displayRemove = false;
            return reorderableList;
        }
    }
}
