using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Serialization;
using LivePhotoBox.Models;
using LivePhotoBox.Services;

internal static partial class Program
{
    internal static readonly JsonSerializerOptions Json = new() { WriteIndented = true,
        Converters = { new JsonStringEnumConverter() } };

    public static int Main(string[] args)
    {
        return args[0] switch
        {
            "pause" => Pause(Path.GetFullPath(args[1])),
            "prepare" => Prepare(Path.GetFullPath(args[1]), Path.GetFullPath(args[2])),
            "inspect" => InspectLarge(args).GetAwaiter().GetResult(),
            "large" => Large(args).GetAwaiter().GetResult(),
            "qualify" => Mixed(args, measuredMinutes: 0).GetAwaiter().GetResult(),
            "soak" => Mixed(args, measuredMinutes: 30).GetAwaiter().GetResult(),
            "mf-repeat" => MfRepeat(args).GetAwaiter().GetResult(),
            "mf-once" => MfRepeat(args, repetitions: 1).GetAwaiter().GetResult(),
            "h264-control" => MfRepeat(args, repetitions: 1, expectMediaFoundation: true).GetAwaiter().GetResult(),
            "mf-quiet" => MfQuiet(args).GetAwaiter().GetResult(),
            "isolate" => Isolate(args).GetAwaiter().GetResult(),
            _ => throw new ArgumentException("Unknown campaign mode.")
        };
    }

    private static int Pause(string directory)
    {
        Directory.CreateDirectory(directory);
        ThreadPool.GetMinThreads(out int originalWorkers, out int originalIo);
        ThreadPool.SetMinThreads(Math.Max(32, originalWorkers), originalIo);
        var events = new List<ManualResetEventSlim>();
        var tokens = new List<CancellationTokenSource>();
        var tasks = new List<Task>();
        int started = 0;
        object baseline = Snapshot();
        int initialActive = ActiveWorkers();
        try
        {
            for (int i = 0; i < 12; i++)
            {
                var pause = new ManualResetEventSlim(false); events.Add(pause);
                var cancel = new CancellationTokenSource(); tokens.Add(cancel);
                tasks.Add(LivePhotoMergeRunnerService.RunAsync(
                    new IMergeTaskInfo[] { new Pair(i, "unused.jpg", "unused.mov", "paused") },
                    new LivePhotoMergeRunOptions { OutputDirectory = directory,
                        OutputProfile = LivePhotoBox.Services.ProductSelectionAdapter.Merge(2, 0), MaxDegreeOfParallelism = 1 },
                    pause, cancel.Token, _ => Interlocked.Increment(ref started), null));
            }
            bool queueConsumed = SpinWait.SpinUntil(() => ThreadPool.PendingWorkItemCount == 0, 5000);
            Thread.Sleep(100); // Coordination observation, excluded from soak time.
            object whilePaused = Snapshot();
            foreach (var cancel in tokens) cancel.Cancel();
            try { Task.WhenAll(tasks).GetAwaiter().GetResult(); }
            catch (OperationCanceledException) { }
            bool settled = SpinWait.SpinUntil(() => ActiveWorkers() <= initialActive, 3000);
            object afterCancellationBeforeAnySet = Snapshot();
            bool passed = queueConsumed && settled && started == 0 &&
                tasks.All(t => t.IsCanceled) && events.All(e => !e.IsSet) &&
                !Directory.EnumerateFiles(directory).Any();
            Save(Path.Combine(directory, "pause.json"), new { passed, initialActive,
                baseline, whilePaused, afterCancellationBeforeAnySet, started,
                cancelled = tasks.Count(t => t.IsCanceled), eventsStillReset = events.All(e => !e.IsSet),
                observation = "Worker release was tested before any Set or process exit." });
            return passed ? 0 : 3;
        }
        finally
        {
            // Failure diagnostic rescue is deliberately after persisted verdict.
            foreach (var pause in events) pause.Set();
            SpinWait.SpinUntil(() => ActiveWorkers() <= initialActive, 3000);
            foreach (var cancel in tokens) cancel.Dispose();
            foreach (var pause in events) pause.Dispose();
            ThreadPool.SetMinThreads(originalWorkers, originalIo);
        }
    }

    internal static int ActiveWorkers()
    {
        ThreadPool.GetMaxThreads(out int maximum, out _);
        ThreadPool.GetAvailableThreads(out int available, out _);
        return maximum - available;
    }

    internal static object Snapshot()
    {
        using var process = Process.GetCurrentProcess(); process.Refresh();
        return new { utc = DateTime.UtcNow, privateBytes = process.PrivateMemorySize64,
            workingSet = process.WorkingSet64, managedBytes = GC.GetTotalMemory(false),
            handles = process.HandleCount, threads = process.Threads.Count, semaphores = SemaphoreCount(),
            poolThreads = ThreadPool.ThreadCount, activeWorkers = ActiveWorkers(),
            pendingWorkItems = ThreadPool.PendingWorkItemCount };
    }

    internal static void Save(string path, object value)
    {
        using var stream = new FileStream(path, FileMode.CreateNew);
        JsonSerializer.Serialize(stream, value, Json);
    }

    internal sealed class Pair(int index, string image, string video, string name) : IMergeTaskInfo
    {
        public int Index => index;
        public string ImagePath => image;
        public string VideoPath => video;
        public string BaseName => name;
        public ProcessStatus Status { get; set; }
        public string Details { get; set; } = string.Empty;
    }
}
