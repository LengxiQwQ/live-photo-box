using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LivePhotoBox.Services;

// Pin all queued originals (including companions and aliases) against replacement
// until publication completes. Readable originals are also pinned against writes;
// unreadable originals still fail at their own item boundary.
internal sealed class SourceInputProtection : IDisposable
{
    private readonly List<IDisposable> leases = new();
    internal SourceInputProtection(IEnumerable<string?> paths)
    {
        try
        {
            foreach (string path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(p!))
                .Distinct(StringComparer.OrdinalIgnoreCase))
                if (File.Exists(path))
                {
                    try { leases.Add(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read)); }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        // Reading data is not needed to pin an unreadable queued
                        // object against transactional replacement. This zero-access
                        // handle does not grant a reader or writer any new authority.
                        var pin = CreateFileW(path, 0, 3 /* share read/write, deny delete */,
                            IntPtr.Zero, 3 /* OPEN_EXISTING */, 0, IntPtr.Zero);
                        if (pin.IsInvalid)
                        {
                            pin.Dispose();
                            if (!File.Exists(path)) continue;
                            throw new IOException("Cannot protect a queued original against replacement: " + path, ex);
                        }
                        leases.Add(pin);
                        LogService.Debug($"Pinned unreadable source against replacement; its item still reports the read failure: '{path}'.");
                    }
                }
        }
        catch { Dispose(); throw; }
    }
    public void Dispose() { foreach (var lease in leases) lease.Dispose(); leases.Clear(); }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr security, uint creation, uint flags, IntPtr template);
}
