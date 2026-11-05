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
    private static string RecoveryRoot => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LivePhotoBox", "Recovery", "publication");

    // Active manifests are held exclusively until every publication handle has
    // closed. Another GUI/CLI instance must never recover an in-flight operation.
    internal static IReadOnlyList<string> RecoverPublications(string? targetDirectory = null)
    {
        var failures = new List<string>();
        Directory.CreateDirectory(RecoveryRoot);
        using var rootPin = WindowsOwnedFilePublisher.PinDirectoryAncestors(RecoveryRoot);
        foreach (string path in Directory.EnumerateFiles(RecoveryRoot, "*.lpbr"))
        {
            FileStream journal;
            try { journal = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException e) when ((e.HResult & 0xffff) is 32 or 33) { continue; }
            try
            {
                using (journal)
                {
                    if (journal.Length > 1024 * 1024) throw new IOException("Recovery manifest exceeds its size limit.");
                    byte[] sealedRecord = new byte[checked((int)journal.Length)];
                    journal.ReadExactly(sealedRecord);
                    var record = JsonSerializer.Deserialize<RecoveryRecord>(UnsealRecovery(sealedRecord))
                        ?? throw new IOException("Recovery record is empty.");
                    if (targetDirectory != null && !string.Equals(record.Directory, targetDirectory, StringComparison.OrdinalIgnoreCase)) continue;
                    using var directoryPin = WindowsOwnedFilePublisher.PinDirectoryAncestors(record.Directory);
                    ValidateRecoveryRecord(record, path);
                    string marker = path + ".done";
                    if (File.Exists(marker))
                    {
                        if (System.Text.Encoding.UTF8.GetString(UnsealRecovery(File.ReadAllBytes(marker))) != path)
                            throw new IOException("Recovery completion marker does not match its transaction.");
                    }
                    else
                    {
                        RecoverRecord(record);
                        WriteRecoveryCompletion(path);
                    }
                    foreach (var slot in record.Slots)
                    {
                        DeleteRecoveryCopy(slot.Stage);
                        if (slot.Backup != null) DeleteRecoveryCopy(slot.Backup);
                        DeleteRecoveryCopy(slot.TemporaryStage);
                    }
                }
                File.Delete(path);
                File.Delete(path + ".done");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or CryptographicException or System.ComponentModel.Win32Exception)
            {
                failures.Add($"[RecoveryRequired] {path}: {e.Message}");
            }
        }
        if (targetDirectory != null && failures.Count > 0)
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
        private PublicationJournal(string path, FileStream lease, List<RecoveryCopy> copies)
        { _path = path; _lease = lease; _copies = copies; }

        internal static PublicationJournal Create(List<OutputSlot> slots, string directory)
        {
            Directory.CreateDirectory(RecoveryRoot);
            using var rootPin = WindowsOwnedFilePublisher.PinDirectoryAncestors(RecoveryRoot);
            using var targetPin = WindowsOwnedFilePublisher.PinDirectoryAncestors(directory);
            var copies = new List<RecoveryCopy>();
            string path = Path.Combine(RecoveryRoot, Guid.NewGuid().ToString("N") + ".lpbr");
            string preparing = Path.ChangeExtension(path, ".preparing");
            var journalHandle = CreateFileW(preparing, GenericRead | GenericWrite | DeleteAccess, 0,
                IntPtr.Zero, CreateNew, FileAttributeNormal | OpenReparsePoint, IntPtr.Zero);
            if (journalHandle.IsInvalid) { journalHandle.Dispose(); throw new IOException("Cannot create recovery manifest."); }
            var lease = new FileStream(journalHandle, FileAccess.ReadWrite);
            try
            {
                var entries = new List<RecoverySlot>();
                foreach (var slot in slots)
                {
                    var stage = Copy(slot.Stage!, copies);
                    var baseline = slot.Baseline!;
                    var backup = baseline.Exists ? Copy(baseline.Backup!, copies) : null;
                    var temporaryStage = new RecoveryCopy(slot.Stage!.Path, slot.Stage.Identity, slot.Stage.Length, slot.Stage.Sha256!);
                    copies.Add(temporaryStage);
                    entries.Add(new(slot.FinalPath, baseline.Identity ?? slot.Stage.Identity, baseline.Exists, stage, backup, temporaryStage));
                }
                var record = new RecoveryRecord(1, directory, CaptureDirectoryIdentity(directory), entries.ToArray());
                var bytes = SealRecovery(JsonSerializer.SerializeToUtf8Bytes(record));
                lease.Write(bytes);
                lease.Flush(true);
                RenameNoReplaceThroughHandle(lease.SafeFileHandle, path);
                return new(path, lease, copies);
            }
            catch
            {
                lease.Dispose();
                File.Delete(preparing);
                File.Delete(path); // No mutation can have started without a durable record.
                foreach (var copy in copies.Where(c => string.Equals(Path.GetDirectoryName(c.Path), RecoveryRoot, StringComparison.OrdinalIgnoreCase))) DeleteRecoveryCopy(copy);
                throw;
            }
        }

        private static RecoveryCopy Copy(TransactionFile file, List<RecoveryCopy> copies)
        {
            string path = Path.Combine(RecoveryRoot, Guid.NewGuid().ToString("N") + ".data");
            using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None);
            CopyHandleBytes(file.Handle, output.SafeFileHandle, file.Length, path, CancellationToken.None);
            output.Flush(true);
            string sha = ComputeSha256(output.SafeFileHandle, file.Length, path, CancellationToken.None);
            if (!string.Equals(sha, file.Sha256, StringComparison.Ordinal)) throw new IOException("Recovery copy differs from its verified transaction artifact.");
            var copy = new RecoveryCopy(path, WindowsFileIdentity.Capture(output.SafeFileHandle), file.Length, sha);
            copies.Add(copy);
            return copy;
        }

        internal void Complete()
        {
            // An authenticated completion marker makes recovery cleanup-only.
            // Keep the main record locked until all mutation handles are closed.
            WriteRecoveryCompletion(_path);
            _complete = true;
        }

        public void Dispose()
        {
            if (!_complete) { _lease.Dispose(); return; } // Failed rollback: retain durable recovery authority.
            try
            {
                foreach (var copy in _copies) DeleteRecoveryCopy(copy);
                _lease.Dispose();
                File.Delete(_path);
                File.Delete(_path + ".done");
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            { LivePhotoBox.Services.LogService.Warn($"Publication recovery cleanup retained: {e.Message}"); }
            finally { _lease.Dispose(); }
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

    private static void ValidateRecoveryRecord(RecoveryRecord record, string path)
    {
        if (record.Version != 1 || record.Slots.Length is < 1 or > 2 || !Path.IsPathFullyQualified(record.Directory))
            throw new IOException("Unsupported recovery record.");
        if (!SameObjectAndAttributes(record.DirectoryIdentity, CaptureDirectoryIdentity(record.Directory)))
            throw new IOException("Recovery output directory identity changed.");
        foreach (var slot in record.Slots)
        {
            if (!Path.IsPathFullyQualified(slot.Path) || !string.Equals(Path.GetDirectoryName(slot.Path), record.Directory, StringComparison.OrdinalIgnoreCase))
                throw new IOException("Recovery target escaped its recorded directory.");
            foreach (var copy in new[] { slot.Stage, slot.Backup }.OfType<RecoveryCopy>())
                if (!string.Equals(Path.GetDirectoryName(copy.Path), RecoveryRoot, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetExtension(copy.Path) != ".data" || copy.Length < 0 || copy.Sha256.Length != 64)
                    throw new IOException("Invalid recovery artifact authority.");
            if (!string.Equals(Path.GetDirectoryName(slot.TemporaryStage.Path), record.Directory, StringComparison.OrdinalIgnoreCase) ||
                !Path.GetFileName(slot.TemporaryStage.Path).StartsWith(".lpb-split-stage-", StringComparison.Ordinal))
                throw new IOException("Invalid transaction temporary stage authority.");
            if (slot.Existed != (slot.Backup != null)) throw new IOException("Incomplete recovery baseline.");
        }
    }

    private static void RecoverRecord(RecoveryRecord record)
    {
        // Validate and pin every member before touching any member of a pair.
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
                    throw BuildIOException("Recovery target cannot be pinned.", error);
                }
                pins.Add(target);
                if (!SameObjectAndAttributes(slot.Identity, CaptureAndValidateRegularFile(target, slot.Path, 1)))
                    throw new IOException("Recovery target was replaced; retained without modification.");
                targets.Add((slot, target));
                var stage = OpenRecoveryCopy(slot.Stage); pins.Add(stage);
                if (slot.Existed) { var backup = OpenRecoveryCopy(slot.Backup!); pins.Add(backup); }
                if (!MatchesRecoveryPrefix(target, slot.Path, slot.Stage) &&
                    !(slot.Backup != null && MatchesRecoveryPrefix(target, slot.Path, slot.Backup)))
                    throw new IOException("Recovery target has unrelated bytes; retained without modification.");
            }
            foreach (var (slot, handle) in targets.AsEnumerable().Reverse())
            {
                if (slot.Existed)
                {
                    using var backup = OpenRecoveryCopy(slot.Backup!);
                    CopyHandleBytes(backup.SafeFileHandle, handle, slot.Backup!.Length, slot.Path, CancellationToken.None);
                    if (GetLength(handle, slot.Path) != slot.Backup.Length || ComputeSha256(handle, slot.Backup.Length, slot.Path, CancellationToken.None) != slot.Backup.Sha256)
                        throw new IOException("Recovery baseline verification failed.");
                }
                else
                {
                    var proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(handle, slot.Identity, record.Directory, out _);
                    if (proof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked)) throw new IOException("Recovery output removal is unproven.");
                }
            }
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
            var handle = CreateFileW(path, access, share, IntPtr.Zero, OpenExisting, OpenReparsePoint, IntPtr.Zero);
            int error = Marshal.GetLastWin32Error();
            if (!handle.IsInvalid || error is not (32 or 33) || attempt >= 50) return handle;
            handle.Dispose();
            Thread.Sleep(10);
        }
    }

    private static FileStream OpenRecoveryCopy(RecoveryCopy copy)
    {
        var handle = OpenRecoveryHandle(copy.Path, GenericRead | FileReadAttributes, FileShareRead);
        if (handle.IsInvalid) { handle.Dispose(); throw new IOException("Recovery backup cannot be read."); }
        try
        {
            if (!SameObjectAndAttributes(copy.Identity, CaptureAndValidateRegularFile(handle, copy.Path, 1)) ||
                GetLength(handle, copy.Path) != copy.Length || ComputeSha256(handle, copy.Length, copy.Path, CancellationToken.None) != copy.Sha256)
                throw new IOException("Recovery artifact identity or bytes changed.");
            return new FileStream(handle, FileAccess.Read);
        }
        catch { handle.Dispose(); throw; }
    }

    private static bool MatchesRecoveryPrefix(SafeFileHandle target, string path, RecoveryCopy copy)
    {
        long length = GetLength(target, path);
        if (length > copy.Length) return false;
        using var source = OpenRecoveryCopy(copy);
        byte[] left = new byte[BufferSize], right = new byte[BufferSize];
        for (long offset = 0; offset < length;)
        {
            int count = (int)Math.Min(BufferSize, length - offset);
            int read = RandomAccess.Read(target, left.AsSpan(0, count), offset);
            int expected = RandomAccess.Read(source.SafeFileHandle, right.AsSpan(0, count), offset);
            if (read != count || expected != count || !left.AsSpan(0, count).SequenceEqual(right.AsSpan(0, count))) return false;
            offset += count;
        }
        return true;
    }

    private static void DeleteRecoveryCopy(RecoveryCopy copy)
    {
        // Missing copies are allowed after a previously completed cleanup step.
        if (!File.Exists(copy.Path)) return;
        using var handle = OpenRecoveryHandle(copy.Path, GenericRead | DeleteAccess | FileReadAttributes, 0);
        if (handle.IsInvalid) throw new IOException("Recovery artifact cleanup could not acquire its object.");
        using var parentPin = WindowsOwnedFilePublisher.PinDirectoryAncestors(Path.GetDirectoryName(copy.Path)!);
        if (!SameObjectAndAttributes(copy.Identity, CaptureAndValidateRegularFile(handle, copy.Path, 1)) ||
            GetLength(handle, copy.Path) != copy.Length || ComputeSha256(handle, copy.Length, copy.Path, CancellationToken.None) != copy.Sha256)
            throw new IOException("Recovery artifact replacement or changed bytes retained.");
        var proof = WindowsOwnedFilePublisher.DeleteOwnedObjectWithProof(handle, copy.Identity, Path.GetDirectoryName(copy.Path)!, out _);
        if (proof is not (OwnedObjectCleanupProof.Gone or OwnedObjectCleanupProof.Unlinked)) throw new IOException("Recovery artifact cleanup unproven.");
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
        var input = new RecoveryBlob { Size = data.Length, Data = Marshal.AllocHGlobal(data.Length) };
        RecoveryBlob output = default;
        try
        {
            Marshal.Copy(data, 0, input.Data, data.Length);
            bool ok = protect ? CryptProtectData(ref input, null, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output)
                : CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 1, out output);
            if (!ok) throw new CryptographicException(Marshal.GetLastWin32Error());
            var bytes = new byte[output.Size]; Marshal.Copy(output.Data, bytes, 0, bytes.Length); return bytes;
        }
        finally { Marshal.FreeHGlobal(input.Data); if (output.Data != IntPtr.Zero) LocalFree(output.Data); }
    }
}
