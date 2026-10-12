using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using UnityEditor;
using UnityEngine;

namespace YAMO.UnityTools.Editor
{
    internal static class MocapMemoryWork
    {
        private static readonly Stack<IEnumerator> work = new Stack<IEnumerator>();
        private static AsyncOperation waiting;
        private static Action<Exception> failed;
        private static Action continuation;
        static MocapMemoryWork() { AssemblyReloadEvents.beforeAssemblyReload += Stop; }
        public static void ContinueWith(Action next) { continuation = next; }
        public static void Run(IEnumerator routine, Action<Exception> onFailure)
        {
            if (work.Count != 0) throw new InvalidOperationException("Mocap 단계가 이미 실행 중입니다.");
            failed = onFailure; work.Push(routine);
            EditorApplication.update -= Tick; EditorApplication.update += Tick;
        }
        private static void Tick()
        {
            if (waiting != null && !waiting.isDone) return;
            waiting = null;
            try
            {
                if (work.Count == 0) { var next = continuation; Stop(); next?.Invoke(); return; }
                var top = work.Peek();
                if (!top.MoveNext()) { (work.Pop() as IDisposable)?.Dispose(); return; }
                if (top.Current is IEnumerator child) work.Push(child);
                else if (top.Current is AsyncOperation operation) waiting = operation;
            }
            catch (Exception exception)
            {
                var callback = failed; Stop(); callback?.Invoke(exception);
            }
        }
        private static void Stop()
        {
            EditorApplication.update -= Tick;
            while (work.Count != 0)
            {
                try { (work.Pop() as IDisposable)?.Dispose(); }
                catch (Exception exception) { UnityEngine.Debug.LogException(exception); }
            }
            waiting = null; failed = null; continuation = null;
        }
        public static IEnumerator Reclaim(string stage)
        {
            yield return null;
            yield return Resources.UnloadUnusedAssets();
            GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
            using (var process = Process.GetCurrentProcess())
                UnityEngine.Debug.Log($"[Mocap Pipeline] {stage}: Private={process.PrivateMemorySize64 / 1048576d:0} MiB, Unity={UnityEngine.Profiling.Profiler.GetTotalAllocatedMemoryLong() / 1048576d:0} MiB");
            yield return null;
        }
        [StructLayout(LayoutKind.Sequential)]
        private sealed class MemoryStatus
        {
            public uint Length = (uint)Marshal.SizeOf(typeof(MemoryStatus));
            public uint Load;
            public ulong TotalPhysical, AvailablePhysical, TotalPage, AvailablePage, TotalVirtual, AvailableVirtual, AvailableExtended;
        }
        [DllImport("kernel32.dll", SetLastError = true)]
        [return: MarshalAs(UnmanagedType.Bool)]
        private static extern bool GlobalMemoryStatusEx([In, Out] MemoryStatus status);
        public static void RequireHeadroom(string stage, long estimate)
        {
#if UNITY_EDITOR_WIN
            var status = new MemoryStatus();
            if (!GlobalMemoryStatusEx(status)) return;
            var required = checked(Math.Max(estimate, 256L * 1024 * 1024) + 512L * 1024 * 1024);
            if ((ulong)required > status.AvailablePhysical || (ulong)required > status.AvailablePage)
                throw new InsufficientMemoryException($"{stage}: 메모리 여유가 부족하여 작업을 중단했습니다. 예상 추가 사용 {required / 1048576d:0} MiB, 가용 RAM {status.AvailablePhysical / 1048576d:0} MiB. 출력 구간을 나누거나 다른 작업을 종료한 뒤 다시 실행하세요.");
#endif
        }
    }
}
