using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Interop;

internal static class OutputDirectoryAttempt
{
    // Expected directory errors stay data; in particular an existing file is
    // never overwritten or removed to make room for a directory.
    internal static IOException? Ensure(string path)
    {
        string full = Path.GetFullPath(path);
        int error = Create(full, 0);
        return error == 0 ? null : new IOException(
            $"Cannot create output directory '{full}': {new Win32Exception(error).Message}");
    }

    private static int Create(string path, int depth)
    {
        if (depth > 512) return 206;
        string native = WindowsNativePath.Extended(path);
        if (CreateDirectoryW(native, 0)) return 0;
        int error = Marshal.GetLastWin32Error();
        if (error == 183)
        {
            uint attributes = GetFileAttributesW(native);
            return attributes != uint.MaxValue && (attributes & 16) != 0 ? 0 : error;
        }
        if (error != 3) return error;
        string? parent = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(parent) || parent == path) return error;
        int parentError = Create(parent, depth + 1);
        if (parentError != 0) return parentError;
        if (CreateDirectoryW(native, 0)) return 0;
        error = Marshal.GetLastWin32Error();
        if (error == 183)
        {
            uint attributes = GetFileAttributesW(native);
            return attributes != uint.MaxValue && (attributes & 16) != 0 ? 0 : error;
        }
        return error;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateDirectoryW(string path, nint security);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFileAttributesW(string path);
}
