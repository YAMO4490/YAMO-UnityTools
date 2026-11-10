using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;

namespace YAMO.UnityTools.Editor
{
    /// <summary>Native MRX media decoding, shared with the final render encoder DLL.</summary>
    internal static class MrxNativeAudio
    {
        static readonly object gate = new object();
        static bool loaded;
        static ErrorFn error;
        static AudioFn audio;
        static AudioClockFn audioClock; // null on DLLs without MMRP audio clock (drift) support
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int AudioFn(byte[] input, byte[] output, byte[] codec, long trim, long delay, long duration,
                             IntPtr cancellation);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int AudioClockFn(byte[] input, byte[] output, byte[] codec, long trim, long delay, long duration,
                                  IntPtr cancellation, int outputChannels, double ratio);
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate IntPtr ErrorFn();
        [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
        delegate int AbiFn();
        [DllImport("kernel32", CharSet = CharSet.Unicode, SetLastError = true)]
        static extern IntPtr LoadLibraryEx(string path, IntPtr reserved, uint flags);
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32", CharSet = CharSet.Unicode)]
        static extern IntPtr AddDllDirectory(string path);
        [DllImport("kernel32")]
        static extern bool RemoveDllDirectory(IntPtr cookie);
        [DllImport("kernel32", CharSet = CharSet.Ansi)]
        static extern IntPtr GetProcAddress(IntPtr module, string name);
        static T Function<T>(IntPtr module, string name)
            where T : Delegate => Marshal.GetDelegateForFunctionPointer<T>(GetProcAddress(module,
                      name));

        static void Load()
        {
            lock (gate)
            {
                if (loaded)
                    return;
                const string name = "StreamingleVideoEncoder2.dll";
                string path = MrxRuntime.FindFile(name) ??
                              throw new FileNotFoundException("Native recording media DLL missing.");
                string dependencies = MrxRuntime.MediaDependencies(Path.GetDirectoryName(path));
                IntPtr cookie = AddDllDirectory(dependencies);
                if (cookie == IntPtr.Zero)
                    throw new IOException("Could not register the MRX audio dependency directory.");
                try
                {
                    IntPtr module = GetModuleHandle(name);
                    if (module == IntPtr.Zero)
                        module = LoadLibraryEx(path, IntPtr.Zero, 0x100 | 0x1000);
                    if (module == IntPtr.Zero)
                        throw new IOException("Recording media DLL load failed (Win32 " + Marshal.GetLastWin32Error() +
                                              ").");
                    if (Function<AbiFn>(module, "srvideo_abi_version")() != 1)
                        throw new InvalidDataException("Recording media DLL ABI mismatch.");


                    error = Function<ErrorFn>(module, "srvideo_error");

                    audio = Function<AudioFn>(module, "srvideo_audio");
                    IntPtr audioClockAddress = GetProcAddress(module, "srvideo_audio_clock");
                    audioClock = audioClockAddress == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<AudioClockFn>(audioClockAddress);
                    loaded = true;
                }
                finally
                {
                    if (cookie != IntPtr.Zero)
                        RemoveDllDirectory(cookie);
                }
            }
        }
        /// <summary>48 kHz stereo PCM. <paramref name="clockRatio"/> is the MRX audio clock ratio; trim and
        /// duration are session time.</summary>
        public static void DecodePreviewAudio(string input, string output, CancellationToken cancellation, ulong trimNs = 0, ulong durationNs = 0,
                                              double clockRatio = 1.0)
        {
            Load();
            if (clockRatio != 1.0 && audioClock == null)
                throw new InvalidDataException("StreamingleVideoEncoder2.dll predates srvideo_audio_clock; rebuild the recording native DLLs.");
            cancellation.ThrowIfCancellationRequested();
            IntPtr flag = Marshal.AllocHGlobal(4);
            Marshal.WriteInt32(flag, 0);
            try
            {
                using (cancellation.Register(() => Marshal.WriteInt32(flag, 1)))
                {
                    int code = clockRatio == 1.0
                        ? audio(Encoding.UTF8.GetBytes(input + "\0"), Encoding.UTF8.GetBytes(output + "\0"),
                                Encoding.UTF8.GetBytes("pcm_s16le\0"), checked((long)trimNs), 0, checked((long)durationNs), flag)
                        : audioClock(Encoding.UTF8.GetBytes(input + "\0"), Encoding.UTF8.GetBytes(output + "\0"),
                                     Encoding.UTF8.GetBytes("pcm_s16le\0"), checked((long)trimNs), 0, checked((long)durationNs), flag,
                                     2, clockRatio);
                    cancellation.ThrowIfCancellationRequested();
                    if (code != 0)
                        throw new IOException(Marshal.PtrToStringUTF8(error()));
                }
            }
            finally
            {
                Marshal.FreeHGlobal(flag);
            }
        }
    }
}
