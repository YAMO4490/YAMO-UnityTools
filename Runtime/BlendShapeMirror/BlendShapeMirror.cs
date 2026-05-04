using System.Collections.Generic;
using UnityEngine;

namespace YAMO.UnityTools
{
    /// <summary>
    /// 소스 SkinnedMeshRenderer 의 모든 블렌드셰이프 값을, 같은 이름을 가진 타겟 SMR 들의 블렌드셰이프에
    /// 매 프레임 통째로 복제한다. 얼굴 메시가 여러 조각으로 나뉘어 있을 때
    /// (예: Face / Face_brow / Face_option / Face_tongue) 페이셜 애니메이션을 한꺼번에 동기화하는 용도.
    /// 플레이 모드에서만 동작.
    /// </summary>
    [AddComponentMenu("YAMO/BlendShape Mirror")]
    public class BlendShapeMirror : MonoBehaviour
    {
        [Tooltip("값을 가져올 소스 SkinnedMeshRenderer. 비어있으면 이 오브젝트의 SMR 사용")]
        public SkinnedMeshRenderer source;

        [Tooltip("같은 이름의 블렌드셰이프 값을 동기화할 대상 SMR 들")]
        public List<SkinnedMeshRenderer> targets = new List<SkinnedMeshRenderer>();

        SkinnedMeshRenderer _resolvedSource;
        Mesh _cachedSourceMesh;

        // _maps[t][i] = target t 에서 source 블렌드셰이프 i 와 같은 이름의 블렌드셰이프 인덱스 (없으면 -1)
        readonly List<int[]> _maps = new List<int[]>();
        readonly List<Mesh> _cachedTargetMeshes = new List<Mesh>();

        void Awake()
        {
            ResolveSource();
        }

        void ResolveSource()
        {
            _resolvedSource = source != null ? source : GetComponent<SkinnedMeshRenderer>();
        }

        /// <summary>매핑 캐시를 강제로 재구축. 런타임에 메시나 타겟 리스트가 바뀐 뒤 호출.</summary>
        public void Rebuild()
        {
            _maps.Clear();
            _cachedTargetMeshes.Clear();
            ResolveSource();
            _cachedSourceMesh = _resolvedSource != null ? _resolvedSource.sharedMesh : null;
            if (_cachedSourceMesh == null) return;

            int count = _cachedSourceMesh.blendShapeCount;
            for (int t = 0; t < targets.Count; t++)
            {
                var tgt = targets[t];
                if (tgt == null || tgt == _resolvedSource || tgt.sharedMesh == null)
                {
                    _maps.Add(null);
                    _cachedTargetMeshes.Add(null);
                    continue;
                }

                var tmesh = tgt.sharedMesh;
                var map = new int[count];
                for (int i = 0; i < count; i++)
                {
                    string name = _cachedSourceMesh.GetBlendShapeName(i);
                    map[i] = tmesh.GetBlendShapeIndex(name); // 없으면 -1
                }
                _maps.Add(map);
                _cachedTargetMeshes.Add(tmesh);
            }
        }

        bool NeedsRebuild()
        {
            ResolveSource();
            if (_resolvedSource == null) return false;
            if (_cachedSourceMesh != _resolvedSource.sharedMesh) return true;
            if (_maps.Count != targets.Count) return true;

            for (int t = 0; t < targets.Count; t++)
            {
                var tgt = targets[t];
                var cached = t < _cachedTargetMeshes.Count ? _cachedTargetMeshes[t] : null;

                if (tgt == null)
                {
                    if (cached != null) return true;
                }
                else if (tgt.sharedMesh != cached)
                {
                    return true;
                }
            }
            return false;
        }

        void LateUpdate()
        {
            if (NeedsRebuild()) Rebuild();
            if (_resolvedSource == null || _cachedSourceMesh == null) return;
            if (_maps.Count == 0) return;

            int count = _cachedSourceMesh.blendShapeCount;
            int n = targets.Count < _maps.Count ? targets.Count : _maps.Count;

            for (int t = 0; t < n; t++)
            {
                var tgt = targets[t];
                if (tgt == null) continue;
                var map = _maps[t];
                if (map == null) continue;

                for (int i = 0; i < count; i++)
                {
                    int ti = map[i];
                    if (ti < 0) continue;
                    float v = _resolvedSource.GetBlendShapeWeight(i);
                    tgt.SetBlendShapeWeight(ti, v);
                }
            }
        }
    }
}
