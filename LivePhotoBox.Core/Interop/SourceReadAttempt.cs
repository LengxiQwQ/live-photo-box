using System;
using System.IO;
using System.Threading;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LivePhotoBox.Interop;

internal sealed record SourceReadAttempt(SafeFileHandle Handle, int Error) : IDisposable
{
    internal bool Succeeded => !Handle.IsInvalid;
    internal static SourceReadAttempt Open(string path,uint share = 1,uint access = 0x80000000)
    {
        string full = Path.GetFullPath(path);
        string extended = full.StartsWith(@"\\?\", StringComparison.Ordinal) ? full
            : full.StartsWith(@"\\", StringComparison.Ordinal) ? @"\\?\UNC\" + full[2..] : @"\\?\" + full;
        var handle = CreateFileW(extended, access, share, 0, 3, 0x00200000, 0);
        return new(handle, handle.IsInvalid ? Marshal.GetLastWin32Error() : 0);
    }
    public void Dispose() => Handle.Dispose();

    internal unsafe (byte[]? Bytes, Exception? Failure) TryReadBytes(int maximumBytes, CancellationToken token)
    {
        if (token.IsCancellationRequested) return (null, new OperationCanceledException(token));
        if (!Succeeded || !TryGetLength(out long length, out int error) || length <= 0 || length > maximumBytes)
            return (null, new IOException("Cannot read complete media within the bounded byte budget."));
        byte[] bytes = new byte[(int)length];
        fixed (byte* data = bytes)
        {
            int offset = 0;
            while (offset < bytes.Length)
            {
                if (token.IsCancellationRequested) return (null, new OperationCanceledException(token));
                if (!ReadFile(Handle, data + offset, (uint)Math.Min(128 * 1024, bytes.Length - offset), out uint read, 0) || read == 0)
                    return (null, new IOException($"Cannot completely read media (system error {Marshal.GetLastWin32Error()})."));
                offset += (int)read;
            }
        }
        return TryGetLength(out long after, out error) && after == length
            ? (bytes, null) : (null, new IOException("Media length changed during reading."));
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool ReadFile(SafeFileHandle file, byte* buffer, uint count, out uint read, nint overlapped);

    internal bool TryGetLength(out long length, out int error)
    {
        bool read = GetFileSizeEx(Handle, out length);
        error = read ? 0 : Marshal.GetLastWin32Error();
        return read;
    }

    internal bool TryGetLastWriteTime(out long fileTime, out int error)
    {
        bool read = GetFileTime(Handle, 0, 0, out fileTime);
        error = read ? 0 : Marshal.GetLastWin32Error();
        return read;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileTime(SafeFileHandle handle, nint creation, nint access, out long write);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(SafeFileHandle handle, out long size);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        nint security, uint creation, uint flags, nint template);
}
