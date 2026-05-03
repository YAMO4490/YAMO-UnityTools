using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

// Originally from VRCDeveloperTool by gatosyocora (MIT License)
// https://github.com/gatosyocora/VRCDeveloperTool
// Refactored for YAMO Unity Tools.

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// SkinnedMeshRenderer 메시에서 선택한 SubMesh(머티리얼 슬롯) 와 그에 속한 정점·BlendShape 데이터를
    /// 일괄 제거한 새 메시(*_deleteSubmesh.asset) 를 생성하고, 머티리얼 참조도 함께 정리하는 에디터 윈도우.
    /// </summary>
    public class SubMeshDeleter : EditorWindow
    {
        private SkinnedMeshRenderer renderer;
        private List<SubMeshInfo> subMeshList;
        private int triangleCount = 0;

        private string saveFolder = "Assets/";
        private bool isOpenedSubMesh = true;
        private Vector2 subMeshScrollPos = Vector2.zero;

        [MenuItem("Tools/YAMO/Mesh/SubMesh Deleter")]
        private static void Open()
        {
            GetWindow<SubMeshDeleter>("SubMesh Deleter");
        }

        private void OnEnable()
        {
            renderer = null;
            subMeshList = null;
            triangleCount = 0;
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
                        var mesh = renderer.sharedMesh;
                        if (mesh != null)
                        {
                            subMeshList = GetSubMeshList(mesh);
                            triangleCount = GetMeshTriangleCount(mesh);
                            saveFolder = GetMeshPath(mesh);
                        }
                    }
                    else
                    {
                        subMeshList = null;
                    }
                }
            }

            if (subMeshList != null)
            {
                isOpenedSubMesh = EditorGUILayout.Foldout(isOpenedSubMesh, "SubMesh");
                if (isOpenedSubMesh)
                {
                    using (var scroll = new EditorGUILayout.ScrollViewScope(subMeshScrollPos))
                    {
                        subMeshScrollPos = scroll.scrollPosition;
                        for (int i = 0; i < subMeshList.Count; i++)
                        {
                            var matName = (renderer != null && i < renderer.sharedMaterials.Length && renderer.sharedMaterials[i] != null)
                                ? renderer.sharedMaterials[i].name
                                : "(none)";
                            subMeshList[i].selected = EditorGUILayout.ToggleLeft(
                                "subMesh " + (i + 1) + "(" + matName + "):" + subMeshList[i].triangleCount,
                                subMeshList[i].selected);
                        }
                    }
                }
            }

            EditorGUILayout.LabelField("Triangle Count", triangleCount + "");

            using (new EditorGUILayout.HorizontalScope())
            {
                EditorGUILayout.LabelField("Mesh SaveFolder", saveFolder);

                if (GUILayout.Button("Select Folder", GUILayout.Width(100)))
                {
                    saveFolder = EditorUtility.OpenFolderPanel("Select saved folder", saveFolder, "");
                    var match = Regex.Match(saveFolder, @"Assets/.*");
                    saveFolder = match.Value + "/";
                    if (saveFolder == "/") saveFolder = "Assets/";
                }
            }

            using (new EditorGUI.DisabledGroupScope(subMeshList == null || subMeshList.Count <= 1))
            {
                if (GUILayout.Button("Delete SubMesh"))
                {
                    DeleteSelectedSubMesh(renderer, subMeshList);

                    var mesh = renderer.sharedMesh;
                    if (mesh != null)
                    {
                        subMeshList = GetSubMeshList(mesh);
                        triangleCount = GetMeshTriangleCount(mesh);
                    }
                }
            }
        }

        private bool DeleteSelectedSubMesh(SkinnedMeshRenderer renderer, List<SubMeshInfo> subMeshList)
        {
            // 삭제할 정점 인덱스 (중복 제거, 내림차순) — 뒤에서부터 RemoveAt 하기 위함
            var deleteVerticesIndicesUniqueDescending = subMeshList
                .Where(x => x.selected)
                .SelectMany(x => x.verticesIndices)
                .Distinct()
                .OrderByDescending(x => x)
                .ToList()
                .AsReadOnly();

            // 삭제할 SubMesh 인덱스
            var deleteSubMeshIndexList = subMeshList
                .Select((value, index) => new { Value = value, Index = index })
                .Where(x => x.Value.selected)
                .Select(x => x.Index)
                .ToList()
                .AsReadOnly();

            var mesh = renderer.sharedMesh;
            var meshCustom = Instantiate(mesh);
            meshCustom.Clear();

            // 정점/속성 배열에서 해당 인덱스 제거
            var vertices = mesh.vertices.ToList();
            var boneWeights = mesh.boneWeights.ToList();
            var uvs = mesh.uv.ToList();
            var normals = mesh.normals.ToList();
            var tangents = mesh.tangents.ToList();
            var uv2s = mesh.uv2.ToList();
            var uv3s = mesh.uv3.ToList();
            var uv4s = mesh.uv4.ToList();
            foreach (var deleteVertexIndex in deleteVerticesIndicesUniqueDescending)
            {
                vertices.RemoveAt(deleteVertexIndex);
                boneWeights.RemoveAt(deleteVertexIndex);
                normals.RemoveAt(deleteVertexIndex);
                tangents.RemoveAt(deleteVertexIndex);
                if (deleteVertexIndex < uvs.Count) uvs.RemoveAt(deleteVertexIndex);
                if (deleteVertexIndex < uv2s.Count) uv2s.RemoveAt(deleteVertexIndex);
                if (deleteVertexIndex < uv3s.Count) uv3s.RemoveAt(deleteVertexIndex);
                if (deleteVertexIndex < uv4s.Count) uv4s.RemoveAt(deleteVertexIndex);
            }
            meshCustom.SetVertices(vertices);
            meshCustom.boneWeights = boneWeights.ToArray();
            meshCustom.normals = normals.ToArray();
            meshCustom.tangents = tangents.ToArray();
            meshCustom.SetUVs(0, uvs);
            meshCustom.SetUVs(1, uv2s);
            meshCustom.SetUVs(2, uv3s);
            meshCustom.SetUVs(3, uv4s);

            // SubMesh 별 폴리곤 처리: 삭제된 정점 인덱스만큼 트라이앵글 인덱스를 당겨준다
            meshCustom.subMeshCount = mesh.subMeshCount - deleteSubMeshIndexList.Count;
            int subMeshNumber = 0;
            for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
            {
                if (deleteSubMeshIndexList.Contains(subMeshIndex)) continue;

                var subMeshTriangles = mesh.GetTriangles(subMeshIndex);
                foreach (var deleteVerticesIndex in deleteVerticesIndicesUniqueDescending)
                    for (int i = 0; i < subMeshTriangles.Length; i++)
                        if (subMeshTriangles[i] > deleteVerticesIndex)
                            subMeshTriangles[i]--;
                meshCustom.SetTriangles(subMeshTriangles, subMeshNumber++);
            }

            // BlendShape 도 동일하게 정점 인덱스 제거하여 재구성
            var deltaVertices = new Vector3[mesh.vertexCount];
            var deltaNormals = new Vector3[mesh.vertexCount];
            var deltaTangents = new Vector3[mesh.vertexCount];
            for (int blendshapeIndex = 0; blendshapeIndex < mesh.blendShapeCount; blendshapeIndex++)
            {
                string blendShapeName = mesh.GetBlendShapeName(blendshapeIndex);
                float frameWeight = mesh.GetBlendShapeFrameWeight(blendshapeIndex, 0);
                mesh.GetBlendShapeFrameVertices(blendshapeIndex, 0, deltaVertices, deltaNormals, deltaTangents);
                var deltaVerticesList = deltaVertices.ToList();
                var deltaNormalsList = deltaNormals.ToList();
                var deltaTangentsList = deltaTangents.ToList();
                foreach (var deleteVertexIndex in deleteVerticesIndicesUniqueDescending)
                {
                    deltaVerticesList.RemoveAt(deleteVertexIndex);
                    deltaNormalsList.RemoveAt(deleteVertexIndex);
                    deltaTangentsList.RemoveAt(deleteVertexIndex);
                }
                meshCustom.AddBlendShapeFrame(blendShapeName, frameWeight,
                    deltaVerticesList.ToArray(),
                    deltaNormalsList.ToArray(),
                    deltaTangentsList.ToArray());
            }

            AssetDatabase.CreateAsset(meshCustom, AssetDatabase.GenerateUniqueAssetPath(saveFolder + mesh.name + "_deleteSubmesh.asset"));
            AssetDatabase.SaveAssets();

            Undo.RecordObject(renderer, "Change mesh " + meshCustom.name);
            renderer.sharedMesh = meshCustom;

            // 삭제된 SubMesh 의 머티리얼 슬롯도 제거
            var materials = renderer.sharedMaterials.ToList();
            for (int index = materials.Count - 1; index >= 0; index--)
                if (deleteSubMeshIndexList.Contains(index))
                    materials.RemoveAt(index);
            renderer.sharedMaterials = materials.ToArray();

            return true;
        }

        private List<SubMeshInfo> GetSubMeshList(Mesh mesh)
        {
            var list = new List<SubMeshInfo>();
            for (int subMeshIndex = 0; subMeshIndex < mesh.subMeshCount; subMeshIndex++)
            {
                list.Add(new SubMeshInfo(mesh, subMeshIndex));
            }
            return list;
        }

        private int GetMeshTriangleCount(Mesh mesh)
        {
            return mesh.triangles.Length / 3;
        }

        private string GetMeshPath(Mesh mesh)
        {
            return Path.GetDirectoryName(AssetDatabase.GetAssetPath(mesh)) + "/";
        }

        public class SubMeshInfo
        {
            public int subMeshIndex;
            public int[] verticesIndices;
            public int vertexCount;
            public int triangleCount;
            public bool selected = false;

            public SubMeshInfo(Mesh mesh, int subMeshIndex)
            {
                this.subMeshIndex = subMeshIndex;
                this.verticesIndices = mesh.GetIndices(subMeshIndex);
                vertexCount = verticesIndices.Length;
                triangleCount = mesh.GetTriangles(subMeshIndex).Length / 3;
            }
        }
    }
}
