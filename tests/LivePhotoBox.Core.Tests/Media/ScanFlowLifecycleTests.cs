using System.Collections.ObjectModel;
using System.Collections.Specialized;
using LivePhotoBox.Collections;
using LivePhotoBox.Services;
using LivePhotoBox.Models;
using LivePhotoBox.Core.Tests.Support;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ScanFlowLifecycleTests
{
    [Fact]
    public async Task RapidReplacement_ClearsImmediately_DrainsOldRun_AndOnlyLatestStarts()
    {
        var queue = new ScanRequestQueue();
        var rows = new List<string>();
        var history = new List<string>();
        var release = NewSignal();
        ScanRequestQueue.Request? first = null;
        var a = queue.RunAsync(true, rows.Clear, async request =>
        {
            first = request;
            history.Add("A-start");
            rows.Add("A");
            await release.Task;
            if (request.IsCurrent) rows.Add("late-A");
            history.Add("A-end");
        });
        Assert.Equal(new[] { "A" }, rows);
        var b = queue.RunAsync(true, rows.Clear, request => { history.Add("B"); rows.Add("B"); return Task.CompletedTask; });
        Assert.Empty(rows);
        Assert.NotNull(first);
        Assert.True(first.Token.IsCancellationRequested);
        Assert.False(first.IsCurrent);
        var c = queue.RunAsync(true, rows.Clear, request => { history.Add("C"); rows.Add("C"); return Task.CompletedTask; });
        Assert.True(queue.IsBusy);
        Assert.Empty(rows);
        release.SetResult();
        await Task.WhenAll(a, b, c);
        Assert.Equal(new[] { "A-start", "A-end", "C" }, history);
        Assert.Equal(new[] { "C" }, rows);
        Assert.False(queue.IsBusy);
    }

    [Fact]
    public async Task Append_RetainsExistingRows_WhileReplacementDropsQueuedAppends()
    {
        var queue = new ScanRequestQueue();
        var rows = new List<string> { "existing" };
        var release = NewSignal();
        var a = queue.RunAsync(false, () => throw new Exception("append must not reset"), async request =>
        {
            await release.Task;
            if (request.IsCurrent) rows.Add("append-A");
        });
        var b = queue.RunAsync(false, rows.Clear, request => { rows.Add("append-B"); return Task.CompletedTask; });
        Assert.Equal(new[] { "existing" }, rows);
        var replace = queue.RunAsync(true, rows.Clear, request => { rows.Add("replacement"); return Task.CompletedTask; });
        Assert.Empty(rows);
        release.SetResult();
        await Task.WhenAll(a, b, replace);
        Assert.Equal(new[] { "replacement" }, rows);
    }

    [Fact]
    public async Task SequentialAppends_RunInOrderWithoutClearingOrCancellingEachOther()
    {
        var queue = new ScanRequestQueue();
        var rows = new List<string> { "existing" };
        var release = NewSignal();
        var a = queue.RunAsync(false, rows.Clear, async request => { await release.Task; request.Token.ThrowIfCancellationRequested(); rows.Add("A"); });
        var b = queue.RunAsync(false, rows.Clear, request => { rows.Add("B"); return Task.CompletedTask; });
        release.SetResult();
        await Task.WhenAll(a, b);
        Assert.Equal(new[] { "existing", "A", "B" }, rows);
    }

    [Fact]
    public async Task CancelPendingReplacement_DoesNotStartIt_AndRetryCanRun()
    {
        var queue = new ScanRequestQueue();
        var release = NewSignal();
        var history = new List<string>();
        var first = queue.RunAsync(true, history.Clear, async request => await release.Task);
        var pending = queue.RunAsync(true, history.Clear, request => { history.Add("must-not-run"); return Task.CompletedTask; });
        queue.Cancel();
        release.SetResult();
        await Task.WhenAll(first, pending);
        Assert.Empty(history);
        Assert.False(queue.IsBusy);
        await queue.RunAsync(true, history.Clear, request => { history.Add("retry"); return Task.CompletedTask; });
        Assert.Equal(new[] { "retry" }, history);
    }

    [Fact]
    public async Task FailureThenReplacement_StillRunsAndReleasesBusyState()
    {
        var queue = new ScanRequestQueue();
        var failed = queue.RunAsync(true, () => { }, _ => Task.FromException(new IOException("test failure")));
        await Assert.ThrowsAsync<IOException>(() => failed);
        bool ran = false;
        await queue.RunAsync(true, () => { }, _ => { ran = true; return Task.CompletedTask; });
        Assert.True(ran);
        Assert.False(queue.IsBusy);
    }

    [Fact]
    public async Task Invalidate_ClearPreventsCapturedOldCallbackFromRestoringRows()
    {
        var queue = new ScanRequestQueue();
        var rows = new List<string>();
        Action? late = null;
        var release = NewSignal();
        var work = queue.RunAsync(true, rows.Clear, async request =>
        {
            late = () => { if (request.IsCurrent) rows.Add("late"); };
            await release.Task;
        });
        queue.Invalidate();
        rows.Clear();
        late!();
        release.SetResult();
        await work;
        Assert.Empty(rows);
        Assert.False(queue.IsBusy);
    }

    [Fact]
    public void AppendedRows_AreNumberedBeforeObserversReceiveOneBatch()
    {
        var source = new BulkObservableCollection<Row>();
        source.AppendNumberedBatch(new[] { new Row() }, (row, index) => row.Index = index);
        int batches = 0;
        source.CollectionChanged += (_, args) =>
        {
            batches++;
            Assert.Equal(NotifyCollectionChangedAction.Add, args.Action);
            Assert.Equal(128, args.NewItems!.Count);
            var added = args.NewItems.Cast<Row>().ToArray();
            Assert.Equal(Enumerable.Range(2, 128), added.Select(row => row.Index));
        };
        source.AppendNumberedBatch(Enumerable.Range(0, 128).Select(_ => new Row()).ToArray(), (row, index) => row.Index = index);
        Assert.Equal(1, batches);
    }

    [Fact]
    public async Task AppendingSmallTail_DoesNotQueueBatchesForUnchangedPrefix()
    {
        var rows = Enumerable.Range(0, 5000).Select(_ => new object()).ToArray();
        var target = new ObservableCollection<object>(rows);
        var dispatch = new Queue<Action>();
        var updater = new CollectionViewUpdater<object>(target, action => { dispatch.Enqueue(action); return true; });
        var desired = rows.Concat(new[] { new object(), new object(), new object() }).ToArray();
        updater.Request(() => desired);
        dispatch.Dequeue()();
        Assert.True(target.Count > rows.Length); // Old implementation spent this turn on 128 unchanged rows.
        while (dispatch.TryDequeue(out var apply)) apply();
        await updater.Completion;
        Assert.Equal(desired, target);
    }

    [Fact]
    public async Task ColumnWork_IsBoundedAndCancellationStopsFurtherMeasurements()
    {
        var dispatch = new Queue<Action>();
        using var cancellation = new CancellationTokenSource();
        int processed = 0;
        var work = UiBatchProcessor.RunAsync(1000, _ => processed++, action => { dispatch.Enqueue(action); return true; }, cancellation.Token);
        dispatch.Dequeue()();
        Assert.InRange(processed, 1, 128);
        int beforeCancel = processed;
        cancellation.Cancel();
        while (dispatch.TryDequeue(out var apply)) apply();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work);
        Assert.Equal(beforeCancel, processed);
    }

    [Fact]
    public async Task ProductionFilenameScanner_SwitchWhileOldBatchBlocked_OnlyPublishesNewDirectory()
    {
        using var workspace = new TestFixtureWorkspace();
        string oldDirectory = Directory.CreateDirectory(Path.Combine(workspace.RootDirectory, "old")).FullName;
        string newDirectory = Directory.CreateDirectory(Path.Combine(workspace.RootDirectory, "new")).FullName;
        foreach (string directory in new[] { oldDirectory, newDirectory })
            for (int i = 0; i < 3; i++)
            {
                await File.WriteAllTextAsync(Path.Combine(directory, $"image-{i}.jpg"), "attributes only");
                await File.WriteAllTextAsync(Path.Combine(directory, $"image-{i}.mp4"), "attributes only");
            }
        var queue = new ScanRequestQueue();
        var rows = new BulkObservableCollection<Row>();
        var oldBatchReady = NewSignal();
        var releaseOld = NewSignal();
        var first = queue.RunAsync(true, rows.Clear, async request =>
        {
            await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { InputDirectory = oldDirectory, Mode = PairingMode.Name },
                request.Token, candidateBatch: async (pairs, token) =>
                {
                    oldBatchReady.SetResult();
                    await releaseOld.Task;
                    if (request.IsCurrent)
                        rows.AppendNumberedBatch(pairs.Select(pair => new Row { Path = pair.ImagePath }).ToArray(), (row, index) => row.Index = index);
                });
        });
        await oldBatchReady.Task;
        var second = queue.RunAsync(true, rows.Clear, async request =>
        {
            var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { InputDirectory = newDirectory, Mode = PairingMode.Name },
                request.Token, candidateBatch: (pairs, token) =>
                {
                    if (request.IsCurrent)
                        rows.AppendNumberedBatch(pairs.Select(pair => new Row { Path = pair.ImagePath }).ToArray(), (row, index) => row.Index = index);
                    return Task.CompletedTask;
                });
            Assert.Equal(ScanOperationStatus.Completed, result.Status);
        });
        Assert.Empty(rows);
        releaseOld.SetResult();
        await Task.WhenAll(first, second);
        Assert.Equal(3, rows.Count);
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(row => row.Index));
        Assert.All(rows, row => Assert.StartsWith(newDirectory, row.Path, StringComparison.OrdinalIgnoreCase));
        Assert.False(queue.IsBusy);
    }

    [Fact]
    public async Task ManualRescan_RetainsPreviousSnapshotUntilNewSnapshotOverwritesIt()
    {
        using var workspace = new TestFixtureWorkspace();
        for (int i = 0; i < 3; i++)
        {
            await File.WriteAllTextAsync(Path.Combine(workspace.RootDirectory, $"new-{i}.jpg"), "attributes only");
            await File.WriteAllTextAsync(Path.Combine(workspace.RootDirectory, $"new-{i}.mp4"), "attributes only");
        }
        var queue = new ScanRequestQueue();
        var previous = new Row { Index = 1, Path = "previous snapshot" };
        var rows = new BulkObservableCollection<Row> { previous };
        var ready = NewSignal();
        var release = NewSignal();
        int changes = 0;
        rows.CollectionChanged += (_, args) =>
        {
            changes++;
            Assert.Equal(NotifyCollectionChangedAction.Reset, args.Action);
            Assert.Equal(3, rows.Count);
        };
        var scan = queue.RunAsync(true, () => { }, async request =>
        {
            var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions
                { InputDirectory = workspace.RootDirectory, Mode = PairingMode.Name }, request.Token);
            ready.SetResult();
            await release.Task;
            if (request.IsCurrent)
                rows.ReplaceRange(result.Pairs.Select((pair, index) => new Row { Index = index + 1, Path = pair.ImagePath }));
        });
        await ready.Task;
        Assert.Same(previous, Assert.Single(rows));
        Assert.Equal(0, changes);
        release.SetResult();
        await scan;
        Assert.Equal(1, changes);
        Assert.DoesNotContain(previous, rows);
        Assert.Equal(new[] { 1, 2, 3 }, rows.Select(row => row.Index));
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);
    private sealed class Row { public int Index { get; set; } public string Path { get; set; } = ""; }
}
