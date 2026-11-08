using System.Collections.ObjectModel;
using System.Collections.Specialized;
using LivePhotoBox.Collections;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ScanListUpdateTests
{
    [Fact]
    public async Task CompletedSnapshot_PublishesAllRowsInOneNotification()
    {
        var target = new BulkObservableCollection<object>();
        var queue = new Queue<Action>();
        var updater = new CollectionViewUpdater<object>(target, action => { queue.Enqueue(action); return true; });
        var rows = Enumerable.Range(0, 5000).Select(_ => new object()).ToArray();
        int notifications = 0;
        target.CollectionChanged += (_, args) =>
        {
            notifications++;
            Assert.Equal(NotifyCollectionChangedAction.Reset, args.Action);
            Assert.Equal(5000, target.Count);
        };
        updater.Request(() => rows, publishAtomically: true);
        Assert.Empty(target);
        Assert.False(updater.Completion.IsCompleted);
        queue.Dequeue()();
        await updater.Completion;
        Assert.Empty(queue);
        Assert.Equal(1, notifications);
        Assert.Equal(rows, target);
    }

    [Fact]
    public async Task CompletedSnapshot_ReplacesPendingIncrementalWorkWithoutPartialRows()
    {
        var target = new BulkObservableCollection<int>();
        var queue = new Queue<Action>();
        var updater = new CollectionViewUpdater<int>(target, action => { queue.Enqueue(action); return true; });
        updater.Request(() => Enumerable.Range(0, 3000).ToArray());
        updater.Request(() => [9, 8, 7], publishAtomically: true);
        while (queue.TryDequeue(out var apply)) apply();
        await updater.Completion;
        Assert.Equal(new[] { 9, 8, 7 }, target);
    }

    [Fact]
    public async Task ReorderingUniqueRows_NeverTemporarilyDuplicatesTheirIdentity()
    {
        var rows = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
        var target = new ObservableCollection<object>(rows);
        var queue = new Queue<Action>();
        var updater = new CollectionViewUpdater<object>(target, action => { queue.Enqueue(action); return true; });
        target.CollectionChanged += (_, _) => Assert.Equal(target.Count, target.Distinct().Count());
        updater.Request(() => rows.Reverse().ToArray());
        while (queue.TryDequeue(out var apply)) apply();
        await updater.Completion;
        Assert.Equal(rows.Reverse(), target);
    }

    [Fact]
    public async Task MixedReorderNewAndFilteredRows_KeepUniqueIdentityAtEveryNotification()
    {
        var rows = Enumerable.Range(0, 64).Select(_ => new object()).ToArray();
        var desired = new[] { rows[51], new object(), rows[2], rows[40], rows[1] };
        var target = new ObservableCollection<object>(rows);
        var queue = new Queue<Action>();
        var updater = new CollectionViewUpdater<object>(target, action => { queue.Enqueue(action); return true; });
        target.CollectionChanged += (_, _) => Assert.Equal(target.Count, target.Distinct().Count());
        updater.Request(() => desired);
        while (queue.TryDequeue(out var apply)) apply();
        await updater.Completion;
        Assert.Equal(desired, target);
    }

    [Fact]
    public async Task ExplicitDuplicateValues_ArePreservedWhenTheyAreActuallyRequested()
    {
        var target = new ObservableCollection<int>([1, 2]);
        var queue = new Queue<Action>();
        var updater = new CollectionViewUpdater<int>(target, action => { queue.Enqueue(action); return true; });
        updater.Request(() => [2, 1, 2]);
        while (queue.TryDequeue(out var apply)) apply();
        await updater.Completion;
        Assert.Equal(new[] { 2, 1, 2 }, target);
    }

    [Fact]
    public async Task ExplicitClear_IsImmediateOneReset_AndOldBatchesCannotRestoreRows()
    {
        var rows = Enumerable.Range(0, 3000).Select(_ => new object()).ToArray();
        var target = new ObservableCollection<object>(rows);
        var queue = new Queue<Action>();
        var updater = new CollectionViewUpdater<object>(target, action => { queue.Enqueue(action); return true; });
        updater.Request(() => rows.Reverse().ToArray());
        queue.Dequeue()();
        Assert.Single(queue);
        int resets = 0, removals = 0;
        target.CollectionChanged += (_, args) =>
        {
            if (args.Action == NotifyCollectionChangedAction.Reset) resets++;
            if (args.Action == NotifyCollectionChangedAction.Remove) removals++;
        };
        updater.ClearImmediately();
        Assert.Empty(target);
        Assert.True(updater.Completion.IsCompletedSuccessfully);
        while (queue.TryDequeue(out var apply)) apply();
        Assert.Empty(target);
        Assert.Equal(1, resets);
        Assert.Equal(0, removals);

        updater.Request(() => rows.Take(3).ToArray());
        while (queue.TryDequeue(out var apply)) apply();
        await updater.Completion;
        Assert.Equal(rows.Take(3), target);
    }

    [Fact]
    public async Task EmptySnapshot_ClearsOnceRatherThanRemovingEachRow()
    {
        var target = new ObservableCollection<int>(Enumerable.Range(0, 3000));
        var queue = new Queue<Action>();
        var updater = new CollectionViewUpdater<int>(target, action => { queue.Enqueue(action); return true; });
        var notifications = new List<NotifyCollectionChangedAction>();
        target.CollectionChanged += (_, args) => notifications.Add(args.Action);
        updater.Request(() => Array.Empty<int>());
        queue.Dequeue()();
        await updater.Completion;
        Assert.Empty(target);
        Assert.Empty(queue);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, notifications);
    }

    [Fact]
    public async Task ScanCompletion_WaitsForCollectionSubmissionThenVisibleLayout()
    {
        var submit = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var layoutStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var layout = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool presentationRequested = false;
        var ready = ScanCompletionSequence.WaitAsync(() => submit.Task, token =>
        {
            presentationRequested = true;
            layoutStarted.SetResult();
            return layout.Task;
        }, CancellationToken.None);
        Assert.False(presentationRequested);
        Assert.False(ready.IsCompleted);
        submit.SetResult();
        await layoutStarted.Task;
        Assert.False(ready.IsCompleted);
        layout.SetResult();
        await ready;
    }

    [Fact]
    public async Task ScanCompletion_CancelledSubmissionCannotReportReady()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        bool presentationRequested = false;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ScanCompletionSequence.WaitAsync(() => Task.CompletedTask,
            token => { presentationRequested = true; return Task.CompletedTask; }, cancellation.Token));
        Assert.False(presentationRequested);
    }

    [Fact]
    public async Task ScanCompletion_LayoutFailureCannotReportReady()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => ScanCompletionSequence.WaitAsync(() => Task.CompletedTask,
            _ => Task.FromException(new InvalidOperationException("layout failed")), CancellationToken.None));
    }

    [Fact]
    public async Task RepeatSnapshot_KeepsRowsAndProducesNoCollectionNotifications()
    {
        var rows = Enumerable.Range(0, 3000).Select(_ => new object()).ToArray();
        var target = new ObservableCollection<object>();
        var queue = new Queue<Action>();
        var updater = new CollectionViewUpdater<object>(target, action => { queue.Enqueue(action); return true; });
        int notifications = 0;
        target.CollectionChanged += (_, args) => { Assert.NotEqual(NotifyCollectionChangedAction.Reset, args.Action); notifications++; };
        updater.Request(() => rows);
        Assert.False(updater.Completion.IsCompleted);
        Assert.True(queue.TryDequeue(out var first));
        first();
        Assert.InRange(target.Count, 1, 128); // 128 mutations or the 4ms UI budget.
        Assert.False(updater.Completion.IsCompleted);
        while (queue.TryDequeue(out var apply)) apply();
        await updater.Completion;
        Assert.Equal(rows.Length, notifications);
        notifications = 0;
        updater.Request(() => rows);
        while (queue.TryDequeue(out var apply)) apply();
        await updater.Completion;
        Assert.Equal(0, notifications);
        Assert.Equal(rows, target);
    }

    [Fact]
    public async Task SortingFilteringAndNewRows_NeverClearOrResetTheVisibleList()
    {
        var rows = Enumerable.Range(0, 3000).Select(_ => new object()).ToArray();
        var target = new ObservableCollection<object>(rows);
        var queue = new Queue<Action>();
        var updater = new CollectionViewUpdater<object>(target, action => { queue.Enqueue(action); return true; });
        int minimumCount = target.Count;
        target.CollectionChanged += (_, args) =>
        {
            Assert.NotEqual(NotifyCollectionChangedAction.Reset, args.Action);
            minimumCount = Math.Min(minimumCount, target.Count);
        };
        var desired = rows.Reverse().Skip(500).Concat([new object()]).ToArray();
        updater.Request(() => desired);
        while (queue.TryDequeue(out var apply)) apply();
        await updater.Completion;
        Assert.Equal(desired, target);
        Assert.Equal(desired.Length, minimumCount);
    }

    [Fact]
    public async Task FrequentRefreshes_CoalesceProjectionAndSupersedeAlreadyQueuedBatches()
    {
        var target = new ObservableCollection<int>();
        var queue = new Queue<Action>();
        var updater = new CollectionViewUpdater<int>(target, action => { queue.Enqueue(action); return true; });
        int projections = 0;
        for (int i = 0; i < 30; i++) updater.Request(() => { projections++; return Enumerable.Range(0, 1000).ToArray(); });
        Assert.Single(queue);
        queue.Dequeue()();
        Assert.Equal(1, projections);
        Assert.InRange(target.Count, 1, 128);
        updater.Request(() => [9, 8, 7]);
        while (queue.TryDequeue(out var apply)) apply();
        await updater.Completion;
        Assert.Equal(new[] { 9, 8, 7 }, target);
    }

    [Fact]
    public async Task UnavailableDispatcher_DoesNotLeaveCompletionPending()
    {
        var updater = new CollectionViewUpdater<int>(new(), _ => false);
        updater.Request(() => [1]);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => updater.Completion);
    }

    [Fact]
    public void UnchangedSourceSnapshot_DoesNotResetSubscribers()
    {
        var rows = Enumerable.Range(0, 3000).Select(_ => new object()).ToArray();
        var target = new BulkObservableCollection<object>();
        target.ReplaceRange(rows);
        int resets = 0;
        target.CollectionChanged += (_, _) => resets++;
        target.ReplaceRangeIfChanged(rows);
        target.ReplaceRangeIfChanged(target);
        Assert.Equal(0, resets);
        target.ReplaceRangeIfChanged(rows.Skip(1));
        Assert.Equal(1, resets);
        Assert.Equal(rows.Skip(1), target);
    }
}
