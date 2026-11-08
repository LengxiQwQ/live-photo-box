using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;
using LivePhotoBox.Media.Workspace;
using System.Runtime.InteropServices;

namespace LivePhotoBox.Interop;

internal static partial class WindowsSplitOutputPublisher
{
    private sealed record SourceActionEntry(string Source, string? Destination, WindowsFileIdentity Identity, long Length, string Sha256);
    private sealed record SourceActionRecord(string Kind, SourceActionEntry[] Files);

    // A source move/recycle is already authorized by a successful output receipt.
    // Recovery reconciles it; it must never replay deletion or move user files.
    internal sealed class SourceActionJournal : IDisposable
    {
        private readonly string _path;
        private readonly FileStream _lease;
        private readonly NeutralArtifactReadLeaseSet? _namespaceLease;
        private bool _completed;
        private bool _disposed;
        internal Exception? CleanupFailure { get; private set; }
        private SourceActionJournal(string path, FileStream lease, NeutralArtifactReadLeaseSet namespaceLease)
            => (_path, _lease, _namespaceLease) = (path, lease, namespaceLease);

        internal sealed record CreationAttempt(SourceActionJournal? Journal, Exception? Failure);

        internal static CreationAttempt TryCreate(string[] paths, string? destination)
        {
            if (OutputDirectoryAttempt.Ensure(RecoveryRoot) is { } directoryFailure) return new(null, directoryFailure);
            var namespaceLease = new NeutralArtifactReadLeaseSet();
            if (namespaceLease.TryPinDirectory(RecoveryRoot) is { } namespaceFailure)
            { namespaceLease.Dispose(); return new(null, namespaceFailure); }
            TransactionFile? created = null;
            FileStream? stream = null;
            bool handedOff = false;
            try
            {
                var entries = new List<SourceActionEntry>();
                using var sources = new NeutralArtifactReadLeaseSet();
                foreach (string path in paths)
                {
                    if (sources.TryPin(new MediaArtifact { Path = path, Kind = MediaArtifactKind.PrimaryImage }) is { } sourceFailure)
                        return new(null, sourceFailure);
                    using var source = SourceReadAttempt.Open(path);
                    if (!source.Succeeded) return new(null, new IOException($"Cannot read source action input (system error {source.Error})."));
                    if (!WindowsFileIdentity.TryCapture(source.Handle, out var identity, out int error) ||
                        !GetFileSizeEx(source.Handle, out long length))
                        return new(null, new IOException($"Cannot capture source action input (system error {error})."));
                    var hash = MediaFileHashAttempt.Compute(path, default);
                    if (hash.Failure is { } hashFailure) return new(null, hashFailure);
                    entries.Add(new(path, destination is null ? null : Path.Combine(destination, Path.GetFileName(path)), identity!, length, hash.Sha256!));
                }
                var creation = TryCreateTransactionFile(RecoveryRoot, "source-action", false);
                if (creation.Failure is { } creationFailure) return new(null, creationFailure);
                created = creation.File!;
                stream = new FileStream(created.Handle, FileAccess.ReadWrite);
                byte[] bytes = SealRecovery(JsonSerializer.SerializeToUtf8Bytes(new SourceActionRecord(destination is null ? "recycle" : "move", entries.ToArray())));
                if (TransactionStreamWriteAttempt.Write(stream, bytes, default) is { } writeFailure) return new(null, writeFailure);
                if (!FlushFileBuffers(created.Handle)) return new(null, BuildIOException("Cannot flush source action record.", Marshal.GetLastWin32Error()));
                string final = Path.Combine(RecoveryRoot, Guid.NewGuid().ToString("N") + ".source");
                if (RenameNoReplaceFailure(created.Handle, final) is { } renameFailure) return new(null, renameFailure);
                handedOff = true;
                return new(new SourceActionJournal(final, stream, namespaceLease), null);
            }
            finally
            {
                if (!handedOff)
                {
                    if (created is not null)
                    {
                        var disposition = new FileDispositionInfo { DeleteFile = 1 };
                        _ = SetFileInformationByHandle(created.Handle, FileDispositionInfoClass, ref disposition, (uint)Marshal.SizeOf<FileDispositionInfo>());
                    }
                    stream?.Dispose();
                    created?.Handle.Dispose();
                    namespaceLease.Dispose();
                }
            }
        }
        internal SourceActionJournal(string[] paths, string? destination)
        {
            var attempt = TryCreate(paths, destination);
            if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
            var prepared = attempt.Journal!;
            (_path, _lease, _namespaceLease) = (prepared._path, prepared._lease, prepared._namespaceLease);
        }
        internal void Complete() => _completed = true;
        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            try
            {
                if (_completed)
                {
                    var disposition = new FileDispositionInfo { DeleteFile = 1 };
                    if (!SetFileInformationByHandle(_lease.SafeFileHandle, FileDispositionInfoClass, ref disposition, (uint)Marshal.SizeOf<FileDispositionInfo>()))
                        CleanupFailure = BuildIOException("Source action record retained after completion: " + _path, Marshal.GetLastWin32Error());
                }
            }
            finally { _lease.Dispose(); _namespaceLease?.Dispose(); }
        }
    }

    internal static IReadOnlyList<string> ReconcileSourceActions()
    {
        var reports = new List<string>();
        if (!Directory.Exists(RecoveryRoot)) return reports;
        using var rootPin = WindowsOwnedFilePublisher.PinDirectoryAncestors(RecoveryRoot);
        foreach (var path in Directory.EnumerateFiles(RecoveryRoot, "*.source"))
        {
            try
            {
                using var lease = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.None);
                if (lease.Length > 1024 * 1024) throw new IOException("Source action record exceeds size limit.");
                var bytes = new byte[checked((int)lease.Length)]; lease.ReadExactly(bytes);
                var record = JsonSerializer.Deserialize<SourceActionRecord>(UnsealRecovery(bytes)) ?? throw new IOException("Missing source action record.");
                foreach (var entry in record.Files)
                {
                    string status = File.Exists(entry.Source) ? "source retained" :
                        entry.Destination != null && File.Exists(entry.Destination) ? "archive present; move may have completed" : "source no longer at original path; completion unconfirmed";
                    reports.Add($"Interrupted source {record.Kind}: {entry.Source}: {status}. No action replayed. Record: {path}");
                }
            }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException or JsonException or CryptographicException)
            { reports.Add($"Source action record retained: {path}: {error.Message}"); }
        }
        return reports;
    }
}
