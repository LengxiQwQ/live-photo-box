using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Runtime.InteropServices;

// Offline calibration: invoke the exact, already-built campaign Snapshot
// implementation without running any product operation or loading a codec.
string runtime = Path.GetFullPath(args[0]);
int count = int.Parse(args[1]), delay = int.Parse(args[2]);
string destination = Path.GetFullPath(args[3]);
Directory.CreateDirectory(destination);
AssemblyLoadContext.Default.Resolving += (_, name) =>
{
    string path = Path.Combine(Path.GetDirectoryName(runtime)!, name.Name + ".dll");
    return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
};
Assembly campaign = AssemblyLoadContext.Default.LoadFromAssemblyPath(runtime);
MethodInfo snapshot = campaign.GetType("Program", true)!.GetMethod("Snapshot", BindingFlags.Static | BindingFlags.NonPublic)!;
using var output = new StreamWriter(new FileStream(Path.Combine(destination, "samples.jsonl"), FileMode.CreateNew));
if (args.Length > 4)
{
    string samples = Path.GetFullPath(args[4]);
    bool trace = args.Length > 5 && args[5] == "vmtrace";
    Action<int, int>? mark = null;
    Func<string, int>? stop = null;
    if (trace)
    {
        nint module = NativeLibrary.Load(Path.GetFullPath(args[6]));
        var start = Marshal.GetDelegateForFunctionPointer<TraceStart>(NativeLibrary.GetExport(module, "TraceStart"));
        var control = Marshal.GetDelegateForFunctionPointer<TraceControl>(NativeLibrary.GetExport(module, "TraceControl"));
        var nativeMark = Marshal.GetDelegateForFunctionPointer<TraceMark>(NativeLibrary.GetExport(module, "TraceMark"));
        var nativeStop = Marshal.GetDelegateForFunctionPointer<TraceStop>(NativeLibrary.GetExport(module, "TraceStop"));
        var faultLog = Marshal.GetDelegateForFunctionPointer<TraceStop>(NativeLibrary.GetExport(module, "TraceFaultLog"));
        if (faultLog(Path.Combine(destination, "faults.bin")) != 0) throw new InvalidOperationException("Trace fault log failed.");
        if (start(131072) != 0 || control() != 0) throw new InvalidOperationException("Trace/control initialization failed.");
        mark = (r, s) => nativeMark(r, s);
        stop = path => nativeStop(path);
    }
    int round = -1;
    int checkpoint = 0;
    string mode = args.Length > 5 ? args[5] : "heap";
    bool withoutMf = mode == "nomf";
    void Observe(string stage)
    {
        mark?.Invoke(round, checkpoint);
        var memory = GC.GetGCMemoryInfo();
        output.WriteLine(JsonSerializer.Serialize(new { round, stage, checkpoint = checkpoint++, resources = snapshot.Invoke(null, null),
            gc = new { memory.TotalCommittedBytes, memory.HeapSizeBytes, memory.FragmentedBytes, memory.MemoryLoadBytes },
            virtualMemory = VirtualStats.ReadAll(),
            modules = trace ? System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>().Select(m => new { m.ModuleName, m.FileName, address = m.BaseAddress.ToInt64(), m.ModuleMemorySize }).ToArray() : null,
            heaps = args.Length > 5 ? null : HeapStats.ReadAll() }));
        output.Flush();
        // Interrupt only this offline harness after all five non-MF product
        // calls completed. CodecRound's using scope still disposes normally.
        if (withoutMf && stage == "hevc8") throw new CompletedNonMfRoutes();
    }
    campaign.GetType("Program", true)!.GetField("StageObserver", BindingFlags.Static | BindingFlags.NonPublic)!.SetValue(null, (Action<string>)Observe);
    MethodInfo codecs = campaign.GetType("Program", true)!.GetMethod("CodecRound", BindingFlags.Static | BindingFlags.NonPublic)!;
    try
    {
        Observe("initial");
        if (mode == "mfonly")
        {
            MethodInfo repeat = campaign.GetType("Program", true)!.GetMethod("MfRepeat", BindingFlags.Static | BindingFlags.NonPublic)!;
            await (Task<int>)repeat.Invoke(null, new object?[] {
                new[] { "h264-control", samples, Path.Combine(destination, "repeat"), Path.Combine(samples, "vivo双文件.mp4") }, count, true })!;
            Observe("mf-repeat-after-diagnostic-gc");
            return;
        }
        for (round = 0; round < count; round++)
        {
            try { await (Task<object>)codecs.Invoke(null, new object?[] { samples, null })!; }
            catch (CompletedNonMfRoutes) when (withoutMf) { }
            Observe("disposed");
        }
        if (withoutMf)
            for (int quiet = 0; quiet <= 60; quiet++)
            {
                if (quiet != 0) await Task.Delay(1000);
                Observe("quiet-" + quiet);
            }
    }
    finally { if (stop is not null && stop(Path.Combine(destination, "vm-trace.bin")) != 0) throw new InvalidOperationException("Trace detach/write failed."); }
    return;
}

for (int i = 0; i < count; i++)
{
    output.WriteLine(JsonSerializer.Serialize(new { index = i, resources = snapshot.Invoke(null, null) }));
    output.Flush();
    if (delay > 0) await Task.Delay(delay);
}

[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int TraceStart(ulong capacity);
internal sealed class CompletedNonMfRoutes : Exception;
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate int TraceControl();
[UnmanagedFunctionPointer(CallingConvention.Cdecl)] internal delegate void TraceMark(int round, int stage);
[UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] internal delegate int TraceStop([MarshalAs(UnmanagedType.LPWStr)] string path);
