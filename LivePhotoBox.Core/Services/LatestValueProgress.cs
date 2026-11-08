using System;

namespace LivePhotoBox.Services;

/// <summary>A bounded mailbox: producers never enqueue a callback per file.</summary>
internal sealed class LatestValueProgress<T> : IProgress<T>
{
    private readonly object gate = new();
    private T latest = default!;
    private bool pending;
    public void Report(T value)
    {
        lock (gate) { latest = value; pending = true; }
    }
    internal bool TryTake(out T value)
    {
        lock (gate)
        {
            value = latest;
            bool found = pending;
            pending = false;
            return found;
        }
    }
}
