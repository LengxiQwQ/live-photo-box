using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using LivePhotoBox.Interop;

namespace LivePhotoBox.Services;

internal static class ScanFileSystem
{
    internal sealed record Metadata(FileAttributes Attributes, long Length, DateTime LastWriteTimeUtc);
    internal static (Metadata? Value, Exception? Failure) Read(string path)
    {
        if (!GetFileAttributesExW(WindowsNativePath.Extended(path), 0, out var data))
            return (null, Error("Cannot read scan member", path, Marshal.GetLastWin32Error()));
        long stamp = ((long)data.WriteHigh << 32) | data.WriteLow;
        if (stamp < 0 || stamp > 2650467743999999999L)
            return (null, new IOException($"Scan member '{path}' has an unsupported modification timestamp."));
        return (new((FileAttributes)data.Attributes, ((long)data.SizeHigh << 32) | data.SizeLow,
            DateTime.FromFileTimeUtc(stamp)), null);
    }

    internal static (IReadOnlyList<string>? Entries, Exception? Failure) Enumerate(string directory, CancellationToken token)
    {
        var entries = new List<string>();
        nint find = FindFirstFileW(WindowsNativePath.Extended(Path.Combine(directory, "*")), out var data);
        if (find == -1)
        {
            int error = Marshal.GetLastWin32Error();
            return error == 2 ? (entries, null) : (null, Error("Cannot enumerate scan directory", directory, error));
        }
        try
        {
            do
            {
                if (token.IsCancellationRequested) return (null, null);
                if (data.Name is not ("." or "..")) entries.Add(Path.Combine(directory, data.Name));
                if (FindNextFileW(find, out data)) continue;
                int error = Marshal.GetLastWin32Error();
                return error == 18 ? (entries, null) : (null, Error("Cannot finish scan directory", directory, error));
            } while (true);
        }
        finally { FindClose(find); }
    }

    private static IOException Error(string action, string path, int error) =>
        new($"{action} '{path}' (system error {error}).", unchecked((int)(0x80070000U | (uint)error)));
    [StructLayout(LayoutKind.Sequential)]
    private struct AttributeData
    {
        internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, SizeHigh, SizeLow;
    }
    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct FindData
    {
        internal uint Attributes, CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh, SizeHigh, SizeLow, Reserved0, Reserved1;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string Name;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 14)] internal string AlternateName;
    }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileAttributesExW(string path, int level, out AttributeData data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern nint FindFirstFileW(string pattern, out FindData data);
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindNextFileW(nint handle, out FindData data);
    [DllImport("kernel32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FindClose(nint handle);
}
