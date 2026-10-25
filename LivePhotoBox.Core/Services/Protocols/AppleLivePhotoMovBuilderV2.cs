namespace LivePhotoBox.Services.Protocols;

/// <summary>Historical public signature; mutation requires the authorized media pipeline.</summary>
public static class AppleLivePhotoMovBuilderV2
{
    public static bool TryRebuild(string movPath, string contentId, string model, double coverSeconds, out string? error)
    {
        error = "[AuthorityViolation] Historical path mutation is unsupported; use the authorized media pipeline.";
        return false;
    }
}
