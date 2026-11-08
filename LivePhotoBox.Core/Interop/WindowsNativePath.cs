using System;
using System.IO;

namespace LivePhotoBox.Interop;

internal static class WindowsNativePath
{
    internal static string Extended(string path)
    {
        string full = Path.GetFullPath(path);
        if (full.StartsWith(@"\\?\", StringComparison.Ordinal)) return full;
        return full.StartsWith(@"\\", StringComparison.Ordinal)
            ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
    }
}
