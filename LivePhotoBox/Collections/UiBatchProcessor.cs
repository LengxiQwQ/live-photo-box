using System;
using System.Diagnostics;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Collections;

internal static class UiBatchProcessor
{
    public static Task RunAsync(int count, Action<int> process, Func<Action, bool> enqueue, CancellationToken token)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int offset = 0;
        Schedule();
        return completion.Task;

        void Schedule()
        {
            if (!enqueue(ApplyBatch)) completion.TrySetCanceled();
        }

        void ApplyBatch()
        {
            try
            {
                token.ThrowIfCancellationRequested();
                var clock = Stopwatch.StartNew();
                int end = Math.Min(offset + 128, count);
                while (offset < end)
                {
                    process(offset++);
                    if (clock.ElapsedMilliseconds >= 4) break;
                }
                if (offset < count) Schedule();
                else completion.TrySetResult();
            }
            catch (OperationCanceledException) { completion.TrySetCanceled(); }
            catch (Exception error) { completion.TrySetException(error); }
        }
    }
}
