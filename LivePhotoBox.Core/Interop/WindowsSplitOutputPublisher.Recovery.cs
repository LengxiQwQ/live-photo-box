using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using LivePhotoBox.Media.Models;
using Microsoft.Win32.SafeHandles;

namespace LivePhotoBox.Interop;

internal static partial class WindowsSplitOutputPublisher
{
    internal static AsyncLocal<string?> RecoveryRootForTests { get; } = new();
    private static string RecoveryRoot => RecoveryRootForTests.Value ?? Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LivePhotoBox", "Recovery", "publication");

    // Active manifests are held exclusively until every publication handle has
    // closed. Another GUI/CLI instance must never recover an in-flight operation.
    internal static IReadOnlyList<string> RecoverPublications(string? targetDirectory = null, bool throwFailures = true)
    {
        var failures = new List<string>();
        if (OutputDirectoryAttempt.Ensure(RecoveryRoot) is { } rootFailure) return new[] { rootFailure.Message };
        var pinAttempt = WindowsOwnedFilePublisher.TryPinDirectoryAncestors(RecoveryRoot);
        if (pinAttempt.Failure is { } pinFailure) return new[] { pinFailure.Message };
        using var rootPin = pinAttempt.Lease!;
        var enumeration = LivePhotoBox.Services.ScanFileSystem.Enumerate(RecoveryRoot, CancellationToken.None);
        if (enumeration.Failure is { } enumerationFailure) return new[] { enumerationFailure.Message };
        foreach (string path in enumeration.Entries!.Where(p => Path.GetExtension(p).Equals(".lpbr", StringComparison.OrdinalIgnoreCase)))
        {
            using var opened = SourceReadAttempt.Open(path, share: 0, access: GenericRead | GenericWrite | DeleteAccess | FileReadAttributes);
            if (!opened.Succeeded)
            {
                if (opened.Error is not (32 or 33)) Issue(BuildIOException("Cannot retain recovery manifest.", opened.Error));
                continue;
            }
            var manifestIdentity = TryCaptureRegularFile(opened.Handle, path, 1);
            if (manifestIdentity.Failure is { } identityFailure) { Issue(identityFailure); continue; }
            var read = TryReadRecoveryBytes(opened.Handle);
            if (read.Failure is { } readFailure) { Issue(readFailure); continue; }
            var unsealed = TryProtectRecovery(read.Bytes!, false);
            if (unsealed.Failure is { } unsealFailure) { Issue(unsealFailure); continue; }
            RecoveryRecord? record;
            try { record = JsonSerializer.Deserialize<RecoveryRecord>(unsealed.Bytes!); }
            catch (JsonException parseFailure) { Issue(parseFailure); continue; }
            if (record == null) { Issue(new IOException("Recovery record is empty.")); continue; }
            if (targetDirectory != null && !string.Equals(record.Directory, targetDirectory, StringComparison.OrdinalIgnoreCase)) continue;
            if (ValidateRecoveryRecordFailure(record) is { } validationFailure) { Issue(validationFailure); continue; }
            var directory = WindowsOwnedFilePublisher.TryPinDirectoryAncestors(record.Directory);
            if (directory.Failure is { } directoryFailure) { Issue(directoryFailure); continue; }
            using var directoryPin = directory.Lease!;
            string markerPath = path + ".done";
            RecoveryCopy? marker;
            using (var markerRead = SourceReadAttempt.Open(markerPath, share: 1))
            {
                if (markerRead.Succeeded)
                {
                    var markerIdentity = TryCaptureRegularFile(markerRead.Handle, markerPath, 1);
                    if (markerIdentity.Failure is { } markerIdentityFailure) { Issue(markerIdentityFailure); continue; }
                    var bytes = TryReadRecoveryBytes(markerRead.Handle);
                    if (bytes.Failure is { } markerReadFailure) { Issue(markerReadFailure); continue; }
                    var decoded = TryProtectRecovery(bytes.Bytes!, false);
                    if (decoded.Failure is { } markerDecodeFailure) { Issue(markerDecodeFailure); continue; }
                    if (System.Text.Encoding.UTF8.GetString(decoded.Bytes!) != path)
                    { Issue(new IOException("Recovery completion marker does not match its transaction.")); continue; }
                    marker = new(markerPath, markerIdentity.Identity!, bytes.Bytes!.Length,
                        Convert.ToHexString(SHA256.HashData(bytes.Bytes)));
                }
                else
                {
                    if (markerRead.Error is not (ErrorFileNotFound or ErrorPathNotFound))
                    { Issue(BuildIOException("Cannot inspect recovery completion marker.", markerRead.Error)); continue; }
                    if (RecoverRecordFailure(record) is { } recoveryFailure) { Issue(recoveryFailure); continue; }
                    if (TryWriteRecoveryCompletion(path, out marker) is { } completionFailure) { Issue(completionFailure); continue; }
                }
            }
            Exception? cleanupFailure = null;
            foreach (var slot in record.Slots)
            {
                cleanupFailure = TryDeleteRecoveryCopy(slot.Stage) ??
                    (slot.Backup != null ? TryDeleteRecoveryCopy(slot.Backup) : null) ?? TryDeleteRecoveryCopy(slot.TemporaryStage);
                if (cleanupFailure != null) break;
            }
            if (cleanupFailure != null) { Issue(cleanupFailure); continue; }
            var proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(opened.Handle, manifestIdentity.Identity!, RecoveryRoot, out _);
            if (proof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked))
            { Issue(new IOException($"Recovery manifest cleanup is unproven ({proof}).")); continue; }
            if (marker != null && TryDeleteRecoveryCopy(marker) is { } markerCleanupFailure) Issue(markerCleanupFailure);
            void Issue(Exception failure) => failures.Add($"[RecoveryRequired] {path}: {failure.Message}");
        }
        if (throwFailures && targetDirectory != null && failures.Count > 0)
            throw new IOException(string.Join(Environment.NewLine, failures));
        return failures;
    }

    private sealed record RecoveryCopy(string Path, WindowsFileIdentity Identity, long Length, string Sha256);
    private sealed record RecoverySlot(string Path, WindowsFileIdentity Identity, bool Existed, RecoveryCopy Stage, RecoveryCopy? Backup, RecoveryCopy TemporaryStage);
    private sealed record RecoveryRecord(int Version, string Directory, WindowsFileIdentity DirectoryIdentity, RecoverySlot[] Slots);

    private sealed class PublicationJournal : IDisposable
    {
        private readonly string _path;
        private readonly FileStream _lease;
        private readonly List<RecoveryCopy> _copies;
        private bool _complete;
        private RecoveryCopy? _completion;
        private PublicationJournal(string path, FileStream lease, List<RecoveryCopy> copies)
        { _path = path; _lease = lease; _copies = copies; }

        internal static PublicationJournal Create(List<OutputSlot> slots, string directory)
        {
            var attempt = TryCreate(slots, directory);
            if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            return attempt.Journal!;
        }

        internal static (PublicationJournal? Journal, Exception? Failure) TryCreate(List<OutputSlot> slots, string directory)
        {
            if (OutputDirectoryAttempt.Ensure(RecoveryRoot) is { } directoryFailure) return (null, directoryFailure);
            var rootAttempt = WindowsOwnedFilePublisher.TryPinDirectoryAncestors(RecoveryRoot);
            if (rootAttempt.Failure is { } rootFailure) return (null, rootFailure);
            using var rootPin = rootAttempt.Lease!;
            var targetAttempt = WindowsOwnedFilePublisher.TryPinDirectoryAncestors(directory);
            if (targetAttempt.Failure is { } targetFailure) return (null, targetFailure);
            using var targetPin = targetAttempt.Lease!;
            var copies = new List<RecoveryCopy>();
            var created = new List<TransactionFile>();
            string path = Path.Combine(RecoveryRoot, Guid.NewGuid().ToString("N") + ".lpbr");
            var journalAttempt = TryCreateRecoveryFile(Path.ChangeExtension(path, ".preparing"));
            if (journalAttempt.Failure is { } creationFailure) return (null, creationFailure);
            var manifest = journalAttempt.File!;
            created.Add(manifest);
            var lease = new FileStream(manifest.Handle, FileAccess.ReadWrite, BufferSize, isAsync: false);
            var entries = new List<RecoverySlot>();
            foreach (var slot in slots)
            {
                var stageAttempt = TryCopy(slot.Stage!, copies, created);
                if (stageAttempt.Failure is { } stageFailure) return Refused(stageFailure);
                var baseline = slot.Baseline!;
                RecoveryCopy? backup = null;
                if (baseline.Exists)
                {
                    var backupAttempt = TryCopy(baseline.Backup!, copies, created);
                    if (backupAttempt.Failure is { } backupFailure) return Refused(backupFailure);
                    backup = backupAttempt.Copy!;
                }
                var temporaryStage = new RecoveryCopy(slot.Stage!.Path, slot.Stage.Identity, slot.Stage.Length, slot.Stage.Sha256!);
                copies.Add(temporaryStage);
                entries.Add(new(slot.FinalPath, baseline.Identity ?? slot.Stage.Identity, baseline.Exists, stageAttempt.Copy!, backup, temporaryStage));
            }
            var directoryIdentity = TryCaptureDirectoryIdentity(directory);
            if (directoryIdentity.Failure is { } identityFailure) return Refused(identityFailure);
            var record = new RecoveryRecord(1, directory, directoryIdentity.Identity!, entries.ToArray());
            var seal = TryProtectRecovery(JsonSerializer.SerializeToUtf8Bytes(record), true);
            if (seal.Failure is { } sealFailure) return Refused(sealFailure);
            if (TransactionStreamWriteAttempt.Write(lease, seal.Bytes!, CancellationToken.None) is { } writeFailure) return Refused(writeFailure);
            if (FlushFailure(manifest.Handle, manifest.Path) is { } flushFailure) return Refused(flushFailure);
            if (RenameNoReplaceFailure(manifest.Handle, path) is { } renameFailure) return Refused(renameFailure);
            foreach (var file in created) if (!ReferenceEquals(file, manifest)) file.Handle.Dispose();
            return (new(path, lease, copies), null);

            (PublicationJournal?, Exception?) Refused(Exception failure)
            {
                var failures = new List<Exception> { failure };
                foreach (var file in created.AsEnumerable().Reverse())
                {
                    var proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(file.Handle, file.Identity, RecoveryRoot, out _);
                    if (proof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked))
                        failures.Add(new IOException($"Uncommitted recovery object cleanup is unproven: '{file.Path}' ({proof})."));
                    file.Handle.Dispose();
                }
                lease.Dispose();
                return (null, failures.Count == 1 ? failure : new AggregateException("Recovery preparation failed; retained cleanup evidence.", failures));
            }
        }

        private static (RecoveryCopy? Copy, Exception? Failure) TryCopy(TransactionFile file, List<RecoveryCopy> copies,
            List<TransactionFile> created)
        {
            string path = Path.Combine(RecoveryRoot, Guid.NewGuid().ToString("N") + ".data");
            var attempt = TryCreateRecoveryFile(path);
            if (attempt.Failure is { } creationFailure) return (null, creationFailure);
            var output = attempt.File!;
            created.Add(output);
            if (CopyHandleBytesFailure(file.Handle, output.Handle, file.Length, path, CancellationToken.None) is { } copyFailure)
                return (null, copyFailure);
            var hash = MediaFileHashAttempt.ComputeHeld(output.Handle, file.Length, CancellationToken.None);
            if (hash.Failure is { } hashFailure) return (null, hashFailure);
            if (!string.Equals(hash.Sha256, file.Sha256, StringComparison.Ordinal))
                return (null, new IOException("Recovery copy differs from its verified transaction artifact."));
            var copy = new RecoveryCopy(path, output.Identity, file.Length, hash.Sha256!);
            copies.Add(copy);
            return (copy, null);
        }

        internal void Complete()
        {
            if (TryComplete() is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }
        internal Exception? TryComplete()
        {
            if (TryWriteRecoveryCompletion(_path, out var completion) is { } failure) return failure;
            _completion = completion;
            _complete = true;
            return null;
        }

        public void Dispose()
        {
            if (!_complete) { _lease.Dispose(); return; }
            try
            {
                foreach (var copy in _copies)
                    if (TryDeleteRecoveryCopy(copy) is { } failure) { Retained(failure); return; }
                var identity = TryCaptureRegularFile(_lease.SafeFileHandle, _path, 1);
                if (identity.Failure is { } identityFailure) { Retained(identityFailure); return; }
                var proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(_lease.SafeFileHandle,
                    identity.Identity!, RecoveryRoot, out _);
                if (proof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked))
                { Retained(new IOException($"Completed recovery manifest cleanup is unproven ({proof}).")); return; }
                // Remove the marker only after the main record is unreachable;
                // otherwise a crash could turn committed output into rollback.
                _lease.Dispose();
                if (_completion is { } marker && TryDeleteRecoveryCopy(marker) is { } markerFailure) Retained(markerFailure);
            }
            finally { _lease.Dispose(); }
            static void Retained(Exception failure) => LivePhotoBox.Services.LogService.Warn($"Publication recovery cleanup retained: {failure.Message}");
        }
    }

    private static (TransactionFile? File, Exception? Failure) TryCreateRecoveryFile(string path)
    {
        var handle = CreateFileW(WindowsNativePath.Extended(path), GenericRead | GenericWrite | DeleteAccess | FileReadAttributes,
            0, IntPtr.Zero, CreateNew, FileAttributeNormal | OpenReparsePoint, IntPtr.Zero);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); return (null, BuildIOException("Cannot create recovery object.", error)); }
        var identity = TryCaptureRegularFile(handle, path, 1);
        if (identity.Failure is { } failure)
        {
            var disposition = new FileDispositionInfo { DeleteFile = 1 };
            bool removed = SetFileInformationByHandle(handle, FileDispositionInfoClass, ref disposition, (uint)Marshal.SizeOf<FileDispositionInfo>());
            handle.Dispose();
            return (null, new IOException($"Recovery object identity failed; creating-handle cleanup={removed}.", failure));
        }
        return (new TransactionFile(path, handle, identity.Identity!), null);
    }

    private static (WindowsFileIdentity? Identity, Exception? Failure) TryCaptureDirectoryIdentity(string path)
    {
        using var handle = CreateFileW(WindowsNativePath.Extended(path), FileReadAttributes, FileShareRead | 2, IntPtr.Zero,
            OpenExisting, OpenReparsePoint | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) return (null, BuildIOException("Recovery directory cannot be pinned.", Marshal.GetLastWin32Error()));
        if (!WindowsFileIdentity.TryCapture(handle, out var identity, out int error) || identity!.IsReparsePoint ||
            (identity.FileAttributes & FileAttributeDirectory) == 0)
            return (null, new IOException($"Recovery directory identity is invalid (system error {error})."));
        return (identity, null);
    }

    private static Exception? TryWriteRecoveryCompletion(string path, out RecoveryCopy? completion)
    {
        completion = null;
        string pending = path + ".done-preparing-" + Guid.NewGuid().ToString("N");
        var attempt = TryCreateRecoveryFile(pending);
        if (attempt.Failure is { } creationFailure) return creationFailure;
        var file = attempt.File!;
        bool published = false;
        try
        {
            var seal = TryProtectRecovery(System.Text.Encoding.UTF8.GetBytes(path), true);
            if (seal.Failure is { } sealFailure) return sealFailure;
            using var borrowed = new SafeFileHandle(file.Handle.DangerousGetHandle(), ownsHandle: false);
            using var stream = new FileStream(borrowed, FileAccess.ReadWrite, BufferSize, isAsync: false);
            if (TransactionStreamWriteAttempt.Write(stream, seal.Bytes!, CancellationToken.None) is { } writeFailure) return writeFailure;
            if (FlushFailure(file.Handle, pending) is { } flushFailure) return flushFailure;
            if (RenameNoReplaceFailure(file.Handle, path + ".done") is { } renameFailure) return renameFailure;
            published = true;
            completion = new RecoveryCopy(path + ".done", file.Identity, seal.Bytes!.Length,
                Convert.ToHexString(SHA256.HashData(seal.Bytes)));
            return null;
        }
        finally
        {
            if (!published)
                WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(file.Handle, file.Identity, RecoveryRoot, out _);
            file.Handle.Dispose();
        }
    }

    private static WindowsFileIdentity CaptureDirectoryIdentity(string directory)
    {
        using var handle = CreateFileW(directory, GenericRead | FileReadAttributes, 7, IntPtr.Zero,
            OpenExisting, OpenReparsePoint | 0x02000000, IntPtr.Zero);
        if (handle.IsInvalid) throw new IOException("Recovery directory cannot be pinned.");
        var identity = WindowsFileIdentity.Capture(handle);
        if (identity.IsReparsePoint) throw new IOException("Recovery directory is a link.");
        return identity;
    }

    private static Exception? ValidateRecoveryRecordFailure(RecoveryRecord record)
    {
        if (record.Version != 1 || record.Slots is not { Length: >= 1 and <= 2 } || string.IsNullOrWhiteSpace(record.Directory) ||
            !Path.IsPathFullyQualified(record.Directory) || record.DirectoryIdentity == null)
            return new IOException("Unsupported recovery record.");
        var directory = TryCaptureDirectoryIdentity(record.Directory);
        if (directory.Failure is { } directoryFailure) return directoryFailure;
        if (!SameObjectAndAttributes(record.DirectoryIdentity, directory.Identity!))
            return new IOException("Recovery output directory identity changed.");
        foreach (var slot in record.Slots)
        {
            if (slot == null || string.IsNullOrWhiteSpace(slot.Path) || !Path.IsPathFullyQualified(slot.Path) ||
                !string.Equals(Path.GetDirectoryName(slot.Path), record.Directory, StringComparison.OrdinalIgnoreCase) || slot.Identity == null || slot.Stage == null || slot.TemporaryStage == null)
                return new IOException("Recovery target escaped its recorded directory or lacks identity evidence.");
            foreach (var copy in new[] { slot.Stage, slot.Backup }.OfType<RecoveryCopy>())
                if (string.IsNullOrWhiteSpace(copy.Path) || !Path.IsPathFullyQualified(copy.Path) ||
                    !string.Equals(Path.GetDirectoryName(copy.Path), RecoveryRoot, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(copy.Path) != ".data" || copy.Length < 0 || copy.Sha256?.Length != 64 || copy.Identity == null)
                    return new IOException("Invalid recovery artifact authority.");
            if (!string.Equals(Path.GetDirectoryName(slot.TemporaryStage.Path), record.Directory, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(slot.TemporaryStage.Path).StartsWith(".lpb-split-stage-", StringComparison.Ordinal) ||
                slot.TemporaryStage.Identity == null || slot.TemporaryStage.Length < 0 || slot.TemporaryStage.Sha256?.Length != 64)
                return new IOException("Invalid transaction temporary stage authority.");
            if (slot.Existed != (slot.Backup != null)) return new IOException("Incomplete recovery baseline.");
        }
        return null;
    }

    private static Exception? RecoverRecordFailure(RecoveryRecord record)
    {
        var pins = new List<IDisposable>();
        var targets = new List<(RecoverySlot Slot, SafeFileHandle Handle)>();
        try
        {
            foreach (var slot in record.Slots)
            {
                SafeFileHandle target = OpenRecoveryHandle(slot.Path, GenericRead | GenericWrite | DeleteAccess | FileReadAttributes, 0);
                if (target.IsInvalid)
                {
                    int error = Marshal.GetLastWin32Error(); target.Dispose();
                    if (!slot.Existed && error is ErrorFileNotFound or ErrorPathNotFound) continue;
                    return BuildIOException("Recovery target cannot be pinned.", error);
                }
                pins.Add(target);
                var identity = TryCaptureRegularFile(target, slot.Path, 1);
                if (identity.Failure is { } identityFailure) return identityFailure;
                if (!SameObjectAndAttributes(slot.Identity, identity.Identity!))
                    return new IOException("Recovery target was replaced; retained without modification.");
                targets.Add((slot, target));
                var stage = TryOpenRecoveryCopy(slot.Stage);
                if (stage.Failure is { } stageFailure) return stageFailure;
                pins.Add(stage.Reader!);
                if (slot.Existed)
                {
                    var backup = TryOpenRecoveryCopy(slot.Backup!);
                    if (backup.Failure is { } backupFailure) return backupFailure;
                    pins.Add(backup.Reader!);
                }
                var stageMatch = TryMatchesRecoveryPrefix(target, slot.Path, slot.Stage);
                if (stageMatch.Failure is { } matchFailure) return matchFailure;
                if (!stageMatch.Matches)
                {
                    if (slot.Backup == null) return new IOException("Recovery target has unrelated bytes; retained without modification.");
                    var backupMatch = TryMatchesRecoveryPrefix(target, slot.Path, slot.Backup);
                    if (backupMatch.Failure is { } backupMatchFailure) return backupMatchFailure;
                    if (!backupMatch.Matches) return new IOException("Recovery target has unrelated bytes; retained without modification.");
                }
            }
            foreach (var (slot, handle) in targets.AsEnumerable().Reverse())
            {
                if (slot.Existed)
                {
                    var opened = TryOpenRecoveryCopy(slot.Backup!);
                    if (opened.Failure is { } backupFailure) return backupFailure;
                    using var backup = opened.Reader!;
                    if (CopyHandleBytesFailure(backup.SafeFileHandle, handle, slot.Backup!.Length, slot.Path, CancellationToken.None) is { } copyFailure)
                        return copyFailure;
                    if (!GetFileSizeEx(handle, out long length)) return BuildIOException("Cannot verify recovered length.", Marshal.GetLastWin32Error());
                    var hash = MediaFileHashAttempt.ComputeHeld(handle, length, CancellationToken.None);
                    if (hash.Failure is { } hashFailure) return hashFailure;
                    if (length != slot.Backup.Length || hash.Sha256 != slot.Backup.Sha256) return new IOException("Recovery baseline verification failed.");
                }
                else
                {
                    var proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(handle, slot.Identity, record.Directory, out _);
                    if (proof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked)) return new IOException("Recovery output removal is unproven.");
                }
            }
            return null;
        }
        finally { foreach (var pin in pins.AsEnumerable().Reverse()) pin.Dispose(); }
    }

    private static void WriteRecoveryCompletion(string path)
    {
        string pending = path + ".done-preparing-" + Guid.NewGuid().ToString("N");
        using (var output = new FileStream(pending, FileMode.CreateNew, FileAccess.Write,
            FileShare.None, 4096, FileOptions.WriteThrough))
        {
            output.Write(SealRecovery(System.Text.Encoding.UTF8.GetBytes(path)));
            output.Flush(true);
        }
        File.Move(pending, path + ".done");
    }

    private static SafeFileHandle OpenRecoveryHandle(string path, uint access, uint share)
    {
        // Windows can signal process exit before all delete-pending filesystem
        // work has settled. Retry only sharing/lock violations, never identity
        // or content mismatches, and retain the record if the lock persists.
        for (int attempt = 0; ; attempt++)
        {
            var handle = CreateFileW(WindowsNativePath.Extended(path), access, share, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
            int error = Marshal.GetLastWin32Error();
            if (!handle.IsInvalid || error is not (32 or 33) || attempt >= 50) return handle;
            handle.Dispose();
            Thread.Sleep(10);
        }
    }

    private static (FileStream? Reader, Exception? Failure) TryOpenRecoveryCopy(RecoveryCopy copy)
    {
        var handle = OpenRecoveryHandle(copy.Path, GenericRead | FileReadAttributes, FileShareRead);
        if (handle.IsInvalid) { int error = Marshal.GetLastWin32Error(); handle.Dispose(); return (null, BuildIOException("Recovery backup cannot be read.", error)); }
        var identity = TryCaptureRegularFile(handle, copy.Path, 1);
        Exception? failure = identity.Failure;
        if (failure == null && !SameObjectAndAttributes(copy.Identity, identity.Identity!)) failure = new IOException("Recovery artifact identity changed.");
        long length = 0;
        if (failure == null && !GetFileSizeEx(handle, out length)) failure = BuildIOException("Cannot verify recovery artifact length.", Marshal.GetLastWin32Error());
        if (failure == null)
        {
            var hash = MediaFileHashAttempt.ComputeHeld(handle, length, CancellationToken.None);
            failure = hash.Failure;
            if (failure == null && (length != copy.Length || hash.Sha256 != copy.Sha256)) failure = new IOException("Recovery artifact bytes changed.");
        }
        if (failure != null) { handle.Dispose(); return (null, failure); }
        return (new FileStream(handle, FileAccess.Read, BufferSize, isAsync: false), null);
    }

    private static unsafe (bool Matches, Exception? Failure) TryMatchesRecoveryPrefix(SafeFileHandle target, string path, RecoveryCopy copy)
    {
        if (!GetFileSizeEx(target, out long length)) return (false, BuildIOException("Cannot inspect recovery target length.", Marshal.GetLastWin32Error()));
        if (length > copy.Length) return (false, null);
        var opened = TryOpenRecoveryCopy(copy);
        if (opened.Failure is { } failure) return (false, failure);
        using var source = opened.Reader!;
        var sourceHandle = source.SafeFileHandle;
        if (!RecoverySeek(target, 0, out _, 0) || !RecoverySeek(sourceHandle, 0, out _, 0))
            return (false, BuildIOException("Cannot position recovery comparison.", Marshal.GetLastWin32Error()));
        byte[] left = new byte[BufferSize], right = new byte[BufferSize];
        fixed (byte* leftPointer = left, rightPointer = right)
        {
            for (long offset = 0; offset < length;)
            {
                uint count = (uint)Math.Min(BufferSize, length - offset);
                if (!RecoveryRead(target, leftPointer, count, out uint read, 0) ||
                    !RecoveryRead(sourceHandle, rightPointer, count, out uint expected, 0))
                    return (false, BuildIOException("Cannot compare complete recovery prefix.", Marshal.GetLastWin32Error()));
                if (read != count || expected != count || !left.AsSpan(0, (int)count).SequenceEqual(right.AsSpan(0, (int)count))) return (false, null);
                offset += count;
            }
        }
        return (true, null);
    }

    private static unsafe (byte[]? Bytes, Exception? Failure) TryReadRecoveryBytes(SafeFileHandle handle)
    {
        if (!GetFileSizeEx(handle, out long length)) return (null, BuildIOException("Cannot inspect recovery record length.", Marshal.GetLastWin32Error()));
        if (length <= 0 || length > 1024 * 1024) return (null, new IOException("Recovery record is empty or exceeds its bounded size."));
        if (!RecoverySeek(handle, 0, out _, 0)) return (null, BuildIOException("Cannot position recovery record.", Marshal.GetLastWin32Error()));
        byte[] bytes = new byte[(int)length];
        fixed (byte* data = bytes)
        {
            uint offset = 0;
            while (offset < bytes.Length)
            {
                if (!RecoveryRead(handle, data + offset, (uint)bytes.Length - offset, out uint read, 0) || read == 0)
                    return (null, BuildIOException("Cannot completely read recovery record.", Marshal.GetLastWin32Error()));
                offset += read;
            }
        }
        return (bytes, null);
    }
    [DllImport("kernel32.dll", EntryPoint = "ReadFile", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern unsafe bool RecoveryRead(SafeFileHandle handle, byte* bytes, uint count, out uint read, nint overlapped);
    [DllImport("kernel32.dll", EntryPoint = "SetFilePointerEx", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RecoverySeek(SafeFileHandle handle, long offset, out long actual, uint origin);

    private static void DeleteRecoveryCopy(RecoveryCopy copy)
    {
        if (TryDeleteRecoveryCopy(copy) is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }

    private static Exception? TryDeleteRecoveryCopy(RecoveryCopy copy)
    {
        using var source = SourceReadAttempt.Open(copy.Path, share: 0, access: GenericRead | DeleteAccess | FileReadAttributes);
        if (!source.Succeeded)
            return source.Error is ErrorFileNotFound or ErrorPathNotFound ? null : BuildIOException("Recovery artifact cleanup could not acquire its object.", source.Error);
        var parent = WindowsOwnedFilePublisher.TryPinDirectoryAncestors(Path.GetDirectoryName(copy.Path)!);
        if (parent.Failure is { } parentFailure) return parentFailure;
        using var parentPin = parent.Lease!;
        var identity = TryCaptureRegularFile(source.Handle, copy.Path, 1);
        if (identity.Failure is { } identityFailure) return identityFailure;
        if (!SameObjectAndAttributes(copy.Identity, identity.Identity!))
            return new IOException("Recovery artifact replacement retained without modification.");
        if (!source.TryGetLength(out long length, out int error)) return BuildIOException("Cannot verify recovery cleanup length.", error);
        var hash = MediaFileHashAttempt.ComputeHeld(source.Handle, length, CancellationToken.None);
        if (hash.Failure is { } hashFailure) return hashFailure;
        if (length != copy.Length || !string.Equals(hash.Sha256, copy.Sha256, StringComparison.Ordinal))
            return new IOException("Recovery artifact changed bytes retained without modification.");
        var proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(source.Handle, copy.Identity, Path.GetDirectoryName(copy.Path)!, out _);
        return proof is OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked ? null : new IOException($"Recovery artifact cleanup unproven ({proof}).");
    }

    // DPAPI authenticates the manifest without introducing a package or key file.
    [StructLayout(LayoutKind.Sequential)] private struct RecoveryBlob { public int Size; public IntPtr Data; }
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptProtectData(ref RecoveryBlob input, string? description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out RecoveryBlob output);
    [DllImport("crypt32.dll", SetLastError = true)] [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CryptUnprotectData(ref RecoveryBlob input, IntPtr description, IntPtr entropy, IntPtr reserved, IntPtr prompt, uint flags, out RecoveryBlob output);
    [DllImport("kernel32.dll")] private static extern IntPtr LocalFree(IntPtr memory);
    private static byte[] SealRecovery(byte[] data) => ProtectRecovery(data, true);
    private static byte[] UnsealRecovery(byte[] data) => ProtectRecovery(data, false);
    private static byte[] ProtectRecovery(byte[] data, bool protect)
    {
        var attempt = TryProtectRecovery(data, protect);
        if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return attempt.Bytes!;
    }
    private static (byte[]? Bytes, Exception? Failure) TryProtectRecovery(byte[] data, bool protect)
    {
        var input = new RecoveryBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        RecoveryBlob output = default;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            bool ok = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) return (null, new CryptographicException(Marshal.GetLastWin32Error()));
            if (output.Size < 0 || output.Size > 1024 * 1024)
                return (null, new IOException("Recovery protection produced an invalid bounded record."));
            var bytes = new byte[output.Size]; Marshal.Copy(output.Data, bytes, 0, bytes.Length); return (bytes, null);
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}
