using System.Collections.Generic;
using UnityEngine;

namespace YAMO.UnityTools
{
    /// <summary>
    /// 특정 블렌드셰이프 값에 반응해서 다른 블렌드셰이프 값을 실시간으로 연동시킨다.
    /// 여러 규칙이 같은 타겟에 걸려 있을 경우, (source * multiplier) 중 Max 값을 타겟에 덮어쓴다.
    /// 플레이 모드에서만 동작한다.
    /// </summary>
    [RequireComponent(typeof(SkinnedMeshRenderer))]
    [AddComponentMenu("YAMO/BlendShape Link")]
    public class BlendShapeLink : MonoBehaviour
    {
        public enum LinkMode
        {
            /// <summary>target 에 (source × multiplier) 를 기여. source 는 그대로 둔다.</summary>
            Multiply = 0,
            /// <summary>target 에 (source × multiplier) 를 기여하고, source 는 0 으로 리셋.
            /// 서로 독립적인 블렌드셰이프(EyeBlink ↔ EyeBlinkSmile) 간 값 이전에 사용.</summary>
            Override = 1,
        }

        [System.Serializable]
        public class LinkRule
        {
            [Tooltip("입력이 될 블렌드셰이프 인덱스")]
            public int sourceIndex = -1;

            [Tooltip("영향을 받을(덮어쓸) 블렌드셰이프 인덱스")]
            public int targetIndex = -1;

            [Tooltip("source 값에 곱해질 배율. 1 이면 그대로, 0.5 면 절반만 반영")]
            public float multiplier = 1f;

            [Tooltip("동작 모드. Multiply: source 유지. Override: source 를 0 으로 리셋해서 target 으로 값 이전")]
            public LinkMode mode = LinkMode.Multiply;

            [Tooltip("이 규칙을 켤지 여부")]
            public bool enabled = true;
        }

        [Tooltip("연동 규칙 목록")]
        public List<LinkRule> rules = new List<LinkRule>();

        SkinnedMeshRenderer _smr;

        // 타겟 인덱스별로 max(source*multiplier)를 모을 버퍼
        readonly Dictionary<int, float> _targetMaxBuffer = new Dictionary<int, float>();

        // Override 모드에서 0 으로 리셋해야 할 source 인덱스들
        readonly HashSet<int> _overrideSources = new HashSet<int>();

        void Awake()
        {
            _smr = GetComponent<SkinnedMeshRenderer>();
        }

        void LateUpdate()
        {
            if (_smr == null || _smr.sharedMesh == null)
                return;

            var mesh = _smr.sharedMesh;
            int count = mesh.blendShapeCount;
            if (count == 0 || rules == null || rules.Count == 0)
                return;

            _targetMaxBuffer.Clear();
            _overrideSources.Clear();

            // 1단계: 모든 규칙에서 source 값을 읽어 target 별 max 계산
            // (source 값을 먼저 전부 읽어야 Override 리셋 전의 원본 값이 보존됨)
            for (int i = 0; i < rules.Count; i++)
            {
                var r = rules[i];
                if (r == null || !r.enabled)
                    continue;
                if (r.sourceIndex < 0 || r.sourceIndex >= count)
                    continue;
                if (r.targetIndex < 0 || r.targetIndex >= count)
                    continue;
                // source == target 은 허용 — "자기 자신의 값을 multiplier 로 감쇠" 용도.
                // (예: mult=0.5 → A = A × 0.5 로 수신량 절반으로 캡)
                // 애니메이션이 매 프레임 원본 값을 다시 써주는 환경을 전제로 한다.

                float src = _smr.GetBlendShapeWeight(r.sourceIndex);
                float v = src * r.multiplier;

                if (_targetMaxBuffer.TryGetValue(r.targetIndex, out float cur))
                {
                    if (v > cur)
                        _targetMaxBuffer[r.targetIndex] = v;
                }
                else
                {
                    _targetMaxBuffer[r.targetIndex] = v;
                }

                if (r.mode == LinkMode.Override)
                    _overrideSources.Add(r.sourceIndex);
            }

            // 2단계: target 값 쓰기
            foreach (var kv in _targetMaxBuffer)
            {
                float clamped = Mathf.Clamp(kv.Value, 0f, 100f);
                _smr.SetBlendShapeWeight(kv.Key, clamped);
            }

            // 3단계: Override 모드로 사용된 source 를 0 으로 리셋
            // (단, 해당 source 가 다른 규칙의 target 이기도 하면 이미 그 값이 쓰였으므로 건너뜀)
            foreach (int srcIdx in _overrideSources)
            {
                if (_targetMaxBuffer.ContainsKey(srcIdx))
                    continue;
                _smr.SetBlendShapeWeight(srcIdx, 0f);
            }
        }
    }
}
