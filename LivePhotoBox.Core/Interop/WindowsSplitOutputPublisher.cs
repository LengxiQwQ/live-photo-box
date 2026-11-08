using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
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
internal static partial class WindowsSplitOutputPublisher
{
    internal sealed record PublicationAttempt(PublishedOwnedFile? Receipt, Exception? Failure);
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

    internal static AsyncLocal<Func<SplitPublicationTraceEvent, CancellationToken, Task>?> TestHook { get; } = new();

    internal static async Task PublishAsync(string imageSourcePath,string videoSourcePath,string imageFinalPath,string videoFinalPath,
        bool overwriteExisting,Func<SplitPublicationTraceEvent,CancellationToken,Task>? testHook,CancellationToken token)
    {
        var attempt=await TryPublishAsync(imageSourcePath,videoSourcePath,imageFinalPath,videoFinalPath,overwriteExisting,testHook,token).ConfigureAwait(false);
        if(attempt.Failure is { } failure)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static async Task<PublicationAttempt> TryPublishAsync(
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
            if(await transaction.PrepareAsync(imageSourcePath,videoSourcePath,token).ConfigureAwait(false) is { } refusal)return await Failed(refusal).ConfigureAwait(false);
            if(await transaction.CommitAsync(token).ConfigureAwait(false) is { } commitFailure)return await Failed(commitFailure).ConfigureAwait(false);
            return new(null,null);
        }
        catch(Exception publicationFailure) {return await Failed(publicationFailure).ConfigureAwait(false);}
        async Task<PublicationAttempt> Failed(Exception publicationFailure)
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
                return new(null,new AggregateException(
                    "Split output publication failed and exact-object rollback/evidence could not fully close the transaction.",failures));
            }

            return new(null,publicationFailure);
        }
    }

    internal static Task<PublishedOwnedFile> PublishSingleAsync(
        string finalPath,
        bool overwriteExisting,
        Func<Stream, CancellationToken, Task> writeStageAsync,
        Func<SplitPublicationTraceEvent, CancellationToken, Task>? testHook,
        CancellationToken token) =>
        PublishSingleCoreAsync(finalPath, overwriteExisting, writeStageAsync, null, testHook, token);

    internal static Task<PublicationAttempt> TryPublishSingleAsync(string finalPath,bool overwriteExisting,
        Func<Stream,CancellationToken,Task<Exception?>> writeStageAsync,
        Func<SplitPublicationTraceEvent,CancellationToken,Task>? testHook,CancellationToken token)
        => TryPublishSingleCoreAsync(finalPath,overwriteExisting,writeStageAsync,null,testHook,token);

    internal static async Task PublishPairTargetAsync(string imagePath,string videoPath,bool overwrite,
        Func<Stream,CancellationToken,Task<Exception?>> imageWriter,Func<Stream,CancellationToken,Task<Exception?>> videoWriter,
        NativeTargetPairProof proof,Func<Exception?> completeTransport,CancellationToken token)
    {
        var attempt=await TryPublishPairTargetAsync(imagePath,videoPath,overwrite,imageWriter,videoWriter,proof,completeTransport,token).ConfigureAwait(false);
        if(attempt.Failure is { } failure)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    internal static async Task<PublicationAttempt> TryPublishPairTargetAsync(string imagePath, string videoPath, bool overwrite,
        Func<Stream, CancellationToken, Task<Exception?>> writeImage, Func<Stream, CancellationToken, Task<Exception?>> writeVideo,
        NativeTargetPairProof expected, Func<Exception?> completeTransport, CancellationToken token)
    {
        using var transaction = new SplitOutputPublicationTransaction(imagePath, videoPath, overwrite, TestHook.Value);
        try
        {
            if(await transaction.PreparePairTargetAsync(writeImage,writeVideo,expected,token).ConfigureAwait(false) is { } preparationFailure)return await Failed(preparationFailure).ConfigureAwait(false);
            // Both sealed files have passed validation. Retire Native staging before commit.
            if(completeTransport() is { } releaseFailure)return await Failed(releaseFailure).ConfigureAwait(false);
            if(await transaction.CommitAsync(token).ConfigureAwait(false) is { } commitFailure)return await Failed(commitFailure).ConfigureAwait(false);
            return new(null,null);
        }
        catch(Exception failure) {return await Failed(failure).ConfigureAwait(false);}
        async Task<PublicationAttempt> Failed(Exception failure)
        {
            Exception? rollback = transaction.Rollback();
            Exception? evidence = await transaction.EmitRollbackEvidenceAsync().ConfigureAwait(false);
            if (rollback is not null || evidence is not null)
                return new(null,new AggregateException("Target pair publication/rollback failed.",
                    new[] { failure, rollback, evidence }.OfType<Exception>()));
            return new(null,failure);
        }
    }

    /// <summary>
    /// The concrete Target Validator reopens complete flushed bytes. Callers supply
    /// semantic expectations and cannot replace or disable the publication verdict.
    /// </summary>
    internal static Task<PublishedOwnedFile> PublishTargetAsync(
        string finalPath,
        bool overwriteExisting,
        Func<Stream, CancellationToken, Task> writeStageAsync,
        TargetValidationExpectations expectations,
        CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(expectations);
        return PublishSingleCoreAsync(finalPath, overwriteExisting, writeStageAsync,
            (stage, cancellation) =>
            {
                NativeTargetValidator.ValidateStage(stage, expectations, cancellation);
                return Task.CompletedTask;
            }, TestHook.Value, token);
    }

    internal static Task<PublicationAttempt> TryPublishTargetAsync(string finalPath, bool overwriteExisting,
        Func<Stream,CancellationToken,Task<Exception?>> writeStageAsync, TargetValidationExpectations expectations, CancellationToken token)
        => TryPublishSingleCoreAsync(finalPath,overwriteExisting,writeStageAsync,
            (stage,cancellation)=>Task.FromResult(NativeTargetValidator.TryValidateStage(stage,expectations,cancellation).Failure),
            TestHook.Value,token);

    private static async Task<PublishedOwnedFile> PublishSingleCoreAsync(
        string finalPath,
        bool overwriteExisting,
        Func<Stream, CancellationToken, Task> writeStageAsync,
        Func<TargetPublicationStage, CancellationToken, Task>? validateStageAsync,
        Func<SplitPublicationTraceEvent, CancellationToken, Task>? testHook,
        CancellationToken token)
    {
        Func<TargetPublicationStage,CancellationToken,Task<Exception?>>? validate=validateStageAsync is null?null:
            async (stage,ct)=> {await validateStageAsync(stage,ct).ConfigureAwait(false);return null;};
        Func<Stream,CancellationToken,Task<Exception?>> write=async (stream,ct)=> {await writeStageAsync(stream,ct).ConfigureAwait(false);return null;};
        var attempt=await TryPublishSingleCoreAsync(finalPath,overwriteExisting,write,validate,testHook,token).ConfigureAwait(false);
        if(attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return attempt.Receipt!;
    }

    private static async Task<PublicationAttempt> TryPublishSingleCoreAsync(
        string finalPath, bool overwriteExisting, Func<Stream,CancellationToken,Task<Exception?>> writeStageAsync,
        Func<TargetPublicationStage,CancellationToken,Task<Exception?>>? validateStageAsync,
        Func<SplitPublicationTraceEvent,CancellationToken,Task>? testHook, CancellationToken token)
    {
        ArgumentNullException.ThrowIfNull(writeStageAsync);
        using var transaction = new SplitOutputPublicationTransaction(
            finalPath,
            overwriteExisting,
            testHook ?? TestHook.Value);

        try
        {
            if(await transaction.PrepareSingleAsync(writeStageAsync, validateStageAsync, token).ConfigureAwait(false) is { } refusal)
                return await Failed(refusal).ConfigureAwait(false);
            if(await transaction.CommitAsync(token).ConfigureAwait(false) is { } commitFailure)return await Failed(commitFailure).ConfigureAwait(false);
            return new(transaction.SingleReceipt!,null);
        }
        catch (Exception publicationFailure)
        {
            return await Failed(publicationFailure).ConfigureAwait(false);
        }

        async Task<PublicationAttempt> Failed(Exception publicationFailure)
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
                return new(null,new AggregateException(
                    "Single output publication failed and exact-object rollback/evidence could not fully close the transaction.",
                    failures));
            }

            return new(null,publicationFailure);
        }
    }

    internal sealed class TargetPublicationStage : IDisposable
    {
        private readonly object _gate = new();
        private readonly List<FileStream> _readers = [];
        private bool _disposed;

        internal TargetPublicationStage(string path, WindowsFileIdentity identity, long length, string sha256)
        {
            Path = path;
            Identity = identity;
            ByteLength = length;
            Sha256 = sha256;
        }

        internal string Path { get; }
        internal WindowsFileIdentity Identity { get; }
        internal long ByteLength { get; }
        internal string Sha256 { get; }

        internal FileStream OpenRead()
        {
            var attempt = TryOpenRead();
            if (attempt.Failure is { } failure)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return attempt.Reader!;
        }

        internal (FileStream? Reader, Exception? Failure) TryOpenRead()
        {
            lock (_gate)
            {
                if (_disposed) return (null, new ObjectDisposedException(nameof(TargetPublicationStage)));
                SafeFileHandle handle = CreateFileW(WindowsNativePath.Extended(Path), GenericRead, 0x7, IntPtr.Zero,
                    OpenExisting, FileAttributeNormal | OpenReparsePoint, IntPtr.Zero);
                if (handle.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error();
                    handle.Dispose();
                    return (null, BuildIOException("Unable to reopen the guarded target stage for validation.", error));
                }
                var identityAttempt = TryCaptureRegularFile(handle, Path, 1);
                Exception? failure = identityAttempt.Failure;
                if (failure == null && !SameObjectAndAttributes(Identity, identityAttempt.Identity!))
                    failure = new IOException("Reopened target stage no longer matches its sealed object.");
                long observedLength = 0;
                if (failure == null && !GetFileSizeEx(handle, out observedLength))
                    failure = BuildIOException("Cannot inspect reopened stage length.", Marshal.GetLastWin32Error());
                else if (failure == null)
                {
                    // The held handle remains the authority throughout validation.
                    var hash = MediaFileHashAttempt.ComputeHeld(handle, ByteLength, CancellationToken.None);
                    failure = hash.Failure;
                    if (failure == null && (observedLength != ByteLength || !string.Equals(hash.Sha256, Sha256, StringComparison.Ordinal)))
                        failure = new IOException("Reopened target stage no longer matches its sealed bytes.");
                }
                if (failure != null) { handle.Dispose(); return (null, failure); }
                var reader = new FileStream(handle, FileAccess.Read, BufferSize, isAsync: false);
                reader.Position = 0;
                _readers.Add(reader);
                return (reader, null);
            }
        }

        public void Dispose()
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                foreach (FileStream reader in _readers) reader.Dispose();
                _readers.Clear();
            }
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
        private PublicationJournal? _journal;
        public PublishedOwnedFile? SingleReceipt { get; private set; }
        private Exception? InitializationFailure { get; }

        public SplitOutputPublicationTransaction(
            string imageFinalPath,
            string videoFinalPath,
            bool overwriteExisting,
            Func<SplitPublicationTraceEvent, CancellationToken, Task>? testHook)
            : this([imageFinalPath, videoFinalPath], overwriteExisting, testHook)
        {
        }

        public SplitOutputPublicationTransaction(
            string finalPath,
            bool overwriteExisting,
            Func<SplitPublicationTraceEvent, CancellationToken, Task>? testHook)
            : this([finalPath], overwriteExisting, testHook)
        {
        }

        private SplitOutputPublicationTransaction(
            IReadOnlyList<string> finalPaths,
            bool overwriteExisting,
            Func<SplitPublicationTraceEvent, CancellationToken, Task>? testHook)
        {
            if (finalPaths.Count is < 1 or > 2)
                throw new ArgumentOutOfRangeException(nameof(finalPaths));

            _imageFinalPath = Path.GetFullPath(finalPaths[0]);
            _videoFinalPath = Path.GetFullPath(finalPaths.Count == 1 ? finalPaths[0] : finalPaths[1]);
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
            _slots = finalPaths.Select(path => new OutputSlot(Path.GetFullPath(path))).ToList();
            var recoveryIssues = RecoverPublications(_targetDirectory, throwFailures: false);
            if (recoveryIssues.Count > 0) InitializationFailure = new IOException(string.Join(Environment.NewLine, recoveryIssues));
        }

        public async Task<Exception?> PrepareSingleAsync(
            Func<Stream, CancellationToken, Task<Exception?>> writeStageAsync,
            Func<TargetPublicationStage, CancellationToken, Task<Exception?>>? validateStageAsync,
            CancellationToken token)
        {
            if (_slots.Count != 1)
                throw new InvalidOperationException("Single-output preparation requires exactly one final target.");

            if(token.IsCancellationRequested) return new OperationCanceledException(token);
            if (InitializationFailure is { } initialFailure) return initialFailure;
            OutputSlot slot = _slots[0];
            var created = TryCreateTransactionFile(_targetDirectory, "stage", deleteOnClose: false,
                allowReadOnlyValidation: validateStageAsync != null);
            if(created.Failure is { } failure) return failure;
            TransactionFile stage=created.File!;
            slot.Stage = stage;

            using (var borrowedHandle = new SafeFileHandle(stage.Handle.DangerousGetHandle(), ownsHandle: false))
            using (var output = new FileStream(borrowedHandle, FileAccess.ReadWrite, BufferSize, isAsync: false))
            {
                if (SetLengthFailure(stage.Handle, 0, stage.Path) is { } truncateFailure) return truncateFailure;
                if(await writeStageAsync(output,token).ConfigureAwait(false) is { } writeFailure)return writeFailure;
                if(token.IsCancellationRequested)return new OperationCanceledException(token);
                if (FlushFailure(stage.Handle, stage.Path) is { } flushFailure) return flushFailure;
            }

            if (FlushFailure(stage.Handle, stage.Path) is { } stageFlushFailure) return stageFlushFailure;
            var stageIdentityAttempt = TryCaptureRegularFile(stage.Handle, stage.Path, 1);
            if (stageIdentityAttempt.Failure is { } identityFailure) return identityFailure;
            WindowsFileIdentity stageIdentity = stageIdentityAttempt.Identity!;
            if (!SameObjectAndAttributes(stage.Identity, stageIdentity))
                return new IOException($"Staged output object '{stage.Path}' changed identity or attributes while being written.");

            if (!GetFileSizeEx(stage.Handle, out long stageLength))
                return BuildIOException("Cannot inspect staged output length.", Marshal.GetLastWin32Error());
            stage.Length = stageLength;
            var hashAttempt = MediaFileHashAttempt.ComputeHeld(stage.Handle, stage.Length, token);
            if (hashAttempt.Failure is { } hashFailure) return hashFailure;
            stage.Sha256 = hashAttempt.Sha256!;

            if (validateStageAsync != null)
            {
                using (var sealedStage = new TargetPublicationStage(stage.Path, stageIdentity, stage.Length, stage.Sha256))
                    if(await validateStageAsync(sealedStage,token).ConfigureAwait(false) is { } validationFailure)return validationFailure;
                if(token.IsCancellationRequested)return new OperationCanceledException(token);
                if (OwnedFileMismatch(stage, stage.Length, stage.Sha256, token) is { } sealFailure) return sealFailure;
            }

            if (!_overwriteExisting)
            {
                slot.Baseline = TargetBaseline.Missing;
                return null;
            }

            if(token.IsCancellationRequested)return new OperationCanceledException(token);
            var baselineAttempt=TryCaptureBaseline(slot,token);
            if(baselineAttempt.Failure is { } baselineFailure)return baselineFailure;
            slot.Baseline=baselineAttempt.Baseline!;
            return null;
        }

        public async Task<Exception?> PrepareAsync(
            string imageSourcePath,
            string videoSourcePath,
            CancellationToken token)
        {
            if(token.IsCancellationRequested)return new OperationCanceledException(token);
            if (InitializationFailure is { } initialFailure) return initialFailure;

            var imageCreated=TryCreateTransactionFile(_targetDirectory,"stage",false);
            if(imageCreated.Failure is { } imageFailure)return imageFailure;
            var imageStage=imageCreated.File!;
            _slots[0].Stage = imageStage;

            var videoCreated=TryCreateTransactionFile(_targetDirectory,"stage",false);
            if(videoCreated.Failure is { } videoFailure)return videoFailure;
            var videoStage=videoCreated.File!;
            _slots[1].Stage = videoStage;

            if(await CopySourceToStageAsync(imageSourcePath,imageStage,token).ConfigureAwait(false) is { } imageCopyFailure)return imageCopyFailure;
            if(await CopySourceToStageAsync(videoSourcePath,videoStage,token).ConfigureAwait(false) is { } videoCopyFailure)return videoCopyFailure;

            if (!_overwriteExisting)
            {
                _slots[0].Baseline = TargetBaseline.Missing;
                _slots[1].Baseline = TargetBaseline.Missing;
                return null;
            }

            // Both final-path baselines and rollback backups are established
            // before CommitAsync is allowed to mutate either final object.
            foreach (OutputSlot slot in _slots)
            {
                if(token.IsCancellationRequested)return new OperationCanceledException(token);
                var baselineAttempt=TryCaptureBaseline(slot,token);
            if(baselineAttempt.Failure is { } baselineFailure)return baselineFailure;
            slot.Baseline=baselineAttempt.Baseline!;
            }
            return null;
        }

        public async Task<Exception?> PreparePairTargetAsync(Func<Stream, CancellationToken, Task<Exception?>> imageWriter,
            Func<Stream, CancellationToken, Task<Exception?>> videoWriter, NativeTargetPairProof expected,
            CancellationToken token)
        {
            if (InitializationFailure is { } initialFailure) return initialFailure;
            var writers = new[] { imageWriter, videoWriter };
            var seals = new List<TargetPublicationStage>();
            try
            {
                for (int i = 0; i < 2; i++)
                {
                    if(token.IsCancellationRequested)return new OperationCanceledException(token);
                    var created=TryCreateTransactionFile(_targetDirectory,"stage",false,true);
                    if(created.Failure is { } creationFailure)return creationFailure;
                    var stage=created.File!;
                    _slots[i].Stage = stage;
                    using (var borrowed = new SafeFileHandle(stage.Handle.DangerousGetHandle(), ownsHandle: false))
                    using (var stream = new FileStream(borrowed, FileAccess.ReadWrite, BufferSize, isAsync: false))
                    {
                        if(await writers[i](stream,token).ConfigureAwait(false) is { } writeFailure)return writeFailure;
                        if (FlushFailure(stage.Handle, stage.Path) is { } flushFailure) return flushFailure;
                    }
                    if (FlushFailure(stage.Handle, stage.Path) is { } stageFlushFailure) return stageFlushFailure;
                    var identityAttempt = TryCaptureRegularFile(stage.Handle, stage.Path, 1);
                    if (identityAttempt.Failure is { } identityFailure) return identityFailure;
                    var identity = identityAttempt.Identity!;
                    if (!SameObjectAndAttributes(identity, stage.Identity)) return new IOException("Target pair stage changed object.");
                    if (!GetFileSizeEx(stage.Handle, out long stageLength))
                return BuildIOException("Cannot inspect staged output length.", Marshal.GetLastWin32Error());
            stage.Length = stageLength;
                    var hashAttempt = MediaFileHashAttempt.ComputeHeld(stage.Handle, stage.Length, token);
            if (hashAttempt.Failure is { } hashFailure) return hashFailure;
            stage.Sha256 = hashAttempt.Sha256!;
                    seals.Add(new TargetPublicationStage(stage.Path, identity, stage.Length, stage.Sha256));
                }
                if(DualFileTargetValidator.ValidationFailure(seals[0],seals[1],expected,token) is { } validationFailure)return validationFailure;
                foreach (var slot in _slots)
                {
                    if (OwnedFileMismatch(slot.Stage!, slot.Stage!.Length, slot.Stage.Sha256!, token) is { } sealFailure) return sealFailure;
                    if(_overwriteExisting)
                    {
                        var baselineAttempt=TryCaptureBaseline(slot,token);
                        if(baselineAttempt.Failure is { } baselineFailure)return baselineFailure;
                        slot.Baseline=baselineAttempt.Baseline!;
                    }
                    else slot.Baseline=TargetBaseline.Missing;
                }
                return null;
            }
            finally { foreach (var seal in seals) seal.Dispose(); }
        }

        public async Task<Exception?> CommitAsync(CancellationToken token)
        {
            if (_slots.Count == 0 || _slots.Exists(slot => slot.Stage == null || slot.Baseline == null))
                throw new InvalidOperationException("Every staged output and target baseline must be ready before commit.");

            // Durable before the first rename/truncate. Recovery copies outlive
            // delete-pending rollback handles and abrupt process termination.
            var journalAttempt = PublicationJournal.TryCreate(_slots, _targetDirectory);
            if (journalAttempt.Failure is { } journalFailure) return journalFailure;
            _journal = journalAttempt.Journal!;

            foreach (OutputSlot slot in _slots)
            {
                if(token.IsCancellationRequested)return new OperationCanceledException(token);
                var failure=slot.Baseline!.Exists
                    ? await CommitExistingTargetAsync(slot,token).ConfigureAwait(false)
                    : await CommitMissingTargetAsync(slot,token).ConfigureAwait(false);
                if(failure is not null)return failure;
            }

            // Existing-target stage objects are no longer needed once both
            // destination writes have completed. Remove them by exact handle
            // while the verified rollback backups remain available.
            foreach (OutputSlot slot in _slots)
            {
                if (slot.Baseline!.Exists)
                    if (RetireOwnedFile(slot.Stage!, "existing-target stage") is { } retireFailure) return retireFailure;
            }

            // A retained backup must be readable for rollback, yet already
            // have an object-based Unlinked proof before the commit barrier.
            foreach (OutputSlot slot in _slots)
            {
                if (slot.Baseline!.Exists)
                    if (MarkBackupUnlinkedForRollback(slot.Baseline) is { } unlinkFailure) return unlinkFailure;
            }

            foreach (OutputSlot slot in _slots)
            {
                if(token.IsCancellationRequested)return new OperationCanceledException(token);
                await InvokeTestHookAsync(slot, SplitPublicationBoundary.BeforeCommitBarrier, token).ConfigureAwait(false);
            }

            if(token.IsCancellationRequested)return new OperationCanceledException(token);
            foreach (OutputSlot slot in _slots)
                if (FinalCommitBarrierFailure(slot, token) is { } barrierFailure) return barrierFailure;

            // Allocate ownership evidence while rollback is still possible.
            // A caller must never acquire a temporary file by reopening its
            // pathname after the transaction releases the creating handle.
            foreach (OutputSlot slot in _slots)
            {
                var receipt = new PublishedOwnedFile(slot.FinalPath,
                    slot.FinalIdentity!, slot.FinalLength!.Value, slot.FinalSha256!);
                WorkspaceOwnershipRegistry.RegisterPublishedFile(
                    receipt, newlyCreatedObject: !slot.Baseline!.Exists);
                if (_slots.Count == 1)
                    SingleReceipt = receipt;
            }

            // Linearization point. No later filesystem operation can require
            // rollback; only already-flushed handle release remains.
            if (_journal.TryComplete() is { } completionFailure) return completionFailure;
            _committed = true;
            foreach (OutputSlot slot in _slots)
                WorkspaceCleanupScope.RegisterPublished(new PublishedOwnedFile(slot.FinalPath,
                    slot.FinalIdentity!, slot.FinalLength!.Value, slot.FinalSha256!));
            return null;
        }

        private async Task<Exception?> CommitExistingTargetAsync(OutputSlot slot, CancellationToken token)
        {
            TargetBaseline baseline = slot.Baseline!;
            TransactionFile stage = slot.Stage!;

            await InvokeTestHookAsync(slot, SplitPublicationBoundary.BeforeMutationRevalidation, token).ConfigureAwait(false);
            if(token.IsCancellationRequested)return new OperationCanceledException(token);

            SafeFileHandle mutationHandle = CreateFileW(WindowsNativePath.Extended(slot.FinalPath), GenericRead | GenericWrite | FileReadAttributes,
                0, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
            if(mutationHandle.IsInvalid) {int error=Marshal.GetLastWin32Error();mutationHandle.Dispose();return BuildIOException("Cannot retain exact target for overwrite.",error);}
            slot.MutationHandle = mutationHandle;

            if(!WindowsFileIdentity.TryCapture(mutationHandle,out var before,out int identityError)||before!.IsReparsePoint||before.LinkCount!=1||
                (before.FileAttributes&FileAttributeDirectory)!=0)return new IOException($"Target mutation identity is invalid (system error {identityError}).");
            var mutationIdentity=before;
            if (!mutationIdentity.Matches(baseline.Identity!))
                return new IOException($"Existing Split target '{slot.FinalPath}' no longer matches the captured object identity and attributes.");

            if(!GetFileSizeEx(mutationHandle,out long mutationLength))return BuildIOException("Cannot verify mutation target length.",Marshal.GetLastWin32Error());
            var beforeHash=MediaFileHashAttempt.ComputeHeld(mutationHandle,mutationLength,token);
            if(beforeHash.Failure is { } beforeHashFailure)return beforeHashFailure;
            string mutationSha=beforeHash.Sha256!;
            if (mutationLength != baseline.Length || !string.Equals(mutationSha, baseline.Sha256, StringComparison.Ordinal))
                return new IOException($"Existing Split target '{slot.FinalPath}' content changed after its baseline was captured.");

            // A workspace output name is not authority to overwrite an object
            // installed there by someone else. Check the exact held target
            // against its producer receipt before the first byte mutation.
            if(WorkspaceOwnershipRegistry.ExistingFileMutationFailure(slot.FinalPath,mutationIdentity) is { } ownershipFailure)return ownershipFailure;

            slot.MutationIdentity = mutationIdentity;
            slot.Mutated = true; // Any subsequent write/flush failure may have changed bytes.
            if (CopyHandleBytesFailure(stage.Handle, mutationHandle, stage.Length, slot.FinalPath, token) is { } copyFailure) return copyFailure;

            if(!WindowsFileIdentity.TryCapture(mutationHandle,out var after,out identityError)||after!.IsReparsePoint||
                (after.FileAttributes&FileAttributeDirectory)!=0)return new IOException($"Target mutation output is invalid (system error {identityError}).");
            var afterWrite=after;
            if(!GetFileSizeEx(mutationHandle,out long finalLength))return BuildIOException("Cannot verify overwritten target length.",Marshal.GetLastWin32Error());
            var afterHash=MediaFileHashAttempt.ComputeHeld(mutationHandle,finalLength,token);
            if(afterHash.Failure is { } afterHashFailure)return afterHashFailure;
            string finalSha=afterHash.Sha256!;
            if (!SameObjectAndAttributes(baseline.Identity!, afterWrite))
                return new IOException($"Existing Split target '{slot.FinalPath}' changed identity or attributes during the in-place update.");
            if (afterWrite.LinkCount != 1)
                return new IOException($"A concurrent hard-link alias was observed for existing Split target '{slot.FinalPath}'.");
            if (finalLength != stage.Length || !string.Equals(finalSha, stage.Sha256, StringComparison.Ordinal))
                return new IOException($"In-place Split output verification failed for '{slot.FinalPath}'.");

            slot.FinalIdentity = afterWrite;
            slot.FinalLength = finalLength;
            slot.FinalSha256 = finalSha;
            await InvokeTestHookAsync(slot, SplitPublicationBoundary.AfterTargetMutation, token).ConfigureAwait(false);
            return null;
        }

        private async Task<Exception?> CommitMissingTargetAsync(OutputSlot slot, CancellationToken token)
        {
            TransactionFile stage = slot.Stage!;
            await InvokeTestHookAsync(slot, SplitPublicationBoundary.BeforeMissingTargetPublication, token).ConfigureAwait(false);
            if(token.IsCancellationRequested)return new OperationCanceledException(token);

            if (stage.Sha256 is null) return new IOException($"Staged Split output '{stage.Path}' has no captured hash.");
            if (OwnedFileMismatch(stage, stage.Length, stage.Sha256, token) is { } sealFailure) return sealFailure;
            if(RenameNoReplaceFailure(stage.Handle,slot.FinalPath) is { } renameFailure)return renameFailure;
            slot.Published = true;

            var identityAttempt = TryCaptureRegularFile(stage.Handle, slot.FinalPath, 1);
            if (identityAttempt.Failure is { } identityFailure) return identityFailure;
            var publishedIdentity = identityAttempt.Identity!;
            if (!GetFileSizeEx(stage.Handle, out long finalLength))
                return BuildIOException("Cannot inspect published output length.", Marshal.GetLastWin32Error());
            var finalHash = MediaFileHashAttempt.ComputeHeld(stage.Handle, finalLength, token);
            if (finalHash.Failure is { } hashFailure) return hashFailure;
            string finalSha = finalHash.Sha256!;
            if (!SameObjectAndAttributes(stage.Identity, publishedIdentity) ||
                finalLength != stage.Length ||
                !string.Equals(finalSha, stage.Sha256, StringComparison.Ordinal))
            {
                return new IOException($"Published Split output '{slot.FinalPath}' no longer matches its exact staged object and bytes.");
            }

            slot.FinalIdentity = publishedIdentity;
            slot.FinalLength = finalLength;
            slot.FinalSha256 = finalSha;
            return null;
        }

        private Exception? FinalCommitBarrierFailure(OutputSlot slot, CancellationToken token)
        {
            TransactionFile stage = slot.Stage!;
            WindowsFileIdentity expectedIdentity = slot.Baseline!.Exists
                ? slot.Baseline.Identity!
                : stage.Identity;
            SafeFileHandle finalHandle = slot.Baseline.Exists
                ? slot.MutationHandle!
                : stage.Handle;

            if(token.IsCancellationRequested)return new OperationCanceledException(token);
            if(!WindowsFileIdentity.TryCapture(finalHandle,out var observed,out int error)||observed!.IsReparsePoint||
                (observed.FileAttributes&FileAttributeDirectory)!=0)return new IOException($"Target final identity is invalid (system error {error}).");
            var actual=observed;
            if(!GetFileSizeEx(finalHandle,out long length))return BuildIOException("Cannot verify committed target length.",Marshal.GetLastWin32Error());
            var hash=MediaFileHashAttempt.ComputeHeld(finalHandle,length,token);
            if(hash.Failure is { } hashFailure)return hashFailure;
            string sha=hash.Sha256!;

            if (!SameObjectAndAttributes(expectedIdentity, actual))
                return new IOException($"Split target '{slot.FinalPath}' changed identity or attributes before the commit barrier.");
            if (actual.LinkCount != 1)
                return new IOException($"A pre-commit hard-link alias was observed for Split target '{slot.FinalPath}'.");
            if (length != stage.Length || !string.Equals(sha, stage.Sha256, StringComparison.Ordinal))
                return new IOException($"Split target '{slot.FinalPath}' does not contain its exact staged bytes at the commit barrier.");

            slot.FinalIdentity = actual;
            slot.FinalLength = length;
            slot.FinalSha256 = sha;
            return null;
        }

        private (TargetBaseline? Baseline, Exception? Failure) TryCaptureBaseline(OutputSlot slot, CancellationToken token)
        {
            if(token.IsCancellationRequested)return (null,new OperationCanceledException(token));
            SafeFileHandle baselineHandle = CreateFileW(WindowsNativePath.Extended(slot.FinalPath), GenericRead | FileReadAttributes,
                FileShareRead, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
            if (baselineHandle.IsInvalid)
            {
                int error=Marshal.GetLastWin32Error(); baselineHandle.Dispose();
                return error is ErrorFileNotFound or ErrorPathNotFound ? (TargetBaseline.Missing,null)
                    : (null,BuildIOException("Cannot retain existing target before publication.",error));
            }

            using (baselineHandle)
            {
                if (!WindowsFileIdentity.TryCapture(baselineHandle,out var captured,out int error) ||
                    captured!.LinkCount!=1 || captured.IsReparsePoint || (captured.FileAttributes&FileAttributeDirectory)!=0)
                    return (null,new IOException($"Cannot verify existing target object (system error {error})."));
                WindowsFileIdentity identity=captured;
                if(!GetFileSizeEx(baselineHandle,out long length))return (null,BuildIOException("Cannot read existing target length.",Marshal.GetLastWin32Error()));
                var hash=MediaFileHashAttempt.ComputeHeld(baselineHandle,length,token);
                if(hash.Failure is { } hashFailure)return (null,hashFailure);
                string sha=hash.Sha256!;
                var baseline = new TargetBaseline(identity, length, sha);
                slot.Baseline = baseline;

                var creation=TryCreateTransactionFile(_targetDirectory,"backup",deleteOnClose:true);
                if(creation.Failure is { } creationFailure)return (null,creationFailure);
                TransactionFile backup=creation.File!;
                baseline.Backup = backup;
                if(CopyHandleBytesFailure(baselineHandle,backup.Handle,length,backup.Path,token) is { } copyFailure)return (null,copyFailure);
                if(!WindowsFileIdentity.TryCapture(backup.Handle,out var copied,out int backupError)||copied!.LinkCount!=1||copied.IsReparsePoint)
                    return (null,new IOException($"Cannot verify backup object (system error {backupError})."));
                var backupAfterCopy=copied;
                if(!GetFileSizeEx(backup.Handle,out long backupLength))return (null,BuildIOException("Cannot read backup length.",Marshal.GetLastWin32Error()));
                var backupHash=MediaFileHashAttempt.ComputeHeld(backup.Handle,backupLength,token);
                if(backupHash.Failure is { } backupHashFailure)return (null,backupHashFailure);
                string backupSha=backupHash.Sha256!;
                if (!SameObjectAndAttributes(backup.Identity, backupAfterCopy) ||
                    backupLength != length ||
                    !string.Equals(backupSha, sha, StringComparison.Ordinal))
                    return (null,new IOException($"Rollback backup for '{slot.FinalPath}' does not match the exact baseline bytes."));

                backup.Length = backupLength;
                backup.Sha256 = backupSha;
                return (baseline,null);
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

        private Exception? MarkBackupUnlinkedForRollback(TargetBaseline baseline)
        {
            if (baseline.Backup is not { } backup)
                return new IOException("An existing Split target has no exact rollback backup.");
            if (!WindowsFileIdentity.TryCapture(backup.Handle, out var before, out int beforeError))
                return BuildIOException("Cannot verify rollback backup identity.", beforeError);
            if (!SameObjectAndAttributes(backup.Identity, before!))
                return new IOException($"Rollback backup '{backup.Path}' changed identity or attributes before cleanup proof.");
            if (before!.LinkCount == 0)
            {
                baseline.BackupCleanupProof = OwnedObjectCleanupProof.Unlinked.ToString();
                baseline.BackupDeletePending = true;
                return null;
            }
            if (before!.LinkCount > 1)
            {
                baseline.BackupCleanupProof = OwnedObjectCleanupProof.AliasAlive.ToString();
                return new IOException($"Rollback backup '{backup.Path}' has an unexpected hard-link alias.");
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
                return BuildIOException(
                    $"Unable to mark exact rollback backup '{backup.Path}' for deletion.",
                    error);
            }

            if (!WindowsFileIdentity.TryCapture(backup.Handle, out var after, out int afterError))
                return BuildIOException("Cannot verify unlinked backup identity.", afterError);
            if (!SameObjectAndAttributes(backup.Identity, after!))
            {
                baseline.BackupCleanupProof = OwnedObjectCleanupProof.Unknown.ToString();
                return new IOException($"Rollback backup '{backup.Path}' changed identity or attributes after deletion was armed.");
            }

            if (after!.LinkCount == 0)
            {
                baseline.BackupCleanupProof = OwnedObjectCleanupProof.Unlinked.ToString();
                baseline.BackupDeletePending = true;
                return null;
            }
            if (after!.LinkCount > 1)
            {
                baseline.BackupCleanupProof = OwnedObjectCleanupProof.AliasAlive.ToString();
                return new IOException($"Rollback backup '{backup.Path}' remains linked through an unexpected alias.");
            }

            baseline.BackupCleanupProof = OwnedObjectCleanupProof.Unknown.ToString();
            return new IOException($"Rollback backup '{backup.Path}' has no exact Unlinked cleanup proof.");
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
                        if (RestoreExistingTarget(slot) is { } restoreFailure)
                            failures.Add(new IOException($"Unable to restore exact original Split target '{slot.FinalPath}'.", restoreFailure));
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
                        if (RetireOwnedFile(slot.Stage, slot.Published ? "published missing target" : "staged output") is { } cleanupFailure)
                            failures.Add(cleanupFailure);
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
                    if (RetireOwnedFile(backup, "rollback backup") is { } cleanupFailure)
                        failures.Add(cleanupFailure);
                    baseline.BackupCleanupProof = backup.CleanupProof;
                }
                catch (Exception exception)
                {
                    baseline.BackupCleanupProof = backup.CleanupProof ?? OwnedObjectCleanupProof.Unknown.ToString();
                    failures.Add(exception);
                }
            }

            if (failures.Count == 0 && _journal?.TryComplete() is { } completionFailure) failures.Add(completionFailure);
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

        private static Exception? RestoreExistingTarget(OutputSlot slot)
        {
            TargetBaseline baseline = slot.Baseline!;
            if (baseline.Backup is not { } backup || slot.MutationHandle is not { } mutation ||
                baseline.Length is not { } baselineLength || baseline.Sha256 is not { } baselineSha)
                return new IOException($"Exact rollback evidence for '{slot.FinalPath}' is unavailable.");
            if (!WindowsFileIdentity.TryCapture(backup.Handle, out var backupIdentity, out int backupError) ||
                !SameObjectAndAttributes(backup.Identity, backupIdentity!))
                return new IOException($"Rollback backup '{backup.Path}' changed identity (system error {backupError}).");
            if (!GetFileSizeEx(backup.Handle, out long backupLength))
                return BuildIOException("Cannot inspect rollback backup length.", Marshal.GetLastWin32Error());
            var backupHash = MediaFileHashAttempt.ComputeHeld(backup.Handle, backupLength, CancellationToken.None);
            if (backupHash.Failure is { } hashFailure) return hashFailure;
            if (backupLength != baselineLength || !string.Equals(backupHash.Sha256, baselineSha, StringComparison.Ordinal))
                return new IOException($"Rollback backup '{backup.Path}' no longer matches its verified original bytes.");
            if (CopyHandleBytesFailure(backup.Handle, mutation, baselineLength, slot.FinalPath, CancellationToken.None) is { } copyFailure)
                return copyFailure;
            if (!WindowsFileIdentity.TryCapture(mutation, out var restoredIdentity, out int restoredError) ||
                !SameObjectAndAttributes(baseline.Identity!, restoredIdentity!) || restoredIdentity!.IsReparsePoint ||
                (restoredIdentity.FileAttributes & FileAttributeDirectory) != 0 || restoredIdentity.LinkCount < 1)
                return new IOException($"Rollback target identity is invalid (system error {restoredError}).");
            if (!GetFileSizeEx(mutation, out long restoredLength))
                return BuildIOException("Cannot verify restored target length.", Marshal.GetLastWin32Error());
            var restoredHash = MediaFileHashAttempt.ComputeHeld(mutation, restoredLength, CancellationToken.None);
            if (restoredHash.Failure is { } restoredFailure) return restoredFailure;
            if (restoredLength != baselineLength || !string.Equals(restoredHash.Sha256, baselineSha, StringComparison.Ordinal))
                return new IOException($"Rollback did not restore the original bytes for '{slot.FinalPath}'.");
            slot.FinalIdentity = restoredIdentity;
            slot.FinalLength = restoredLength;
            slot.FinalSha256 = restoredHash.Sha256;
            slot.Mutated = false;
            return null;
        }

        private Exception? RetireOwnedFile(TransactionFile file, string role)
        {
            if (file.CleanupProven)
                return null;
            OwnedObjectCleanupProof proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(
                file.Handle,
                file.Identity,
                _targetDirectory,
                out bool deletePendingArmed);
            file.DeletePendingArmed |= deletePendingArmed;
            file.CleanupProof = proof.ToString();
            if (proof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked))
                return new IOException($"Exact cleanup of transaction-owned Split {role} '{file.Path}' is unproven ({proof}).");

            file.CleanupProven = true;
            if (!file.Handle.IsClosed)
                CloseHandleNoThrow(file.Handle);
            file.HandleDisposedByProof = file.Handle.IsClosed;
            return null;
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
            _journal?.Dispose();
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

    private static TransactionFile CreateTransactionFile(string directory,string role,bool deleteOnClose,bool allowReadOnlyValidation=false)
    {
        var result=TryCreateTransactionFile(directory,role,deleteOnClose,allowReadOnlyValidation);
        if(result.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return result.File!;
    }

    private static (TransactionFile? File,Exception? Failure) TryCreateTransactionFile(string directory, string role, bool deleteOnClose,
        bool allowReadOnlyValidation = false)
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
                shareMode: allowReadOnlyValidation ? FileShareRead : 0,
                IntPtr.Zero,
                CreateNew,
                flags,
                IntPtr.Zero);
            if (!handle.IsInvalid)
            {
                if(WindowsFileIdentity.TryCapture(handle,out WindowsFileIdentity? identity,out int infoError) &&
                    identity!.LinkCount==1 && !identity.IsReparsePoint && (identity.FileAttributes&FileAttributeDirectory)==0)
                    return (new TransactionFile(path,handle,identity),null);
                var disposition=new FileDispositionInfo { DeleteFile=1 };
                bool retired=SetFileInformationByHandle(handle,FileDispositionInfoClass,ref disposition,(uint)Marshal.SizeOf<FileDispositionInfo>());
                handle.Dispose();
                return (null,new IOException($"Cannot verify transaction-owned stage identity (system error {infoError}); creating-handle cleanup={retired}."));
            }

            int error = Marshal.GetLastWin32Error();
            handle.Dispose();
            if (error is ErrorFileExists or ErrorAlreadyExists)
                continue;

            return (null,BuildIOException($"Unable to create transaction-owned Split {role} '{path}'.", error));
        }

        return (null,new IOException($"Unable to allocate a unique transaction-owned Split {role} name in '{directory}'."));
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

    private static async Task<Exception?> CopySourceToStageAsync(string sourcePath,TransactionFile stage,CancellationToken token)
    {
        using(var borrowed=new SafeFileHandle(stage.Handle.DangerousGetHandle(),ownsHandle:false))
        using(var stream=new FileStream(borrowed,FileAccess.ReadWrite,BufferSize,isAsync:false))
        {
            if(await MediaFileHashAttempt.CopyToAsync(sourcePath,stream,token).ConfigureAwait(false) is { } failure)return failure;
            if (FlushFailure(stage.Handle, stage.Path) is { } flushFailure) return flushFailure;
        }
        if (FlushFailure(stage.Handle, stage.Path) is { } stageFlushFailure) return stageFlushFailure;
        if(!WindowsFileIdentity.TryCapture(stage.Handle,out var identity,out int error)||!SameObjectAndAttributes(stage.Identity,identity!))
            return new IOException($"Staged Split object changed identity or attributes (system error {error}).");
        stage.Length=GetLength(stage.Handle,stage.Path);
        var hash = MediaFileHashAttempt.ComputeHeld(stage.Handle, stage.Length, token);
        if (hash.Failure is { } hashFailure) return hashFailure;
        stage.Sha256=hash.Sha256!;
        return null;
    }

    private static void CopyHandleBytes(
        SafeFileHandle source,
        SafeFileHandle destination,
        long length,
        string destinationDescription,
        CancellationToken token)
    {
        if (CopyHandleBytesFailure(source, destination, length, destinationDescription, token) is { } failure)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Exception? CopyHandleBytesFailure(
        SafeFileHandle source,
        SafeFileHandle destination,
        long length,
        string destinationDescription,
        CancellationToken token)
    {
        if (token.IsCancellationRequested) return new OperationCanceledException(token);
        if (!GetFileSizeEx(source, out long sourceLength) || sourceLength != length)
            return new IOException("Exact Split source length changed before copying.");
        if (SetLengthFailure(destination, 0, destinationDescription) is { } truncateFailure) return truncateFailure;
        using var borrowed = new SafeFileHandle(destination.DangerousGetHandle(), ownsHandle: false);
        using var stream = new FileStream(borrowed, FileAccess.ReadWrite, BufferSize, isAsync: false);
        // Rollback backups may already be delete-pending through their retained
        // exact-object proof; their source handle remains readable until rollback.
        if (MediaFileHashAttempt.CopyHeldFileTo(source, stream, token, allowUnlinked: true) is { } copyFailure) return copyFailure;
        if (SetLengthFailure(destination, length, destinationDescription) is { } lengthFailure) return lengthFailure;
        return FlushFailure(destination, destinationDescription);
    }

    private static void VerifyOwnedFileMatches(TransactionFile file, long expectedLength, string expectedSha256, CancellationToken token)
    {
        if (OwnedFileMismatch(file, expectedLength, expectedSha256, token) is { } failure)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Exception? OwnedFileMismatch(TransactionFile file, long expectedLength, string expectedSha256, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new OperationCanceledException(token);
        if (!WindowsFileIdentity.TryCapture(file.Handle, out var actual, out int error) ||
            actual!.LinkCount != 1 || actual.IsReparsePoint || (actual.FileAttributes & FileAttributeDirectory) != 0 ||
            !SameObjectAndAttributes(file.Identity, actual))
            return new IOException($"Transaction-owned Split object '{file.Path}' changed identity or attributes (system error {error}).");
        if (!GetFileSizeEx(file.Handle, out long length))
            return BuildIOException("Cannot verify transaction-owned Split file length.", Marshal.GetLastWin32Error());
        var hash = MediaFileHashAttempt.ComputeHeld(file.Handle, length, token);
        if (hash.Failure is { } failure) return failure;
        if (length != expectedLength || !string.Equals(hash.Sha256, expectedSha256, StringComparison.Ordinal))
            return new IOException($"Transaction-owned Split object '{file.Path}' changed content before publication.");
        return null;
    }

    private static string ComputeSha256(SafeFileHandle handle, long length, string description, CancellationToken token)
    {
        var attempt = MediaFileHashAttempt.ComputeHeld(handle, length, token);
        if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return attempt.Sha256!;
    }

    private static (WindowsFileIdentity? Identity, Exception? Failure) TryCaptureRegularFile(
        SafeFileHandle handle, string path, uint? requiredLinkCount)
    {
        if (!WindowsFileIdentity.TryCapture(handle, out var identity, out int error))
            return (null, BuildIOException($"Cannot inspect transaction object '{path}'.", error));
        if (identity!.IsReparsePoint || (identity.FileAttributes & FileAttributeDirectory) != 0)
            return (null, new IOException($"Split transaction object '{path}' is not a regular non-reparse file."));
        if (requiredLinkCount.HasValue && identity.LinkCount != requiredLinkCount.Value)
            return (null, new IOException($"Split transaction object '{path}' has link count {identity.LinkCount}; expected {requiredLinkCount.Value}."));
        return (identity, null);
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
        if (SetLengthFailure(handle, length, description) is { } failure)
            System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Exception? SetLengthFailure(SafeFileHandle handle, long length, string description)
    {
        var endOfFile = new FileEndOfFileInfo { EndOfFile = length };
        if (!SetFileInformationByHandle(
                handle,
                FileEndOfFileInfoClass,
                ref endOfFile,
                (uint)Marshal.SizeOf<FileEndOfFileInfo>()))
        {
            int error = Marshal.GetLastWin32Error();
            return BuildIOException($"Unable to set the exact length of Split file object '{description}'.", error);
        }
        return null;
    }

    private static void Flush(SafeFileHandle handle, string description)
    {
        if (FlushFailure(handle, description) is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Exception? FlushFailure(SafeFileHandle handle, string description)
        => FlushFileBuffers(handle) ? null : BuildIOException($"Unable to flush Split file object '{description}' to disk.", Marshal.GetLastWin32Error());

    private static void RenameNoReplaceThroughHandle(SafeFileHandle handle,string finalPath)
    {
        if(RenameNoReplaceFailure(handle,finalPath) is { } failure)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static IOException? RenameNoReplaceFailure(SafeFileHandle handle, string finalPath)
    {
        string absolutePath = Path.GetFullPath(finalPath);
        byte[] nameBytes = System.Text.Encoding.Unicode.GetBytes(WindowsNativePath.Extended(absolutePath));
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
            return null;

        int error = Marshal.GetLastWin32Error();
        string reason = error is ErrorFileExists or ErrorAlreadyExists
            ? "The no-replace Split publication refused an occupied foreign destination."
            : "Handle-bound no-replace Split publication failed.";
        return BuildIOException($"{reason} Destination '{absolutePath}'.", error);
    }

    internal static Exception? MoveAuthorizedSourceNoReplace(SafeFileHandle handle, string destination)
        => RenameNoReplaceFailure(handle, destination);

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

    private static SafeFileHandle CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        IntPtr securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile) =>
        NativeCreateFileW(LivePhotoBox.Interop.WindowsNativePath.Extended(fileName), desiredAccess, shareMode,
            securityAttributes, creationDisposition, flagsAndAttributes, templateFile);

    [DllImport("kernel32.dll", EntryPoint = "CreateFileW", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle NativeCreateFileW(
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
