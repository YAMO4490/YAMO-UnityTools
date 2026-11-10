using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace YAMO.UnityTools.Editor
{
    /// <summary>Shared embedded CPython/MMRP reader. No child process or CLI fallback.</summary>
    internal static class MrxNativeReader
    {
        const string Library = "StreamingleMrxStreaming2.dll";
        static readonly object gate = new object();
        static ExportFn export;
        static string pythonHome, moduleDirectory;
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int AbiFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int ExportFn([MarshalAs(UnmanagedType.LPWStr)] string home, byte[] modules, byte[] source,
                              byte[] destination, byte[] ranges, byte[] metadata, int analysisOnly, IntPtr cancellation,
                              byte[] error, int capacity);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryEx(string name, IntPtr reserved, uint flags);
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr AddDllDirectory(string path);
        [DllImport("kernel32", SetLastError = true)]
        static extern bool RemoveDllDirectory(IntPtr cookie);
        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);
        [DllImport("advapi32.dll", CharSet = CharSet.Unicode, EntryPoint = "RegGetValueW")]
        static extern int RegGetValue(IntPtr root, string key, string value, uint flags, out uint type, StringBuilder data, ref uint bytes);
        static byte[] Utf8(string value) => Encoding.UTF8.GetBytes((value ?? "") + "\0");

        static string RegisteredPythonHome(IntPtr root, string tag)
        {
            var value = new StringBuilder(32768);
            uint bytes = (uint)value.Capacity * sizeof(char);
            // PEP 514: query the 64-bit PythonCore registration without launching python.exe.
            int result = RegGetValue(root, @"Software\Python\PythonCore\" + tag + @"\InstallPath", null,
                0x2 | 0x4 | 0x10000, out _, value, ref bytes);
            return result == 0 ? value.ToString() : null;
        }

        internal static IEnumerable<string> PythonHomeCandidates()
        {
            foreach (var root in new[] { new IntPtr(unchecked((int)0x80000001)), new IntPtr(unchecked((int)0x80000002)) })
                foreach (string tag in new[] { "3.14", "3.14-64" })
                    yield return RegisteredPythonHome(root, tag);
            string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            yield return Path.Combine(local, "Python", "pythoncore-3.14-64");
            yield return Path.Combine(local, "Programs", "Python", "Python314");
            yield return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "Python314");
        }

        static bool IsSupportedPythonHome(string candidate, out string home, out string reason)
        {
            home = null; reason = "No installation path was registered.";
            if (string.IsNullOrWhiteSpace(candidate)) return false;
            try
            {
                string full = Path.GetFullPath(candidate);
                string dll = Path.Combine(full, "python314.dll");
                if (!File.Exists(dll)) { reason = "python314.dll is missing."; return false; }
                using (var file = File.OpenRead(dll))
                using (var reader = new BinaryReader(file))
                {
                    if (file.Length < 0x40 || reader.ReadUInt16() != 0x5a4d)
                    { reason = "python314.dll is not a Windows PE DLL."; return false; }
                    file.Position = 0x3c;
                    uint header = reader.ReadUInt32();
                    if (header > file.Length - 26) { reason = "python314.dll has a truncated PE header."; return false; }
                    file.Position = header;
                    if (reader.ReadUInt32() != 0x00004550 || reader.ReadUInt16() != 0x8664)
                    { reason = "python314.dll must target Windows x64."; return false; }
                    file.Position = header + 24;
                    if (reader.ReadUInt16() != 0x20b)
                    { reason = "python314.dll must use the x64 PE32+ ABI."; return false; }
                }
                if (!File.Exists(Path.Combine(full, "Lib", "encodings", "__init__.py")) &&
                    !HasZippedStandardLibrary(full))
                { reason = "The Python standard library (Lib/encodings or python314.zip with encodings) is missing or invalid."; return false; }
                home = full; reason = null; return true;
            }
            catch (Exception error) when (error is IOException || error is UnauthorizedAccessException || error is ArgumentException || error is NotSupportedException)
            { reason = error.Message; return false; }
        }

        static bool HasZippedStandardLibrary(string home)
        {
            string path = Path.Combine(home, "python314.zip");
            if (!File.Exists(path)) return false;
            try
            {
                using (var archive = ZipFile.OpenRead(path))
                    return archive.GetEntry("encodings/__init__.py") != null ||
                           archive.GetEntry("encodings/__init__.pyc") != null;
            }
            catch (InvalidDataException) { return false; }
        }

        // Explicit overrides must fail as configured; never silently select another installation.
        internal static string SelectPythonHome(string configured, IEnumerable<string> candidates)
        {
            if (!string.IsNullOrWhiteSpace(configured))
            {
                if (IsSupportedPythonHome(configured, out string selected, out string reason)) return selected;
                throw new FileNotFoundException("YAMO_MRX_PYTHON_HOME is invalid: " + configured + ". " + reason);
            }
            foreach (string candidate in candidates)
                if (IsSupportedPythonHome(candidate, out string home, out _)) return home;
            throw new FileNotFoundException("MRX requires an installed CPython 3.14 x64 DLL runtime and standard library. " +
                "No supported PythonCore registration or default installation was found. Set YAMO_MRX_PYTHON_HOME to its installation directory.");
        }

        static void EnsureLoaded(CancellationToken cancellation)
        {
            cancellation.ThrowIfCancellationRequested();
            lock (gate)
            {
                cancellation.ThrowIfCancellationRequested();
                if (export != null)
                    return;
                if (Environment.OSVersion.Platform != PlatformID.Win32NT || IntPtr.Size != 8)
                    throw new PlatformNotSupportedException("The embedded MRX reader requires Windows x64.");
                string path = MrxRuntime.FindFile(Library);
                if (path == null)
                    throw new FileNotFoundException("The recording MRX reader DLL is missing: " + Library);
                pythonHome = SelectPythonHome(Environment.GetEnvironmentVariable("YAMO_MRX_PYTHON_HOME"), MrxRuntime.PythonHomes());
                moduleDirectory = Path.Combine(Path.GetDirectoryName(path), "MrxModules");
                if (!File.Exists(Path.Combine(moduleDirectory, "recording_archive.py")))
                    throw new FileNotFoundException("The MRX DLL's MMRP modules are missing: " + moduleDirectory);

                cancellation.ThrowIfCancellationRequested();
                IntPtr cookie = AddDllDirectory(pythonHome);
                if (cookie == IntPtr.Zero)
                    throw new IOException("MRX Python DLL search path failed (Win32 " + Marshal.GetLastWin32Error() + ").");
                try
                {
                    // The Python interpreter cannot safely be finalized across Unity domain
                    // reloads. Keep one native module reference for the entire host lifetime.
                    IntPtr module = GetModuleHandle(Library);
                    if (module == IntPtr.Zero)
                        module = LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x1000);
                    if (module == IntPtr.Zero)
                        throw new IOException("MRX DLL load failed (Win32 " + Marshal.GetLastWin32Error() + ").");
                    var abi = Marshal.GetDelegateForFunctionPointer<AbiFn>(GetProcAddress(module, "srmrx_abi_version"));
                    if (abi() != 3)
                        throw new InvalidDataException("Unsupported MRX native reader ABI.");
                    var nextExport = Marshal.GetDelegateForFunctionPointer<ExportFn>(GetProcAddress(module, "srmrx_export"));
                    export = nextExport;
                }
                finally
                {
                    if (cookie != IntPtr.Zero)
                        RemoveDllDirectory(cookie);
                }
            }
        }

        internal static void Export(string source, string destination)
        {
            EnsureLoaded(CancellationToken.None);
            var flag = Marshal.AllocHGlobal(sizeof(int));
            try
            {
                Marshal.WriteInt32(flag, 0);
                var error = new byte[8192];
                var code = export(pythonHome, Utf8(moduleDirectory), Utf8(source), Utf8(destination),
                    Utf8(null), Utf8(null), 0, flag, error, error.Length);
                if (code != 0)
                {
                    var end = Array.IndexOf(error, (byte)0);
                    throw new IOException("MRX native import: " + Encoding.UTF8.GetString(error, 0, end < 0 ? error.Length : end));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(flag);
            }
        }
    }
}
