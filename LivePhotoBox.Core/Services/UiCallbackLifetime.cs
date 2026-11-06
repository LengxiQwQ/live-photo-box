using System.Threading;

namespace LivePhotoBox.Services;

/// <summary>Invalidates UI submissions already queued before a reset or shutdown.</summary>
internal sealed class UiCallbackLifetime
{
    private int _generation;
    private int _stopped;
    internal int Generation => Volatile.Read(ref _generation);
    internal bool IsCurrent(int generation) => Volatile.Read(ref _stopped) == 0 && generation == Generation;
    internal void Reset() => Interlocked.Increment(ref _generation);
    internal void Stop()
    {
        Interlocked.Exchange(ref _stopped, 1);
        Reset();
    }
}
