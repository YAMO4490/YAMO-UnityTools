using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.Animations;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.Audio;
using UnityEngine.Playables;
using UnityEngine.SceneManagement;
using UnityEngine.Timeline;
using Object = UnityEngine.Object;

namespace YAMO.UnityTools.Editor
{
    internal static class MrxTimelineBipedExport
    {
        private const int DefaultRate = 60;

        internal static IEnumerable<AnimationTrack> FindTracks(TimelineAsset timeline) =>
            timeline.GetOutputTracks().OfType<AnimationTrack>()
                .Where(track => track.name.StartsWith("MRX · ", StringComparison.Ordinal) ||
                    track.GetClips().Any(clip => !string.IsNullOrEmpty(clip.GetRawFbx()) || FindRawHands(clip) != null));

        private static MrxRawHands FindRawHands(TimelineClip clip)
        {
            var animation = (clip.asset as AnimationPlayableAsset)?.clip;
            var path = AssetDatabase.GetAssetPath(animation);
            return string.IsNullOrEmpty(path) ? null :
                AssetDatabase.LoadAllAssetsAtPath(path).OfType<MrxRawHands>().SingleOrDefault();
        }

        internal static string[] ExportWholeTakes(PlayableDirector source, IReadOnlyList<TimelineClip> takes, string directory, int rate = DefaultRate)
        {
            if (!(source.playableAsset is TimelineAsset sourceTimeline))
                throw new InvalidOperationException("Timeline을 지정하세요.");

            var tracks = FindTracks(sourceTimeline).ToArray();
            var outputs = new List<string>();
            foreach (var take in takes)
            {
                var track = take.GetParentTrack() as AnimationTrack;
                var animator = track == null ? null : source.GetGenericBinding(track) as Animator;
                if (!tracks.Contains(track) || animator == null || animator.avatar == null || !animator.avatar.isValid ||
                    !animator.avatar.isHuman || string.IsNullOrEmpty(take.GetRawFbx()))
                    throw new InvalidOperationException("원본 FBX와 Humanoid Animator가 연결된 MRX 테이크를 선택하세요.");

                outputs.AddRange(ExportWholeTake(track, (AnimationPlayableAsset)take.asset, animator, directory, rate));
            }
            return outputs.ToArray();
        }

        private static string[] ExportWholeTake(AnimationTrack originalTrack, AnimationPlayableAsset originalClip,
            Animator animator, string directory, int rate)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            var timeline = ScriptableObject.CreateInstance<TimelineAsset>();
            var owner = new GameObject("MRX Whole Take");
            SceneManager.MoveGameObjectToScene(owner, scene);
            try
            {
                var director = owner.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                var track = timeline.CreateTrack<AnimationTrack>(null, originalTrack.name);
                track.trackOffset = originalTrack.trackOffset;
                track.position = originalTrack.position;
                track.rotation = originalTrack.rotation;
                var clip = track.CreateClip(originalClip.clip);
                clip.start = 0;
                var playable = (AnimationPlayableAsset)clip.asset;
                playable.removeStartOffset = originalClip.removeStartOffset;
                playable.applyFootIK = originalClip.applyFootIK;
                playable.position = originalClip.position;
                playable.rotation = originalClip.rotation;
                director.SetGenericBinding(track, animator);
                var stem = Path.GetFileNameWithoutExtension(clip.GetRawFbx());
                if (stem.EndsWith("_Raw", StringComparison.Ordinal))
                    stem = stem.Substring(0, stem.Length - 4);
                return Export(director, directory, new[] { track }, 0, timeline.duration, stem, rate);
            }
            finally
            {
                EditorSceneManager.ClosePreviewScene(scene);
                foreach (var track in timeline.GetOutputTracks().ToArray())
                {
                    foreach (var clip in track.GetClips().ToArray())
                        Object.DestroyImmediate(clip.asset);
                    Object.DestroyImmediate(track);
                }
                Object.DestroyImmediate(timeline);
            }
        }
        internal static string[] Export(PlayableDirector source, string directory,
            AnimationTrack[] selection, double rangeStart, double rangeEnd, string outputStem = null, int rate = DefaultRate)
        {
            if (AnimationMode.InAnimationMode())
                throw new InvalidOperationException("Timeline Preview를 끈 뒤 내보내세요.");
            if (!(source.playableAsset is TimelineAsset timeline))
                throw new InvalidOperationException("Timeline을 지정하세요.");

            var duration = rangeEnd - rangeStart;
            if (rate <= 0 || !(rangeStart >= 0 && rangeEnd <= timeline.duration && duration > 0) || duration * rate > 50000)
                throw new InvalidOperationException("Timeline 범위 안에서 유효한 시작과 끝을 지정하세요.");

            var tracks = FindTracks(timeline).Intersect(selection).ToArray();
            if (tracks.Length == 0)
                throw new InvalidOperationException("출력할 MRX 모션 트랙이 없습니다.");
            if (tracks.Any(track => track.mutedInHierarchy ||
                !track.GetClips().Any(clip => clip.start < rangeEnd && clip.end > rangeStart)))
                throw new InvalidOperationException("음소거된 트랙이나 출력 범위에 클립이 없는 트랙을 제외하세요.");

            var targets = tracks.Select(track => (track, avatar: source.GetGenericBinding(track) as Animator)).ToArray();
            if (targets.Any(item => item.avatar == null || item.avatar.avatar == null || !item.avatar.avatar.isHuman))
                throw new InvalidOperationException("모든 선택 트랙에 Humanoid Animator가 필요합니다.");
            var sources = new Dictionary<string, MrxRawHands>(StringComparer.OrdinalIgnoreCase);
            try
            {
                var raw = new Dictionary<TimelineClip, MrxRawHands>();
                foreach (var clip in tracks.SelectMany(track => track.GetClips()))
                {
                    var path = clip.GetRawFbx();
                    var hands = string.IsNullOrEmpty(path) ? FindRawHands(clip) : null;
                    if (!string.IsNullOrEmpty(path) && !sources.TryGetValue(path, out hands))
                    {
                        hands = MrxTimelineSource.ReadHands(path);
                        sources.Add(path, hands);
                    }
                    if (hands == null)
                        throw new InvalidOperationException($"'{clip.displayName}'의 Raw 손 데이터가 없습니다. MRX를 다시 가져오세요.");
                    raw.Add(clip, hands);
                }

                var outputs = new List<string>();
                var reserved = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var target in targets)
                {
                    var stem = string.Concat((outputStem ?? target.avatar.name).Select(c => Path.GetInvalidFileNameChars().Contains(c) ? '_' : c));
                    var path = Path.Combine(directory, stem + ".fbx");
                    for (var number = 2; File.Exists(path) || !reserved.Add(path); number++)
                        path = Path.Combine(directory, $"{stem}_{number}.fbx");
                    ExportTarget(timeline, target.avatar, new[] { target.track }, raw, rangeStart, duration, path, rate);
                    outputs.Add(path);
                }
                return outputs.ToArray();
            }
            finally
            {
                foreach (var hands in sources.Values)
                    Object.DestroyImmediate(hands);
            }
        }

        private static void ExportTarget(TimelineAsset timeline, Animator original,
            AnimationTrack[] tracks, Dictionary<TimelineClip, MrxRawHands> raw, double rangeStart, double duration, string path, int rate)
        {
            var scene = EditorSceneManager.NewPreviewScene();
            AnimationClip baked = null;
            GameObjectRecorder recorder = null;
            try
            {
                var copy = Object.Instantiate(original.gameObject);
                SceneManager.MoveGameObjectToScene(copy, scene);
                copy.SetActive(true);
                foreach (var script in copy.GetComponentsInChildren<MonoBehaviour>(true))
                    script.enabled = false;

                var animator = copy.GetComponent<Animator>();
                animator.runtimeAnimatorController = null;
                animator.cullingMode = AnimatorCullingMode.AlwaysAnimate;
                animator.enabled = true;
                animator.applyRootMotion = true;
                var biped = copy.GetComponentsInChildren<Transform>(true).SingleOrDefault(t => t.name == "Bip001");
                if (biped == null)
                    throw new InvalidOperationException($"{original.name}에 고유한 Bip001 루트가 필요합니다.");

                var directorRoot = new GameObject("MRX Export");
                SceneManager.MoveGameObjectToScene(directorRoot, scene);
                var director = directorRoot.AddComponent<PlayableDirector>();
                director.playableAsset = timeline;
                director.timeUpdateMode = DirectorUpdateMode.Manual;
                director.extrapolationMode = DirectorWrapMode.Hold;
                foreach (var track in tracks)
                    director.SetGenericBinding(track, animator);
                director.RebuildGraph();
                for (var i = 0; i < director.playableGraph.GetOutputCount(); i++)
                {
                    var output = director.playableGraph.GetOutput(i);
                    if (output.GetPlayableOutputType() == typeof(AudioPlayableOutput))
                        output.SetWeight(0);
                }

                var mapping = new Dictionary<Transform, Transform>();
                var skeleton = CloneBiped(biped, null, scene, mapping).gameObject;
                skeleton.AddComponent<Animator>();
                recorder = new GameObjectRecorder(skeleton);
                recorder.BindComponentsOfType<Transform>(skeleton, true);

                var left = new TargetArm(animator, true);
                var right = new TargetArm(animator, false);
                var clips = tracks.SelectMany(track => track.GetClips()).OrderBy(clip => clip.start).ToArray();
                var frames = Mathf.CeilToInt((float)(duration * rate));
                var previous = 0d;
                for (var frame = 0; frame <= frames; frame++)
                {
                    var time = Math.Min(duration, frame / (double)rate);
                    director.time = rangeStart + Math.Min(time, duration - 1e-7);
                    director.Evaluate();
                    if (TrySampleHands(clips, raw, director.time, out var l, out var r))
                    {
                        left.Apply(l);
                        right.Apply(r);
                    }

                    foreach (var pair in mapping)
                    {
                        if (pair.Key == biped)
                        {
                            pair.Value.SetPositionAndRotation(pair.Key.position, pair.Key.rotation);
                            pair.Value.localScale = pair.Key.lossyScale;
                        }
                        else
                        {
                            pair.Value.localPosition = pair.Key.localPosition;
                            pair.Value.localRotation = pair.Key.localRotation;
                            pair.Value.localScale = pair.Key.localScale;
                        }
                    }
                    recorder.TakeSnapshot((float)(time - previous));
                    previous = time;
                    if (EditorUtility.DisplayCancelableProgressBar("MRX Biped FBX", original.name, frame / (float)frames))
                        throw new OperationCanceledException();
                }

                baked = new AnimationClip { name = Path.GetFileNameWithoutExtension(path), frameRate = rate };
                recorder.SaveToClip(baked, rate);
                Debug.Log($"[MRX Biped FBX] {original.name}: {mapping.Count} bones, " +
                          $"max raw hand position error {Mathf.Max(left.MaxPositionError, right.MaxPositionError) * 1000:0.###} mm, " +
                          $"rotation error {Mathf.Max(left.MaxRotationError, right.MaxRotationError):0.###}°.");
                BipedFbxExportService.Export(new BipedFbxExportSettings
                {
                    TargetRoot = skeleton, Clip = baked, OutputPath = path, FrameRate = rate,
                    BipedBonesOnly = true, ExportGeometry = false, ExportUnrendered = true, RecordBlendShapes = false
                });
            }
            finally
            {
                if (recorder != null)
                    recorder.ResetRecording();
                if (baked != null)
                    Object.DestroyImmediate(baked);
                EditorSceneManager.ClosePreviewScene(scene);
                EditorUtility.ClearProgressBar();
            }
        }

        private static Transform CloneBiped(Transform source, Transform parent, Scene scene,
            Dictionary<Transform, Transform> mapping)
        {
            var copy = new GameObject(source.name).transform;
            SceneManager.MoveGameObjectToScene(copy.gameObject, scene);
            copy.SetParent(parent, false);
            mapping.Add(source, copy);
            foreach (Transform child in source)
                if (child.name.StartsWith("Bip001 ", StringComparison.Ordinal))
                    CloneBiped(child, copy, scene, mapping);
            return copy;
        }

        private static bool TrySampleHands(TimelineClip[] clips, Dictionary<TimelineClip, MrxRawHands> raw,
            double time, out MrxRawHands.Arm left, out MrxRawHands.Arm right)
        {
            left = right = default;
            var total = 0f;
            foreach (var clip in clips)
            {
                if (time < clip.start || time >= clip.end)
                    continue;
                var weight = clip.EvaluateMixIn(time) * clip.EvaluateMixOut(time);
                if (weight <= 0)
                    continue;
                var takeTime = clip.GetMrxSourceTime(time);
                total += weight;
                left = MrxRawHands.Arm.Blend(left, raw[clip].Sample(true, takeTime), weight / total);
                right = MrxRawHands.Arm.Blend(right, raw[clip].Sample(false, takeTime), weight / total);
            }
            return total > 0;
        }

        private sealed class TargetArm
        {
            private readonly Transform upper, lower, hand;
            private readonly Quaternion worldBasis, handRest;
            private readonly Vector3 hingeAxis = Vector3.forward;
            internal float MaxPositionError { get; private set; }
            internal float MaxRotationError { get; private set; }

            internal TargetArm(Animator animator, bool left)
            {
                upper = animator.GetBoneTransform(left ? HumanBodyBones.LeftUpperArm : HumanBodyBones.RightUpperArm);
                lower = animator.GetBoneTransform(left ? HumanBodyBones.LeftLowerArm : HumanBodyBones.RightLowerArm);
                hand = animator.GetBoneTransform(left ? HumanBodyBones.LeftHand : HumanBodyBones.RightHand);
                if (upper == null || lower == null || hand == null || lower.parent != upper || hand.parent != lower)
                    throw new InvalidOperationException("Biped 상완, 전완, 손이 직접 연결되어 있어야 합니다.");

                worldBasis = animator.transform.rotation;
                var rest = animator.avatar.humanDescription.skeleton.ToDictionary(bone => bone.name, bone => bone.rotation);
                var rotation = Quaternion.identity;
                for (var bone = hand; bone != animator.transform && bone != null; bone = bone.parent)
                    rotation = (rest.TryGetValue(bone.name, out var local) ? local : bone.localRotation) * rotation;
                handRest = rotation;
            }

            internal void Apply(MrxRawHands.Arm raw)
            {
                var shoulder = upper.position;
                var armLength = Vector3.Distance(shoulder, lower.position) + Vector3.Distance(lower.position, hand.position);
                var target = worldBasis * raw.hand * armLength;
                var pole = worldBasis * raw.elbow;
                if (target.sqrMagnitude < 1e-10f)
                    return;

                // Solve the permitted hinge angle from the requested shoulder-to-hand distance.
                // Any unreachable goal is projected to the closest reachable distance.
                var upperPose = upper.localRotation;
                var lowerPose = lower.localRotation;
                var handPose = hand.localRotation;
                YAMO.UnityTools.ForearmHingeCorrection.Apply(upper, lower, hand, hingeAxis, 1f);
                var referenceBend = lower.localEulerAngles.z;
                upper.localRotation = upperPose;
                lower.localRotation = lowerPose;
                hand.localRotation = handPose;

                lower.localRotation = Quaternion.identity;
                var offset = lower.position - shoulder;
                var axis = (upper.rotation * hingeAxis).normalized;
                var wrist = hand.position - lower.position;
                var center = Vector3.Project(wrist, axis);
                var radial = wrist - center;
                var tangent = Vector3.Cross(axis, radial);
                var baseline = offset + center;
                var a = 2 * Vector3.Dot(baseline, radial);
                var b = 2 * Vector3.Dot(baseline, tangent);
                var amplitude = Mathf.Sqrt(a * a + b * b);
                var phase = Mathf.Atan2(b, a);
                var bend = amplitude < 1e-8f ? 0f :
                    Mathf.Acos(Mathf.Clamp((target.sqrMagnitude - baseline.sqrMagnitude - radial.sqrMagnitude) / amplitude, -1, 1));
                var positive = (phase + bend) * Mathf.Rad2Deg;
                var negative = (phase - bend) * Mathf.Rad2Deg;
                var angle = Mathf.Abs(Mathf.DeltaAngle(referenceBend, positive)) <= Mathf.Abs(Mathf.DeltaAngle(referenceBend, negative))
                    ? positive : negative;
                lower.localRotation = Quaternion.AngleAxis(angle, hingeAxis);

                var from = hand.position - shoulder;
                if (from.sqrMagnitude > 1e-10f)
                {
                    upper.rotation = Quaternion.FromToRotation(from, target) * upper.rotation;
                    var direction = target.normalized;
                    var currentPole = Vector3.ProjectOnPlane(lower.position - shoulder, direction);
                    var goalPole = Vector3.ProjectOnPlane(pole, direction);
                    if (currentPole.sqrMagnitude > 1e-10f && goalPole.sqrMagnitude > 1e-10f)
                        upper.rotation = Quaternion.AngleAxis(Vector3.SignedAngle(currentPole, goalPole, direction), direction) * upper.rotation;
                }
                var rotation = worldBasis * raw.rotation * handRest;
                hand.rotation = rotation;
                MaxPositionError = Mathf.Max(MaxPositionError, Vector3.Distance(shoulder + target, hand.position));
                MaxRotationError = Mathf.Max(MaxRotationError, Quaternion.Angle(rotation, hand.rotation));
            }
        }
    }
}
