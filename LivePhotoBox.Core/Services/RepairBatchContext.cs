using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;

namespace LivePhotoBox.Services;

// One operation owns this discovery cache and its read leases. Cached facts
// select candidates only; the repair service still inspects the actual pair.
internal sealed class RepairBatchContext : IDisposable
{
    private sealed record Entry(string Path, SourceInspectionResult Inspection, PairingHint? Hint);
    private sealed record DirectoryIndex(DateTime Stamp, List<Entry> Entries, HashSet<string> Names, Dictionary<string, List<Entry>> Pairs, Dictionary<string, Entry> Paths);
    private readonly Dictionary<string, DirectoryIndex> directories = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<IDisposable> leases = [];
    private readonly HashSet<string> published = new(StringComparer.OrdinalIgnoreCase);
    internal void RecordPublished(params string?[] paths)
    {
        gate.Wait();
        try
        {
            foreach (var path in paths)
            {
                if (path == null) continue;
                string fullPath = Path.GetFullPath(path);
                if (published.Contains(fullPath)) continue;
                // Exclusion belongs to this published object, not a reusable
                // filename. Keep it immutable until the batch is disposed.
                leases.Add(File.Open(fullPath, FileMode.Open, FileAccess.Read, FileShare.Read));
                published.Add(fullPath);
            }
        }
        finally { gate.Release(); }
    }
    private readonly SemaphoreSlim gate = new(1, 1);
    internal int DiscoveryInspections { get; private set; }
    internal int DirectoryScans { get; private set; }
    internal int PairingHintInspections { get; private set; }
    private static string Key(LivePhotoBox.Media.Models.SourceProtocol protocol, string id)
        => ((int)protocol).ToString() + ":" + (protocol == LivePhotoBox.Media.Models.SourceProtocol.AppleLivePhoto ? id.ToUpperInvariant() : id);

    internal async Task<IReadOnlyList<(string Path, SourceInspectionResult Inspection)>> CandidatesAsync(string source, CancellationToken token, LivePhotoBox.Media.Models.SourceProtocol? protocol = null, string? identifier = null)
    {
        string directory = Path.GetDirectoryName(Path.GetFullPath(source))!;
        await gate.WaitAsync(token);
        try
        {
            if (!directories.TryGetValue(directory, out var index))
            {
                leases.Add(WindowsOwnedFilePublisher.PinDirectoryAncestors(directory));
                var stamp = Directory.GetLastWriteTimeUtc(directory);
                var entries = new List<Entry>();
                DirectoryScans++;
                var names = SourceNames(directory);
                foreach (var path in names)
                {
                    token.ThrowIfCancellationRequested();
                    if (Path.GetExtension(path).ToLowerInvariant() is not (".jpg" or ".jpeg" or ".heic" or ".heif" or ".mov" or ".mp4")) continue;
                    try
                    {
                        // Prevent changes/replacement of any indexed candidate,
                        // including a previously unrelated file gaining a duplicate ID.
                        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) continue;
                        leases.Add(File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read));
                        DiscoveryInspections++;
                        var inspection = await NativeMediaService.InspectMediaForDiscoveryAsync(path, null, token);
                        PairingHintInspections++;
                        var hint = await NativePairingHint.ReadAsync(path, token);
                        entries.Add(new(path, inspection, hint));
                    }
                    catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
                    {
                        entries.Add(new(path, SourceInspectionResult.Rejected(new(
                            SourceInspectionFailureCategory.Io, SourceInspectionStage.Read, 0, ex.Message)), null));
                    }
                }
                if (stamp != Directory.GetLastWriteTimeUtc(directory))
                    throw new IOException("Repair candidate directory changed during discovery; rescan before processing.");
                var pairs = new Dictionary<string, List<Entry>>(StringComparer.Ordinal);
                foreach (var entry in entries)
                    if (entry.Hint is { } hint)
                    {
                        string key = Key(hint.Protocol, hint.Identifier);
                        if (!pairs.TryGetValue(key, out var group)) pairs[key] = group = [];
                        group.Add(entry);
                    }
                index = new(stamp, entries, names, pairs, entries.ToDictionary(e => e.Path, StringComparer.OrdinalIgnoreCase));
                directories.Add(directory, index);
            }
            if (index.Stamp != Directory.GetLastWriteTimeUtc(directory))
            {
                var names = SourceNames(directory);
                if (!index.Names.SetEquals(names))
                    throw new IOException("Repair candidate directory changed after discovery; rescan before processing.");
                // All original candidates remain pinned; only this operation's
                // published copies may have been added to the directory.
                index = index with { Stamp = Directory.GetLastWriteTimeUtc(directory) };
                directories[directory] = index;
            }
            if (protocol != null)
            {
                if (identifier == null && index.Paths.TryGetValue(Path.GetFullPath(source), out var entry)) identifier = entry.Hint?.Identifier;
                if (identifier == null || !index.Pairs.TryGetValue(Key(protocol.Value, identifier), out var matches)) return [];
                return matches.Select(e => (e.Path, e.Inspection)).ToArray();
            }
            return index.Entries.Select(e => (e.Path, e.Inspection)).ToArray();
        }
        finally { gate.Release(); }
    }

    private HashSet<string> SourceNames(string directory) => Directory.EnumerateFiles(directory)
        .Select(Path.GetFullPath).Where(p => !published.Contains(p)).ToHashSet(StringComparer.OrdinalIgnoreCase);

    public void Dispose()
    {
        for (int i = leases.Count - 1; i >= 0; i--) leases[i].Dispose();
        leases.Clear(); directories.Clear(); gate.Dispose();
    }
}
