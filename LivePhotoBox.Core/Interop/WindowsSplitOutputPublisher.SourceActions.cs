using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;

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
        private bool _completed;
        internal SourceActionJournal(string[] paths, string? destination)
        {
            Directory.CreateDirectory(RecoveryRoot);
            using var pin = WindowsOwnedFilePublisher.PinDirectoryAncestors(RecoveryRoot);
            _path = Path.Combine(RecoveryRoot, Guid.NewGuid().ToString("N") + ".source");
            var entries = paths.Select(path =>
            {
                using var input = File.OpenRead(path);
                return new SourceActionEntry(path, destination == null ? null : Path.Combine(destination, Path.GetFileName(path)),
                    WindowsFileIdentity.Capture(input.SafeFileHandle), input.Length, Convert.ToHexString(SHA256.HashData(input)));
            }).ToArray();
            _lease = new FileStream(_path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 4096, FileOptions.WriteThrough);
            try
            {
                _lease.Write(SealRecovery(JsonSerializer.SerializeToUtf8Bytes(new SourceActionRecord(destination == null ? "recycle" : "move", entries))));
                _lease.Flush(true);
            }
            catch { _lease.Dispose(); throw; }
        }
        internal void Complete() => _completed = true;
        public void Dispose() { _lease.Dispose(); if (_completed) File.Delete(_path); }
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
