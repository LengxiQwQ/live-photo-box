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
    internal bool IsComplete { get; private set; } = true;
    internal string? Diagnostic { get; private set; }
    internal HashSet<string> FailedPaths { get; } = new(StringComparer.OrdinalIgnoreCase);
    internal SourceInputProtection(IEnumerable<string?> paths)
    {
        try
        {
            foreach (string path in paths.Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.GetFullPath(p!))
                .Distinct(StringComparer.OrdinalIgnoreCase))
                if (File.Exists(path))
                {
                    var readable = LivePhotoBox.Interop.SourceReadAttempt.Open(path);
                    if (readable.Succeeded) leases.Add(readable);
                    else
                    {
                        readable.Dispose();
                        var pin = CreateFileW(LivePhotoBox.Interop.WindowsNativePath.Extended(path), 0, 3,
                            IntPtr.Zero, 3, 0x00200000, IntPtr.Zero);
                        if (pin.IsInvalid)
                        {
                            int error = Marshal.GetLastWin32Error();
                            pin.Dispose();
                            if (!File.Exists(path)) continue;
                            IsComplete = false;
                            FailedPaths.Add(path);
                            Diagnostic = $"无法保护来源文件，已停止相关写入（系统错误 {error}）：{path}";
                        }
                        else leases.Add(pin);
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
