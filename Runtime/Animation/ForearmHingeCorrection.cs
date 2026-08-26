using UnityEngine;

namespace YAMO.UnityTools
{
    /// <summary>
    /// Applies the single-axis forearm constraint while controlling how much of the
    /// removed forearm rotation is allowed to move into the hand.
    /// </summary>
    public static class ForearmHingeCorrection
    {
        public static bool Apply(
            Animator animator,
            HumanBodyBones upperBone,
            HumanBodyBones lowerBone,
            HumanBodyBones handBone,
            Vector3 localAxis,
            float handRotationCompensation)
        {
            if (animator == null)
                return false;

            return Apply(
                animator.GetBoneTransform(upperBone),
                animator.GetBoneTransform(lowerBone),
                animator.GetBoneTransform(handBone),
                localAxis,
                handRotationCompensation);
        }

        public static bool Apply(
            Transform upper,
            Transform lower,
            Transform hand,
            Vector3 localAxis,
            float handRotationCompensation)
        {
            if (upper == null || lower == null || hand == null || localAxis.sqrMagnitude <= 1e-10f)
                return false;

            localAxis.Normalize();
            handRotationCompensation = Mathf.Clamp01(handRotationCompensation);

            var originalHandPosition = hand.position;
            var originalHandWorldRotation = hand.rotation;
            var originalHandLocalRotation = hand.localRotation;
            var shoulderPosition = upper.position;
            var elbowPosition = lower.position;

            lower.localRotation = Quaternion.identity;
            var handAtZero = hand.position - elbowPosition;

            lower.localRotation = Quaternion.AngleAxis(90f, localAxis);
            var handAtNinety = hand.position - elbowPosition;

            var parentRotation = lower.parent != null ? lower.parent.rotation : Quaternion.identity;
            var worldAxis = (parentRotation * localAxis).normalized;
            var centerOffset = Vector3.Dot(handAtZero, worldAxis) * worldAxis;
            var radialZero = handAtZero - centerOffset;
            var radialNinety = handAtNinety - centerOffset;
            var targetOffset = originalHandPosition - elbowPosition - centerOffset;
            var targetInPlane = targetOffset - Vector3.Dot(targetOffset, worldAxis) * worldAxis;

            var angle = 0f;
            if (targetInPlane.sqrMagnitude > 1e-10f && radialZero.sqrMagnitude > 1e-10f)
            {
                angle = Mathf.Atan2(
                    Vector3.Dot(targetInPlane.normalized, radialNinety.normalized),
                    Vector3.Dot(targetInPlane.normalized, radialZero.normalized)) * Mathf.Rad2Deg;
            }

            lower.localRotation = Quaternion.AngleAxis(angle, localAxis);

            var currentDirection = hand.position - shoulderPosition;
            var targetDirection = originalHandPosition - shoulderPosition;
            if (currentDirection.sqrMagnitude > 1e-8f && targetDirection.sqrMagnitude > 1e-8f)
            {
                upper.rotation = Quaternion.FromToRotation(
                    currentDirection.normalized,
                    targetDirection.normalized) * upper.rotation;
            }

            // Reproduce the old world-preserving behavior first. The resulting local
            // rotation contains the exact amount pushed into the wrist by constraining
            // the forearm. Blending back to the source local rotation removes it
            // without unstable Euler-angle subtraction.
            hand.rotation = originalHandWorldRotation;
            hand.localRotation = Quaternion.Slerp(
                hand.localRotation,
                originalHandLocalRotation,
                handRotationCompensation);
            return true;
        }
    }
}
