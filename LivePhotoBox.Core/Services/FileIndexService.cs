using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Services;

// Application-session facts only. Never holds a source lease or execution authority.
internal sealed class FileIndexService
{
    internal static FileIndexService Shared { get; } = new();
    private sealed record Version(long Size, long Stamp, WindowsFileIdentity Identity);
    private sealed class Entry(Version version)
    {
        internal Version Version = version;
        internal readonly Dictionary<DiscoveryCapability, Flight> Facts = new();
        internal long Used = Environment.TickCount64;
    }
    private sealed class Flight
    {
        internal readonly CancellationTokenSource Cancellation = new();
        internal Task<MediaCandidate?> Work = null!;
        internal int Consumers;
    }
    private readonly object gate = new();
    private readonly Dictionary<string, Entry> entries = new(StringComparer.OrdinalIgnoreCase);
    private readonly SemaphoreSlim admission = new(256, 256);
    private readonly SemaphoreSlim global = new(4, 4);
    private readonly Dictionary<string, SemaphoreSlim> volumes = new(StringComparer.OrdinalIgnoreCase);
    internal long Reads, BytesRead, ReadCalls, AppleReads, VivoReads, MarkerReads;
    private static Version? Observe(string path)
    {
        using var source = SourceReadAttempt.Open(path, share: 7);
        if (!source.Succeeded || !WindowsFileIdentity.TryCapture(source.Handle, out var identity, out _) ||
            identity!.IsReparsePoint || (identity.FileAttributes & 0x10) != 0 ||
            !source.TryGetLength(out long length, out _) || !source.TryGetLastWriteTime(out long stamp, out _)) return null;
        return new(length, stamp, identity);
    }
    private static MediaCandidate Unavailable(string detail) => new(null, 0, 0, ImageContainer.Unknown,
        VideoContainer.Unknown, FactState.ReadFailed, Diagnostic: detail);
    internal async Task<MediaCandidate?> GetAsync(string path, DiscoveryCapability capability, CancellationToken token, bool retry = false)
    {
        if (!await ResultCancellation.EnterAsync(admission, token).ConfigureAwait(false)) return null;
        try { return await GetCoreAsync(path,capability,token,retry).ConfigureAwait(false); }
        finally { admission.Release(); }
    }
    private async Task<MediaCandidate?> GetCoreAsync(string path, DiscoveryCapability capability, CancellationToken token, bool retry)
    {
        if (token.IsCancellationRequested) return null;
        path = Path.GetFullPath(path);
        var version = Observe(path);
        if (version == null) return Unavailable("Discovery could not safely read this file; no processing authority was issued.");
        Flight flight;
        lock (gate)
        {
            if (!entries.TryGetValue(path, out var entry) || entry.Version != version)
            {
                // Old in-flight consumers will detect a changed version at completion.
                entries[path] = entry = new(version);
            }
            entry.Used = Environment.TickCount64;
            if (entry.Facts.TryGetValue(capability, out var old) &&
                (old.Cancellation.IsCancellationRequested || old.Work.IsFaulted || old.Work.IsCanceled ||
                 retry && old.Work.IsCompletedSuccessfully && old.Work.Result?.State is FactState.ReadFailed or FactState.Incomplete))
                entry.Facts.Remove(capability);
            if (!entry.Facts.TryGetValue(capability, out flight!))
            {
                flight = new();
                entry.Facts.Add(capability, flight);
                flight.Work = ReadAsync(path, version, capability, flight.Cancellation.Token);
            }
            flight.Consumers++;
            // Only idle records are evicted. A bounded dispatcher limits active records.
            if (entries.Count > 8192)
                foreach (var key in entries.Where(e => e.Value.Facts.Values.All(f => f.Consumers == 0 && f.Work.IsCompleted))
                    .OrderBy(e => e.Value.Used).Take(entries.Count - 8192).Select(e => e.Key).ToArray()) entries.Remove(key);
        }
        try { return await ResultCancellation.ObserveAsync(flight.Work, token).ConfigureAwait(false); }
        finally
        {
            lock (gate)
                if (--flight.Consumers == 0 && !flight.Work.IsCompleted) flight.Cancellation.Cancel();
        }
    }
    private async Task<MediaCandidate?> ReadAsync(string path, Version version, DiscoveryCapability capability, CancellationToken token)
    {
        SemaphoreSlim volume;
        lock (gate)
        {
            string root = Path.GetPathRoot(path)!;
            if (!volumes.TryGetValue(root, out volume!)) volumes[root] = volume = new(2, 2);
        }
        if (!await ResultCancellation.EnterAsync(volume, token).ConfigureAwait(false)) return null;
        try
        {
            if (!await ResultCancellation.EnterAsync(global, token).ConfigureAwait(false)) return null;
            try
            {
                Interlocked.Increment(ref Reads);
                if (capability == DiscoveryCapability.AppleId) Interlocked.Increment(ref AppleReads);
                else if (capability == DiscoveryCapability.VivoId) Interlocked.Increment(ref VivoReads);
                else Interlocked.Increment(ref MarkerReads);
                var fact = await NativeMediaCandidate.ReadAsync(path, token, capability).ConfigureAwait(false);
                if (Observe(path) != version) return Unavailable("File changed during discovery; rescan this member.");
                Interlocked.Add(ref BytesRead, checked((long)(fact?.BytesRead ?? 0)));
                Interlocked.Add(ref ReadCalls, checked((long)(fact?.ReadCount ?? 0)));
                return fact;
            }
            finally { global.Release(); }
        }
        finally { volume.Release(); }
    }
}
