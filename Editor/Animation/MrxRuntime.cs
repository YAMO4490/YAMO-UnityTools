using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace YAMO.UnityTools.Editor
{
    internal static class MrxRuntime
    {
        private const string Platform = "Windows-x64";
        private const string PackageAssetPath = "Packages/com.yamo.unitytools";
        private static string toolDirectory;

        internal static string InitializeOnMainThread()
        {
            var package = UnityEditor.PackageManager.PackageInfo.FindForAssetPath(PackageAssetPath);
            var root = package?.resolvedPath ?? Path.GetFullPath(PackageAssetPath);
            toolDirectory = Path.Combine(root, "Tools~", "Mrx");
            return Path.Combine(toolDirectory, "project_bundle.py");
        }
        private const string StreamingleAssets = "F:/Streamingle_URP/Assets";

        private static string ToolDirectory => toolDirectory ??
            Path.GetFullPath(Path.Combine(PackageAssetPath, "Tools~", "Mrx"));

        private static IEnumerable<string> RuntimeRoots()
        {
            var configured = Environment.GetEnvironmentVariable("YAMO_MRX_RUNTIME");
            if (!string.IsNullOrWhiteSpace(configured))
                yield return Path.GetFullPath(configured);

            yield return Path.Combine(ToolDirectory, "Runtime", Platform);
            yield return Path.GetFullPath(Path.Combine("Assets/StreamingAssets/StreamingleRecording", Platform));
            yield return Path.Combine(StreamingleAssets, "StreamingAssets/StreamingleRecording", Platform);
        }

        internal static string FindFile(string name) =>
            RuntimeRoots().Select(root => Path.Combine(root, name)).FirstOrDefault(File.Exists);

        internal static string MediaDependencies(string runtime)
        {
            var bundled = Path.Combine(runtime, "Media");
            if (File.Exists(Path.Combine(bundled, "avformat-63.dll")))
                return bundled;

            var assets = Path.GetFullPath(Path.Combine(runtime, "..", "..", ".."));
            var dependencies = Path.Combine(assets, "Internal/External Video Decoder/Plugins/x86_64");
            if (File.Exists(Path.Combine(dependencies, "avformat-63.dll")))
                return dependencies;

            throw new DirectoryNotFoundException("MRX audio decoder dependencies are missing. Install the matching Streamingle Media DLLs.");
        }

        internal static IEnumerable<string> PythonHomes()
        {
            foreach (var root in RuntimeRoots())
                yield return Path.Combine(root, "Python");

            foreach (var home in MrxNativeReader.PythonHomeCandidates())
                yield return home;

            if (Directory.Exists("Codex"))
                foreach (var date in Directory.EnumerateDirectories("Codex").OrderByDescending(path => path, StringComparer.Ordinal))
                    yield return Path.GetFullPath(Path.Combine(date, "Mrx", "python"));
        }
    }
}
