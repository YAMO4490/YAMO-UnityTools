using System;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    /// <summary>
    /// Copy only an FBX and its importer settings. AssetDatabase.CopyAsset flushes
    /// unrelated dirty assets in Unity 6; importing a fresh GUID avoids that save.
    /// </summary>
    internal static class MocapFbxAssetCopy
    {
        internal static bool Copy(string source, string destination)
        {
            var assets = Path.GetFullPath(Application.dataPath).TrimEnd('\\', '/') + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(destination);
            if (!source.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ||
                !destination.EndsWith(".fbx", StringComparison.OrdinalIgnoreCase) ||
                !full.StartsWith(assets, StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("FBX 작업 복사본은 Assets 안의 새 .fbx 경로여야 합니다.");
            if (File.Exists(destination) || File.Exists(destination + ".meta") ||
                AssetDatabase.LoadMainAssetAtPath(destination) != null)
                throw new IOException("FBX 복사 대상이 이미 존재합니다: " + destination);
            var metadata = File.ReadAllText(source + ".meta");
            var guid = new Regex(@"(?m)^guid: [0-9a-fA-F]{32}(?=\r?$)");
            if (guid.Matches(metadata).Count != 1)
                throw new InvalidDataException("FBX importer의 원본 GUID를 결정할 수 없습니다: " + source);
            metadata = guid.Replace(metadata, "guid: " + Guid.NewGuid().ToString("N"));
            File.Copy(source, destination, false);
            File.WriteAllText(destination + ".meta", metadata, new UTF8Encoding(false));
            AssetDatabase.ImportAsset(destination, ImportAssetOptions.ForceUpdate | ImportAssetOptions.ForceSynchronousImport);
            if (!(AssetImporter.GetAtPath(destination) is ModelImporter))
                throw new InvalidDataException("FBX 작업 복사본 import 실패: " + destination);
            return true;
        }
    }
}
