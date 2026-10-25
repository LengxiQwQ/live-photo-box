using System.Collections.Concurrent;
using System.Diagnostics;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P8MergeQueueTests
{
    [Theory]
    [InlineData(-1L)]
    [InlineData(4294967295L)]
    public async Task InvalidInterval_RejectsBeforeOutputCreation(long milliseconds)
    {
        using var workspace = new TestFixtureWorkspace();
        string output = Path.Combine(workspace.RootDirectory, "not-created");
        using var pause = new ManualResetEventSlim(true);
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LivePhotoMergeRunnerService.RunAsync(
            [], Options(output, TimeSpan.FromMilliseconds(milliseconds)), pause, CancellationToken.None, null, null));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task BatchInterval_DelaysNextBatchStartAndCountsEveryFailureOnce()
    {
        using var workspace = new TestFixtureWorkspace();
        using var pause = new ManualResetEventSlim(true);
        var elapsed = Stopwatch.StartNew();
        var starts = new ConcurrentQueue<(int Index, TimeSpan Elapsed)>();
        var completions = new ConcurrentQueue<(int Index, bool Success, int Count)>();
        TimeSpan interval = TimeSpan.FromMilliseconds(400);
        await LivePhotoMergeRunnerService.RunAsync(MissingTasks(workspace.RootDirectory, 2),
            Options(workspace.RootDirectory, interval), pause, CancellationToken.None,
            task => starts.Enqueue((task.Index, elapsed.Elapsed)),
            (task, success, _, count) => completions.Enqueue((task.Index, success, count)));
        Assert.Equal(new[] { 1, 2 }, starts.Select(row => row.Index));
        // Compare actual callbacks so Native startup time cannot accidentally
        // satisfy the interval. Allow only a small timer/callback observation gap.
        Assert.True(starts.Last().Elapsed - starts.First().Elapsed >= interval - TimeSpan.FromMilliseconds(10));
        Assert.Equal(new[] { 1, 2 }, completions.Select(row => row.Count));
        Assert.All(completions, row => Assert.False(row.Success));
    }

    [Fact]
    public async Task PauseAfterFirstBatch_ResumesWithoutDuplicateCallbacks()
    {
        using var workspace = new TestFixtureWorkspace();
        using var pause = new ManualResetEventSlim(true);
        var paused = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = new ConcurrentQueue<int>();
        var completions = new ConcurrentQueue<int>();
        Task run = LivePhotoMergeRunnerService.RunAsync(MissingTasks(workspace.RootDirectory, 2),
            Options(workspace.RootDirectory, TimeSpan.Zero), pause, CancellationToken.None,
            task => starts.Enqueue(task.Index), (_, _, _, count) =>
            {
                completions.Enqueue(count);
                if (count == 1) { pause.Reset(); paused.TrySetResult(); }
            });
        await paused.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        Assert.False(run.IsCompleted);
        Assert.Equal(new[] { 1 }, starts);
        Assert.Equal(new[] { 1 }, completions);
        pause.Set();
        await run.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(new[] { 1, 2 }, starts);
        Assert.Equal(new[] { 1, 2 }, completions);
    }

    [Fact]
    public async Task CancelDuringInterval_RetiresQueueWithoutStartingNextBatch()
    {
        using var workspace = new TestFixtureWorkspace();
        using var pause = new ManualResetEventSlim(true);
        using var cancellation = new CancellationTokenSource();
        var firstDone = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var starts = new ConcurrentQueue<int>();
        var completions = new ConcurrentQueue<int>();
        Task run = LivePhotoMergeRunnerService.RunAsync(MissingTasks(workspace.RootDirectory, 2),
            Options(workspace.RootDirectory, TimeSpan.FromSeconds(10)), pause, cancellation.Token,
            task => starts.Enqueue(task.Index), (_, _, _, count) =>
            { completions.Enqueue(count); firstDone.TrySetResult(); });
        await firstDone.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Task.Delay(50);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        await Task.Delay(50);
        Assert.Equal(new[] { 1 }, starts);
        Assert.Equal(new[] { 1 }, completions);
    }

    [Fact]
    public async Task CancelWhileInitiallyPaused_ProducesNoTaskCallbacks()
    {
        using var workspace = new TestFixtureWorkspace();
        using var pause = new ManualResetEventSlim(false);
        using var cancellation = new CancellationTokenSource();
        int starts = 0, completions = 0;
        Task run = LivePhotoMergeRunnerService.RunAsync(MissingTasks(workspace.RootDirectory, 2),
            Options(workspace.RootDirectory, TimeSpan.Zero), pause, cancellation.Token,
            _ => Interlocked.Increment(ref starts), (_, _, _, _) => Interlocked.Increment(ref completions));
        await Task.Delay(50);
        Assert.False(run.IsCompleted);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => run.WaitAsync(TimeSpan.FromSeconds(10)));
        pause.Set();
        await Task.Delay(50);
        Assert.Equal(0, starts);
        Assert.Equal(0, completions);
    }

    [Fact]
    public async Task ParallelFailures_CompleteEachTaskOnceWithUniqueProgressCounts()
    {
        using var workspace = new TestFixtureWorkspace();
        using var pause = new ManualResetEventSlim(true);
        var starts = new ConcurrentQueue<int>();
        var completions = new ConcurrentQueue<(int Index, int Count, bool Success)>();
        await LivePhotoMergeRunnerService.RunAsync(MissingTasks(workspace.RootDirectory, 4),
            Options(workspace.RootDirectory, TimeSpan.Zero, parallel: 2), pause, CancellationToken.None,
            task => starts.Enqueue(task.Index),
            (task, success, _, count) => completions.Enqueue((task.Index, count, success)));
        Assert.Equal(new[] { 1, 2, 3, 4 }, starts.Order());
        Assert.Equal(new[] { 1, 2, 3, 4 }, completions.Select(row => row.Index).Order());
        Assert.Equal(new[] { 1, 2, 3, 4 }, completions.Select(row => row.Count).Order());
        Assert.All(completions, row => Assert.False(row.Success));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [Trait("Category", "DerivedRealSamples")]
    public async Task ExplicitOutputPath_UsesOnlyExplicitOverwriteAuthority(bool overwrite)
    {
        string inputs = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P8_PLAIN_PAIR_ROOT")
            ?? throw new InvalidOperationException("Frozen derived ordinary media inputs are required.");
        using var workspace = new TestFixtureWorkspace();
        string output = Path.Combine(workspace.RootDirectory, "foreign.jpg");
        byte[] foreign = [0x50, 0x38, 0x2D, 0x66, 0x6F, 0x72, 0x65, 0x69, 0x67, 0x6E];
        await File.WriteAllBytesAsync(output, foreign);
        var result = await LivePhotoMergeRunnerService.ProcessSinglePairAsync(
            Path.Combine(inputs, "image.jpg"), Path.Combine(inputs, "video.mp4"), "authority", 1,
            new LivePhotoMergeRunOptions { OutputDirectory = workspace.RootDirectory,
                OutputProfile = OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4),
                OutputFilePath = output, OverwriteExisting = overwrite }, workspace.RootDirectory, CancellationToken.None);
        Assert.Equal(overwrite, result.IsSuccess);
        byte[] after = await File.ReadAllBytesAsync(output);
        if (overwrite) Assert.True(after.Length > foreign.Length, result.Details);
        else Assert.Equal(foreign, after);
        Assert.Equal(new[] { output }, Directory.GetFiles(workspace.RootDirectory, "*", SearchOption.AllDirectories));
    }

    private static LivePhotoMergeRunOptions Options(string output, TimeSpan interval, int parallel = 1) => new()
    {
        OutputDirectory = output,
        OutputProfile = OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4),
        TaskStartInterval = interval,
        MaxDegreeOfParallelism = parallel
    };

    private static IMergeTaskInfo[] MissingTasks(string root, int count) => Enumerable.Range(1, count)
        .Select(index => (IMergeTaskInfo)new QueueTask(index, Path.Combine(root, $"missing-{index}.jpg"),
            Path.Combine(root, $"missing-{index}.mp4"))).ToArray();

    private sealed record QueueTask(int Index, string ImagePath, string VideoPath) : IMergeTaskInfo
    {
        public string BaseName => $"task-{Index}";
        public ProcessStatus Status { get; set; }
        public string Details { get; set; } = string.Empty;
    }
}
