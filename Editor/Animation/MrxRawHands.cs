using System;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    internal sealed class MrxRawHands : ScriptableObject
    {
        [Serializable]
        internal struct Arm
        {
            public Vector3 elbow;
            public Vector3 hand;
            public Quaternion rotation;

            public static Arm Blend(Arm from, Arm to, float weight) => new Arm
            {
                elbow = Vector3.Lerp(from.elbow, to.elbow, weight),
                hand = Vector3.Lerp(from.hand, to.hand, weight),
                rotation = Quaternion.Slerp(from.rotation, to.rotation, weight)
            };
        }

        [SerializeField] internal float[] times;
        [SerializeField] internal Arm[] left;
        [SerializeField] internal Arm[] right;

        internal Arm Sample(bool isLeft, float time)
        {
            var values = isLeft ? left : right;
            var index = Array.BinarySearch(times, time);
            if (index >= 0)
                return values[index];

            index = ~index;
            if (index == 0)
                return values[0];
            if (index == times.Length)
                return values[index - 1];

            return Arm.Blend(values[index - 1], values[index],
                Mathf.InverseLerp(times[index - 1], times[index], time));
        }
    }
}
