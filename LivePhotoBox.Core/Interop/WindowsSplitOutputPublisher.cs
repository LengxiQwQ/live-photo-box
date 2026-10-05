using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using Microsoft.Win32.SafeHandles;

namespace LivePhotoBox.Interop;

internal enum SplitPublicationBoundary
{
    BeforeMutationRevalidation,
    AfterTargetMutation,
    BeforeCommitBarrier,
    BeforeMissingTargetPublication,
    AfterRollbackCleanup
}

internal sealed record SplitPublicationTargetEvidence(
    string FinalPath,
    string BaselineKind,
    WindowsFileIdentity? BaselineIdentity,
    long? BaselineLength,
    string? BaselineSha256,
    WindowsFileIdentity? StageIdentity,
    long? StageLength,
    string? StageSha256,
    string? StageCleanupProof,
    WindowsFileIdentity? BackupIdentity,
    long? BackupLength,
    string? BackupSha256,
    string? BackupCleanupProof,
    WindowsFileIdentity? FinalIdentity,
    long? FinalLength,
    string? FinalSha256);

internal sealed record SplitPublicationTraceEvent(
    SplitPublicationBoundary Boundary,
    string FinalPath,
    SplitPublicationTargetEvidence Evidence);

/// <summary>
/// Split-only Windows publication transaction. Existing targets retain their
/// exact file object and are updated through an exclusive handle; absent
/// targets are published by a full-path, kernel no-replace rename.
/// </summary>
internal static class WindowsSplitOutputPublisher
{
    private const int BufferSize = 128 * 1024;
    private const int FileRenameInfoClass = 3;
    private const int FileDispositionInfoClass = 4;
    private const int ErrorFileNotFound = 2;
    private const int ErrorPathNotFound = 3;
    private const int ErrorFileExists = 80;
    private const int ErrorAlreadyExists = 183;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint DeleteAccess = 0x00010000;
    private const uint FileReadAttributes = 0x00000080;
    private const uint FileShareRead = 0x00000001;
    private const uint CreateNew = 1;
    private const uint OpenExisting = 3;
    private const uint FileAttributeNormal = 0x00000080;
    private const uint FileAttributeDirectory = 0x00000010;
    private const uint OpenReparsePoint = 0x00200000;
    private const uint FileFlagDeleteOnClose = 0x04000000;

    internal static async Task PublishAsync(
        string imageSourcePath,
        string videoSourcePath,
        string imageFinalPath,
        string videoFinalPath,
        bool overwriteExisting,
        Func<SplitPublicationTraceEvent, CancellationToken, Task>? testHook,
        CancellationToken token)
    {
        using var transaction = new SplitOutputPublicationTransaction(
            imageFinalPath,
            videoFinalPath,
            overwriteExisting,
            testHook);

        try
        {
            await transaction.PrepareAsync(imageSourcePath, videoSourcePath, token).ConfigureAwait(false);
            await transaction.CommitAsync(token).ConfigureAwait(false);
        }
        catch (Exception publicationFailure)
        {
            Exception? rollbackFailure = transaction.Rollback();
            Exception? evidenceFailure = await transaction.EmitRollbackEvidenceAsync().ConfigureAwait(false);
            if (rollbackFailure != null || evidenceFailure != null)
            {
                var failures = new List<Exception> { publicationFailure };
                if (rollbackFailure != null)
                    failures.Add(rollbackFailure);
                if (evidenceFailure != null)
                    failures.Add(evidenceFailure);
                throw new AggregateException(
                    "Split output publication failed and exact-object rollback/evidence could not fully close the transaction.",
                    failures);
            }

            throw;
        }
    }

    private sealed class SplitOutputPublicationTransaction : IDisposable
    {
        private readonly string _imageFinalPath;
        private readonly string _videoFinalPath;
        private readonly string _targetDirectory;
        private readonly bool _overwriteExisting;
        private readonly Func<SplitPublicationTraceEvent, CancellationToken, Task>? _testHook;
        private readonly List<OutputSlot> _slots;
        private bool _committed;
        private bool _rolledBack;

        public SplitOutputPublicationTransaction(
            string imageFinalPath,
            string videoFinalPath,
            bool overwriteExisting,
            Func<SplitPublicationTraceEvent, CancellationToken, Task>? testHook)
        {
            _imageFinalPath = Path.GetFullPath(imageFinalPath);
            _videoFinalPath = Path.GetFullPath(videoFinalPath);
            _overwriteExisting = overwriteExisting;
            _testHook = testHook;

            string? imageDirectory = Path.GetDirectoryName(_imageFinalPath);
            string? videoDirectory = Path.GetDirectoryName(_videoFinalPath);
            if (string.IsNullOrWhiteSpace(imageDirectory) ||
                !string.Equals(imageDirectory, videoDirectory, StringComparison.OrdinalIgnoreCase))
            {
                throw new IOException("Split image and video outputs must share one target directory for transactional publication.");
            }

            _targetDirectory = imageDirectory;
            _slots =
            [
                new OutputSlot(_imageFinalPath),
                new OutputSlot(_videoFinalPath)
            ];
        }

        public async Task PrepareAsync(
            string imageSourcePath,
            string videoSourcePath,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            TransactionFile imageStage = CreateTransactionFile(_targetDirectory, "stage", deleteOnClose: false);
            _slots[0].Stage = imageStage;

            TransactionFile videoStage = CreateTransactionFile(_targetDirectory, "stage", deleteOnClose: false);
            _slots[1].Stage = videoStage;

            await CopySourceToStageAsync(imageSourcePath, imageStage, token).ConfigureAwait(false);
            await CopySourceToStageAsync(videoSourcePath, videoStage, token).ConfigureAwait(false);

            if (!_overwriteExisting)
            {
                _slots[0].Baseline = TargetBaseline.Missing;
                _slots[1].Baseline = TargetBaseline.Missing;
                return;
            }

            // Both final-path baselines and rollback backups are established
            // before CommitAsync is allowed to mutate either final object.
            foreach (OutputSlot slot in _slots)
            {
                token.ThrowIfCancellationRequested();
                slot.Baseline = CaptureBaseline(slot, token);
            }
        }

        public async Task CommitAsync(CancellationToken token)
        {
            if (_slots.Count != 2 || _slots.Exists(slot => slot.Stage == null || slot.Baseline == null))
                throw new InvalidOperationException("Both staged Split outputs and their baselines must be ready before commit.");

            foreach (OutputSlot slot in _slots)
            {
                token.ThrowIfCancellationRequested();
                if (slot.Baseline!.Exists)
                    await CommitExistingTargetAsync(slot, token).ConfigureAwait(false);
                else
                    await CommitMissingTargetAsync(slot, token).ConfigureAwait(false);
            }

            // Existing-target stage objects are no longer needed once both
            // destination writes have completed. Remove them by exact handle
            // while the verified rollback backups remain available.
            foreach (OutputSlot slot in _slots)
            {
                if (slot.Baseline!.Exists)
                    RetireOwnedFile(slot.Stage!, "existing-target stage");
            }

            // A retained backup must be readable for rollback, yet already
            // have an object-based Unlinked proof before the commit barrier.
            foreach (OutputSlot slot in _slots)
            {
                if (slot.Baseline!.Exists)
                    MarkBackupUnlinkedForRollback(slot.Baseline);
            }

            foreach (OutputSlot slot in _slots)
            {
                token.ThrowIfCancellationRequested();
                await InvokeTestHookAsync(slot, SplitPublicationBoundary.BeforeCommitBarrier, token).ConfigureAwait(false);
            }

            token.ThrowIfCancellationRequested();
            foreach (OutputSlot slot in _slots)
                VerifyFinalAtCommitBarrier(slot, token);

            // Linearization point. No later filesystem operation can require
            // rollback; only already-flushed handle release remains.
            _committed = true;
        }

        private async Task CommitExistingTargetAsync(OutputSlot slot, CancellationToken token)
        {
            TargetBaseline baseline = slot.Baseline!;
            TransactionFile stage = slot.Stage!;

            await InvokeTestHookAsync(slot, SplitPublicationBoundary.BeforeMutationRevalidation, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            SafeFileHandle mutationHandle = OpenMutationTarget(slot.FinalPath);
            slot.MutationHandle = mutationHandle;

            WindowsFileIdentity mutationIdentity = CaptureAndValidateRegularFile(
                mutationHandle,
                slot.FinalPath,
                requiredLinkCount: 1);
            if (!mutationIdentity.Matches(baseline.Identity!))
                throw new IOException($"Existing Split target '{slot.FinalPath}' no longer matches the captured object identity and attributes.");

            long mutationLength = GetLength(mutationHandle, slot.FinalPath);
            string mutationSha = ComputeSha256(mutationHandle, mutationLength, slot.FinalPath, token);
            if (mutationLength != baseline.Length || !string.Equals(mutationSha, baseline.Sha256, StringComparison.Ordinal))
                throw new IOException($"Existing Split target '{slot.FinalPath}' content changed after its baseline was captured.");

            slot.MutationIdentity = mutationIdentity;
            slot.Mutated = true; // Any subsequent write/flush failure may have changed bytes.
            CopyHandleBytes(stage.Handle, mutationHandle, stage.Length, slot.FinalPath, token);

            WindowsFileIdentity afterWrite = CaptureAndValidateRegularFile(
                mutationHandle,
                slot.FinalPath,
                requiredLinkCount: null);
            long finalLength = GetLength(mutationHandle, slot.FinalPath);
            string finalSha = ComputeSha256(mutationHandle, finalLength, slot.FinalPath, token);
            if (!SameObjectAndAttributes(baseline.Identity!, afterWrite))
                throw new IOException($"Existing Split target '{slot.FinalPath}' changed identity or attributes during the in-place update.");
            if (afterWrite.LinkCount != 1)
                throw new IOException($"A concurrent hard-link alias was observed for existing Split target '{slot.FinalPath}'.");
            if (finalLength != stage.Length || !string.Equals(finalSha, stage.Sha256, StringComparison.Ordinal))
                throw new IOException($"In-place Split output verification failed for '{slot.FinalPath}'.");

            slot.FinalIdentity = afterWrite;
            slot.FinalLength = finalLength;
            slot.FinalSha256 = finalSha;
            await InvokeTestHookAsync(slot, SplitPublicationBoundary.AfterTargetMutation, token).ConfigureAwait(false);
        }

        private async Task CommitMissingTargetAsync(OutputSlot slot, CancellationToken token)
        {
            TransactionFile stage = slot.Stage!;
            await InvokeTestHookAsync(slot, SplitPublicationBoundary.BeforeMissingTargetPublication, token).ConfigureAwait(false);
            token.ThrowIfCancellationRequested();

            VerifyOwnedFileMatches(
                stage,
                stage.Length,
                stage.Sha256 ?? throw new IOException($"Staged Split output '{stage.Path}' has no captured hash."),
                token);
            RenameNoReplaceThroughHandle(stage.Handle, slot.FinalPath);
            slot.Published = true;

            WindowsFileIdentity publishedIdentity = CaptureAndValidateRegularFile(
                stage.Handle,
                slot.FinalPath,
                requiredLinkCount: 1);
            long finalLength = GetLength(stage.Handle, slot.FinalPath);
            string finalSha = ComputeSha256(stage.Handle, finalLength, slot.FinalPath, token);
            if (!SameObjectAndAttributes(stage.Identity, publishedIdentity) ||
                finalLength != stage.Length ||
                !string.Equals(finalSha, stage.Sha256, StringComparison.Ordinal))
            {
                throw new IOException($"Published Split output '{slot.FinalPath}' no longer matches its exact staged object and bytes.");
            }

            slot.FinalIdentity = publishedIdentity;
            slot.FinalLength = finalLength;
            slot.FinalSha256 = finalSha;
        }

        private void VerifyFinalAtCommitBarrier(OutputSlot slot, CancellationToken token)
        {
            TransactionFile stage = slot.Stage!;
            WindowsFileIdentity expectedIdentity = slot.Baseline!.Exists
                ? slot.Baseline.Identity!
                : stage.Identity;
            SafeFileHandle finalHandle = slot.Baseline.Exists
                ? slot.MutationHandle!
                : stage.Handle;

            WindowsFileIdentity actual = CaptureAndValidateRegularFile(
                finalHandle,
                slot.FinalPath,
                requiredLinkCount: null);
            long length = GetLength(finalHandle, slot.FinalPath);
            string sha = ComputeSha256(finalHandle, length, slot.FinalPath, token);

            if (!SameObjectAndAttributes(expectedIdentity, actual))
                throw new IOException($"Split target '{slot.FinalPath}' changed identity or attributes before the commit barrier.");
            if (actual.LinkCount != 1)
                throw new IOException($"A pre-commit hard-link alias was observed for Split target '{slot.FinalPath}'.");
            if (length != stage.Length || !string.Equals(sha, stage.Sha256, StringComparison.Ordinal))
                throw new IOException($"Split target '{slot.FinalPath}' does not contain its exact staged bytes at the commit barrier.");

            slot.FinalIdentity = actual;
            slot.FinalLength = length;
            slot.FinalSha256 = sha;
        }

        private TargetBaseline CaptureBaseline(OutputSlot slot, CancellationToken token)
        {
            SafeFileHandle baselineHandle = OpenBaselineTarget(slot.FinalPath);
            if (baselineHandle.IsInvalid)
                return TargetBaseline.Missing;

            using (baselineHandle)
            {
                WindowsFileIdentity identity = CaptureAndValidateRegularFile(
                    baselineHandle,
                    slot.FinalPath,
                    requiredLinkCount: 1);
                long length = GetLength(baselineHandle, slot.FinalPath);
                string sha = ComputeSha256(baselineHandle, length, slot.FinalPath, token);
                var baseline = new TargetBaseline(identity, length, sha);
                slot.Baseline = baseline;

                TransactionFile backup = CreateTransactionFile(_targetDirectory, "backup", deleteOnClose: true);
                baseline.Backup = backup;
                CopyHandleBytes(baselineHandle, backup.Handle, length, backup.Path, token);
                WindowsFileIdentity backupAfterCopy = CaptureAndValidateRegularFile(
                    backup.Handle,
                    backup.Path,
                    requiredLinkCount: 1);
                long backupLength = GetLength(backup.Handle, backup.Path);
                string backupSha = ComputeSha256(backup.Handle, backupLength, backup.Path, token);
                if (!SameObjectAndAttributes(backup.Identity, backupAfterCopy) ||
                    backupLength != length ||
                    !string.Equals(backupSha, sha, StringComparison.Ordinal))
                    throw new IOException($"Rollback backup for '{slot.FinalPath}' does not match the exact baseline bytes.");

                backup.Length = backupLength;
                backup.Sha256 = backupSha;
                return baseline;
            }
        }

        private async Task InvokeTestHookAsync(
            OutputSlot slot,
            SplitPublicationBoundary boundary,
            CancellationToken token)
        {
            if (_testHook == null)
                return;

            await _testHook(
                new SplitPublicationTraceEvent(boundary, slot.FinalPath, BuildEvidence(slot)),
                token).ConfigureAwait(false);
        }

        private static SplitPublicationTargetEvidence BuildEvidence(OutputSlot slot)
        {
            TargetBaseline? baseline = slot.Baseline;
            TransactionFile? stage = slot.Stage;
            TransactionFile? backup = baseline?.Backup;
            return new SplitPublicationTargetEvidence(
                slot.FinalPath,
                baseline == null ? "NotCaptured" : baseline.Exists ? "Existing" : "Missing",
                baseline?.Identity,
                baseline?.Length,
                baseline?.Sha256,
                stage?.Identity,
                stage?.Length,
                stage?.Sha256,
                stage?.CleanupProof,
                backup?.Identity,
                backup?.Length,
                backup?.Sha256,
                baseline?.BackupCleanupProof,
                slot.FinalIdentity,
                slot.FinalLength,
                slot.FinalSha256);
        }

        private void MarkBackupUnlinkedForRollback(TargetBaseline baseline)
        {
            TransactionFile backup = baseline.Backup
                ?? throw new IOException("An existing Split target has no exact rollback backup.");
            WindowsFileIdentity before = WindowsFileIdentity.Capture(backup.Handle);
            if (!SameObjectAndAttributes(backup.Identity, before))
                throw new IOException($"Rollback backup '{backup.Path}' changed identity or attributes before cleanup proof.");
            if (before.LinkCount == 0)
            {
                baseline.BackupCleanupProof = OwnedObjectCleanupProof.Unlinked.ToString();
                baseline.BackupDeletePending = true;
                return;
            }
            if (before.LinkCount > 1)
            {
                baseline.BackupCleanupProof = OwnedObjectCleanupProof.AliasAlive.ToString();
                throw new IOException($"Rollback backup '{backup.Path}' has an unexpected hard-link alias.");
            }

            var disposition = new FileDispositionInfo { DeleteFile = 1 };
            if (!SetFileInformationByHandle(
                    backup.Handle,
                    FileDispositionInfoClass,
                    ref disposition,
                    (uint)Marshal.SizeOf<FileDispositionInfo>()))
            {
                int error = Marshal.GetLastWin32Error();
                baseline.BackupCleanupProof = OwnedObjectCleanupProof.Unknown.ToString();
                throw BuildIOException(
                    $"Unable to mark exact rollback backup '{backup.Path}' for deletion.",
                    error);
            }

            WindowsFileIdentity after = WindowsFileIdentity.Capture(backup.Handle);
            if (!SameObjectAndAttributes(backup.Identity, after))
            {
                baseline.BackupCleanupProof = OwnedObjectCleanupProof.Unknown.ToString();
                throw new IOException($"Rollback backup '{backup.Path}' changed identity or attributes after deletion was armed.");
            }

            if (after.LinkCount == 0)
            {
                baseline.BackupCleanupProof = OwnedObjectCleanupProof.Unlinked.ToString();
                baseline.BackupDeletePending = true;
                return;
            }
            if (after.LinkCount > 1)
            {
                baseline.BackupCleanupProof = OwnedObjectCleanupProof.AliasAlive.ToString();
                throw new IOException($"Rollback backup '{backup.Path}' remains linked through an unexpected alias.");
            }

            baseline.BackupCleanupProof = OwnedObjectCleanupProof.Unknown.ToString();
            throw new IOException($"Rollback backup '{backup.Path}' has no exact Unlinked cleanup proof.");
        }

        public Exception? Rollback()
        {
            if (_committed || _rolledBack)
                return null;

            _rolledBack = true;
            var failures = new List<Exception>();

            for (int index = _slots.Count - 1; index >= 0; index--)
            {
                OutputSlot slot = _slots[index];
                if (slot.Baseline?.Exists == true && slot.Mutated)
                {
                    try
                    {
                        RestoreExistingTarget(slot);
                    }
                    catch (Exception exception)
                    {
                        failures.Add(new IOException(
                            $"Unable to restore exact original Split target '{slot.FinalPath}' through its retained handle.",
                            exception));
                    }
                }

                if (slot.Stage != null && !slot.Stage.CleanupProven)
                {
                    try
                    {
                        RetireOwnedFile(slot.Stage, slot.Published ? "published missing target" : "staged output");
                    }
                    catch (Exception exception)
                    {
                        slot.Stage.CleanupProof ??= OwnedObjectCleanupProof.Unknown.ToString();
                        failures.Add(exception);
                    }
                }
            }

            for (int index = _slots.Count - 1; index >= 0; index--)
            {
                TargetBaseline? baseline = _slots[index].Baseline;
                TransactionFile? backup = baseline?.Backup;
                if (backup == null || baseline!.BackupDeletePending)
                    continue;

                try
                {
                    RetireOwnedFile(backup, "rollback backup");
                    baseline.BackupCleanupProof = backup.CleanupProof;
                }
                catch (Exception exception)
                {
                    baseline.BackupCleanupProof = backup.CleanupProof ?? OwnedObjectCleanupProof.Unknown.ToString();
                    failures.Add(exception);
                }
            }

            return failures.Count switch
            {
                0 => null,
                1 => failures[0],
                _ => new AggregateException("One or more exact-object Split rollback actions failed.", failures)
            };
        }

        public async Task<Exception?> EmitRollbackEvidenceAsync()
        {
            if (_testHook == null)
                return null;

            var failures = new List<Exception>();
            foreach (OutputSlot slot in _slots)
            {
                try
                {
                    await InvokeTestHookAsync(
                        slot,
                        SplitPublicationBoundary.AfterRollbackCleanup,
                        CancellationToken.None).ConfigureAwait(false);
                }
                catch (Exception exception)
                {
                    failures.Add(exception);
                }
            }

            return failures.Count switch
            {
                0 => null,
                1 => failures[0],
                _ => new AggregateException("One or more post-rollback evidence snapshots could not be recorded.", failures)
            };
        }

        private static void RestoreExistingTarget(OutputSlot slot)
        {
            TargetBaseline baseline = slot.Baseline!;
            TransactionFile backup = baseline.Backup
                ?? throw new IOException($"Rollback backup for '{slot.FinalPath}' is unavailable.");
            SafeFileHandle mutation = slot.MutationHandle
                ?? throw new IOException($"Retained mutation handle for '{slot.FinalPath}' is unavailable.");
            long baselineLength = baseline.Length
                ?? throw new IOException($"Rollback backup for '{slot.FinalPath}' has no captured original length.");
            string baselineSha = baseline.Sha256
                ?? throw new IOException($"Rollback backup for '{slot.FinalPath}' has no captured original SHA-256.");

            WindowsFileIdentity backupIdentity = WindowsFileIdentity.Capture(backup.Handle);
            if (!SameObjectAndAttributes(backup.Identity, backupIdentity) ||
                GetLength(backup.Handle, backup.Path) != baselineLength ||
                !string.Equals(
                    ComputeSha256(backup.Handle, baselineLength, backup.Path, CancellationToken.None),
                    baselineSha,
                    StringComparison.Ordinal))
            {
                throw new IOException($"Rollback backup for '{slot.FinalPath}' no longer matches its verified original bytes.");
            }

            CopyHandleBytes(backup.Handle, mutation, baselineLength, slot.FinalPath, CancellationToken.None);
            WindowsFileIdentity restoredIdentity = CaptureAndValidateRegularFile(
                mutation,
                slot.FinalPath,
                requiredLinkCount: null);
            long restoredLength = GetLength(mutation, slot.FinalPath);
            string restoredSha = ComputeSha256(mutation, restoredLength, slot.FinalPath, CancellationToken.None);

            if (!SameObjectAndAttributes(baseline.Identity!, restoredIdentity) ||
                restoredIdentity.LinkCount < 1 ||
                restoredLength != baselineLength ||
                !string.Equals(restoredSha, baselineSha, StringComparison.Ordinal))
            {
                throw new IOException($"Rollback did not restore the original identity, attributes, length, and SHA-256 for '{slot.FinalPath}'.");
            }

            slot.FinalIdentity = restoredIdentity;
            slot.FinalLength = restoredLength;
            slot.FinalSha256 = restoredSha;
            slot.Mutated = false;
        }

        private void RetireOwnedFile(TransactionFile file, string role)
        {
            if (file.CleanupProven)
                return;
            OwnedObjectCleanupProof proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
                file.Handle,
                file.Identity,
                _targetDirectory,
                out bool deletePendingArmed);
            file.DeletePendingArmed |= deletePendingArmed;
            file.CleanupProof = proof.ToString();
            if (proof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked))
                throw new IOException($"Exact cleanup of transaction-owned Split {role} '{file.Path}' is unproven ({proof}).");

            file.CleanupProven = true;
            if (!file.Handle.IsClosed)
                CloseHandleNoThrow(file.Handle);
            file.HandleDisposedByProof = file.Handle.IsClosed;
        }

        public void Dispose()
        {
            foreach (OutputSlot slot in _slots)
            {
                CloseHandleNoThrow(slot.MutationHandle);
                if (slot.Stage != null && !slot.Stage.HandleDisposedByProof)
                    CloseHandleNoThrow(slot.Stage.Handle);
                if (slot.Baseline?.Backup is { HandleDisposedByProof: false } backup)
                    CloseHandleNoThrow(backup.Handle);
            }
        }
    }

    private sealed class OutputSlot(string finalPath)
    {
        public string FinalPath { get; } = finalPath;
        public TransactionFile? Stage { get; set; }
        public TargetBaseline? Baseline { get; set; }
        public SafeFileHandle? MutationHandle { get; set; }
        public WindowsFileIdentity? MutationIdentity { get; set; }
        public WindowsFileIdentity? FinalIdentity { get; set; }
        public long? FinalLength { get; set; }
        public string? FinalSha256 { get; set; }
        public bool Mutated { get; set; }
        public bool Published { get; set; }
    }

    private sealed class TargetBaseline
    {
        public static TargetBaseline Missing => new(exists: false, null, null, null);

        public TargetBaseline(
            WindowsFileIdentity identity,
            long length,
            string sha256)
            : this(exists: true, identity, length, sha256)
        {
        }

        private TargetBaseline(
            bool exists,
            WindowsFileIdentity? identity,
            long? length,
            string? sha256)
        {
            Exists = exists;
            Identity = identity;
            Length = length;
            Sha256 = sha256;
        }

        public bool Exists { get; }
        public WindowsFileIdentity? Identity { get; }
        public long? Length { get; }
        public string? Sha256 { get; }
        public TransactionFile? Backup { get; set; }
        public bool BackupDeletePending { get; set; }
        public string? BackupCleanupProof { get; set; }
    }

    private sealed class TransactionFile(
        string path,
        SafeFileHandle handle,
        WindowsFileIdentity identity)
    {
        public string Path { get; } = path;
        public SafeFileHandle Handle { get; } = handle;
        public WindowsFileIdentity Identity { get; } = identity;
        public long Length { get; set; }
        public string? Sha256 { get; set; }
        public string? CleanupProof { get; set; }
        public bool DeletePendingArmed { get; set; }
        public bool CleanupProven { get; set; }
        public bool HandleDisposedByProof { get; set; }
    }

    private static TransactionFile CreateTransactionFile(string directory, string role, bool deleteOnClose)
    {
        for (int attempt = 0; attempt < 32; attempt++)
        {
            string path = Path.Combine(directory, $".lpb-split-{role}-{Guid.NewGuid():N}.tmp");
            uint flags = FileAttributeNormal | OpenReparsePoint;
            if (deleteOnClose)
                flags |= FileFlagDeleteOnClose;

            SafeFileHandle handle = CreateFileW(
                path,
                GenericRead | GenericWrite | DeleteAccess | FileReadAttributes,
                shareMode: 0,
                IntPtr.Zero,
                CreateNew,
                flags,
                IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                try
                {
                    WindowsFileIdentity identity = CaptureAndValidateRegularFile(handle, path, requiredLinkCount: 1);
                    return new TransactionFile(path, handle, identity);
                }
                catch
                {
                    handle.Dispose();
                    throw;
                }
            }

            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ErrorFileExists or ErrorAlreadyExists)
                continue;

            throw BuildIOException($"Unable to create transaction-owned Split {role} '{path}'.", error);
        }

        throw new IOException($"Unable to allocate a unique transaction-owned Split {role} name in '{directory}'.");
    }

    private static SafeFileHandle OpenBaselineTarget(string path)
    {
        SafeFileHandle handle = CreateFileW(
            path,
            GenericRead | FileReadAttributes,
            FileShareRead,
            IntPtr.Zero,
            OpenExisting,
            OpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
            return handle;

        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        if (error is ErrorFileNotFound or ErrorPathNotFound)
            return new SafeFileHandle(IntPtr.Zero, ownsHandle: false);

        throw BuildIOException($"Unable to capture existing Split target '{path}'.", error);
    }

    private static SafeFileHandle OpenMutationTarget(string path)
    {
        SafeFileHandle handle = CreateFileW(
            path,
            GenericRead | GenericWrite | FileReadAttributes,
            shareMode: 0,
            IntPtr.Zero,
            OpenExisting,
            OpenReparsePoint,
            IntPtr.Zero);
        if (!handle.IsInvalid)
            return handle;

        int error = Marshal.GetLastWin32Error();
        handle.Dispose();
        throw BuildIOException($"Unable to acquire the exclusive Split mutation handle for '{path}'.", error);
    }

    private static async Task CopySourceToStageAsync(
        string sourcePath,
        TransactionFile stage,
        CancellationToken token)
    {
        await using var source = new FileStream(
            sourcePath,
            FileMode.Open,
            FileAccess.Read,
            FileShare.Read,
            bufferSize: BufferSize,
            useAsync: true);

        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long offset = 0;
            while (true)
            {
                token.ThrowIfCancellationRequested();
                int read = await source.ReadAsync(buffer.AsMemory(0, BufferSize), token).ConfigureAwait(false);
                if (read == 0)
                    break;
                RandomAccess.Write(stage.Handle, buffer.AsSpan(0, read), offset);
                offset += read;
            }

            SetLength(stage.Handle, offset, stage.Path);
            Flush(stage.Handle, stage.Path);
            WindowsFileIdentity actualIdentity = CaptureAndValidateRegularFile(stage.Handle, stage.Path, requiredLinkCount: 1);
            if (!SameObjectAndAttributes(stage.Identity, actualIdentity))
                throw new IOException($"Staged Split object '{stage.Path}' changed identity or attributes while being written.");
            stage.Length = GetLength(stage.Handle, stage.Path);
            stage.Sha256 = ComputeSha256(stage.Handle, stage.Length, stage.Path, token);
            if (stage.Length != offset)
                throw new IOException($"Staged Split output '{stage.Path}' has an unexpected final length.");
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void CopyHandleBytes(
        SafeFileHandle source,
        SafeFileHandle destination,
        long length,
        string destinationDescription,
        CancellationToken token)
    {
        SetLength(destination, 0, destinationDescription);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long offset = 0;
            while (offset < length)
            {
                token.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(buffer.Length, length - offset);
                int read = RandomAccess.Read(source, buffer.AsSpan(0, requested), offset);
                if (read <= 0)
                    throw new IOException($"Short read while copying an exact Split transaction object to '{destinationDescription}'.");
                RandomAccess.Write(destination, buffer.AsSpan(0, read), offset);
                offset += read;
            }

            SetLength(destination, length, destinationDescription);
            Flush(destination, destinationDescription);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static void VerifyOwnedFileMatches(
        TransactionFile file,
        long expectedLength,
        string expectedSha256,
        CancellationToken token)
    {
        WindowsFileIdentity actual = CaptureAndValidateRegularFile(file.Handle, file.Path, requiredLinkCount: 1);
        long length = GetLength(file.Handle, file.Path);
        string sha = ComputeSha256(file.Handle, length, file.Path, token);
        if (!SameObjectAndAttributes(file.Identity, actual) ||
            length != expectedLength ||
            !string.Equals(sha, expectedSha256, StringComparison.Ordinal))
        {
            throw new IOException($"Transaction-owned Split object '{file.Path}' changed identity or content before publication.");
        }
    }

    private static string ComputeSha256(
        SafeFileHandle handle,
        long length,
        string description,
        CancellationToken token)
    {
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = ArrayPool<byte>.Shared.Rent(BufferSize);
        try
        {
            long offset = 0;
            while (offset < length)
            {
                token.ThrowIfCancellationRequested();
                int requested = (int)Math.Min(buffer.Length, length - offset);
                int read = RandomAccess.Read(handle, buffer.AsSpan(0, requested), offset);
                if (read <= 0)
                    throw new IOException($"Short read while hashing exact Split object '{description}'.");
                hash.AppendData(buffer, 0, read);
                offset += read;
            }

            return Convert.ToHexString(hash.GetHashAndReset());
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
        }
    }

    private static WindowsFileIdentity CaptureAndValidateRegularFile(
        SafeFileHandle handle,
        string path,
        uint? requiredLinkCount)
    {
        WindowsFileIdentity identity = WindowsFileIdentity.Capture(handle);
        if (identity.IsReparsePoint || (identity.FileAttributes & FileAttributeDirectory) != 0)
            throw new IOException($"Split transaction object '{path}' is not a regular non-reparse file.");
        if (requiredLinkCount.HasValue && identity.LinkCount != requiredLinkCount.Value)
            throw new IOException($"Split transaction object '{path}' has link count {identity.LinkCount}; expected {requiredLinkCount.Value}.");
        return identity;
    }

    private static bool SameObjectAndAttributes(WindowsFileIdentity expected, WindowsFileIdentity actual) =>
        expected.VolumeSerialNumber == actual.VolumeSerialNumber &&
        expected.FileIndex == actual.FileIndex &&
        expected.FileAttributes == actual.FileAttributes;

    private static long GetLength(SafeFileHandle handle, string description)
    {
        if (!GetFileSizeEx(handle, out long length))
        {
            int error = Marshal.GetLastWin32Error();
            throw BuildIOException($"Unable to determine the length of Split file object '{description}'.", error);
        }

        return length;
    }

    private static void SetLength(SafeFileHandle handle, long length, string description)
    {
        var endOfFile = new FileEndOfFileInfo { EndOfFile = length };
        if (!SetFileInformationByHandle(
                handle,
                FileEndOfFileInfoClass,
                ref endOfFile,
                (uint)Marshal.SizeOf<FileEndOfFileInfo>()))
        {
            int error = Marshal.GetLastWin32Error();
            throw BuildIOException($"Unable to set the exact length of Split file object '{description}'.", error);
        }
    }

    private static void Flush(SafeFileHandle handle, string description)
    {
        if (!FlushFileBuffers(handle))
        {
            int error = Marshal.GetLastWin32Error();
            throw BuildIOException($"Unable to flush Split file object '{description}' to disk.", error);
        }
    }

    private static void RenameNoReplaceThroughHandle(SafeFileHandle handle, string finalPath)
    {
        string absolutePath = Path.GetFullPath(finalPath);
        byte[] nameBytes = System.Text.Encoding.Unicode.GetBytes(absolutePath);
        int replaceOffset = checked((int)Marshal.OffsetOf<FileRenameInfoHeader>(nameof(FileRenameInfoHeader.ReplaceIfExists)));
        int rootOffset = checked((int)Marshal.OffsetOf<FileRenameInfoHeader>(nameof(FileRenameInfoHeader.RootDirectory)));
        int lengthOffset = checked((int)Marshal.OffsetOf<FileRenameInfoHeader>(nameof(FileRenameInfoHeader.FileNameLength)));
        int fileNameOffset = checked(lengthOffset + sizeof(uint));
        byte[] buffer = new byte[checked(fileNameOffset + nameBytes.Length)];
        Span<byte> bytes = buffer;
        BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(replaceOffset), 0); // ReplaceIfExists = FALSE.
        if (IntPtr.Size == sizeof(long))
            BinaryPrimitives.WriteInt64LittleEndian(bytes.Slice(rootOffset), 0); // RootDirectory = null.
        else
            BinaryPrimitives.WriteInt32LittleEndian(bytes.Slice(rootOffset), 0);
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.Slice(lengthOffset), (uint)nameBytes.Length);
        nameBytes.CopyTo(bytes.Slice(fileNameOffset));

        if (SetFileInformationByHandle(handle, FileRenameInfoClass, buffer, (uint)buffer.Length))
            return;

        int error = Marshal.GetLastWin32Error();
        string reason = error is ErrorFileExists or ErrorAlreadyExists
            ? "The no-replace Split publication refused an occupied foreign destination."
            : "Handle-bound no-replace Split publication failed.";
        throw BuildIOException($"{reason} Destination '{absolutePath}'.", error);
    }

    private static IOException BuildIOException(string message, int error) =>
        new(message, new Win32Exception(error));

    private static void CloseHandleNoThrow(SafeFileHandle? handle)
    {
        if (handle == null || handle.IsClosed)
            return;
        try
        {
            handle.Dispose();
        }
        catch
        {
            // All buffered/file-system work is flushed before the commit
            // barrier. Closing the retained kernel handles is release-only.
        }
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileRenameInfoHeader
    {
        public byte ReplaceIfExists;
        public IntPtr RootDirectory;
        public uint FileNameLength;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileDispositionInfo
    {
        public byte DeleteFile;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct FileEndOfFileInfo
    {
        public long EndOfFile;
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
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        [In] byte[] fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        ref FileDispositionInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetFileInformationByHandle(
        SafeFileHandle fileHandle,
        int fileInformationClass,
        ref FileEndOfFileInfo fileInformation,
        uint bufferSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetFileSizeEx(SafeFileHandle fileHandle, out long fileSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool FlushFileBuffers(SafeFileHandle fileHandle);

    private const int FileEndOfFileInfoClass = 6;
}
