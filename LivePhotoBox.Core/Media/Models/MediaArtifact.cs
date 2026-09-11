using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace LivePhotoBox.Media.Models;

/// <summary>
/// Windows file-object identity captured at the extraction/publish boundary.
/// Content length and SHA-256 do not identify a filesystem object: a different
/// object can contain the same bytes.
/// </summary>
public sealed record WindowsFileIdentity
{
    public required uint VolumeSerialNumber { get; init; }
    public required ulong FileIndex { get; init; }
    public required uint LinkCount { get; init; }
    public required uint FileAttributes { get; init; }

    internal static WindowsFileIdentity Capture(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        using SafeFileHandle handle = CreateFileW(
            path,
            GenericRead,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            OpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                $"Unable to open '{path}' to capture its Windows file identity.");
        }

        return Capture(handle);
    }

    /// <summary>
    /// Captures object identity from an already-open handle.  Used when a file
    /// was just created/written by the transaction itself, so ownership is
    /// derived from the object we hold — never re-guessed from a pathname after
    /// another process could have replaced the file.
    /// </summary>
    internal static WindowsFileIdentity Capture(SafeFileHandle handle)
    {
        if (handle is null || handle.IsInvalid)
        {
            throw new ArgumentException("A valid open file handle is required to capture identity.", nameof(handle));
        }

        if (!GetFileInformationByHandle(handle, out ByHandleFileInformation info))
        {
            throw new System.ComponentModel.Win32Exception(
                Marshal.GetLastWin32Error(),
                "Unable to capture the Windows file identity from the open handle.");
        }

        return new WindowsFileIdentity
        {
            VolumeSerialNumber = info.VolumeSerialNumber,
            FileIndex = ((ulong)info.FileIndexHigh << 32) | info.FileIndexLow,
            LinkCount = info.NumberOfLinks,
            FileAttributes = info.FileAttributes
        };
    }

    internal bool Matches(WindowsFileIdentity other) => this == other;

    internal bool IsReparsePoint => (FileAttributes & ReparsePointAttribute) != 0;

    [StructLayout(LayoutKind.Sequential)]
    private struct NativeFileTime
    {
        public uint LowDateTime;
        public uint HighDateTime;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ByHandleFileInformation
    {
        public uint FileAttributes;
        public NativeFileTime CreationTime;
        public NativeFileTime LastAccessTime;
        public NativeFileTime LastWriteTime;
        public uint VolumeSerialNumber;
        public uint FileSizeHigh;
        public uint FileSizeLow;
        public uint NumberOfLinks;
        public uint FileIndexHigh;
        public uint FileIndexLow;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileInformationByHandle(
        SafeFileHandle fileHandle,
        out ByHandleFileInformation fileInformation);

    private const uint GenericRead = 0x80000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint ReparsePointAttribute = 0x00000400;
}

/// <summary>
/// Immutable typed media artifact representation within a transaction workspace or source.
/// </summary>
public sealed record MediaArtifact
{
    public required string Path { get; init; }
    public required MediaArtifactKind Kind { get; init; }
    public string MimeType { get; init; } = string.Empty;
    public ImageContainer ImageContainer { get; init; } = ImageContainer.Unknown;
    public ImageCodec ImageCodec { get; init; } = ImageCodec.Unknown;
    public VideoContainer VideoContainer { get; init; } = VideoContainer.Unknown;
    public VideoCodec VideoCodec { get; init; } = VideoCodec.Unknown;
    public long ByteLength { get; init; }
    public long SourceOffset { get; init; }
    public string? Sha256 { get; init; }
    /// <summary>
    /// Filesystem object identity captured when the artifact was produced.
    /// This is separate from content integrity and is required for cleanup
    /// artifacts that may later be used by a destructive cleaner.
    /// </summary>
    public WindowsFileIdentity? FileIdentity { get; init; }
}
