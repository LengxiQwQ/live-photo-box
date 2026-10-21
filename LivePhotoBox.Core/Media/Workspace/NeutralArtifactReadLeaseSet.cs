using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using LivePhotoBox.Media.Models;
using Microsoft.Win32.SafeHandles;

namespace LivePhotoBox.Media.Workspace;

/// <summary>
/// Windows transport protection for path-based final observations. Every reader
/// sees the producer's same object and bytes until the semantic graph is complete.
/// Neither the handles nor Windows identities are returned as Neutral semantics.
/// </summary>
internal sealed class NeutralArtifactReadLeaseSet : IDisposable
{
    private readonly HashSet<string> _files = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<SafeFileHandle> _handles = [];
    private bool _disposed;

    internal void Pin(MediaArtifact? artifact)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (artifact is null) return;
        string path = Path.GetFullPath(artifact.Path);
        if (_files.Contains(path)) return;

        // Pin the complete path namespace as well as the leaf. An ancestor
        // rename must not redirect a later path-based Native observation.
        PinDirectory(Path.GetDirectoryName(path));
        SafeFileHandle handle = Open(path, 0x80000000, 0x1, 0x00200000);
        try
        {
            WindowsFileIdentity actual = WindowsFileIdentity.Capture(handle);
            if (actual.IsReparsePoint || (actual.FileAttributes & 0x10) != 0 ||
                actual.LinkCount != 1 ||
                (artifact.FileIdentity is { } expected && !expected.Matches(actual)))
            {
                throw new InvalidDataException("Final observation artifact is not the producer's exact regular file object.");
            }
            long length = RandomAccess.GetLength(handle);
            if (length <= 0 || (artifact.ByteLength > 0 && length != artifact.ByteLength))
                throw new InvalidDataException("Final observation artifact length disagrees with its producer evidence.");
            _handles.Add(handle);
            _files.Add(path);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private void PinDirectory(string? path)
    {
        if (path is null || _directories.Contains(path)) return;
        PinDirectory(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)));
        // READ_ATTRIBUTES, share READ/WRITE but not DELETE: sibling output
        // creation remains possible, while the directory cannot be replaced.
        SafeFileHandle handle = Open(path, 0x80, 0x3, 0x02200000);
        try
        {
            WindowsFileIdentity identity = WindowsFileIdentity.Capture(handle);
            if (identity.IsReparsePoint || (identity.FileAttributes & 0x10) == 0)
                throw new InvalidDataException("Final observation namespace contains a redirecting or non-directory object.");
            _handles.Add(handle);
            _directories.Add(path);
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    private static SafeFileHandle Open(string path, uint access, uint share, uint flags)
    {
        SafeFileHandle handle = CreateFileW(path, access, share, IntPtr.Zero, 3, flags, IntPtr.Zero);
        if (!handle.IsInvalid) return handle;
        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw new IOException("Could not retain immutable final observation input: " + path, new Win32Exception(error));
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        for (int i = _handles.Count - 1; i >= 0; i--) _handles[i].Dispose();
        _handles.Clear();
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(string path, uint access, uint share,
        IntPtr securityAttributes, uint creationDisposition, uint flags, IntPtr template);
}
