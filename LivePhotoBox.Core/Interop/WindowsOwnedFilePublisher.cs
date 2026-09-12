using System;
using System.Buffers.Binary;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using Microsoft.Win32.SafeHandles;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Interop;

/// <summary>
/// Minimal internal Win32 filesystem-transaction primitive for the P3 final
/// commit (Managed final commit object-identity closeout).  It exists ONLY so
/// the Cleaner can publish the object whose identity it verified: the staging
/// object is opened once, verified from the OPEN HANDLE against the Native
/// staged-output registry identity, and renamed THROUGH THAT SAME HANDLE
/// (SetFileInformationByHandle / FileRenameInfo, ReplaceIfExists = FALSE).
///
/// This closes the last ownership seam of the transaction:
///   verify A -> (handle stays open) -> rename A -> capture final identity A
/// A pathname is never re-opened between verify and rename, so a foreign
/// object that takes over the staged pathname can never be selected as the
/// publish source, and a foreign object at the destination can never be
/// overwritten (the no-overwrite decision is made atomically by the kernel
/// rename, never by a File.Exists check).
///
/// This is a narrow commit primitive, NOT a general filesystem backend (P4
/// PlatformFilesystem is out of scope): no public API, no cross-platform
/// abstraction, no change to the Native Cleaner sinks.
/// </summary>
internal static class WindowsOwnedFilePublisher
{
    /// <summary>
    /// Opens the staged object, captures its identity from the OPEN HANDLE and
    /// verifies it is exactly the object this transaction registered (volume
    /// serial + file index + single link, no reparse point).  The returned
    /// handle is OPEN; the caller must keep it open until
    /// <see cref="PublishOwnedHandle"/> completes so the verified object is
    /// the published object.
    ///
    /// The handle requests GENERIC_READ | DELETE | FILE_READ_ATTRIBUTES and
    /// grants NO FILE_SHARE_WRITE: while the commit handle is open, no other
    /// writer can modify the object's content between verify and rename.  An
    /// already-incompatible writer causes the open itself to fail closed.
    /// </summary>
    public static SafeFileHandle OpenOwnedStagedFile(string path, WindowsFileIdentity expected)
    {
        SafeFileHandle handle = CreateFileForCommit(
            path,
            CommitDesiredAccess,
            FileShareRead | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            OpenReparsePoint,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int win32Error = Marshal.GetLastWin32Error();
            throw new CleanerException(
                CleanerFailureCategory.ArtifactChangedSinceExtraction,
                CleanerFailureStage.Commit,
                SourceProtocol.Unknown,
                $"Unable to open staged object '{path}' for exact-object commit (Win32 error {win32Error}).");
        }

        try
        {
            WindowsFileIdentity actual = WindowsFileIdentity.Capture(handle);
            if (actual.IsReparsePoint ||
                actual.VolumeSerialNumber != expected.VolumeSerialNumber ||
                actual.FileIndex != expected.FileIndex ||
                actual.LinkCount != expected.LinkCount ||
                expected.LinkCount != 1)
            {
                throw new CleanerException(
                    CleanerFailureCategory.ArtifactChangedSinceExtraction,
                    CleanerFailureStage.Commit,
                    SourceProtocol.Unknown,
                    $"Staged object at '{path}' is not the exact object this transaction registered (volume/file-index/link-count/reparse mismatch); refusing to publish a foreign object.");
            }

            return handle;
        }
        catch
        {
            handle.Dispose();
            throw;
        }
    }

    /// <summary>
    /// Publishes the object referenced by <paramref name="handle"/> to
    /// <paramref name="finalPath"/> THROUGH THE HANDLE:
    ///
    ///   1. kernel-atomic no-overwrite rename (ReplaceIfExists = FALSE) —
    ///      a pre-existing destination (even one placed mid-flight) fails the
    ///      rename itself, never an overwrite;
    ///   2. final identity captured from the SAME handle and compared against
    ///      <paramref name="expected"/> (identity continuity: verify who you
    ///      publish);
    ///   3. final byte length and SHA-256 captured from the SAME handle, so
    ///      the returned evidence corresponds to the exact object that was
    ///      verified and published — never a pathname re-lookup.
    ///
    /// On failure the owned object is NOT deleted here; the caller removes it
    /// through the same handle (exact-object cleanup) after the exception.
    /// </summary>
    public static PublishedOwnedFile PublishOwnedHandle(
        SafeFileHandle handle,
        string finalPath,
        WindowsFileIdentity expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        if (!RenameThroughHandle(handle, finalPath, out int win32Error))
        {
            throw BuildPublishFailure(finalPath, win32Error);
        }

        cancellationToken.ThrowIfCancellationRequested();

        WindowsFileIdentity finalIdentity = WindowsFileIdentity.Capture(handle);
        if (finalIdentity.IsReparsePoint ||
            finalIdentity.VolumeSerialNumber != expected.VolumeSerialNumber ||
            finalIdentity.FileIndex != expected.FileIndex ||
            finalIdentity.LinkCount != expected.LinkCount)
        {
            throw new CleanerException(
                CleanerFailureCategory.ArtifactChangedSinceExtraction,
                CleanerFailureStage.Commit,
                SourceProtocol.Unknown,
                $"Published object identity changed during the same-handle rename; fail closed for '{finalPath}'.");
        }

        if (!GetFileSizeEx(handle, out long finalLength))
        {
            int sizeError = Marshal.GetLastWin32Error();
            throw new CleanerException(
                CleanerFailureCategory.PublishFailed,
                CleanerFailureStage.Commit,
                SourceProtocol.Unknown,
                $"Unable to capture the final length of the published object '{finalPath}' (Win32 error {sizeError}).");
        }

        string sha256 = ComputeSha256FromHandle(handle, finalPath, cancellationToken);

        return new PublishedOwnedFile(finalPath, finalIdentity, finalLength, sha256);
    }

    /// <summary>
    /// Marks the object referenced by <paramref name="handle"/> for deletion
    /// through the handle itself (FileDispositionInfo).  The caller holds the
    /// only verified handle, so this deletes exactly the transaction-owned
    /// object wherever it currently sits (staged pathname, race side pathname,
    /// or already-renamed destination) and can never delete a foreign object
    /// that took over a pathname.  Returns false when the disposition could
    /// not be set (the caller must then fall back to the journal rollback).
    /// </summary>
    public static bool DeleteOwnedObject(SafeFileHandle handle)
    {
        var disposition = new FileDispositionInfo { DeleteFile = 1 };
        return SetFileInformationByHandle(
            handle,
            FileDispositionInfoClass,
            ref disposition,
            (uint)Marshal.SizeOf<FileDispositionInfo>());
    }

    private static bool RenameThroughHandle(SafeFileHandle handle, string finalPath, out int win32Error)
    {
        if (string.IsNullOrWhiteSpace(finalPath))
        {
            win32Error = ErrorInvalidParameter;
            return false;
        }

        byte[] nameBytes = Encoding.Unicode.GetBytes(finalPath);
        int replaceOffset = checked((int)Marshal.OffsetOf<FileRenameInfoHeader>("ReplaceIfExists"));
        int rootOffset = checked((int)Marshal.OffsetOf<FileRenameInfoHeader>("RootDirectory"));
        int lengthOffset = checked((int)Marshal.OffsetOf<FileRenameInfoHeader>("FileNameLength"));
        int fileNameOffset = checked(lengthOffset + sizeof(uint));

        byte[] buffer = new byte[checked(fileNameOffset + nameBytes.Length)];
        Span<byte> span = buffer;
        BinaryPrimitives.WriteInt32LittleEndian(span.Slice(replaceOffset), 0); // ReplaceIfExists = FALSE
        if (IntPtr.Size == 8)
        {
            BinaryPrimitives.WriteInt64LittleEndian(span.Slice(rootOffset), 0); // RootDirectory = null
        }
        else
        {
            BinaryPrimitives.WriteInt32LittleEndian(span.Slice(rootOffset), 0);
        }
        BinaryPrimitives.WriteUInt32LittleEndian(span.Slice(lengthOffset), (uint)nameBytes.Length);
        nameBytes.CopyTo(span.Slice(fileNameOffset));

        if (!SetFileInformationByHandle(handle, FileRenameInfoClass, buffer, (uint)buffer.Length))
        {
            win32Error = Marshal.GetLastWin32Error();
            return false;
        }

        win32Error = 0;
        return true;
    }

    private static CleanerException BuildPublishFailure(string finalPath, int win32Error)
    {
        string detail = win32Error == ErrorFileExists || win32Error == ErrorAlreadyExists
            ? $"Destination file already exists: '{finalPath}'. The no-overwrite handle rename refused to replace a foreign object."
            : $"Handle-based no-overwrite publish of the owned object to '{finalPath}' failed (Win32 error {win32Error}).";
        return new CleanerException(
            CleanerFailureCategory.PublishFailed,
            CleanerFailureStage.Commit,
            SourceProtocol.Unknown,
            detail);
    }

    private static string ComputeSha256FromHandle(SafeFileHandle handle, string finalPath, CancellationToken cancellationToken)
    {
        if (!GetFileSizeEx(handle, out long length))
        {
            throw new CleanerException(
                CleanerFailureCategory.PublishFailed,
                CleanerFailureStage.Commit,
                SourceProtocol.Unknown,
                $"Unable to determine the length of the published object '{finalPath}' while computing its SHA-256 (Win32 error {Marshal.GetLastWin32Error()}).");
        }

        using var sha = SHA256.Create();
        byte[] buffer = new byte[ShaBufferSize];
        long offset = 0;
        while (offset < length)
        {
            cancellationToken.ThrowIfCancellationRequested();
            int read = RandomAccess.Read(handle, buffer, offset);
            if (read <= 0)
            {
                throw new CleanerException(
                    CleanerFailureCategory.PublishFailed,
                    CleanerFailureStage.Commit,
                    SourceProtocol.Unknown,
                    $"Short read while computing the SHA-256 of the published object '{finalPath}'.");
            }
            sha.TransformBlock(buffer, 0, read, null, 0);
            offset += read;
        }

        sha.TransformFinalBlock([], 0, 0);
        return Convert.ToHexString(sha.Hash
            ?? throw new CleanerException(
                CleanerFailureCategory.PublishFailed,
                CleanerFailureStage.Commit,
                SourceProtocol.Unknown,
                $"SHA-256 computation of the published object '{finalPath}' produced no hash."));
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInfoHeader
    {
        public int ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint FileNameLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        public byte DeleteFile;
    }

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileForCommit(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        int fileInformationClass,
        [In] byte[] fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle hFile,
        int fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(SafeFileHandle hFile, out long lpFileSize);

    // GENERIC_READ | DELETE | FILE_READ_ATTRIBUTES
    private const uint CommitDesiredAccess = 0x80000000 | 0x00010000 | 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const int FileRenameInfoClass = 3; // FILE_INFO_BY_HANDLE_CLASS::FileRenameInfo (user-mode enum; 3, NOT the ntifs FileRenameInformation=10)
    private const int FileDispositionInfoClass = 4; // FILE_INFO_BY_HANDLE_CLASS::FileDispositionInfo
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const int ErrorInvalidParameter = 87;
    private const int ShaBufferSize = 128 * 1024;
}

/// <summary>
/// Evidence produced by a same-handle publication: the final pathname, the
/// object identity captured from the publishing HANDLE (never a pathname
/// re-lookup), and the byte length / SHA-256 of that same object.  This is the
/// only source of truth for the final <see cref="MediaArtifact"/> ownership
/// fields of a committed Cleaner output.
/// </summary>
internal sealed record PublishedOwnedFile(
    string FinalPath,
    WindowsFileIdentity FileIdentity,
    long ByteLength,
    string Sha256);
