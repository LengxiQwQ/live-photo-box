using System;
using System.Threading.Tasks;

namespace LivePhotoBox.Services;

internal static class QueuedUiOperation
{
    internal static Task RunAsync(Func<Action, bool> enqueue, Action operation)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            if (!enqueue(() =>
            {
                try { operation(); completion.TrySetResult(); }
                catch (Exception error) { completion.TrySetException(error); }
            })) completion.TrySetException(new InvalidOperationException("The UI dispatcher rejected the operation."));
        }
        catch (Exception error) { completion.TrySetException(error); }
        return completion.Task;
    }
}
