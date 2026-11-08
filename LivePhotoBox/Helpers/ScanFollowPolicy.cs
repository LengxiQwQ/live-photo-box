namespace LivePhotoBox.Helpers;

// Completed-snapshot presentation: scanning never creates a scroll target.
internal sealed class ScanFollowPolicy
{
    public bool IsScanning { get; private set; }

    public void Begin(int existingRows)
    {
        IsScanning = true;
    }

    public void End() => IsScanning = false;

    public bool TryGetTarget(int visibleRows, bool pausedByUser, out int target)
    {
        target = -1;
        return false;
    }
}
