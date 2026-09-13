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
    /// <paramref name="finalPath"/> THROUGH THE HANDLE.  ALL fallible work —
    /// cancellation, identity verification, byte length and SHA-256 — runs
    /// BEFORE the rename, so the kernel rename is the LAST filesystem-state-
    /// changing operation of this method:
    ///
    ///   1. identity captured from the SAME handle and compared against
    ///      <paramref name="expected"/> (identity continuity: verify who you
    ///      publish).  A rename does not change the filesystem object, and the
    ///      commit handle is held without FILE_SHARE_WRITE, so no writer can
    ///      alter the content between this capture and the rename;
    ///   2. byte length and SHA-256 captured from the SAME handle, so the
    ///      returned evidence corresponds to the exact object that was
    ///      verified and published — never a pathname re-lookup;
    ///   3. kernel-atomic no-overwrite rename (ReplaceIfExists = FALSE) as the
    ///      LAST step — a pre-existing destination (even one placed
    ///      mid-flight) fails the rename itself, never an overwrite.
    ///
    /// A throw from this method therefore means the rename did NOT happen and
    /// the object is still at its staged pathname.  The caller registers the
    /// publication in the transaction journal BEFORE calling this method, so
    /// the journal always knows the object's real location; on failure the
    /// caller removes the owned object through the same handle (exact-object
    /// cleanup) after the exception.
    /// </summary>
    public static PublishedOwnedFile PublishOwnedHandle(
        SafeFileHandle handle,
        string finalPath,
        WindowsFileIdentity expected,
        CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        WindowsFileIdentity identity = WindowsFileIdentity.Capture(handle);
        if (identity.IsReparsePoint ||
            identity.VolumeSerialNumber != expected.VolumeSerialNumber ||
            identity.FileIndex != expected.FileIndex ||
            identity.LinkCount != expected.LinkCount)
        {
            throw new CleanerException(
                CleanerFailureCategory.ArtifactChangedSinceExtraction,
                CleanerFailureStage.Commit,
                SourceProtocol.Unknown,
                $"Staged object identity changed before the same-handle rename; fail closed for '{finalPath}'.");
        }

        if (!GetFileSizeEx(handle, out long finalLength))
        {
            int sizeError = Marshal.GetLastWin32Error();
            throw new CleanerException(
                CleanerFailureCategory.PublishFailed,
                CleanerFailureStage.Commit,
                SourceProtocol.Unknown,
                $"Unable to capture the length of the object being published to '{finalPath}' (Win32 error {sizeError}).");
        }

        string sha256 = ComputeSha256FromHandle(handle, finalPath, cancellationToken);

        // LAST filesystem-state-changing operation: kernel-atomic no-overwrite
        // rename through the verified handle.  Nothing fallible runs after it,
        // so a throw from this method implies the rename never happened and
        // the object is still at its staged pathname.
        if (!RenameThroughHandle(handle, finalPath, out int win32Error))
        {
            throw BuildPublishFailure(finalPath, win32Error);
        }

        return new PublishedOwnedFile(finalPath, identity, finalLength, sha256);
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

    /// <summary>
    /// True when the object referenced by <paramref name="handle"/> has ZERO
    /// links — a foreign actor already unlinked its pathname while the handle
    /// was retained (e.g. File.Delete on the destination).  A zero-link
    /// object exists under NO pathname and is freed when the last handle
    /// closes, so it can never leak as a transaction-owned artifact: a failed
    /// disposition on it (which is always what happens for an unlinked file)
    /// is NOT a cleanup failure.  The caller still keeps the published record
    /// so rollback can verify the destination pathname itself (a foreign
    /// occupant is then refused by identity).
    /// </summary>
    public static bool IsObjectUnlinked(SafeFileHandle handle)
    {
        try
        {
            return WindowsFileIdentity.Capture(handle).LinkCount == 0;
        }
        catch
        {
            return false;
        }
    }

    /// <summary>
    /// Opens a directory for exact-object ownership (staging directory).
    /// The handle requests GENERIC_READ | DELETE and grants NO FILE_SHARE_WRITE,
    /// mirroring the file commit lease: while the transaction retains it, no
    /// foreign process can mutate the directory (create/delete entries), and
    /// the transaction can delete the directory through the handle itself no
    /// matter what pathname it currently occupies (rename-away is irrelevant).
    /// Identity is captured from the OPEN HANDLE and must not be a reparse
    /// point (BACKUP_SEMANTICS + OPEN_REPARSE_POINT never follows one).
    /// </summary>
    public static SafeFileHandle OpenOwnedDirectory(string path)
    {
        SafeFileHandle handle = CreateFileForCommit(
            path,
            CommitDesiredAccess,
            FileShareRead | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            OpenReparsePoint | BackupSemantics,
            IntPtr.Zero);
        if (handle.IsInvalid)
        {
            int win32Error = Marshal.GetLastWin32Error();
            throw new CleanerException(
                CleanerFailureCategory.ArtifactChangedSinceExtraction,
                CleanerFailureStage.Commit,
                SourceProtocol.Unknown,
                $"Unable to open directory '{path}' for exact-object ownership (Win32 error {win32Error}).");
        }

        try
        {
            WindowsFileIdentity actual = WindowsFileIdentity.Capture(handle);
            if (actual.IsReparsePoint)
            {
                throw new CleanerException(
                    CleanerFailureCategory.ArtifactChangedSinceExtraction,
                    CleanerFailureStage.Commit,
                    SourceProtocol.Unknown,
                    $"Directory at '{path}' is a reparse point; refusing to treat a redirecting directory as a transaction-owned object.");
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
    /// Kernel-backed "does THIS exact filesystem object still exist anywhere?"
    /// query.  After a disposition/delete sequence the object is looked up by
    /// its exact 64-bit FileId on its volume (FILE_OPEN_BY_FILE_ID) — no
    /// pathname is consulted and no namespace is scanned.  A disposition
    /// success alone is NOT proof that the object is gone: an external actor
    /// may have created a hard link while the retained handle was open (the
    /// NTFS share semantics do NOT deny hard-link creation; see probe H1), in
    /// which case the object survives under the alias and the by-FileId lookup
    /// still succeeds.  Gone means the lookup fails; Alive means the object
    /// still exists (cleanup is unproven); Unknown means the volume itself
    /// could not be opened, which must fail closed, never be reported as gone.
    /// </summary>
    public static ObjectGoneCheck IsObjectGoneByFileId(WindowsFileIdentity identity, string volumeProbePath)
    {
        SafeFileHandle? handle = null;
        try
        {
            string volumeName = GetVolumeNameForPath(volumeProbePath, out bool resolved);
            if (!resolved)
            {
                return ObjectGoneCheck.Unknown;
            }

            using SafeFileHandle volumeHandle = CreateFileForCommit(
                volumeName,
                GenericRead,
                FileShareRead | FileShareWrite | FileShareDelete,
                IntPtr.Zero,
                OpenExisting,
                BackupSemantics,
                IntPtr.Zero);
            if (volumeHandle.IsInvalid)
            {
                return ObjectGoneCheck.Unknown;
            }

            byte[] idBytes = BitConverter.GetBytes(identity.FileIndex);
            GCHandle idPinned = GCHandle.Alloc(idBytes, GCHandleType.Pinned);
            try
            {
                var objectName = new UnicodeString
                {
                    Length = checked((ushort)idBytes.Length),
                    MaximumLength = checked((ushort)idBytes.Length),
                    Buffer = idPinned.AddrOfPinnedObject()
                };
                GCHandle namePinned = GCHandle.Alloc(objectName, GCHandleType.Pinned);
                try
                {
                    var attributes = new ObjectAttributes
                    {
                        Length = checked((uint)Marshal.SizeOf<ObjectAttributes>()),
                        RootDirectory = volumeHandle.DangerousGetHandle(),
                        ObjectName = namePinned.AddrOfPinnedObject(),
                        Attributes = ObjCaseInsensitive
                    };
                    int status = NtOpenFile(
                        out handle,
                        FileReadAttributes,
                        ref attributes,
                        out _,
                        FileShareRead | FileShareWrite | FileShareDelete,
                        FileOpenByFileId | FileNonDirectoryFile);
                    if (status == 0)
                    {
                        return ObjectGoneCheck.Alive;
                    }
                    // "Gone" may ONLY be concluded from the kernel statuses that
                    // prove the exact FileId is no longer resolvable.  Any other
                    // failure (e.g. STATUS_ACCESS_DENIED while the object is
                    // alive, transient system errors) is Unknown and the caller
                    // must fail closed — never report the object as gone while it
                    // may still exist under an alias.
                    if (status is StatusObjectNameNotFound or StatusObjectPathNotFound)
                    {
                        return ObjectGoneCheck.Gone;
                    }
                    return ObjectGoneCheck.Unknown;
                }
                finally
                {
                    namePinned.Free();
                }
            }
            finally
            {
                idPinned.Free();
            }
        }
        catch
        {
            return ObjectGoneCheck.Unknown;
        }
        finally
        {
            handle?.Dispose();
        }
    }

    private static string GetVolumeNameForPath(string path, out bool resolved)
    {
        resolved = false;
        var root = new System.Text.StringBuilder(512);
        if (!GetVolumePathNameW(path, root, (uint)root.Capacity))
        {
            return string.Empty;
        }
        var volume = new System.Text.StringBuilder(512);
        if (!GetVolumeNameForVolumeMountPointW(root.ToString(), volume, (uint)volume.Capacity))
        {
            return string.Empty;
        }
        resolved = true;
        return volume.ToString();
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

    private const uint GenericRead = 0x80000000;
    private const uint FileShareWrite = 0x00000002;
    private const uint BackupSemantics = 0x02000000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint ObjCaseInsensitive = 0x00000040;
    private const uint FileOpenByFileId = 0x00002000;
    private const uint FileNonDirectoryFile = 0x00000040;

    // NTSTATUS values: the ONLY ones that prove a FileId is no longer
    // resolvable (probe F2/F3: a deleted object resolves 0xC000000D).
    private const int StatusObjectNameNotFound = unchecked((int)0xC000000D);
    private const int StatusObjectPathNotFound = unchecked((int)0xC000000F);

    [StructLayout(LayoutKind.Sequential)]
    private struct UnicodeString
    {
        public ushort Length;
        public ushort MaximumLength;
        public IntPtr Buffer;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ObjectAttributes
    {
        public uint Length;
        public IntPtr RootDirectory;
        public IntPtr ObjectName;
        public uint Attributes;
        public IntPtr SecurityDescriptor;
        public IntPtr SecurityQualityOfService;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoStatusBlock
    {
        public IntPtr Status;
        public IntPtr Information;
    }

    [DllImport("ntdll.dll")]
    private static extern int NtOpenFile(
        out SafeFileHandle fileHandle,
        uint desiredAccess,
        ref ObjectAttributes objectAttributes,
        out IoStatusBlock ioStatusBlock,
        uint shareAccess,
        uint openOptions);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumePathNameW(
        string lpszFileName,
        StringBuilder lpszVolumePathName,
        uint cchBufferLength);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetVolumeNameForVolumeMountPointW(
        string lpszVolumeMountPoint,
        StringBuilder lpszVolumeName,
        uint cchBufferLength);
}

/// <summary>
/// Result of the kernel-backed by-FileId object-existence query.  Gone is
/// reported ONLY when the exact FileId can no longer be resolved on its
/// volume; Alive means the object still exists under some namespace entry;
/// Unknown means the volume could not be opened and the query is inconclusive
/// (callers must fail closed — never treat Unknown as gone).
/// </summary>
public enum ObjectGoneCheck
{
    Gone,
    Alive,
    Unknown
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
