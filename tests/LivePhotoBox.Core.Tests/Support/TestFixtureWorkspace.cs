using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Core.Tests.Support;

/// <summary>
/// Real-filesystem workspace for legacy tests that create fixture artifacts
/// through the Native test harness.  The isolated root is removed after each
/// test; cleanup never recursively traverses a reparse point.
/// </summary>
internal sealed class TestFixtureWorkspace : IMediaWorkspace
{
    private readonly WindowsFileIdentity _rootIdentity;
    private SafeFileHandle? _rootPinHandle;
    private bool _disposeStarted;
    private bool _disposed;

    internal TestFixtureWorkspace()
    {
        string parent = Path.Combine(Path.GetTempPath(), "LivePhotoBox", "test-fixtures");
        Directory.CreateDirectory(parent);
        RootDirectory = Path.GetFullPath(Path.Combine(parent, "fixture-" + Guid.NewGuid().ToString("N")));

        SafeFileHandle rootPin = WindowsOwnedFilePublisher.CreateOwnedWorkspaceRoot(RootDirectory);
        try
        {
            _rootIdentity = WindowsFileIdentity.Capture(rootPin);
            _rootPinHandle = rootPin;
        }
        catch
        {
            rootPin.Dispose();
            throw;
        }
    }

    public string RootDirectory { get; }

    public string AllocateFilePath(string prefix, string extension)
    {
        ObjectDisposedException.ThrowIf(_disposeStarted || _disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);

        string ext = extension.StartsWith('.') ? extension : "." + extension;
        string path = Path.GetFullPath(Path.Combine(RootDirectory, prefix + "-" + Guid.NewGuid().ToString("N") + ext));
        EnsureWithinRoot(path);
        return path;
    }

    public async Task<string> ComputeFileSha256Async(
        string filePath,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposeStarted || _disposed, this);
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        if (!File.Exists(filePath))
        {
            throw new FileNotFoundException("File not found for SHA-256 computation.", filePath);
        }

        await using var stream = new FileStream(
            filePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete,
            64 * 1024,
            useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public async Task AssertSourceUnmodifiedAsync(
        string sourcePath,
        string expectedSha256,
        CancellationToken cancellationToken = default)
    {
        string actualSha256 = await ComputeFileSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(actualSha256, expectedSha256, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Test fixture source changed: '{sourcePath}' expected {expectedSha256}, actual {actualSha256}.");
        }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposeStarted = true;

        if (_rootPinHandle is not null)
        {
            WindowsOwnedFilePublisher.ValidateWorkspaceDirectoryHandle(_rootPinHandle, RootDirectory, _rootIdentity);
            DeleteChildrenWithoutFollowingReparsePoints(RootDirectory);
            _rootPinHandle.Dispose();
            _rootPinHandle = null;
        }

        OwnedObjectCleanupProof rootProof;
        using (SafeFileHandle cleanupLease = WindowsOwnedFilePublisher.OpenWorkspaceCleanupLease(RootDirectory, _rootIdentity))
        {
            rootProof = WindowsOwnedFilePublisher.DeleteEmptyWorkspaceRootWithProof(
                cleanupLease,
                _rootIdentity,
                Path.GetDirectoryName(RootDirectory)!);
        }
        if (rootProof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked))
        {
            throw new IOException($"Exact cleanup of test fixture root '{RootDirectory}' was not proven ({rootProof}).");
        }
        _disposed = true;
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private void DeleteChildrenWithoutFollowingReparsePoints(string directory)
    {
        EnsureWithinRoot(directory);
        foreach (string child in Directory.EnumerateFileSystemEntries(directory))
        {
            string fullPath = Path.GetFullPath(child);
            EnsureWithinRoot(fullPath);
            FileAttributes attributes = File.GetAttributes(fullPath);
            bool isDirectory = (attributes & FileAttributes.Directory) != 0;
            bool isReparsePoint = (attributes & FileAttributes.ReparsePoint) != 0;

            if (isReparsePoint)
            {
                // Remove the link object itself without enumerating its target.
                if (isDirectory) Directory.Delete(fullPath, recursive: false);
                else File.Delete(fullPath);
            }
            else if (isDirectory)
            {
                DeleteChildrenWithoutFollowingReparsePoints(fullPath);
                Directory.Delete(fullPath, recursive: false);
            }
            else
            {
                File.Delete(fullPath);
            }
        }
    }

    private void EnsureWithinRoot(string path)
    {
        string rootPrefix = RootDirectory.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
        string fullPath = Path.GetFullPath(path);
        if (!string.Equals(fullPath, RootDirectory, StringComparison.OrdinalIgnoreCase) &&
            !fullPath.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Refusing to operate outside test fixture root '{RootDirectory}': '{fullPath}'.");
        }
    }
}
