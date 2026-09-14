using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

/// <summary>
/// Focused R1 probes for the one object-gone contract used by both files and
/// staging directories.  These tests deliberately exercise the real Windows
/// identity and disposition APIs; the error-classifier cases keep ambiguous
/// kernel results deterministic.
/// </summary>
public sealed class CleanerObjectGoneProofTests
{
    [Theory]
    [InlineData(2)]   // ERROR_FILE_NOT_FOUND
    [InlineData(3)]   // ERROR_PATH_NOT_FOUND
    public void OpenFileByIdErrorClassifier_DefiniteNotFound_IsNotAlive(int error)
    {
        Assert.Equal(ObjectAliveCheck.NotAlive, WindowsOwnedFilePublisher.ClassifyOpenFileByIdError(error));
    }

    [Theory]
    [InlineData(87)]  // ERROR_INVALID_PARAMETER
    [InlineData(5)]   // ERROR_ACCESS_DENIED
    [InlineData(32)]  // ERROR_SHARING_VIOLATION
    [InlineData(50)]  // ERROR_NOT_SUPPORTED
    [InlineData(1)]   // ERROR_INVALID_FUNCTION / other inconclusive failure
    public void OpenFileByIdErrorClassifier_AmbiguousKernelFailure_IsUnknown(int error)
    {
        Assert.Equal(ObjectAliveCheck.Unknown, WindowsOwnedFilePublisher.ClassifyOpenFileByIdError(error));
    }

    [Fact]
    public void OpenFileById_LiveExactFile_IsAlive()
    {
        using var workspace = new MediaWorkspace();
        string path = workspace.AllocateFilePath("r1-live", ".bin");
        File.WriteAllBytes(path, [0x4C, 0x50, 0x42]);

        WindowsFileIdentity identity = WindowsFileIdentity.Capture(path);
        Assert.Equal(ObjectAliveCheck.Alive,
            WindowsOwnedFilePublisher.IsObjectAliveByFileId(identity, workspace.RootDirectory));
    }

    [Fact]
    public void OpenFileById_GenuinelyDeletedExactFile_NeverClaimsAlive()
    {
        using var workspace = new MediaWorkspace();
        string path = workspace.AllocateFilePath("r1-deleted", ".bin");
        File.WriteAllBytes(path, [0x52, 0x31]);
        WindowsFileIdentity identity = WindowsFileIdentity.Capture(path);

        File.Delete(path);

        // A supported filesystem may return a definite NOT_FOUND or may reject
        // this FileId descriptor as unsupported.  Both are safe outcomes; the
        // latter must remain Unknown.  No other result is acceptable, and the
        // deterministic classifier tests above prove that a genuine
        // ERROR_FILE_NOT_FOUND result is the only route to NotAlive.
        ObjectAliveCheck result =
            WindowsOwnedFilePublisher.IsObjectAliveByFileId(identity, workspace.RootDirectory);
        Assert.True(
            result is ObjectAliveCheck.NotAlive or ObjectAliveCheck.Unknown,
            $"A deleted exact object may only be NotAlive on definite not-found or Unknown on an ambiguous descriptor; actual result was {result}.");
        Assert.Equal(ObjectAliveCheck.NotAlive,
            WindowsOwnedFilePublisher.ClassifyOpenFileByIdError(2));
    }

    [Fact]
    public void DeleteExactFile_UsesDeletePendingAndProvesCleanup()
    {
        using var workspace = new MediaWorkspace();
        string path = workspace.AllocateFilePath("r1-file", ".bin");
        File.WriteAllBytes(path, [0x52, 0x31, 0x2D, 0x46]);
        WindowsFileIdentity identity = WindowsFileIdentity.Capture(path);
        SafeFileHandle handle = OpenDeleteCapableHandle(path);

        try
        {
            OwnedObjectCleanupProof proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
                handle,
                identity,
                workspace.RootDirectory,
                out bool deletePendingArmed);

            Assert.True(deletePendingArmed, "the object proof must observe an armed disposition");
            Assert.Contains(proof, new[] { OwnedObjectCleanupProof.Gone, OwnedObjectCleanupProof.Unlinked });
            Assert.False(File.Exists(path));
        }
        finally
        {
            handle.Dispose();
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [Fact]
    public void DeleteExactDirectory_UsesTheSameObjectProofContract()
    {
        using var workspace = new MediaWorkspace();
        string path = Path.Combine(workspace.RootDirectory, "r1-directory");
        SafeFileHandle handle = WindowsOwnedFilePublisher.CreateOwnedDirectory(path);
        WindowsFileIdentity identity = WindowsFileIdentity.Capture(handle);

        try
        {
            OwnedObjectCleanupProof proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
                handle,
                identity,
                workspace.RootDirectory,
                out bool deletePendingArmed);

            Assert.True(deletePendingArmed, "directory cleanup must not skip the disposition stage");
            Assert.Contains(proof, new[] { OwnedObjectCleanupProof.Gone, OwnedObjectCleanupProof.Unlinked });
            Assert.False(Directory.Exists(path));
        }
        finally
        {
            handle.Dispose();
            if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
        }
    }

    [Fact]
    public void DeleteAccepted_DirectoryObjectCanStillBeAliveUntilLastHandleCloses()
    {
        using var workspace = new MediaWorkspace();
        string path = Path.Combine(workspace.RootDirectory, "r1-delete-pending-directory");
        SafeFileHandle handle = WindowsOwnedFilePublisher.CreateOwnedDirectory(path);
        WindowsFileIdentity identity = WindowsFileIdentity.Capture(handle);
        SafeFileHandle observer = OpenDirectoryObserver(path);

        try
        {
            // FileDispositionInfo returning TRUE only arms delete-pending.  A
            // second valid handle still proves that the exact directory
            // object is alive until the last handle closes, so this BOOL can
            // never itself be recorded as ExactDeleted.
            Assert.True(WindowsOwnedFilePublisher.DeleteOwnedObject(handle));
            handle.Dispose();

            WindowsFileIdentity stillOpen = WindowsFileIdentity.Capture(observer);
            Assert.Equal(identity.VolumeSerialNumber, stillOpen.VolumeSerialNumber);
            Assert.Equal(identity.FileIndex, stillOpen.FileIndex);
        }
        finally
        {
            handle.Dispose();
            observer.Dispose();
            if (Directory.Exists(path)) Directory.Delete(path);
        }
    }

    [Fact]
    public void DeleteExactFileWithHardLinkAlias_IsNotGoneProof()
    {
        using var workspace = new MediaWorkspace();
        string path = workspace.AllocateFilePath("r1-alias-source", ".bin");
        string alias = workspace.AllocateFilePath("r1-alias", ".bin");
        File.WriteAllBytes(path, [0x41, 0x4C, 0x49, 0x41, 0x53]);
        Assert.True(CreateHardLinkW(alias, path, IntPtr.Zero),
            $"CreateHardLinkW failed with Win32 error {Marshal.GetLastWin32Error()}.");

        WindowsFileIdentity identity = WindowsFileIdentity.Capture(path);
        SafeFileHandle handle = OpenDeleteCapableHandle(path);
        try
        {
            OwnedObjectCleanupProof proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
                handle,
                identity,
                workspace.RootDirectory,
                out bool deletePendingArmed);

            Assert.False(deletePendingArmed, "an existing alias must be rejected before arming disposition");
            Assert.Equal(OwnedObjectCleanupProof.AliasAlive, proof);
            Assert.True(File.Exists(path));
            Assert.True(File.Exists(alias));
            Assert.Equal(ObjectAliveCheck.Alive,
                WindowsOwnedFilePublisher.IsObjectAliveByFileId(identity, workspace.RootDirectory));
        }
        finally
        {
            handle.Dispose();
            if (File.Exists(alias)) File.Delete(alias);
            if (File.Exists(path)) File.Delete(path);
        }
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateHardLinkW(
        string lpFileName,
        string lpExistingFileName,
        IntPtr lpSecurityAttributes);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    private static SafeFileHandle OpenDeleteCapableHandle(string path)
        => CreateFileW(
            path,
            GenericRead | DeleteAccess | FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            OpenReparsePoint,
            IntPtr.Zero);

    private static SafeFileHandle OpenDirectoryObserver(string path)
        => CreateFileW(
            path,
            GenericRead | DeleteAccess | FileReadAttributes,
            FileShareRead | FileShareWrite | FileShareDelete,
            IntPtr.Zero,
            OpenExisting,
            OpenReparsePoint | BackupSemantics,
            IntPtr.Zero);

    private const uint GenericRead = 0x80000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint FileShareDelete = 0x00000004;
    private const uint OpenExisting = 3;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint BackupSemantics = 0x02000000;
}
