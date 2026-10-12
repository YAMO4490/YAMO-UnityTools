using System;
using System.Collections;
using System.IO;
using UnityEngine;
using YAMO.UnityTools;

namespace YAMO.UnityTools.Editor
{
    internal static class MocapPoseClipBuilder
    {
        public static IEnumerator Build(string path, string name, Func<string, float, bool> progress,
            Action<ForearmHingeBakeResult> completed)
        {
            var spool = path + ".bones";
            AnimationClip clip = null;
            try
            {
                int frames, bones, rate; float duration; string[] paths;
                using (var reader = new BinaryReader(File.OpenRead(path)))
                {
                    if (reader.ReadInt32() != MocapPoseStream.Magic) throw new InvalidDataException("새 Pose Stream 형식이 아닙니다.");
                    frames = reader.ReadInt32(); bones = reader.ReadInt32(); rate = reader.ReadInt32(); duration = reader.ReadSingle();
                    if (frames < 2 || bones < 1 || bones > 512 || rate < 1 || duration <= 0f || float.IsNaN(duration) || float.IsInfinity(duration))
                        throw new InvalidDataException("Pose Stream 헤더가 올바르지 않습니다.");
                    paths = new string[bones]; for (var b = 0; b < bones; b++) paths[b] = reader.ReadString();
                    var bytes = checked((long)frames * bones * MocapPoseStream.BytesPerPose);
                    if (reader.BaseStream.Length - reader.BaseStream.Position != bytes) throw new InvalidDataException("Pose Stream 파일이 잘렸거나 길이가 다릅니다.");
                    if (new DriveInfo(Path.GetPathRoot(Path.GetFullPath(spool))).AvailableFreeSpace < bytes + 64L * 1024 * 1024)
                        throw new IOException("Pose Stream 변환에 필요한 임시 디스크 공간이 부족합니다.");
                    MocapMemoryWork.RequireHeadroom("AnimationClip 생성", checked((long)frames * bones * 7 * 32 * 2));
                    const int blockFrames = 256;
                    var block = new byte[checked(blockFrames * bones * MocapPoseStream.BytesPerPose)];
                    var boneBlock = new byte[blockFrames * MocapPoseStream.BytesPerPose];
                    using (var output = new FileStream(spool, FileMode.Create, FileAccess.Write, FileShare.None))
                    {
                        output.SetLength(bytes);
                        for (var start = 0; start < frames; start += blockFrames)
                        {
                            var count = Math.Min(blockFrames, frames - start);
                            var size = count * bones * MocapPoseStream.BytesPerPose;
                            var read = 0;
                            while (read < size) { var n = reader.BaseStream.Read(block, read, size - read); if (n == 0) throw new EndOfStreamException(); read += n; }
                            for (var b = 0; b < bones; b++)
                            {
                                for (var f = 0; f < count; f++) Buffer.BlockCopy(block, (f * bones + b) * MocapPoseStream.BytesPerPose, boneBlock, f * MocapPoseStream.BytesPerPose, MocapPoseStream.BytesPerPose);
                                output.Position = ((long)b * frames + start) * MocapPoseStream.BytesPerPose;
                                output.Write(boneBlock, 0, count * MocapPoseStream.BytesPerPose);
                            }
                            Cancel(progress, "녹화 파일 정리", start / (float)frames * 0.2f);
                            yield return null;
                        }
                    }
                }
                clip = new AnimationClip { name = name, frameRate = rate, hideFlags = HideFlags.HideAndDontSave };
                var properties = new[] { "localRotation.x", "localRotation.y", "localRotation.z", "localRotation.w", "localPosition.x", "localPosition.y", "localPosition.z" };
                using (var reader = new BinaryReader(File.OpenRead(spool)))
                {
                    for (var b = 0; b < bones; b++)
                    {
                        var poses = new float[checked(frames * 7)];
                        var previous = Quaternion.identity; var firstPosition = Vector3.zero; var animated = false;
                        for (var f = 0; f < frames; f++)
                        {
                            var offset = f * 7; for (var c = 0; c < 7; c++) poses[offset + c] = reader.ReadSingle();
                            var q = new Quaternion(poses[offset], poses[offset + 1], poses[offset + 2], poses[offset + 3]);
                            if (f > 0 && Quaternion.Dot(previous, q) < 0f) for (var c = 0; c < 4; c++) poses[offset + c] = -poses[offset + c];
                            previous = new Quaternion(poses[offset], poses[offset + 1], poses[offset + 2], poses[offset + 3]);
                            var p = new Vector3(poses[offset + 4], poses[offset + 5], poses[offset + 6]);
                            if (f == 0) firstPosition = p; else if ((p - firstPosition).sqrMagnitude > 1e-6f) animated = true;
                            if (f % 2048 == 0) { Cancel(progress, $"본 {b + 1}/{bones} 읽기", 0.2f + 0.8f * b / bones); yield return null; }
                        }
                        for (var c = 0; c < 7; c++)
                        {
                            // Preserve fixed bone offsets explicitly through FBX export.
                            var constantPosition = c >= 4 && !animated;
                            var keys = constantPosition
                                ? new[] { new Keyframe(0f, poses[c]), new Keyframe(duration, poses[c]) }
                                : new Keyframe[frames];
                            if (!constantPosition)
                                for (var f = 0; f < frames; f++) keys[f] = new Keyframe(Mathf.Min(f / (float)rate, duration), poses[f * 7 + c]);
                            clip.SetCurve(paths[b], typeof(Transform), properties[c], new AnimationCurve(keys));
                            keys = null;
                            Cancel(progress, $"본 {b + 1}/{bones} 커브 생성", 0.2f + 0.8f * (b + (c + 1f) / 7f) / bones);
                            yield return null;
                        }
                        poses = null;
                    }
                }
                clip.EnsureQuaternionContinuity();
                completed(new ForearmHingeBakeResult { Clip = clip, FrameCount = frames, BoneCount = bones });
                clip = null;
            }
            finally
            {
                if (clip != null) UnityEngine.Object.DestroyImmediate(clip);
                if (File.Exists(spool)) File.Delete(spool);
            }
        }
        private static void Cancel(Func<string, float, bool> progress, string message, float value)
        {
            if (progress?.Invoke(message, value) == true) throw new OperationCanceledException("클립 생성이 취소되었습니다.");
        }
    }
}
