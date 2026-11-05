using System;
using System.Runtime.InteropServices;
using System.Threading;

namespace LivePhotoBox.Services;

/// <summary>A dedicated OS thread owns the process deadline, not the UI or thread pool.</summary>
internal sealed class ShutdownDeadline
{
    private readonly ManualResetEvent _exiting = new(false);
    internal ShutdownDeadline(TimeSpan timeout, Action timeoutDiagnostic)
    {
        new Thread(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            if (_exiting.WaitOne(timeout - TimeSpan.FromMilliseconds(100))) return;
            new Thread(() => { try { timeoutDiagnostic(); } catch { } })
                { IsBackground = true, Name = "LPB shutdown timeout log" }.Start();
            var remaining = timeout - clock.Elapsed;
            if (remaining > TimeSpan.Zero && _exiting.WaitOne(remaining)) return;
            TerminateProcess(GetCurrentProcess(), 1460);
        }) { IsBackground = true, Name = "LPB shutdown deadline" }.Start();
    }
    internal void ProcessExiting() => _exiting.Set();
    [DllImport("kernel32.dll")] private static extern IntPtr GetCurrentProcess();
    [DllImport("kernel32.dll")] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(IntPtr process, uint exitCode);
}
