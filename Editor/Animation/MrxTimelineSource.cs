using System;
using System.IO;
using System.Linq;
using Newtonsoft.Json.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEditor.Timeline.Actions;
using UnityEngine;
using UnityEngine.Timeline;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor
{
    internal static class MrxTimelineSource
    {
        internal const string Prefix = "YAMO-MRX-RAW:";

        internal static string RawFbx(AnimationClip animation)
        {
            var path = AssetDatabase.GetAssetPath(animation);
            if (string.IsNullOrEmpty(path))
                return null;
            var data = AssetImporter.GetAtPath(path)?.userData;
            return data != null && data.StartsWith(Prefix, StringComparison.Ordinal)
                ? AssetDatabase.GUIDToAssetPath(data.Substring(Prefix.Length)) : null;
        }

        internal static string GetRawFbx(this TimelineClip clip) =>
            RawFbx((clip.asset as AnimationPlayableAsset)?.clip);

        internal static float GetMrxSourceTime(this TimelineClip clip, double timelineTime) =>
            (float)clip.ToLocalTime(timelineTime);

        internal static MrxRawHands ReadHands(string path)
        {
            var prefab = AssetDatabase.LoadAssetAtPath<GameObject>(path);
            var clip = AssetDatabase.LoadAllAssetsAtPath(path).OfType<AnimationClip>()
                .SingleOrDefault(animation => !animation.name.StartsWith("__preview__", StringComparison.Ordinal));
            if (prefab == null || clip == null || clip.humanMotion)
                throw new InvalidDataException("MRX Raw FBX 원본 클립을 읽을 수 없습니다: " + path);

            var scene = EditorSceneManager.NewPreviewScene();
            try
            {
                var root = (GameObject)PrefabUtility.InstantiatePrefab(prefab, scene);
                var bones = root.GetComponentsInChildren<Transform>(true);
                Transform[] Arm(string side) => new[] { "UArm", "FArm", "Hand" }
                    .Select(name => bones.Single(bone => bone.name == side + name)).ToArray();
                var left = Arm("L");
                var right = Arm("R");
                var bind = MrxHumanoidClip.ReadBindPose();
                var leftRest = InverseBindRotation(left[2], root.transform, bind);
                var rightRest = InverseBindRotation(right[2], root.transform, bind);
                var leftLength = Length(left);
                var rightLength = Length(right);
                var count = Mathf.CeilToInt(clip.length * 60) + 1;
                var data = ScriptableObject.CreateInstance<MrxRawHands>();
                data.times = new float[count];
                data.left = new MrxRawHands.Arm[count];
                data.right = new MrxRawHands.Arm[count];
                for (var frame = 0; frame < count; frame++)
                {
                    var time = Mathf.Min(frame / 60f, clip.length);
                    clip.SampleAnimation(root, time);
                    data.times[frame] = time;
                    data.left[frame] = Read(left, leftLength, leftRest);
                    data.right[frame] = Read(right, rightLength, rightRest);
                }
                return data;
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
            }
        }

        private static Quaternion InverseBindRotation(Transform hand, Transform root, JToken reference)
        {
            var rotation = Quaternion.identity;
            for (var bone = hand; bone != root; bone = bone.parent)
            {
                var bind = reference[bone.name] ??
                    throw new InvalidDataException($"MMRP 기준 자세에 '{bone.name}' 본이 없습니다.");
                rotation = MrxHumanoidClip.BindRotation(bind) * rotation;
            }
            return Quaternion.Inverse(root.rotation * rotation);
        }
        private static float Length(Transform[] arm) => Vector3.Distance(arm[0].position, arm[1].position) +
            Vector3.Distance(arm[1].position, arm[2].position);

        private static MrxRawHands.Arm Read(Transform[] arm, float length, Quaternion rest) => new MrxRawHands.Arm
        {
            elbow = (arm[1].position - arm[0].position) / length,
            hand = (arm[2].position - arm[0].position) / length,
            rotation = arm[2].rotation * rest
        };
    }
    [MenuEntry("YAMO MMRP/원본 FBX 찾기")]
    internal sealed class MrxSourceClipAction : ClipAction
    {
        public override ActionValidity Validate(System.Collections.Generic.IEnumerable<TimelineClip> clips) =>
            clips.Any(clip => !string.IsNullOrEmpty(clip.GetRawFbx())) ? ActionValidity.Valid : ActionValidity.NotApplicable;

        public override bool Execute(System.Collections.Generic.IEnumerable<TimelineClip> clips)
        {
            var assets = clips.Select(clip => clip.GetRawFbx()).Where(path => !string.IsNullOrEmpty(path))
                .Distinct().Select(AssetDatabase.LoadMainAssetAtPath).Where(asset => asset != null).ToArray();
            if (assets.Length == 0)
                return false;
            Selection.objects = assets;
            EditorGUIUtility.PingObject(assets[0]);
            return true;
        }
    }
}
