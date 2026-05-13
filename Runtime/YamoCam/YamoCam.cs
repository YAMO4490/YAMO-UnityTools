using UnityEngine;

#if UNITY_EDITOR
using UnityEditor;
#endif

namespace YAMO.UnityTools
{
    /// <summary>
    /// 카메라 컨트롤 컴포넌트. Follow / LookAt / Orbital / Noise 4 모듈을 가짐.
    /// 에디트 모드에서도 동작 (updateInEditMode = true 시).
    /// </summary>
    [ExecuteAlways]
    [AddComponentMenu("YAMO/YAMO Cam")]
    public class YamoCam : MonoBehaviour
    {
        // ── Follow ──
        public bool enableFollow = true;
        public Transform[] followTargets = new Transform[1];
        public Vector3 positionOffset;
        [Min(0f)] public float followSmoothSpeed = 5f;
        [Min(0f)] public float followDistanceElasticity = 1.5f;
        [Min(1)] public int followFrameInterval = 1;
        public bool followX = true;
        public bool followY = true;
        public bool followZ = true;
        [Range(0f, 100f)] public float moveRatioX = 100f;
        [Range(0f, 100f)] public float moveRatioY = 100f;
        [Range(0f, 100f)] public float moveRatioZ = 100f;

        // ── LookAt ──
        public bool enableLookAt = true;
        public Transform[] lookAtTargets = new Transform[1];
        public Vector3 lookAtOffset;
        [Min(0f)] public float lookAtSmoothSpeed = 5f;
        public Vector3 worldUp = Vector3.up;
        [Min(1)] public int lookAtFrameInterval = 1;
        public bool rotateX = true;
        public bool rotateY = true;
        public bool rotateZ = true;
        [Range(0f, 100f)] public float rotateRatioX = 100f;
        [Range(0f, 100f)] public float rotateRatioY = 100f;
        [Range(0f, 100f)] public float rotateRatioZ = 100f;

        // ── Orbital ──
        public bool enableOrbital = false;
        public Transform[] orbitCenters = new Transform[0];
        [Min(0f)] public float orbitHorizontalRadius = 5f;
        public float orbitHorizontalSpeed = 15f;
        public float orbitHorizontalPhaseOffset = 0f;
        [Min(0f)] public float orbitVerticalRadius = 1f;
        public float orbitVerticalSpeed = 8f;
        public float orbitVerticalPhaseOffset = 0f;
        public float orbitVerticalAngleMin = -20f;
        public float orbitVerticalAngleMax = 40f;
        public float orbitHeightOffset = 2f;

        // ── Noise (Hand-held) ──
        public bool enableNoise = false;

        [Min(0f)] public float posNoiseAmplitude = 0.003f;
        [Min(0f)] public float posNoiseFrequency = 0.4f;
        public bool posNoiseX = true;
        public bool posNoiseY = true;
        public bool posNoiseZ = true;

        [Min(0f)] public float rotNoiseAmplitude = 0.25f;
        [Min(0f)] public float rotNoiseFrequency = 0.3f;
        public bool rotNoiseX = true;
        public bool rotNoiseY = true;
        public bool rotNoiseZ = false;

        // ── Editor ──
        public bool updateInEditMode = true;
        public bool applyPlayModeChangesToEditor = false;

        private int _followFrameCounter;
        private float _followAccDelta;
        private int _lookAtFrameCounter;
        private float _lookAtAccDelta;
        private float _orbitalTime;
        private float _noiseTime;
        private float _noiseSeedX, _noiseSeedY, _noiseSeedZ;
        private float _noiseSeedRX, _noiseSeedRY, _noiseSeedRZ;

#if UNITY_EDITOR
        private double _lastEditorTime;
        private bool _snapshotPending;
        private YamoCamSnapshot _snapshot;
#endif

        private void OnEnable()
        {
            _followFrameCounter = 0;
            _followAccDelta = 0f;
            _lookAtFrameCounter = 0;
            _lookAtAccDelta = 0f;
            _orbitalTime = 0f;
            _noiseTime = 0f;
            _noiseSeedX = Random.Range(0f, 1000f);
            _noiseSeedY = Random.Range(0f, 1000f);
            _noiseSeedZ = Random.Range(0f, 1000f);
            _noiseSeedRX = Random.Range(0f, 1000f);
            _noiseSeedRY = Random.Range(0f, 1000f);
            _noiseSeedRZ = Random.Range(0f, 1000f);

#if UNITY_EDITOR
            _lastEditorTime = EditorApplication.timeSinceStartup;
            EditorApplication.update -= EditorTick;
            EditorApplication.update += EditorTick;
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
#endif
        }

        private void OnDisable()
        {
#if UNITY_EDITOR
            EditorApplication.update -= EditorTick;
            // playModeStateChanged 구독은 OnDestroy까지 유지 (ExitingPlayMode → OnDisable → EnteredEditMode 순서 때문)
#endif
        }

        private void OnDestroy()
        {
#if UNITY_EDITOR
            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
#endif
        }

        private void LateUpdate()
        {
            if (!Application.isPlaying) return;

            float dt = Time.deltaTime;

            if (enableFollow)
            {
                _followAccDelta += dt;
                _followFrameCounter++;
                if (_followFrameCounter >= followFrameInterval)
                {
                    ApplyFollow(_followAccDelta);
                    _followFrameCounter = 0;
                    _followAccDelta = 0f;
                }
            }

            if (enableOrbital)
            {
                ApplyOrbital(dt);
            }

            if (enableLookAt)
            {
                _lookAtAccDelta += dt;
                _lookAtFrameCounter++;
                if (_lookAtFrameCounter >= lookAtFrameInterval)
                {
                    ApplyLookAt(_lookAtAccDelta);
                    _lookAtFrameCounter = 0;
                    _lookAtAccDelta = 0f;
                }
            }

            if (enableNoise)
            {
                ApplyNoise(dt);
            }
        }

#if UNITY_EDITOR
        private void EditorTick()
        {
            if (Application.isPlaying || !updateInEditMode || this == null || !isActiveAndEnabled) return;

            double now = EditorApplication.timeSinceStartup;
            float deltaTime = Mathf.Max(0.0001f, (float)(now - _lastEditorTime));
            _lastEditorTime = now;

            if (enableFollow) ApplyFollow(deltaTime);
            if (enableOrbital) ApplyOrbital(deltaTime);
            if (enableLookAt) ApplyLookAt(deltaTime);
            if (enableNoise) ApplyNoise(deltaTime);
            EditorApplication.QueuePlayerLoopUpdate();
        }

        private void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            if (state == PlayModeStateChange.ExitingPlayMode)
            {
                _snapshotPending = applyPlayModeChangesToEditor;
                if (_snapshotPending) _snapshot = CaptureSnapshot();
            }
            else if (state == PlayModeStateChange.EnteredEditMode && _snapshotPending)
            {
                Undo.RecordObject(this, "Apply PlayMode Changes to Editor");
                ApplySnapshot(_snapshot);
                EditorUtility.SetDirty(this);
                _snapshotPending = false;
            }
        }

        private YamoCamSnapshot CaptureSnapshot() => new YamoCamSnapshot
        {
            enableFollow = enableFollow,
            followTargets = (Transform[])followTargets.Clone(),
            positionOffset = positionOffset,
            followSmoothSpeed = followSmoothSpeed,
            followDistanceElasticity = followDistanceElasticity,
            followFrameInterval = followFrameInterval,
            followX = followX, followY = followY, followZ = followZ,
            moveRatioX = moveRatioX, moveRatioY = moveRatioY, moveRatioZ = moveRatioZ,

            enableLookAt = enableLookAt,
            lookAtTargets = (Transform[])lookAtTargets.Clone(),
            lookAtOffset = lookAtOffset,
            lookAtSmoothSpeed = lookAtSmoothSpeed,
            worldUp = worldUp,
            lookAtFrameInterval = lookAtFrameInterval,
            rotateX = rotateX, rotateY = rotateY, rotateZ = rotateZ,
            rotateRatioX = rotateRatioX, rotateRatioY = rotateRatioY, rotateRatioZ = rotateRatioZ,

            enableOrbital = enableOrbital,
            orbitCenters = (Transform[])orbitCenters.Clone(),
            orbitHorizontalRadius = orbitHorizontalRadius,
            orbitHorizontalSpeed = orbitHorizontalSpeed,
            orbitHorizontalPhaseOffset = orbitHorizontalPhaseOffset,
            orbitVerticalRadius = orbitVerticalRadius,
            orbitVerticalSpeed = orbitVerticalSpeed,
            orbitVerticalPhaseOffset = orbitVerticalPhaseOffset,
            orbitVerticalAngleMin = orbitVerticalAngleMin,
            orbitVerticalAngleMax = orbitVerticalAngleMax,
            orbitHeightOffset = orbitHeightOffset,

            enableNoise = enableNoise,
            posNoiseAmplitude = posNoiseAmplitude,
            posNoiseFrequency = posNoiseFrequency,
            posNoiseX = posNoiseX, posNoiseY = posNoiseY, posNoiseZ = posNoiseZ,
            rotNoiseAmplitude = rotNoiseAmplitude,
            rotNoiseFrequency = rotNoiseFrequency,
            rotNoiseX = rotNoiseX, rotNoiseY = rotNoiseY, rotNoiseZ = rotNoiseZ,

            updateInEditMode = updateInEditMode,
        };

        private void ApplySnapshot(YamoCamSnapshot s)
        {
            enableFollow = s.enableFollow;
            followTargets = s.followTargets;
            positionOffset = s.positionOffset;
            followSmoothSpeed = s.followSmoothSpeed;
            followDistanceElasticity = s.followDistanceElasticity;
            followFrameInterval = s.followFrameInterval;
            followX = s.followX; followY = s.followY; followZ = s.followZ;
            moveRatioX = s.moveRatioX; moveRatioY = s.moveRatioY; moveRatioZ = s.moveRatioZ;

            enableLookAt = s.enableLookAt;
            lookAtTargets = s.lookAtTargets;
            lookAtOffset = s.lookAtOffset;
            lookAtSmoothSpeed = s.lookAtSmoothSpeed;
            worldUp = s.worldUp;
            lookAtFrameInterval = s.lookAtFrameInterval;
            rotateX = s.rotateX; rotateY = s.rotateY; rotateZ = s.rotateZ;
            rotateRatioX = s.rotateRatioX; rotateRatioY = s.rotateRatioY; rotateRatioZ = s.rotateRatioZ;

            enableOrbital = s.enableOrbital;
            orbitCenters = s.orbitCenters;
            orbitHorizontalRadius = s.orbitHorizontalRadius;
            orbitHorizontalSpeed = s.orbitHorizontalSpeed;
            orbitHorizontalPhaseOffset = s.orbitHorizontalPhaseOffset;
            orbitVerticalRadius = s.orbitVerticalRadius;
            orbitVerticalSpeed = s.orbitVerticalSpeed;
            orbitVerticalPhaseOffset = s.orbitVerticalPhaseOffset;
            orbitVerticalAngleMin = s.orbitVerticalAngleMin;
            orbitVerticalAngleMax = s.orbitVerticalAngleMax;
            orbitHeightOffset = s.orbitHeightOffset;

            enableNoise = s.enableNoise;
            posNoiseAmplitude = s.posNoiseAmplitude;
            posNoiseFrequency = s.posNoiseFrequency;
            posNoiseX = s.posNoiseX; posNoiseY = s.posNoiseY; posNoiseZ = s.posNoiseZ;
            rotNoiseAmplitude = s.rotNoiseAmplitude;
            rotNoiseFrequency = s.rotNoiseFrequency;
            rotNoiseX = s.rotNoiseX; rotNoiseY = s.rotNoiseY; rotNoiseZ = s.rotNoiseZ;

            updateInEditMode = s.updateInEditMode;
        }

        private struct YamoCamSnapshot
        {
            public bool enableFollow;
            public Transform[] followTargets;
            public Vector3 positionOffset;
            public float followSmoothSpeed, followDistanceElasticity;
            public int followFrameInterval;
            public bool followX, followY, followZ;
            public float moveRatioX, moveRatioY, moveRatioZ;

            public bool enableLookAt;
            public Transform[] lookAtTargets;
            public Vector3 lookAtOffset;
            public float lookAtSmoothSpeed;
            public Vector3 worldUp;
            public int lookAtFrameInterval;
            public bool rotateX, rotateY, rotateZ;
            public float rotateRatioX, rotateRatioY, rotateRatioZ;

            public bool enableOrbital;
            public Transform[] orbitCenters;
            public float orbitHorizontalRadius, orbitHorizontalSpeed, orbitHorizontalPhaseOffset;
            public float orbitVerticalRadius, orbitVerticalSpeed, orbitVerticalPhaseOffset;
            public float orbitVerticalAngleMin, orbitVerticalAngleMax, orbitHeightOffset;

            public bool enableNoise;
            public float posNoiseAmplitude, posNoiseFrequency;
            public bool posNoiseX, posNoiseY, posNoiseZ;
            public float rotNoiseAmplitude, rotNoiseFrequency;
            public bool rotNoiseX, rotNoiseY, rotNoiseZ;

            public bool updateInEditMode;
        }
#endif

        private Vector3 GetCenterPosition(Transform[] targets)
        {
            Vector3 sum = Vector3.zero;
            int count = 0;
            for (int i = 0; i < targets.Length; i++)
            {
                if (targets[i] != null)
                {
                    sum += targets[i].position;
                    count++;
                }
            }
            if (count == 0) return transform.position;
            return sum / count;
        }

        private Quaternion GetCenterRotation(Transform[] targets)
        {
            for (int i = 0; i < targets.Length; i++)
                if (targets[i] != null) return targets[i].rotation;
            return Quaternion.identity;
        }

        private void ApplyFollow(float deltaTime)
        {
            Vector3 centerPos = GetCenterPosition(followTargets);
            Quaternion centerRot = GetCenterRotation(followTargets);

            // positionOffset은 타겟 로컬 공간 기준 → 월드로 변환
            Vector3 targetPos = centerPos + centerRot * positionOffset;
            Vector3 currentPos = transform.position;

            float distance = Vector3.Distance(currentPos, targetPos);
            float speedMultiplier = 1f + (distance * followDistanceElasticity);
            float t = 1f - Mathf.Exp(-followSmoothSpeed * speedMultiplier * deltaTime);

            // 축 마스킹을 타겟 로컬 공간에서 수행
            Quaternion invRot = Quaternion.Inverse(centerRot);
            Vector3 currentLocal = invRot * (currentPos - centerPos);
            Vector3 fullNextLocal = invRot * (Vector3.Lerp(currentPos, targetPos, t) - centerPos);

            Vector3 nextLocal = currentLocal;
            if (followX) nextLocal.x = Mathf.Lerp(currentLocal.x, fullNextLocal.x, moveRatioX * 0.01f);
            if (followY) nextLocal.y = Mathf.Lerp(currentLocal.y, fullNextLocal.y, moveRatioY * 0.01f);
            if (followZ) nextLocal.z = Mathf.Lerp(currentLocal.z, fullNextLocal.z, moveRatioZ * 0.01f);

            transform.position = centerPos + centerRot * nextLocal;
        }

        private void ApplyOrbital(float deltaTime)
        {
            // orbitCenters가 비어있으면 followTargets를 폴백으로 사용
            Transform[] centers = (orbitCenters != null && orbitCenters.Length > 0) ? orbitCenters : followTargets;
            Vector3 centerPos = GetCenterPosition(centers);

            _orbitalTime += deltaTime;

            // Horizontal: continuous 360° loop
            float hAngleDeg = (orbitHorizontalPhaseOffset + orbitHorizontalSpeed * _orbitalTime) % 360f;
            float hAngleRad = hAngleDeg * Mathf.Deg2Rad;

            // Vertical: ping-pong with sine easing for smooth turnaround
            float vCycle = orbitVerticalSpeed * _orbitalTime + orbitVerticalPhaseOffset;
            float vNormalized = (Mathf.Sin(vCycle * Mathf.Deg2Rad) + 1f) * 0.5f;
            float vAngleDeg = Mathf.Lerp(orbitVerticalAngleMin, orbitVerticalAngleMax, vNormalized);
            float vAngleRad = vAngleDeg * Mathf.Deg2Rad;

            // Spherical to Cartesian offset
            float cosV = Mathf.Cos(vAngleRad);
            Vector3 orbitOffset = new Vector3(
                Mathf.Sin(hAngleRad) * orbitHorizontalRadius * cosV,
                Mathf.Sin(vAngleRad) * orbitVerticalRadius + orbitHeightOffset,
                Mathf.Cos(hAngleRad) * orbitHorizontalRadius * cosV
            );

            transform.position = centerPos + orbitOffset;
        }

        private void ApplyLookAt(float deltaTime)
        {
            Vector3 centerPos = GetCenterPosition(lookAtTargets);
            Vector3 lookPoint = centerPos + lookAtOffset;
            Vector3 direction = lookPoint - transform.position;
            if (direction.sqrMagnitude < 0.0001f) return;

            Quaternion targetRot = Quaternion.LookRotation(direction.normalized, worldUp);

            float t = 1f - Mathf.Exp(-lookAtSmoothSpeed * deltaTime);
            Quaternion fullRot = Quaternion.Slerp(transform.rotation, targetRot, t);

            Vector3 currentEuler = transform.rotation.eulerAngles;
            Vector3 fullEuler = fullRot.eulerAngles;

            Vector3 resultEuler = currentEuler;
            if (rotateX) resultEuler.x = Mathf.LerpAngle(currentEuler.x, fullEuler.x, rotateRatioX * 0.01f);
            if (rotateY) resultEuler.y = Mathf.LerpAngle(currentEuler.y, fullEuler.y, rotateRatioY * 0.01f);
            if (rotateZ) resultEuler.z = Mathf.LerpAngle(currentEuler.z, fullEuler.z, rotateRatioZ * 0.01f);

            transform.rotation = Quaternion.Euler(resultEuler);
        }

        private void ApplyNoise(float deltaTime)
        {
            _noiseTime += deltaTime;

            // Position noise
            if (posNoiseAmplitude > 0f)
            {
                float pt = _noiseTime * posNoiseFrequency;
                float nx = posNoiseX ? (Mathf.PerlinNoise(_noiseSeedX + pt, 0f) - 0.5f) * 2f * posNoiseAmplitude : 0f;
                float ny = posNoiseY ? (Mathf.PerlinNoise(_noiseSeedY + pt, 0f) - 0.5f) * 2f * posNoiseAmplitude : 0f;
                float nz = posNoiseZ ? (Mathf.PerlinNoise(_noiseSeedZ + pt, 0f) - 0.5f) * 2f * posNoiseAmplitude : 0f;
                transform.position += transform.rotation * new Vector3(nx, ny, nz);
            }

            // Rotation noise
            if (rotNoiseAmplitude > 0f)
            {
                float rt = _noiseTime * rotNoiseFrequency;
                float rx = rotNoiseX ? (Mathf.PerlinNoise(_noiseSeedRX + rt, 0f) - 0.5f) * 2f * rotNoiseAmplitude : 0f;
                float ry = rotNoiseY ? (Mathf.PerlinNoise(_noiseSeedRY + rt, 0f) - 0.5f) * 2f * rotNoiseAmplitude : 0f;
                float rz = rotNoiseZ ? (Mathf.PerlinNoise(_noiseSeedRZ + rt, 0f) - 0.5f) * 2f * rotNoiseAmplitude : 0f;
                transform.rotation *= Quaternion.Euler(rx, ry, rz);
            }
        }
    }
}
