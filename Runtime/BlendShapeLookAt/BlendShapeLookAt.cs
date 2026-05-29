using UnityEngine;

namespace YAMO.UnityTools
{
    /// <summary>
    /// Blend Shape Look At
    ///
    /// 메인 카메라(또는 지정 타겟)의 위치를 실시간으로 추적해,
    /// ARKit 규격 시선 블렌드셰이프(eyeLookUp/Down/In/Out Left/Right)를 LateUpdate에서
    /// 덮어써서 캐릭터가 카메라를 바라보게 만드는 컴포넌트.
    ///
    /// - Eye Bone / LookAt Constraint 없이 블렌드셰이프만으로 시선 처리.
    /// - 머리 본의 로컬 기준 축을 직접 지정할 수 있어 리그마다 다른 본 방향에 대응.
    /// - 대상이 뒤쪽으로 넘어가면 정면 응시로 자동 복귀.
    /// - 씬 뷰 기즈모로 기준 축·카메라 방향을 시각적으로 확인 가능.
    /// </summary>
    [AddComponentMenu("YAMO/Blend Shape Look At")]
    public class BlendShapeLookAt : MonoBehaviour
    {
        [Header("전역 제어")]
        [Tooltip("시선 추적 활성화 여부 (타임라인 애니메이션으로 On/Off 제어 가능)")]
        public bool active = true;

        [Header("참조")]
        [Tooltip("시선 블렌드셰이프가 있는 SkinnedMeshRenderer (Face 메시)")]
        public SkinnedMeshRenderer faceMesh;

        [Tooltip("머리 본 Transform (시선 방향 계산의 기준점)")]
        public Transform headTransform;

        [Tooltip("바라볼 대상 (비어있으면 메인 카메라 자동 사용)")]
        public Transform target;

        [Header("강도")]
        [Range(0f, 1f)]
        [Tooltip("전체 시선 추적 강도 (0이면 꺼짐)")]
        public float intensity = 1f;

        [Range(0f, 1f)]
        [Tooltip("수평 시선 강도")]
        public float horizontalIntensity = 1f;

        [Range(0f, 1f)]
        [Tooltip("수직 시선 강도")]
        public float verticalIntensity = 1f;

        [Header("각도 범위")]
        [Range(5f, 60f)]
        [Tooltip("블렌드셰이프 100%에 도달하는 수평 각도")]
        public float maxHorizontalAngle = 30f;

        [Range(5f, 60f)]
        [Tooltip("블렌드셰이프 100%에 도달하는 수직 각도")]
        public float maxVerticalAngle = 25f;

        [Header("기준 축")]
        [Tooltip("머리 본 로컬 공간에서 정면을 가리키는 축\n예) Z+ → (0,0,1) / Z- → (0,0,-1) / Y+ → (0,1,0)")]
        public Vector3 headForwardLocal = new Vector3(0f, 1f, 0f);

        [Tooltip("머리 본 로컬 공간에서 위를 가리키는 축 (보통 (0,1,0))")]
        public Vector3 headUpLocal = new Vector3(-1f, 0f, 0f);

        [Header("디버그 기즈모")]
        [Tooltip("씬 뷰 기즈모 화살표 길이 (파란=정면 / 빨간=오른쪽 / 초록=위 / 노란=카메라 방향)")]
        public float gizmoSize = 0.08f;

        [Header("부드러움")]
        [Range(0f, 0.3f)]
        [Tooltip("시선 이동 보간 시간 (0이면 즉시 반영)")]
        public float smoothTime = 0.05f;

        // ARKit 규격 블렌드셰이프 이름 (인덱스 고정)
        static readonly string[] k_ShapeNames =
        {
            "eyeLookUpLeft",    // 0
            "eyeLookUpRight",   // 1
            "eyeLookDownLeft",  // 2
            "eyeLookDownRight", // 3
            "eyeLookInLeft",    // 4
            "eyeLookInRight",   // 5
            "eyeLookOutLeft",   // 6
            "eyeLookOutRight",  // 7
        };

        readonly int[] _idx = new int[8];
        float _curH, _curV;
        float _velH, _velV;
        Camera _cam;

        void OnEnable()
        {
            CacheIndices();
            _cam = Camera.main;
        }

        void OnValidate()
        {
            CacheIndices();
        }

        void CacheIndices()
        {
            if (faceMesh == null) return;
            var mesh = faceMesh.sharedMesh;
            for (int i = 0; i < 8; i++)
                _idx[i] = mesh.GetBlendShapeIndex(k_ShapeNames[i]);
        }

        void LateUpdate()
        {
            if (!active)
            {
                if (faceMesh != null)
                    for (int i = 0; i < 8; i++)
                        SetWeight(i, 0f);
                _curH = _curV = _velH = _velV = 0f;
                return;
            }

            var t = target;
            if (t == null)
            {
                if (_cam == null) _cam = Camera.main;
                if (_cam == null) return;
                t = _cam.transform;
            }
            if (faceMesh == null || headTransform == null) return;

            Vector3 dirWorld = (t.position - headTransform.position).normalized;
            Vector3 dir      = headTransform.InverseTransformDirection(dirWorld);

            // 설정된 기준 축으로 좌표 분해
            Vector3 fwd   = headForwardLocal.normalized;
            Vector3 upV   = headUpLocal.normalized;
            Vector3 right = Vector3.Cross(upV, fwd).normalized;
            upV = Vector3.Cross(fwd, right).normalized; // 직교 보정

            float fComp = Vector3.Dot(dir, fwd);
            float hComp = Vector3.Dot(dir, right);
            float vComp = Vector3.Dot(dir, upV);

            // 대상이 뒤쪽(fComp ≤ 0)이면 정면 응시로 복귀
            float hNorm = 0f;
            float vNorm = 0f;
            if (fComp > 0f)
            {
                float hAngle = Mathf.Atan2(hComp, fComp) * Mathf.Rad2Deg;
                float vAngle = Mathf.Atan2(vComp, fComp) * Mathf.Rad2Deg;
                hNorm = Mathf.Clamp(hAngle / maxHorizontalAngle, -1f, 1f) * intensity * horizontalIntensity;
                vNorm = Mathf.Clamp(vAngle / maxVerticalAngle,   -1f, 1f) * intensity * verticalIntensity;
            }

            if (smoothTime > 0.001f)
            {
                _curH = Mathf.SmoothDamp(_curH, hNorm, ref _velH, smoothTime);
                _curV = Mathf.SmoothDamp(_curV, vNorm, ref _velV, smoothTime);
            }
            else
            {
                _curH = hNorm;
                _curV = vNorm;
            }

            float wUp   = Mathf.Max(0f,  _curV) * 100f;
            float wDown = Mathf.Max(0f, -_curV) * 100f;

            // In = 코 쪽, Out = 바깥쪽
            // 오른쪽 주시(H+): 왼눈 In, 오른눈 Out
            // 왼쪽  주시(H-): 왼눈 Out, 오른눈 In
            float inL  = Mathf.Max(0f,  _curH) * 100f;
            float outL = Mathf.Max(0f, -_curH) * 100f;
            float inR  = Mathf.Max(0f, -_curH) * 100f;
            float outR = Mathf.Max(0f,  _curH) * 100f;

            SetWeight(0, wUp);   // eyeLookUpLeft
            SetWeight(1, wUp);   // eyeLookUpRight
            SetWeight(2, wDown); // eyeLookDownLeft
            SetWeight(3, wDown); // eyeLookDownRight
            SetWeight(4, inL);   // eyeLookInLeft
            SetWeight(5, inR);   // eyeLookInRight
            SetWeight(6, outL);  // eyeLookOutLeft
            SetWeight(7, outR);  // eyeLookOutRight
        }

        void SetWeight(int slot, float value)
        {
            int i = _idx[slot];
            if (i >= 0) faceMesh.SetBlendShapeWeight(i, value);
        }

#if UNITY_EDITOR
        void OnDrawGizmosSelected()
        {
            if (headTransform == null) return;

            Vector3 fwd   = headForwardLocal.normalized;
            Vector3 upV   = headUpLocal.normalized;
            Vector3 right = Vector3.Cross(upV, fwd).normalized;
            upV = Vector3.Cross(fwd, right).normalized;

            Vector3 pos    = headTransform.position;
            Vector3 fwdW   = headTransform.TransformDirection(fwd);
            Vector3 rightW = headTransform.TransformDirection(right);
            Vector3 upW    = headTransform.TransformDirection(upV);

            Gizmos.color = Color.blue;   Gizmos.DrawRay(pos, fwdW   * gizmoSize); // 정면
            Gizmos.color = Color.red;    Gizmos.DrawRay(pos, rightW * gizmoSize); // 오른쪽
            Gizmos.color = Color.green;  Gizmos.DrawRay(pos, upW    * gizmoSize); // 위

            var t = target != null ? target : (_cam != null ? _cam.transform : null);
            if (t != null)
            {
                Gizmos.color = Color.yellow;
                Gizmos.DrawLine(pos, t.position); // 카메라 방향
            }
        }
#endif
    }
}
