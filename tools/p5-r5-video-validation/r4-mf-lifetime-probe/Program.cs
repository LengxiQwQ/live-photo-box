using System.Reflection;
using System.Runtime.Loader;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Media.Workspace;

// Offline diagnostic consumer of the exact candidate public API. No codec or
// product lifetime policy is changed. Forced GC is diagnostic only.
internal static class Program
{
    static async Task Main(string[] args)
    {
        string runtime = Path.GetFullPath(args[0]);
        AssemblyLoadContext.Default.Resolving += (_, name) => {
            string path = Path.Combine(runtime, name.Name + ".dll");
            return File.Exists(path) ? AssemblyLoadContext.Default.LoadFromAssemblyPath(path) : null;
        };
        await Run(runtime, args[1], int.Parse(args[2]), args[3], args.Length > 4 ? args[4] : null);
    }

    static async Task Run(string runtime, string source, int count, string destination, string? probe)
    {
        source = Path.GetFullPath(source);
        bool stableCounter = probe == "stable";
        if (stableCounter) probe = null;
        Directory.CreateDirectory(destination);
        var campaign = AssemblyLoadContext.Default.LoadFromAssemblyPath(Path.Combine(runtime, "Campaign.dll"));
        var snapshot = campaign.GetType("Program", true)!.GetMethod("Snapshot", BindingFlags.Static | BindingFlags.NonPublic)!;
        int LegacySemaphores() => JsonSerializer.SerializeToElement(snapshot.Invoke(null, null)).GetProperty("semaphores").GetInt32();
        // Warm both reflection/JIT paths before comparing owned-handle deltas.
        LegacySemaphores(); StableCounters.Semaphores();
        LegacySemaphores(); StableCounters.Semaphores();
        int before = LegacySemaphores(), stableBefore = StableCounters.Semaphores();
        if (stableBefore != before) throw new InvalidOperationException($"Counter baseline mismatch legacy={before} stable={stableBefore}");
        using (var first = new Semaphore(0, 1))
        using (var second = new Semaphore(0, 1))
            if (StableCounters.Semaphores() != before + 2 || LegacySemaphores() != before + 2)
                throw new InvalidOperationException("Counter known-delta mismatch");
        if (StableCounters.Semaphores() != before) throw new InvalidOperationException("Counter release mismatch");
        using var output = new StreamWriter(new FileStream(Path.Combine(destination, "samples.jsonl"), FileMode.CreateNew));
        TraceMark? mark = null;
        TraceStop? stop = null;
        if (probe is not null)
        {
            var module = NativeLibrary.Load(Path.GetFullPath(probe));
            var start = Marshal.GetDelegateForFunctionPointer<TraceStart>(NativeLibrary.GetExport(module, "TraceStart"));
            var control = Marshal.GetDelegateForFunctionPointer<TraceControl>(NativeLibrary.GetExport(module, "TraceControl"));
            mark = Marshal.GetDelegateForFunctionPointer<TraceMark>(NativeLibrary.GetExport(module, "TraceMark"));
            stop = Marshal.GetDelegateForFunctionPointer<TraceStop>(NativeLibrary.GetExport(module, "TraceStop"));
            if (start(131072) != 0 || control() != 0) throw new InvalidOperationException("Diagnostic trace initialization");
        }
        int checkpoint = 0;
        void Observe(string stage, int iteration = -1)
        {
            mark?.Invoke(iteration, checkpoint);
            var memory = GC.GetGCMemoryInfo();
            output.WriteLine(JsonSerializer.Serialize(new { stage, iteration, checkpoint = checkpoint++, stableCounter,
                resources = stableCounter ? StableCounters.Snapshot() : snapshot.Invoke(null, null),
                modules = probe is null ? null : System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>()
                    .Select(m => new { m.ModuleName, m.FileName, address = m.BaseAddress.ToInt64(), m.ModuleMemorySize }).ToArray(),
                virtualMemory = VirtualStats.ReadAll(), gc = new { memory.TotalCommittedBytes, memory.HeapSizeBytes,
                    memory.FragmentedBytes, collection0 = GC.CollectionCount(0), collection1 = GC.CollectionCount(1), collection2 = GC.CollectionCount(2) } }));
            output.Flush();
        }
        try {
        Observe("initial");
        for (int i = 0; i < count; i++)
        {
            await ConvertOne(source);
            Observe("disposed", i);
        }
        for (int i = 0; i <= 60; i++)
        {
            if (i != 0) await Task.Delay(1000);
            Observe("natural-quiet", i);
        }
        Observe("before-diagnostic-gc");
        GC.Collect(); Observe("after-first-collect");
        GC.WaitForPendingFinalizers(); Observe("after-wait-finalizers");
        GC.Collect(); Observe("after-second-collect");
        for (int i = 1; i <= 5; i++) { await Task.Delay(1000); Observe("after-gc-quiet", i); }
        }
        finally { if (stop is not null && stop(Path.Combine(destination, "vm-trace.bin")) != 0) throw new InvalidOperationException("Diagnostic trace detach/write"); }
    }

    // Keep operation locals outside the GC-observation method's async state
    // machine, so retaining a local result cannot bias the lifetime diagnosis.
    static async Task ConvertOne(string source)
    {
        string root;
        using (var workspace = new MediaWorkspace())
        {
            root = workspace.RootDirectory;
            var result = await new VideoConverter().ConvertAsync(new VideoConversionRequest {
                SourceArtifact = new MediaArtifact { Path = source, Kind = MediaArtifactKind.MotionVideo,
                    VideoContainer = VideoContainer.Mp4, VideoCodec = VideoCodec.H264, ByteLength = new FileInfo(source).Length },
                TargetContainer = VideoContainer.Mp4, TargetCodec = VideoCodec.H264, TargetDirectory = root });
            if (!result.Success || result.OutputArtifact is null || result.ExecutionRecord.Truth.BackendName != "WindowsMediaFoundation" ||
                result.ExecutionRecord.Truth.FallbackOccurred) throw new InvalidOperationException(result.ErrorMessage ?? "MF control mismatch");
            using var stream = File.OpenRead(result.OutputArtifact.Path);
            if (Convert.ToHexString(SHA256.HashData(stream)) != result.OutputArtifact.Sha256)
                throw new InvalidOperationException("Output identity mismatch");
        }
        if (Directory.Exists(root)) throw new InvalidOperationException("Workspace residue");
    }
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int TraceStart(ulong capacity);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate int TraceControl();
    [UnmanagedFunctionPointer(CallingConvention.Cdecl)] delegate void TraceMark(int iteration, int checkpoint);
    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Unicode)] delegate int TraceStop([MarshalAs(UnmanagedType.LPWStr)] string path);
}
