using System;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Collections;

// Owned by the UI thread. Replacement cancels the previous scope immediately;
// execution drains serially, so an old finally cannot tear down a newer scan.
internal sealed class ScanRequestQueue
{
    private long _generation;
    private int _pending;
    private CancellationTokenSource _cancellation = new();
    private Task _tail = Task.CompletedTask;

    public long Generation => _generation;
    public bool IsBusy => _pending > 0;
    public bool IsCancelled => _cancellation.IsCancellationRequested;

    public Task RunAsync(bool replace, Action reset, Func<Request, Task> execute)
    {
        if (replace || _cancellation.IsCancellationRequested) Advance();
        if (replace) reset();
        var request = new Request(this, _generation, _cancellation.Token);
        var predecessor = _tail;
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _tail = completion.Task;
        _pending++;
        _ = RunAfterAsync();
        return completion.Task;

        async Task RunAfterAsync()
        {
            Exception? failure = null;
            try
            {
                try { await predecessor.ConfigureAwait(true); }
                catch (Exception) { /* The original request owns its failure. */ }
                if (request.IsCurrent && !request.Token.IsCancellationRequested)
                    await execute(request).ConfigureAwait(true);
            }
            catch (Exception error) { failure = error; }
            finally
            {
                _pending--;
                if (failure != null) completion.TrySetException(failure);
                else completion.TrySetResult();
            }
        }
    }

    public void Cancel() => _cancellation.Cancel();
    public void Invalidate() => Advance();

    private void Advance()
    {
        var previous = _cancellation;
        _generation++;
        _cancellation = new();
        previous.Cancel();
        _ = DisposeAfterAsync(previous, _tail);
    }

    private static async Task DisposeAfterAsync(CancellationTokenSource cancellation, Task drain)
    {
        try { await drain.ConfigureAwait(true); }
        catch (Exception) { /* The request's caller observes this failure. */ }
        cancellation.Dispose();
    }

    internal sealed class Request(ScanRequestQueue owner, long generation, CancellationToken token)
    {
        public long Generation => generation;
        public CancellationToken Token => token;
        public bool IsCurrent => owner._generation == generation;
    }
}
