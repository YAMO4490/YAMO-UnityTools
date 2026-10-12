using System;
using System.IO;
using UnityEngine;

namespace YAMO.UnityTools
{
    // Frame-major, fixed-size samples. No clip-sized managed arrays are retained.
    public sealed class MocapPoseStream : IDisposable
    {
        public const int Magic = 0x594D5032;
        public const int BytesPerPose = 7 * sizeof(float);
        private BinaryWriter writer;
        public MocapPoseStream(string path, int frames, int rate, float duration, string[] paths)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path)));
            writer = new BinaryWriter(new FileStream(path, FileMode.Create, FileAccess.Write,
                FileShare.None, 64 * 1024));
            writer.Write(Magic); writer.Write(frames); writer.Write(paths.Length);
            writer.Write(rate); writer.Write(duration);
            foreach (var bonePath in paths) writer.Write(bonePath);
        }
        public void Write(Quaternion rotation, Vector3 position)
        {
            writer.Write(rotation.x); writer.Write(rotation.y); writer.Write(rotation.z); writer.Write(rotation.w);
            writer.Write(position.x); writer.Write(position.y); writer.Write(position.z);
        }
        public void Dispose() { writer?.Dispose(); writer = null; }
    }
}
