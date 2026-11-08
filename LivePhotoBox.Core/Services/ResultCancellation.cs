using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services;

// Cancellation is an outcome for product orchestration, including queued work.
internal static class ResultCancellation
{
    internal static async Task<bool> EnterAsync(SemaphoreSlim semaphore, CancellationToken token)
    {
        while (!token.IsCancellationRequested)
            if (await semaphore.WaitAsync(50, CancellationToken.None).ConfigureAwait(false))
            {
                if (!token.IsCancellationRequested) return true;
                semaphore.Release();
                return false;
            }
        return false;
    }

    internal static async Task<T?> ObserveAsync<T>(Task<T?> work, CancellationToken token) where T : class
    {
        if (token.IsCancellationRequested) return null;
        if (!token.CanBeCanceled || work.IsCompleted) return await work.ConfigureAwait(false);
        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = token.Register(static state => ((TaskCompletionSource<bool>)state!).TrySetResult(true), cancellation);
        if (await Task.WhenAny(work, cancellation.Task).ConfigureAwait(false) != work) return null;
        return await work.ConfigureAwait(false);
    }
}
