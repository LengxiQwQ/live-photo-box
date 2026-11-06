using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class ShutdownLifecycleTests
{
    [Fact]
    public async Task CancellationCompletionDoesNotReleaseAnActiveConsumer()
    {
        var lifetime = new WorkLifetime();
        var consumer = Assert.IsType<WorkLifetime.Lease>(lifetime.TryBegin("writer.finalize"));
        await lifetime.StopAsync();
        Assert.Null(lifetime.TryBegin("late.writer"));
        Assert.False(lifetime.WaitForIdleAsync().IsCompleted);
        Assert.Equal(new[] { "writer.finalize" }, lifetime.ActiveOperations);
        consumer.Dispose();
        consumer.Dispose();
        await lifetime.WaitForIdleAsync();
        Assert.Empty(lifetime.ActiveOperations);
        await lifetime.StopAsync();
    }

    [Fact]
    public void QueuedThumbnailIsInvalidAfterResetOrStop()
    {
        var lifetime = new UiCallbackLifetime();
        int generation = lifetime.Generation;
        Assert.True(lifetime.IsCurrent(generation));
        lifetime.Reset();
        Assert.False(lifetime.IsCurrent(generation));
        int next = lifetime.Generation;
        Assert.True(lifetime.IsCurrent(next));
        lifetime.Stop();
        lifetime.Stop();
        Assert.False(lifetime.IsCurrent(next));
        Assert.False(lifetime.IsCurrent(lifetime.Generation));
    }

    [Fact]
    public async Task RejectedCloseDoesNotExecuteOrReportCompletion()
    {
        bool closed = false;
        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            QueuedUiOperation.RunAsync(_ => false, () => closed = true));
        Assert.False(closed);
    }

    [Fact]
    public async Task QueuedCloseWaitsForExecutionAndPropagatesCallbackFailure()
    {
        Action? queued = null;
        var error = new InvalidOperationException("close failed");
        var operation = QueuedUiOperation.RunAsync(action => { queued = action; return true; }, () => throw error);
        Assert.False(operation.IsCompleted);
        queued!();
        Assert.Same(error, await Assert.ThrowsAsync<InvalidOperationException>(() => operation));
    }

    [Fact]
    public async Task SuccessfulCloseCompletesAfterTheCallback()
    {
        Action? queued = null;
        bool closed = false;
        var operation = QueuedUiOperation.RunAsync(action => { queued = action; return true; }, () => closed = true);
        Assert.False(closed);
        Assert.False(operation.IsCompleted);
        queued!();
        await operation;
        Assert.True(closed);
    }
}
