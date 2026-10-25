namespace LivePhotoBox.Services.Protocols;

/// <summary>Historical public signature; mutation requires the authorized media pipeline.</summary>
public static class AppleLivePhotoMebxWriter
{
    public static bool TryAppendStillImageTrack(string movPath, double coverSeconds, out string? error)
    {
        error = "[AuthorityViolation] Historical path mutation is unsupported; use the authorized media pipeline.";
        return false;
    }
}
