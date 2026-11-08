using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;

namespace LivePhotoBox.Services;

// Scanning uses shared short-lived identity facts. Only this operation
// retains leases for its own published outputs; execution inspects the actual pair.
internal sealed class RepairBatchContext : IDisposable
{
    private readonly List<IDisposable> leases = [];
    private readonly HashSet<string> published = new(StringComparer.OrdinalIgnoreCase);
    // GUI shares discovery facts; execution inspection remains fresh.
    internal LivePhotoBox.Models.RepairOptions DiagnosticOptions { get; init; } = new();
    internal DiscoveryInspectionSession? DiscoverySession { get; init; }
    internal Task<SourceInspectionResult> InspectAsync(string primary, string? secondary, CancellationToken token)
        => DiscoverySession?.InspectForDiscoveryAsync(primary, secondary, token)
            ?? NativeMediaService.InspectMediaForDiscoveryAsync(primary, secondary, token);
    internal async Task<DiscoveryPairingCandidate?> ReadHintAsync(string path, CancellationToken token,
        DiscoveryCapability capability = DiscoveryCapability.AppleId)
    {
        PairingHintInspections++;
        return (await FileIndexService.Shared.GetAsync(path,capability,token).ConfigureAwait(false))?.Pairing;
    }
    internal Exception? PublicationTrackingFailure { get; private set; }
    internal Exception? RecordPublished(params string?[] paths)
    {
        gate.Wait();
        try
        {
            foreach (var path in paths)
            {
                if (path == null) continue;
                string fullPath = Path.GetFullPath(path);
                if (published.Contains(fullPath)) continue;
                var source = SourceReadAttempt.Open(fullPath);
                if (!source.Succeeded)
                {
                    int error = source.Error; source.Dispose();
                    return PublicationTrackingFailure = new IOException($"已完成的导出无法建立后续批次配对保护（系统错误 {error}）：{fullPath}。已导出文件保留，相关后续配对检查停止。");
                }
                leases.Add(source);
                published.Add(fullPath);
            }
            return null;
        }
        finally { gate.Release(); }
    }

    internal LivePhotoRepairService.RepairResult ExplainTrackingFailure(LivePhotoRepairService.RepairResult result)
    {
        if (PublicationTrackingFailure == null) return result;
        // Publication already passed its full content/identity transaction.
        // This failure concerns later batch bookkeeping; retain that actual
        // media outcome and expose the independent limitation in details.
        return result with { Message = result.Message + " " + PublicationTrackingFailure.Message,
            Limitations = [.. result.Limitations, new RepairLimitation("batch_pairing_protection", PublicationTrackingFailure.Message)] };
    }
    private readonly SemaphoreSlim gate = new(1, 1);
    internal int DiscoveryInspections { get; private set; }
    internal int DirectoryScans { get; private set; }
    internal int PairingHintInspections { get; private set; }
    private static string Key(LivePhotoBox.Media.Models.SourceProtocol protocol, string id)
        => ((int)protocol).ToString() + ":" + (protocol == LivePhotoBox.Media.Models.SourceProtocol.AppleLivePhoto ? id.ToUpperInvariant() : id);

    internal sealed record CandidateAttempt(IReadOnlyList<(string Path, string Identifier)> Candidates, Exception? Failure);

    // Compatibility entry point for callers that explicitly require strict semantics.
    internal async Task<IReadOnlyList<(string Path, string Identifier)>> CandidatesAsync(string source, CancellationToken token,
        LivePhotoBox.Media.Models.SourceProtocol? protocol = null, string? identifier = null)
    {
        var attempt = await TryCandidatesAsync(source, token, protocol, identifier).ConfigureAwait(false);
        if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return attempt.Candidates;
    }

    internal async Task<CandidateAttempt> TryCandidatesAsync(string source, CancellationToken token,
        LivePhotoBox.Media.Models.SourceProtocol? protocol = null, string? identifier = null)
    {
        if (token.IsCancellationRequested) return new([], new OperationCanceledException(token));
        if (PublicationTrackingFailure is { } trackingFailure) return new([], trackingFailure);
        var capability = protocol == LivePhotoBox.Media.Models.SourceProtocol.VivoLegacyDualFile ? DiscoveryCapability.VivoId : DiscoveryCapability.AppleId;
        if (protocol != null)
        {
            identifier ??= (await ReadHintAsync(source, token, capability).ConfigureAwait(false))?.Identifier;
            if (token.IsCancellationRequested) return new([], new OperationCanceledException(token));
            if (identifier == null) return new([], null);
        }
        string directory = Path.GetDirectoryName(Path.GetFullPath(source))!;
        if (!await ResultCancellation.EnterAsync(gate, token).ConfigureAwait(false)) return new([], new OperationCanceledException(token));
        try
        {
            DirectoryScans++;
            var before = TrySourceNames(directory, token);
            if (before.Failure is { } enumerationFailure) return new([], enumerationFailure);
            var names = before.Names!;
            var results = new List<(string Path, string Identifier)>();
            foreach (var path in names)
            {
                if (token.IsCancellationRequested) return new([], new OperationCanceledException(token));
                if (!LivePhotoScanSupport.IsImage(path) && !LivePhotoScanSupport.IsVideo(path)) continue;
                if (protocol == null) { results.Add((path, string.Empty)); continue; }
                var fact = await FileIndexService.Shared.GetAsync(path, capability, token).ConfigureAwait(false);
                if (token.IsCancellationRequested) return new([], new OperationCanceledException(token));
                if (fact == null || fact.State is FactState.Incomplete or FactState.ReadFailed)
                    return new([], new IOException("Repair identity scope is incomplete; no companion can be confirmed safely."));
                if (fact.Pairing is { } hint && Key(hint.Protocol, hint.Identifier) == Key(protocol.Value, identifier!))
                    results.Add((path, hint.Identifier));
            }
            var after = TrySourceNames(directory, token);
            if (after.Failure is { } recheckFailure) return new([], recheckFailure);
            if (!names.SetEquals(after.Names!)) return new([], new IOException("Repair candidate scope changed during discovery."));
            return new(results, null);
        }
        finally { gate.Release(); }
    }

    private (HashSet<string>? Names, Exception? Failure) TrySourceNames(string directory, CancellationToken token)
    {
        var listed = ScanFileSystem.Enumerate(directory, token);
        if (token.IsCancellationRequested) return (null, new OperationCanceledException(token));
        if (listed.Failure is { } failure) return (null, failure);
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var path in listed.Entries!)
        {
            if (published.Contains(path)) continue;
            var read = ScanFileSystem.Read(path);
            if (read.Failure is { } readFailure) return (null, readFailure);
            if ((read.Value!.Attributes & FileAttributes.Directory) == 0) names.Add(path);
        }
        return (names, null);
    }

    public void Dispose()
    {
        for (int i = leases.Count - 1; i >= 0; i--) leases[i].Dispose();
        leases.Clear(); gate.Dispose();
    }
}
