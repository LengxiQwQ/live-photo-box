using System;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Collections;

internal static class ScanCompletionSequence
{
    public static async Task WaitAsync(Func<Task> submitLists, Func<CancellationToken, Task> presentLists, CancellationToken token)
    {
        await submitLists().ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
        await presentLists(token).ConfigureAwait(true);
        token.ThrowIfCancellationRequested();
    }
}
