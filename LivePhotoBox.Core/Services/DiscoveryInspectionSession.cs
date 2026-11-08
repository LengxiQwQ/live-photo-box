using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;

namespace LivePhotoBox.Services;

// Page/operation facade. Facts are shared by the application; execution checks
// are deliberately fresh and never cached as a file's permanent health.
internal sealed class DiscoveryInspectionSession : IDisposable
{
    private readonly HashSet<(string,DiscoveryCapability)> requested = new();
    internal int SingleInspections { get; private set; }
    internal int PairInspections { get; private set; }
    internal int HintInspections { get; private set; }
    internal int CandidateInspections { get; private set; }
    internal ulong CandidateBytesRead { get; private set; }
    internal async Task<MediaCandidate?> ReadFastCandidateAsync(string path, CancellationToken token,
        DiscoveryCapability capability = DiscoveryCapability.AppleId)
    {
        var before = FileIndexService.Shared.Reads;
        bool explicitRetry;
        lock (requested) explicitRetry = requested.Add((Path.GetFullPath(path).ToUpperInvariant(),capability));
        // A new user scan retries a prior incomplete/failed capability once;
        // successful facts remain reusable and no full-inspection fallback exists.
        var result = await FileIndexService.Shared.GetAsync(path, capability, token, retry:explicitRetry).ConfigureAwait(false);
        if (FileIndexService.Shared.Reads != before) { CandidateInspections++; CandidateBytesRead += result?.BytesRead ?? 0; }
        return result;
    }
    internal async Task<DiscoveryPairingCandidate?> ReadPairingCandidateAsync(string path, CancellationToken token,
        DiscoveryCapability capability = DiscoveryCapability.AppleId)
        => (await ReadFastCandidateAsync(path, token, capability).ConfigureAwait(false))?.Pairing;
    // Explicit strict compatibility call only. Preserve its real hash semantics;
    // candidate readers never call this as a fallback.
    internal Task<PairingHint?> ReadPairingHintAsync(string path, CancellationToken token)
    {
        HintInspections++;
        return NativePairingHint.ReadAsync(path,token);
    }
    internal Task<SourceInspectionResult> InspectForDiscoveryAsync(string primary, string? secondary, CancellationToken token)
    {
        if (secondary == null) SingleInspections++; else PairInspections++;
        return NativeMediaService.InspectMediaForDiscoveryAsync(primary, secondary, token);
    }
    public void Dispose() { }
}
