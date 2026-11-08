using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Win32.SafeHandles;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;

namespace LivePhotoBox.Media.Workspace;

public sealed class MediaWorkspace : IMediaWorkspace
{
    private const int MaximumDisposeAttempts = 3;
    private const uint FileAttributeDirectory = 0x00000010;

    private readonly string _rootDirectory;
    private readonly object _sync = new();
    private readonly object _disposeGate = new();
    private readonly Dictionary<string, WorkspaceFileReceipt> _ownedFiles = new(StringComparer.OrdinalIgnoreCase);
    private readonly WindowsFileIdentity _rootIdentity;
    private readonly bool _isProduction;
    private SafeFileHandle? _rootPinHandle;
    private SafeFileHandle? _rootRecoveryHandle;
    private bool _disposeStarted;
    private bool _disposed;
    private int _disposeAttempts;

    internal static AsyncLocal<Action<MediaWorkspaceCleanupStage, string>?> TestCleanupHook { get; } = new();
    internal static AsyncLocal<Action<string>?> BeforeCleanupEnumerationForTests { get; } = new();

    public string RootDirectory => _rootDirectory;

    public WorkspaceCleanupAttempt? LastCleanupAttempt { get; private set; }

    public sealed record WorkspaceCleanupAttempt(bool Succeeded, Exception? Failure, string? LimitationMessage);

    internal sealed record CreationAttempt(MediaWorkspace? Workspace, Exception? Failure);

    internal static CreationAttempt TryCreate(CancellationToken token = default, string? parentDirectory = null)
    {
        if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
        string parent = parentDirectory ?? Path.Combine(Path.GetTempPath(), "LivePhotoBox");
        if (OutputDirectoryAttempt.Ensure(parent) is { } parentFailure) return new(null, parentFailure);
        string root = Path.GetFullPath(Path.Combine(parent, "ws-" + Guid.NewGuid().ToString("N")));
        var creation = WindowsOwnedFilePublisher.TryCreateOwnedWorkspaceRoot(root);
        if (creation.Failure is { } creationFailure) return new(null, creationFailure);
        var pin = creation.Handle!;
        var identity = WindowsOwnedFilePublisher.TryValidateWorkspaceDirectoryHandle(pin, root, null);
        if (identity.Failure is { } identityFailure)
        {
            pin.Dispose();
            return new(null, new IOException($"Workspace identity could not be established; preserving private path '{root}'.", identityFailure));
        }
        var recovery = WindowsOwnedFilePublisher.TryOpenWorkspaceRecoveryHandle(root, identity.Identity!);
        if (recovery.Failure is { } recoveryFailure)
        {
            // Do not delete a path after losing exact cleanup authority. This
            // empty private root is reported; no source or media was written.
            pin.Dispose();
            return new(null, new IOException($"Workspace recovery handle unavailable; preserving empty private path '{root}'.", recoveryFailure));
        }
        var workspace = new MediaWorkspace(root, pin, recovery.Handle!, identity.Identity!, isProduction: true);
        WorkspaceCleanupScope.Register(workspace);
        return new(workspace, null);
    }

    private MediaWorkspace(string root, SafeFileHandle pin, SafeFileHandle recovery, WindowsFileIdentity identity, bool isProduction = false)
    {
        _rootDirectory = root; _rootIdentity = identity;
        _rootPinHandle = pin; _rootRecoveryHandle = recovery;
        _isProduction = isProduction;
        WorkspaceOwnershipRegistry.RegisterWorkspace(this, root);
    }

    public MediaWorkspace()
    {
        string parent = Path.Combine(Path.GetTempPath(), "LivePhotoBox");
        Directory.CreateDirectory(parent);
        _rootDirectory = Path.GetFullPath(Path.Combine(parent, "ws-" + Guid.NewGuid().ToString("N")));

        SafeFileHandle? rootPin = null;
        SafeFileHandle? rootRecovery = null;
        WindowsFileIdentity? rootIdentity = null;
        try
        {
            rootPin = WindowsOwnedFilePublisher.CreateOwnedWorkspaceRoot(_rootDirectory);
            rootIdentity = WindowsFileIdentity.Capture(rootPin);
            rootRecovery = WindowsOwnedFilePublisher.OpenWorkspaceRecoveryHandle(_rootDirectory, rootIdentity);

            _rootIdentity = rootIdentity;
            _rootPinHandle = rootPin;
            _rootRecoveryHandle = rootRecovery;
            WorkspaceOwnershipRegistry.RegisterWorkspace(this, _rootDirectory);
        }
        catch (Exception constructorFailure)
        {
            rootRecovery?.Dispose();
            rootPin?.Dispose();

            if (rootIdentity is not null)
            {
                try
                {
                    using SafeFileHandle cleanupLease = WindowsOwnedFilePublisher.OpenWorkspaceCleanupLease(
                        _rootDirectory,
                        rootIdentity);
                    OwnedObjectCleanupProof proof = WindowsOwnedFilePublisher.DeleteEmptyWorkspaceRootWithProof(
                        cleanupLease,
                        rootIdentity,
                        parent);
                    if (proof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked))
                    {
                        throw new IOException($"Exact cleanup of partially constructed workspace root '{_rootDirectory}' was not proven ({proof}).");
                    }
                }
                catch (Exception cleanupFailure)
                {
                    throw new IOException(
                        $"Workspace construction failed and its exact root cleanup could not be proven; preserving '{_rootDirectory}'.",
                        new AggregateException(constructorFailure, cleanupFailure));
                }
            }

            throw;
        }
    }

    public string AllocateFilePath(string prefix, string extension)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(prefix);
        ArgumentException.ThrowIfNullOrWhiteSpace(extension);
        lock (_sync)
        {
            ThrowIfNotActive();
            string ext = extension.StartsWith('.') ? extension : "." + extension;
            string fileName = $"{prefix}-{Guid.NewGuid():N}{ext}";
            return Path.Combine(_rootDirectory, fileName);
        }
    }

    public async Task<string> ComputeFileSha256Async(string filePath, CancellationToken cancellationToken = default)
    {
        lock (_sync) ThrowIfNotActive();
        if (!File.Exists(filePath))
            throw new FileNotFoundException("File not found for SHA256 computation.", filePath);

        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 64 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream, cancellationToken).ConfigureAwait(false);
        return Convert.ToHexString(hash);
    }

    public async Task AssertSourceUnmodifiedAsync(string sourcePath, string expectedSha256, CancellationToken cancellationToken = default)
    {
        string actualSha = await ComputeFileSha256Async(sourcePath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(expectedSha256, actualSha, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                $"Source file immutability violation: '{sourcePath}' was modified in-place! Expected {expectedSha256}, got {actualSha}.");
        }
    }

    internal bool RegisterCreatedFile(
        string path,
        WindowsFileIdentity identity,
        long byteLength,
        string? sha256,
        string role)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(identity);
        ArgumentException.ThrowIfNullOrWhiteSpace(role);
        if (byteLength < 0) throw new ArgumentOutOfRangeException(nameof(byteLength));

        string normalizedPath = ValidateDirectChildPath(path);
        ValidateOwnedFileIdentity(identity, normalizedPath);
        lock (_sync)
        {
            ThrowIfNotActive();
            var receipt = new WorkspaceFileReceipt(normalizedPath, identity, byteLength, sha256, role);
            if (_ownedFiles.TryGetValue(normalizedPath, out WorkspaceFileReceipt? existing))
            {
                if (!SameFileId(existing.Identity, identity))
                    throw new IOException($"A different file object is already registered for workspace path '{normalizedPath}'.");
                _ownedFiles[normalizedPath] = receipt;
                return true;
            }

            _ownedFiles.Add(normalizedPath, receipt);
            return true;
        }
    }

    internal bool RegisterPublishedFile(PublishedOwnedFile receipt, bool newlyCreatedObject)
    {
        ArgumentNullException.ThrowIfNull(receipt);
        string normalizedPath = ValidateDirectChildPath(receipt.FinalPath);
        ValidateOwnedFileIdentity(receipt.FileIdentity, normalizedPath);
        if (receipt.ByteLength < 0) throw new IOException($"Published workspace receipt has a negative length for '{normalizedPath}'.");

        lock (_sync)
        {
            ThrowIfNotActive();
            var updated = new WorkspaceFileReceipt(
                normalizedPath,
                receipt.FileIdentity,
                receipt.ByteLength,
                receipt.Sha256,
                "managed-published-file");

            string? priorPath = null;
            foreach (KeyValuePair<string, WorkspaceFileReceipt> entry in _ownedFiles)
            {
                if (SameFileId(entry.Value.Identity, receipt.FileIdentity) &&
                    !string.Equals(entry.Key, normalizedPath, StringComparison.OrdinalIgnoreCase))
                {
                    priorPath = entry.Key;
                    break;
                }
            }
            if (_ownedFiles.TryGetValue(normalizedPath, out WorkspaceFileReceipt? existing))
            {
                if (!SameFileId(existing.Identity, receipt.FileIdentity))
                    throw new IOException($"A different file object is already registered for workspace path '{normalizedPath}'.");
                if (priorPath != null) _ownedFiles.Remove(priorPath);
                _ownedFiles[normalizedPath] = updated;
                return true;
            }

            if (!newlyCreatedObject && priorPath == null)
            {
                throw new IOException(
                    $"Refusing to claim existing workspace target '{normalizedPath}' without a previously registered exact object identity.");
            }

            _ownedFiles.Add(normalizedPath, updated);
            if (priorPath != null) _ownedFiles.Remove(priorPath);
            return true;
        }
    }

    internal bool RetireFile(
        string path,
        WindowsFileIdentity expectedIdentity,
        OwnedObjectCleanupProof proof)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        string normalizedPath = ValidateDirectChildPath(path);

        if (proof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked))
            throw new IOException($"Workspace receipt '{normalizedPath}' cannot be retired without exact gone/unlinked proof ({proof}).");

        lock (_sync)
        {
            if (!_ownedFiles.TryGetValue(normalizedPath, out WorkspaceFileReceipt? existing))
                return false;
            if (!SameFileId(existing.Identity, expectedIdentity))
                throw new IOException($"Workspace receipt identity changed before retirement for '{normalizedPath}'.");

            _ownedFiles.Remove(normalizedPath);
            return true;
        }
    }

    /// <summary>
    /// Retires any receipt for an exact filesystem object after a proof-based
    /// deletion, regardless of the pathname currently recorded in the
    /// workspace.  A producer can register its final path before a handle
    /// rename commits; if rollback later deletes that same object while it is
    /// still at its staging path, the FileId is the only stable join key.
    /// </summary>
    internal void RetireFileByIdentity(
        WindowsFileIdentity expectedIdentity,
        OwnedObjectCleanupProof proof)
    {
        if (expectedIdentity is null ||
            proof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked) ||
            (expectedIdentity.FileAttributes & FileAttributeDirectory) != 0)
        {
            return;
        }

        lock (_sync)
        {
            while (true)
            {
                string? matchingPath = null;
                foreach (KeyValuePair<string, WorkspaceFileReceipt> entry in _ownedFiles)
                {
                    if (SameFileId(entry.Value.Identity, expectedIdentity))
                    {
                        matchingPath = entry.Key;
                        break;
                    }
                }

                if (matchingPath is null) return;
                _ownedFiles.Remove(matchingPath);
            }
        }
    }

    /// <summary>Read-only exact-ID view for transaction tests.</summary>
    internal bool HasRegisteredFile(string path, WindowsFileIdentity expectedIdentity)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(expectedIdentity);
        string normalizedPath = WorkspaceOwnershipRegistry.NormalizePath(path);
        lock (_sync)
        {
            return _ownedFiles.TryGetValue(normalizedPath, out WorkspaceFileReceipt? receipt) &&
                SameFileId(receipt.Identity, expectedIdentity);
        }
    }

    public WorkspaceCleanupAttempt TryCleanup()
    {
        lock (_disposeGate)
        {
            return TryCleanupCore();
        }
    }

    public void Dispose()
    {
        lock (_disposeGate)
            DisposeCore();
    }

    private void DisposeCore()
    {
        var attempt = TryCleanupCore();
        if (!attempt.Succeeded && !_isProduction)
        {
            throw CleanupFailed(attempt.Failure!);
        }
    }

    public ValueTask DisposeAsync()
    {
        Dispose();
        return ValueTask.CompletedTask;
    }

    private WorkspaceCleanupAttempt TryCleanupCore()
    {
        lock (_sync)
        {
            if (_disposed)
                return LastCleanupAttempt ?? new WorkspaceCleanupAttempt(true, null, null);
            if (_disposeAttempts >= MaximumDisposeAttempts)
            {
                return FailCleanup(
                    new IOException($"The bounded workspace cleanup retry limit ({MaximumDisposeAttempts}) was exhausted."),
                    "工作区清理重试次数已达上限");
            }

            _disposeAttempts++;
            _disposeStarted = true;
        }

        try
        {
            if (_rootPinHandle is not null)
            {
                var pinValidation = WindowsOwnedFilePublisher.TryValidateWorkspaceDirectoryHandle(
                    _rootPinHandle,
                    _rootDirectory,
                    _rootIdentity);
                _rootPinHandle.Dispose();
                _rootPinHandle = null;
                if (pinValidation.Failure is { } pinFailure)
                {
                    return FailCleanup(pinFailure, $"工作区引脚句柄验证失败（{pinFailure.Message}）；已安全保留临时目录。");
                }
            }

            try
            {
                TestCleanupHook.Value?.Invoke(MediaWorkspaceCleanupStage.PinReleasedBeforeCleanupLease, _rootDirectory);
            }
            catch (Exception hookEx)
            {
                return FailCleanup(hookEx, $"工作区清理测试钩子报告限制（{hookEx.Message}）；已安全保留临时目录。");
            }

            if (!Directory.Exists(_rootDirectory) && !File.Exists(_rootDirectory))
            {
                _rootRecoveryHandle?.Dispose();
                _rootRecoveryHandle = null;
                ObjectAliveCheck rootAlive = WindowsOwnedFilePublisher.IsObjectAliveByFileId(
                    _rootIdentity,
                    Path.GetDirectoryName(_rootDirectory)!);
                if (rootAlive != ObjectAliveCheck.NotAlive)
                {
                    return FailCleanup(
                        new IOException($"Workspace root '{_rootDirectory}' is missing by pathname but its exact FileId is not proven gone ({rootAlive})."),
                        $"工作区根目录按路径缺失但对象未获证实销毁；已安全保留。");
                }
                FinishDispose();
                var missingSuccess = new WorkspaceCleanupAttempt(true, null, null);
                LastCleanupAttempt = missingSuccess;
                return missingSuccess;
            }

            var leaseAttempt = WindowsOwnedFilePublisher.TryOpenWorkspaceCleanupLease(
                _rootDirectory,
                _rootIdentity);
            if (leaseAttempt.Failure is { } leaseFailure)
            {
                return FailCleanup(leaseFailure, $"无法获取工作区清理租约（{leaseFailure.Message}）；已安全保留临时工作区。");
            }

            SafeFileHandle cleanupLease = leaseAttempt.Handle!;
            try
            {
                var dirValidation = WindowsOwnedFilePublisher.TryValidateWorkspaceDirectoryHandle(
                    cleanupLease,
                    _rootDirectory,
                    _rootIdentity);
                if (dirValidation.Failure is { } dirFailure)
                {
                    return FailCleanup(dirFailure, $"工作区清理租约句柄验证失败（{dirFailure.Message}）；已安全保留临时工作区。");
                }

                WorkspaceFileReceipt[] receipts;
                lock (_sync) receipts = [.. _ownedFiles.Values];
                var actualPaths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                BeforeCleanupEnumerationForTests.Value?.Invoke(_rootDirectory);
                var enumeration = ScanFileSystem.Enumerate(_rootDirectory, CancellationToken.None);
                if (enumeration.Failure is { } enumerationFailure)
                    return FailCleanup(enumerationFailure, $"无法枚举工作区内容（{enumerationFailure.Message}）；已安全保留临时工作区。");
                foreach (string childPath in enumeration.Entries!)
                {
                    string normalizedChild = WorkspaceOwnershipRegistry.NormalizePath(childPath);
                    if (!actualPaths.TryAdd(normalizedChild, childPath))
                    {
                        return FailCleanup(
                            new IOException($"Workspace root contains an ambiguous duplicate child path '{normalizedChild}'."),
                            $"工作区包含重名冲突项 '{normalizedChild}'；已安全保留临时工作区。");
                    }
                }

                var receiptByPath = new Dictionary<string, WorkspaceFileReceipt>(StringComparer.OrdinalIgnoreCase);
                foreach (WorkspaceFileReceipt receipt in receipts)
                    receiptByPath.Add(receipt.Path, receipt);

                foreach (string childPath in actualPaths.Keys)
                {
                    if (!receiptByPath.ContainsKey(childPath))
                    {
                        return FailCleanup(
                            new IOException($"Workspace root contains an unregistered child '{childPath}'; preserving the root and all unknown content."),
                            $"工作区检测到未登记的外来文件或目录 '{childPath}'；已安全保留临时目录及其内容。");
                    }
                }

                var openedFiles = new List<(WorkspaceFileReceipt Receipt, SafeFileHandle Handle)>();
                try
                {
                    foreach (WorkspaceFileReceipt receipt in receipts)
                    {
                        if (!actualPaths.ContainsKey(receipt.Path))
                        {
                            ObjectAliveCheck objectAlive = WindowsOwnedFilePublisher.IsObjectAliveByFileId(
                                receipt.Identity,
                                _rootDirectory);
                            if (objectAlive != ObjectAliveCheck.NotAlive)
                            {
                                return FailCleanup(
                                    new IOException($"Registered workspace file '{receipt.Path}' is absent, but its exact FileId is not proven gone ({objectAlive})."),
                                    $"登记的临时文件 '{receipt.Path}' 缺失且对象未获证实销毁；已安全保留临时工作区。");
                            }
                            continue;
                        }

                        var openAttempt = WindowsOwnedFilePublisher.TryOpenOwnedStagedFile(
                            actualPaths[receipt.Path],
                            receipt.Identity);
                        if (openAttempt.Failure is { } openFailure)
                        {
                            return FailCleanup(openFailure, $"临时文件 '{receipt.Path}' 被占用或对象已被替换（{openFailure.Message}）；已安全保留相关文件及工作区。");
                        }
                        openedFiles.Add((receipt, openAttempt.Handle!));
                    }

                    foreach (var opened in openedFiles)
                    {
                        OwnedObjectCleanupProof proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
                            opened.Handle,
                            opened.Receipt.Identity,
                            _rootDirectory,
                            out _);
                        if (proof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked))
                        {
                            return FailCleanup(
                                new IOException($"Exact cleanup of registered workspace file '{opened.Receipt.Path}' was not proven ({proof})."),
                                $"临时文件 '{opened.Receipt.Path}' 的删除未获证实（{proof}）；已保留临时工作区。");
                        }
                    }
                }
                finally
                {
                    foreach (var opened in openedFiles)
                        opened.Handle.Dispose();
                }

                var remaining = ScanFileSystem.Enumerate(_rootDirectory, CancellationToken.None);
                if (remaining.Failure is { } remainingFailure)
                    return FailCleanup(remainingFailure, $"无法确认工作区剩余内容（{remainingFailure.Message}）；已安全保留工作区。");
                if (remaining.Entries!.Count > 0)
                {
                    string residual = remaining.Entries[0];
                    return FailCleanup(
                        new IOException($"Workspace root still contains '{residual}' after exact registered-file cleanup; preserving the root."),
                        $"清理已登记临时文件后工作区仍有残留项 '{residual}'；已安全保留工作区。");
                }

                _rootRecoveryHandle?.Dispose();
                _rootRecoveryHandle = null;

                OwnedObjectCleanupProof rootProof = WindowsOwnedFilePublisher.DeleteEmptyWorkspaceRootWithProof(
                    cleanupLease,
                    _rootIdentity,
                    Path.GetDirectoryName(_rootDirectory)!);
                cleanupLease = null!; // DeleteEmptyWorkspaceRootWithProof consumes the lease on every result.
                if (rootProof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked))
                {
                    return FailCleanup(
                        new IOException($"Exact deletion of workspace root '{_rootDirectory}' was not proven ({rootProof})."),
                        $"工作区根目录 '{_rootDirectory}' 的删除未获证实（{rootProof}）；已保留临时路径。");
                }
            }
            finally
            {
                cleanupLease?.Dispose();
            }

            FinishDispose();
            var success = new WorkspaceCleanupAttempt(true, null, null);
            LastCleanupAttempt = success;
            return success;
        }
        catch (Exception ex)
        {
            return FailCleanup(ex, $"工作区清理发生意外错误（{ex.Message}）；已安全保留临时目录。");
        }
    }

    private WorkspaceCleanupAttempt FailCleanup(Exception failure, string limitation)
    {
        var attempt = new WorkspaceCleanupAttempt(false, failure, limitation);
        LastCleanupAttempt = attempt;
        if (_isProduction)
        {
            _rootPinHandle?.Dispose();
            _rootPinHandle = null;
            _rootRecoveryHandle?.Dispose();
            _rootRecoveryHandle = null;
            lock (_sync)
            {
                _disposed = true;
            }
            WorkspaceOwnershipRegistry.UnregisterWorkspace(this, _rootDirectory);
        }
        return attempt;
    }

    private string ValidateDirectChildPath(string path)
    {
        string normalizedPath = WorkspaceOwnershipRegistry.NormalizePath(path);
        string? parent = Path.GetDirectoryName(normalizedPath);
        if (string.IsNullOrEmpty(parent) ||
            !string.Equals(
                WorkspaceOwnershipRegistry.NormalizePath(parent),
                WorkspaceOwnershipRegistry.NormalizePath(_rootDirectory),
                StringComparison.OrdinalIgnoreCase))
        {
            throw new IOException($"Workspace ownership receipt path '{normalizedPath}' is not a direct child of '{_rootDirectory}'.");
        }

        return normalizedPath;
    }

    private void ValidateOwnedFileIdentity(WindowsFileIdentity identity, string path)
    {
        if (identity.IsReparsePoint ||
            (identity.FileAttributes & FileAttributeDirectory) != 0 ||
            identity.LinkCount != 1 ||
            identity.VolumeSerialNumber != _rootIdentity.VolumeSerialNumber)
        {
            throw new IOException($"Workspace file receipt for '{path}' is not a single-link regular file on the exact workspace volume.");
        }
    }

    private void ThrowIfNotActive()
    {
        if (_disposed || _disposeStarted)
            throw new ObjectDisposedException(nameof(MediaWorkspace), "This workspace is disposing or has already been disposed.");
    }

    private void FinishDispose()
    {
        _rootPinHandle?.Dispose();
        _rootPinHandle = null;
        _rootRecoveryHandle?.Dispose();
        _rootRecoveryHandle = null;
        lock (_sync)
        {
            _ownedFiles.Clear();
            _disposed = true;
        }
        WorkspaceOwnershipRegistry.UnregisterWorkspace(this, _rootDirectory);
    }

    private IOException CleanupFailed(Exception inner)
    {
        if (inner is IOException io && io.Message.StartsWith("[CleanupFailed]", StringComparison.Ordinal))
            return io;
        return new IOException(
            $"[CleanupFailed] Media workspace cleanup failed for '{_rootDirectory}': {inner.Message}",
            inner);
    }

    private static bool SameFileId(WindowsFileIdentity left, WindowsFileIdentity right)
        => left.VolumeSerialNumber == right.VolumeSerialNumber && left.FileIndex == right.FileIndex;

    private sealed record WorkspaceFileReceipt(
        string Path,
        WindowsFileIdentity Identity,
        long ByteLength,
        string? Sha256,
        string Role);
}

internal enum MediaWorkspaceCleanupStage
{
    PinReleasedBeforeCleanupLease
}
