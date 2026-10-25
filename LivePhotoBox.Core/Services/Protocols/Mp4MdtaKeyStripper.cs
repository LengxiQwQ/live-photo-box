using System;
namespace LivePhotoBox.Services.Protocols;

public static class Mp4MdtaKeyStripper
{
    public static bool TryStripHuaweiKeys(string path, out string? error)
    {
        error = "[AuthorityViolation] Historical path mutation is unsupported; use the authorized media pipeline.";
        return false;
    }
    public static bool TryStripUuidBox(string path, string userType, out string? error)
    {
        error = "[AuthorityViolation] Historical path mutation is unsupported; use the authorized media pipeline.";
        return false;
    }
    public static bool TryStripTracks(string path, string[] stsdKeyFragments, out string? error)
    {
        error = "[AuthorityViolation] Historical path mutation is unsupported; use the authorized media pipeline.";
        return false;
    }
    public static bool TryStripMebxTracks(string path, out string? error)
    {
        error = "[AuthorityViolation] Historical path mutation is unsupported; use the authorized media pipeline.";
        return false;
    }
    public static bool TryStripMdtaKeys(string path, Func<string, string, bool> shouldRemove, out string? error)
    {
        error = "[AuthorityViolation] Historical path mutation is unsupported; use the authorized media pipeline.";
        return false;
    }
}
