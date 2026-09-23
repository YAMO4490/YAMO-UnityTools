using System.Collections.Generic;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    /// <summary>Read-only FBX material-slot preflight, shared by Asset Checker and Avatar Bake.</summary>
    public static class FbxMaterialSlotChecker
    {
        public enum IssueKind { SlotCountMismatch, MissingMesh, EmptySubMesh, NullMaterial, RepeatedMaterial }

        public sealed class Issue
        {
            public Renderer Renderer;
            public Mesh Mesh;
            public string Path;
            public int MaterialCount;
            public int SubMeshCount;
            public IssueKind Kind;
            public bool BlocksBake;
            public string Reason;
            public string Solution;

            public string Summary => $"{Path}: Materials {MaterialCount} / SubMeshes {SubMeshCount}\n{Reason}\n해결: {Solution}";
        }

        public sealed class Report
        {
            public int RendererCount;
            public readonly List<Issue> Issues = new List<Issue>();
            public bool HasErrors => Issues.Exists(issue => issue.BlocksBake);
        }

        public static Report Scan(GameObject root)
        {
            var report = new Report();
            if (root == null) return report;
            foreach (var renderer in root.GetComponentsInChildren<Renderer>(true))
            {
                // Particle, line and trail renderers do not have this mesh/material contract.
                if (!(renderer is SkinnedMeshRenderer) && !(renderer is MeshRenderer)) continue;
                report.RendererCount++;
                var mesh = GetMesh(renderer);
                var materials = renderer.sharedMaterials;
                if (mesh == null)
                {
                    Add(report, root, renderer, null, materials.Length, IssueKind.MissingMesh, false,
                        "메시가 없어 머티리얼 슬롯 대응을 검사할 수 없습니다.",
                        "sharedMesh 또는 MeshFilter의 Mesh를 지정하거나 내보내기 대상에서 제외하세요.");
                    continue;
                }

                int subMeshes = mesh.subMeshCount;
                if (materials.Length != subMeshes)
                {
                    bool excess = materials.Length > subMeshes;
                    Add(report, root, renderer, mesh, materials.Length, IssueKind.SlotCountMismatch, true,
                        excess
                            ? "머티리얼 슬롯이 서브메시보다 많습니다. 추가 슬롯의 겹쳐 그리기는 FBX 왕복 시 보존되지 않아 슬롯 매핑이 실패할 수 있습니다."
                            : "서브메시 수보다 머티리얼 슬롯이 적어 FBX의 슬롯 순서/개수를 원본과 일대일 대응할 수 없습니다.",
                        excess
                            ? "추가 재질이 불필요하면 Renderer > Materials의 초과 슬롯을 제거해 개수를 맞추세요. " +
                              "헤어/윤곽선 등의 겹쳐 그리기가 필요하면 마지막 서브메시 면을 메시 사본의 추가 서브메시로 복제하여 재질을 배정하거나, " +
                              "고유 이름의 별도 Renderer/메시로 분리하세요. 본/블렌드셰이프를 보존하고 외관을 비교한 뒤 다시 검사하세요. 슬롯을 무조건 삭제하면 외관이 바뀔 수 있습니다."
                            : "각 서브메시에 대응하는 Materials 슬롯과 재질을 배정하세요. 불필요한 서브메시는 메시 편집기에서 정리한 뒤 재임포트하세요.");
                }

                for (int i = 0; i < subMeshes; i++)
                {
                    // Descriptor metadata works without enabling Read/Write or copying geometry.
                    if (mesh.GetIndexCount(i) == 0)
                        Add(report, root, renderer, mesh, materials.Length, IssueKind.EmptySubMesh, false,
                            $"SubMesh [{i}]에 면이 없습니다. FBX 임포트에서 빈 슬롯이 사라질 수 있습니다.",
                            "메시 편집기에서 빈 서브메시와 대응 재질 슬롯을 함께 정리하거나 필요한 면을 배정한 뒤 재임포트하세요.");
                }

                var firstSlots = new Dictionary<Material, int>();
                for (int i = 0; i < materials.Length; i++)
                {
                    var material = materials[i];
                    if (material == null)
                    {
                        Add(report, root, renderer, mesh, materials.Length, IssueKind.NullMaterial, false,
                            $"Materials [{i}]가 비어 있습니다. FBX의 기본 재질 대체/슬롯 정리로 결과가 달라질 수 있습니다.",
                            "해당 슬롯에 사용할 머티리얼 자산을 배정한 뒤 다시 검사하세요.");
                    }
                    else if (firstSlots.TryGetValue(material, out var first))
                    {
                        Add(report, root, renderer, mesh, materials.Length, IssueKind.RepeatedMaterial, false,
                            $"Materials [{first}], [{i}]가 같은 재질 '{material.name}'을 참조합니다. FBX 왕복 시 슬롯이 합쳐질 수 있습니다.",
                            "슬롯을 구분해야 한다면 별도 이름의 머티리얼 자산 사본을 배정하세요. 합쳐도 되는 영역이면 메시 편집기에서 서브메시와 슬롯을 함께 정리하세요.");
                    }
                    else firstSlots.Add(material, i);
                }
            }
            return report;
        }

        public static Mesh GetMesh(Renderer renderer)
        {
            if (renderer is SkinnedMeshRenderer smr) return smr.sharedMesh;
            if (renderer is MeshRenderer) return renderer.GetComponent<MeshFilter>()?.sharedMesh;
            return null;
        }

        private static void Add(Report report, GameObject root, Renderer renderer, Mesh mesh,
            int materialCount, IssueKind kind, bool blocksBake, string reason, string solution)
        {
            string relative = AnimationUtility.CalculateTransformPath(renderer.transform, root.transform);
            report.Issues.Add(new Issue
            {
                Renderer = renderer,
                Mesh = mesh,
                Path = root.name + (string.IsNullOrEmpty(relative) ? "" : "/" + relative),
                MaterialCount = materialCount,
                SubMeshCount = mesh != null ? mesh.subMeshCount : 0,
                Kind = kind,
                BlocksBake = blocksBake,
                Reason = reason,
                Solution = solution,
            });
        }
    }
}