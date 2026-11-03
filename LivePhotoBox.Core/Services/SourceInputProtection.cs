using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LivePhotoBox.Services;

// Keep all queued originals (including companion objects and aliases) read-only
// until the last publication completes. Missing inputs fail at their item boundary.
internal sealed class SourceInputProtection : IDisposable
{
    private readonly List<FileStream> leases = new();
    internal SourceInputProtection(IEnumerable<string?> paths)
    {
        try
        {
            foreach (string path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(p!))
                .Distinct(StringComparer.OrdinalIgnoreCase))
                if (File.Exists(path)) leases.Add(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read));
        }
        catch { Dispose(); throw; }
    }
    public void Dispose() { foreach (var lease in leases) lease.Dispose(); leases.Clear(); }
}
