using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using LivePhotoBox.Interop;
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
        if (TryPin(artifact) is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal Exception? TryPin(MediaArtifact? artifact)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (artifact is null) return null;
        string path = Path.GetFullPath(artifact.Path);
        if (_files.Contains(path)) return null;

        // Pin the complete path namespace as well as the leaf. An ancestor
        // rename must not redirect a later path-based Native observation.
        if (TryPinDirectory(Path.GetDirectoryName(path)) is { } directoryFailure) return directoryFailure;
        SafeFileHandle handle = TryOpen(path, 0x80000000, 0x1, 0x00200000);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); return OpenFailure(path, error); }
        bool retained = false;
        try
        {
            if (!WindowsFileIdentity.TryCapture(handle, out var captured, out int error)) return OpenFailure(path, error);
            WindowsFileIdentity actual = captured!;
            if (actual.IsReparsePoint || (actual.FileAttributes & 0x10) != 0 ||
                actual.LinkCount != 1 ||
                (artifact.FileIdentity is { } expected && !expected.Matches(actual)))
            {
                return new InvalidDataException("Final observation artifact is not the producer's exact regular file object.");
            }
            if (!GetFileSizeEx(handle, out long length)) return OpenFailure(path, Marshal.GetLastWin32Error());
            if (length <= 0 || (artifact.ByteLength > 0 && length != artifact.ByteLength))
                return new InvalidDataException("Final observation artifact length disagrees with its producer evidence.");
            _handles.Add(handle);
            _files.Add(path);
            retained = true;
            return null;
        }
        finally { if (!retained) handle.Dispose(); }
    }

    internal Exception? TryPinDirectory(string? path)
    {
        if (path is null || _directories.Contains(path)) return null;
        if (TryPinDirectory(Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path))) is { } parentFailure) return parentFailure;
        // READ_ATTRIBUTES, share READ/WRITE but not DELETE: sibling output
        // creation remains possible, while the directory cannot be replaced.
        SafeFileHandle handle = TryOpen(path, 0x80, 0x3, 0x02200000);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); return OpenFailure(path, error); }
        bool retained = false;
        try
        {
            if (!WindowsFileIdentity.TryCapture(handle, out var captured, out int error)) return OpenFailure(path, error);
            WindowsFileIdentity identity = captured!;
            if (identity.IsReparsePoint || (identity.FileAttributes & 0x10) == 0)
                return new InvalidDataException("Final observation namespace contains a redirecting or non-directory object.");
            _handles.Add(handle);
            _directories.Add(path);
            retained = true;
            return null;
        }
        finally { if (!retained) handle.Dispose(); }
    }

    private static SafeFileHandle TryOpen(string path, uint access, uint share, uint flags)
        => CreateFileW(WindowsNativePath.Extended(path), access, share, IntPtr.Zero, 3, flags, IntPtr.Zero);

    private static Exception OpenFailure(string path, int error)
        => new IOException("Could not retain immutable final observation input: " + path, new Win32Exception(error));

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(SafeFileHandle handle, out long size);

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
