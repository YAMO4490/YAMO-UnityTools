// 이 파일은 UniGLTF 의 BoneNormalizer.cs 를 fork 한 버전입니다.
//   원본: Assets/External/UniGLTF/Runtime/MeshUtility/BoneNormalizer.cs
//
// 차이점:
//   - 회전 보존 옵션 추가. 원본은 모든 transform 의 회전을 identity 로 만들지만,
//     fork 는 NormalizeOptions 의 필터에 따라 선택적으로 회전을 보존합니다.
//     (스케일은 원본과 마찬가지로 항상 1 로 정규화됨)
//
// 회전 보존 시 동작:
//   - CopyAndBuild 에서 dst transform 의 world rotation 을 source 와 동일하게 설정
//   - NormalizeSkinnedMesh 에서 SMR transform 의 회전이 보존되면 mesh ApplyMatrix
//     를 identity 로(= no-op) 처리. 그렇지 않으면 원본처럼 SMR rotation 을 mesh 에 적용
//   - NormalizeNoneSkinnedMesh 도 동일 원리로 분기

using System;
using System.Collections.Generic;
using System.Linq;
using UniGLTF;                  // Transform.Traverse() extension
using UniGLTF.MeshUtility;      // Mesh.Copy(), ApplyMatrix(), ApplyRotationAndScale()
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// 회전 보존 정책. 모든 transform 에 대해 "이 transform 의 world rotation 을 보존할지"
    /// 를 결정하는 predicate 형태로 동작합니다.
    /// </summary>
    public class NormalizeOptions
    {
        /// <summary>
        /// 모든 transform 의 회전을 보존. true 면 RotationFilter 는 무시.
        /// </summary>
        public bool PreserveAllRotations = false;

        /// <summary>
        /// PreserveAllRotations = false 일 때, 이 함수가 true 를 반환하는 transform 만
        /// 회전 보존. null 이면 모두 zero (원본 BoneNormalizer 동작).
        /// </summary>
        public Func<Transform, bool> RotationFilter = null;

        /// <summary>
        /// 옵션 → predicate 으로 정규화.
        /// </summary>
        internal Func<Transform, bool> ResolvePredicate()
        {
            if (PreserveAllRotations) return _ => true;
            if (RotationFilter != null) return RotationFilter;
            return _ => false;
        }
    }

    public static class YamoBoneNormalizer
    {
        public delegate Avatar CreateAvatarFunc(GameObject original, GameObject normalized, Dictionary<Transform, Transform> boneMap);

        // ============================================================
        // Public entrypoint
        // ============================================================

        public static (GameObject normalized, Dictionary<Transform, Transform> boneMap) Execute(
            GameObject go, CreateAvatarFunc createAvatar, NormalizeOptions options = null)
        {
            options = options ?? new NormalizeOptions();
            var shouldPreserve = options.ResolvePredicate();

            // 1) 새 hierarchy 생성 (회전/스케일 정규화 적용)
            var (normalized, boneMap) = NormalizeHierarchy(go, createAvatar, shouldPreserve);

            // 2) 각 메시 처리: bind pose 재계산 + 필요 시 mesh 회전 보정
            foreach (var src in go.transform.Traverse())
            {
                if (!boneMap.TryGetValue(src, out var dst)) continue;

                NormalizeSkinnedMesh(src, dst, boneMap, shouldPreserve);
                NormalizeNoneSkinnedMesh(src, dst, shouldPreserve);
            }

            return (normalized, boneMap);
        }

        // ============================================================
        // Hierarchy build
        // ============================================================

        static (GameObject, Dictionary<Transform, Transform>) NormalizeHierarchy(
            GameObject go, CreateAvatarFunc createAvatar, Func<Transform, bool> shouldPreserve)
        {
            var boneMap = new Dictionary<Transform, Transform>();

            var normalized = new GameObject(go.name + "(normalized)");
            normalized.transform.position = go.transform.position;
            // 루트도 보존 대상이면 회전 복사. 그렇지 않으면 identity.
            if (shouldPreserve(go.transform))
            {
                normalized.transform.rotation = go.transform.rotation;
            }
            CopyAndBuild(go.transform, normalized.transform, boneMap, shouldPreserve);

            // 새 hierarchy 에 Avatar 부착
            {
                var animator = normalized.AddComponent<Animator>();
                var avatar = createAvatar(go, normalized, boneMap);
                avatar.name = go.name + ".normalized";
                animator.avatar = avatar;
            }

            return (normalized, boneMap);
        }

        /// <summary>
        /// 회전(옵션)과 스케일(항상 1)을 정규화한 hierarchy 복사.
        /// </summary>
        static void CopyAndBuild(Transform src, Transform dst,
            Dictionary<Transform, Transform> boneMap,
            Func<Transform, bool> shouldPreserve)
        {
            boneMap[src] = dst;

            foreach (Transform child in src)
            {
                if (child.gameObject.activeSelf)
                {
                    var dstChild = new GameObject(child.name);
                    dstChild.transform.SetParent(dst);
                    dstChild.transform.position = child.position;

                    // 회전 보존 옵션. 스케일은 (1,1,1) 그대로 유지.
                    if (shouldPreserve(child))
                    {
                        dstChild.transform.rotation = child.rotation;
                    }

                    CopyAndBuild(child, dstChild.transform, boneMap, shouldPreserve);
                }
            }
        }

        // ============================================================
        // Skinned mesh normalize (forked)
        // ============================================================

        class BlendShapeReport
        {
            string m_name;
            int m_count;
            struct BlendShapeStat
            {
                public int Index;
                public string Name;
                public int VertexCount;
                public int NormalCount;
                public int TangentCount;
                public override string ToString()
                    => string.Format("[{0}]{1}: {2}, {3}, {4}\n", Index, Name, VertexCount, NormalCount, TangentCount);
            }
            List<BlendShapeStat> m_stats = new List<BlendShapeStat>();
            public int Count => m_stats.Count;
            public BlendShapeReport(Mesh mesh) { m_name = mesh.name; m_count = mesh.vertexCount; }
            public void SetCount(int index, string name, int v, int n, int t)
            {
                m_stats.Add(new BlendShapeStat { Index = index, Name = name, VertexCount = v, NormalCount = n, TangentCount = t });
            }
            public override string ToString()
                => string.Format("NormalizeSkinnedMesh: {0}({1}verts)\n{2}",
                    m_name, m_count, string.Join("", m_stats.Select(x => x.ToString()).ToArray()));
        }

        static bool CopyOrDropWeight(int[] indexMap, int srcIndex, float weight, Action<int, float> setter)
        {
            if (srcIndex < 0 || srcIndex >= indexMap.Length) { setter(0, 0); return false; }
            var dstIndex = indexMap[srcIndex];
            if (dstIndex != -1) { setter(dstIndex, weight); return true; }
            setter(0, 0); return false;
        }

        public static BoneWeight[] MapBoneWeight(BoneWeight[] src,
            Dictionary<Transform, Transform> boneMap,
            Transform[] srcBones, Transform[] dstBones)
        {
            var indexMap = new int[srcBones.Length];
            for (int i = 0; i < srcBones.Length; ++i)
            {
                var srcBone = srcBones[i];
                if (srcBone == null)
                {
                    indexMap[i] = -1;
                    Debug.LogWarningFormat("bones[{0}] is null", i);
                }
                else if (boneMap.TryGetValue(srcBone, out Transform dstBone))
                {
                    var dstIndex = Array.IndexOf(dstBones, dstBone);
                    if (dstIndex == -1) throw new Exception();
                    indexMap[i] = dstIndex;
                }
                else
                {
                    indexMap[i] = -1;
                    Debug.LogWarningFormat("{0} is removed", srcBone.name);
                }
            }

            var newBoneWeights = new BoneWeight[src.Length];
            for (int i = 0; i < src.Length; ++i)
            {
                BoneWeight bw = src[i];
                CopyOrDropWeight(indexMap, bw.boneIndex0, bw.weight0, (ni, nw) => { newBoneWeights[i].boneIndex0 = ni; newBoneWeights[i].weight0 = nw; });
                CopyOrDropWeight(indexMap, bw.boneIndex1, bw.weight1, (ni, nw) => { newBoneWeights[i].boneIndex1 = ni; newBoneWeights[i].weight1 = nw; });
                CopyOrDropWeight(indexMap, bw.boneIndex2, bw.weight2, (ni, nw) => { newBoneWeights[i].boneIndex2 = ni; newBoneWeights[i].weight2 = nw; });
                CopyOrDropWeight(indexMap, bw.boneIndex3, bw.weight3, (ni, nw) => { newBoneWeights[i].boneIndex3 = ni; newBoneWeights[i].weight3 = nw; });
            }
            return newBoneWeights;
        }

        static void NormalizeSkinnedMesh(Transform src, Transform dst,
            Dictionary<Transform, Transform> boneMap,
            Func<Transform, bool> shouldPreserve)
        {
            var srcRenderer = src.GetComponent<SkinnedMeshRenderer>();
            if (srcRenderer == null
                || !srcRenderer.enabled
                || srcRenderer.sharedMesh == null
                || srcRenderer.sharedMesh.vertexCount == 0)
            {
                return;
            }

            var srcMesh = srcRenderer.sharedMesh;
            var originalSrcMesh = srcMesh;

            var dstBones = srcRenderer.bones
                .Where(x => x != null && boneMap.ContainsKey(x))
                .Select(x => boneMap[x])
                .ToArray();

            var hasBoneWeight = srcRenderer.bones != null && srcRenderer.bones.Length > 0;
            if (!hasBoneWeight)
            {
                srcMesh = srcMesh.Copy(true);
                var bw = new BoneWeight { boneIndex0 = 0, boneIndex1 = 0, boneIndex2 = 0, boneIndex3 = 0,
                                          weight0 = 1.0f, weight1 = 0.0f, weight2 = 0.0f, weight3 = 0.0f };
                srcMesh.boneWeights = Enumerable.Range(0, srcMesh.vertexCount).Select(_ => bw).ToArray();
                srcMesh.bindposes = new Matrix4x4[] { Matrix4x4.identity };

                srcRenderer.rootBone = srcRenderer.transform;
                dstBones = new[] { boneMap[srcRenderer.transform] };
                srcRenderer.bones = new[] { srcRenderer.transform };
                srcRenderer.sharedMesh = srcMesh;
            }

            // BakeMesh — 현재 본 포즈를 vertex 에 굽기
            var mesh = srcMesh.Copy(false);
            mesh.name = srcMesh.name + ".baked";
            srcRenderer.BakeMesh(mesh);

            var blendShapeValues = new Dictionary<int, float>();
            for (int i = 0; i < srcMesh.blendShapeCount; i++)
            {
                var val = srcRenderer.GetBlendShapeWeight(i);
                if (val > 0) blendShapeValues.Add(i, val);
            }

            mesh.boneWeights = MapBoneWeight(srcMesh.boneWeights, boneMap, srcRenderer.bones, dstBones);

            // bindposes 재계산. dst bone 이 회전을 보존했든 아니든 동일 공식이 성립.
            mesh.bindposes = dstBones.Select(x => x.worldToLocalMatrix * dst.transform.localToWorldMatrix).ToArray();

            // ── 핵심 변경 ───────────────────────────────────────────────
            // SMR 의 회전이 보존되면 mesh 에 추가 회전 적용 X (identity).
            // 그렇지 않으면 원본 동작: SMR 의 world rotation 만큼 mesh 회전.
            bool smrRotPreserved = shouldPreserve(src);
            Matrix4x4 m;
            if (smrRotPreserved)
            {
                m = Matrix4x4.identity;
            }
            else
            {
                m = default;
                m.SetTRS(Vector3.zero, src.rotation, Vector3.one);
                mesh.ApplyMatrix(m);
            }
            // identity 일 땐 ApplyMatrix 호출 자체를 생략(no-op 이긴 하지만 안전).

            // ── BlendShapes ────────────────────────────────────────────
            var backcup = new List<float>();
            for (int i = 0; i < srcMesh.blendShapeCount; ++i)
            {
                backcup.Add(srcRenderer.GetBlendShapeWeight(i));
                srcRenderer.SetBlendShapeWeight(i, 0);
            }

            var meshVertices = mesh.vertices;
            var meshNormals = mesh.normals;
#if VRM_NORMALIZE_BLENDSHAPE_TANGENT
            var meshTangents = mesh.tangents.Select(x => (Vector3)x).ToArray();
#endif

            var originalBlendShapePositions = new Vector3[meshVertices.Length];
            var originalBlendShapeNormals   = new Vector3[meshVertices.Length];
            var originalBlendShapeTangents  = new Vector3[meshVertices.Length];

            var report = new BlendShapeReport(srcMesh);
            var blendShapeMesh = new Mesh();
            for (int i = 0; i < srcMesh.blendShapeCount; ++i)
            {
                srcRenderer.sharedMesh.GetBlendShapeFrameVertices(i, 0,
                    originalBlendShapePositions, originalBlendShapeNormals, originalBlendShapeTangents);
                var hasVertices = originalBlendShapePositions.Count(x => x != Vector3.zero);
                var hasNormals  = originalBlendShapeNormals.Count(x => x != Vector3.zero);
#if VRM_NORMALIZE_BLENDSHAPE_TANGENT
                var hasTangents = originalBlendShapeTangents.Count(x => x != Vector3.zero);
#else
                var hasTangents = 0;
#endif
                var name = srcMesh.GetBlendShapeName(i);
                if (string.IsNullOrEmpty(name)) name = string.Format("{0}", i);
                report.SetCount(i, name, hasVertices, hasNormals, hasTangents);

                srcRenderer.SetBlendShapeWeight(i, 100.0f);
                srcRenderer.BakeMesh(blendShapeMesh);
                if (blendShapeMesh.vertices.Length != mesh.vertices.Length) throw new Exception("different vertex count");

                var value = blendShapeValues.ContainsKey(i) ? blendShapeValues[i] : 0;
                srcRenderer.SetBlendShapeWeight(i, value);

                Vector3[] vertices = blendShapeMesh.vertices;
                for (int j = 0; j < vertices.Length; ++j)
                {
                    if (originalBlendShapePositions[j] == Vector3.zero) vertices[j] = Vector3.zero;
                    else vertices[j] = m.MultiplyPoint(vertices[j]) - meshVertices[j];
                    // m 이 identity 면 결과는 vertices[j] - meshVertices[j] (회전 보정 없음)
                }

                Vector3[] normals = blendShapeMesh.normals;
                for (int j = 0; j < normals.Length; ++j)
                {
                    if (originalBlendShapeNormals[j] == Vector3.zero) normals[j] = Vector3.zero;
                    else normals[j] = m.MultiplyVector(normals[j].normalized) - meshNormals[j];
                }

                Vector3[] tangents = blendShapeMesh.tangents.Select(x => (Vector3)x).ToArray();
#if VRM_NORMALIZE_BLENDSHAPE_TANGENT
                for (int j = 0; j < tangents.Length; ++j)
                {
                    if (originalBlendShapeTangents[j] == Vector3.zero) tangents[j] = Vector3.zero;
                    else tangents[j] = m.MultiplyVector(tangents[j]) - meshTangents[j];
                }
#endif

                var frameCount = srcMesh.GetBlendShapeFrameCount(i);
                for (int f = 0; f < frameCount; f++)
                {
                    var weight = srcMesh.GetBlendShapeFrameWeight(i, f);
                    try
                    {
                        mesh.AddBlendShapeFrame(name, weight, vertices,
                            hasNormals > 0 ? normals : null,
                            hasTangents > 0 ? tangents : null);
                    }
                    catch (Exception)
                    {
                        Debug.LogErrorFormat("fail to mesh.AddBlendShapeFrame {0}.{1}", mesh.name, srcMesh.GetBlendShapeName(i));
                        throw;
                    }
                }
            }

            if (report.Count > 0) Debug.LogFormat("{0}", report.ToString());

            var dstRenderer = dst.gameObject.AddComponent<SkinnedMeshRenderer>();
            dstRenderer.sharedMaterials = srcRenderer.sharedMaterials;
            if (srcRenderer.rootBone != null) dstRenderer.rootBone = boneMap[srcRenderer.rootBone];
            dstRenderer.bones = dstBones;
            dstRenderer.sharedMesh = mesh;

            if (!hasBoneWeight)
            {
                srcRenderer.bones = new Transform[] { };
                srcRenderer.sharedMesh = originalSrcMesh;
            }
            for (int i = 0; i < backcup.Count; ++i)
            {
                srcRenderer.SetBlendShapeWeight(i, backcup[i]);
            }
        }

        // ============================================================
        // Static (non-skinned) mesh normalize (forked)
        // ============================================================

        static void NormalizeNoneSkinnedMesh(Transform src, Transform dst,
            Func<Transform, bool> shouldPreserve)
        {
            var srcFilter = src.GetComponent<MeshFilter>();
            if (srcFilter == null || srcFilter.sharedMesh == null || srcFilter.sharedMesh.vertexCount == 0) return;

            var srcRenderer = src.GetComponent<MeshRenderer>();
            if (srcRenderer == null || !srcRenderer.enabled) return;

            var dstFilter = dst.gameObject.AddComponent<MeshFilter>();
            var dstMesh = srcFilter.sharedMesh.Copy(false);

            // 회전 보존이면 스케일만 적용. 그렇지 않으면 원본처럼 localToWorldMatrix(회전+스케일).
            if (shouldPreserve(src))
            {
                var scale = src.lossyScale;
                var scaleMatrix = Matrix4x4.Scale(scale);
                dstMesh.ApplyRotationAndScale(scaleMatrix);
            }
            else
            {
                dstMesh.ApplyRotationAndScale(src.localToWorldMatrix);
            }

            dstFilter.sharedMesh = dstMesh;

            var dstRenderer = dst.gameObject.AddComponent<MeshRenderer>();
            dstRenderer.sharedMaterials = srcRenderer.sharedMaterials;
        }
    }
}
