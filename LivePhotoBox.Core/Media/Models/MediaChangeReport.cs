using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using LivePhotoBox.Interop;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Media.Models;

internal enum MediaChangeAction { Copied, Mapped, Reencoded, Degraded, Omitted, Unverified }
internal enum MediaChangeReason { None, RepresentationChanged, AuthorizedSourceCleanup, ContainerCodecUnsupported,
    TargetProtocolRestriction, ImplementationUnsupported, InvalidInput, EvidenceUnavailable, EmptyTrackWithoutSamples }

internal sealed record MediaAssetChange
{
    public required string AssetId { get; init; }
    public required string Stage { get; init; }
    public required MediaChangeAction Action { get; init; }
    public required MediaChangeReason ReasonCode { get; init; }
    public required string Reason { get; init; }
    public string? SourceRepresentation { get; init; }
    public string? TargetRepresentation { get; init; }
    public string? SourceSha256 { get; init; }
    public string? TargetSha256 { get; init; }
    public string? SourceArtifactSha256 { get; init; }
    public string? TargetArtifactSha256 { get; init; }
    public string EvidenceScope { get; init; } = "PreparedNeutral";
}

internal sealed record MediaChangeReport
{
    public int SchemaVersion { get; init; } = 1;
    public IReadOnlyList<MediaAssetChange> Items { get; init; } = [];
    public IReadOnlyList<ProductCommittedArtifact> Outputs { get; init; } = [];
    public IReadOnlyList<RemovedProtocolFact> RemovedProtocolFacts { get; init; } = [];
    public IReadOnlyList<NotCarriedItem> ToNotCarried() => Items
        .Where(x => x.Action is MediaChangeAction.Reencoded or MediaChangeAction.Degraded or MediaChangeAction.Omitted or MediaChangeAction.Unverified)
        .Select(x => new NotCarriedItem { Item = x.AssetId, Reason = x.Reason, Outcome = x.Action switch
        {
            MediaChangeAction.Reencoded => PreservationOutcome.Reencoded,
            MediaChangeAction.Degraded => PreservationOutcome.DegradedToSdr,
            MediaChangeAction.Omitted => PreservationOutcome.DiscardedNotApplicable,
            _ => PreservationOutcome.PartiallyPreserved
        }}).ToArray();
}

internal sealed record MediaAssetObservation
{
    public string Id { get; init; } = "";
    public string Kind { get; init; } = "";
    public string Handler { get; init; } = "";
    public string Codec { get; init; } = "";
    public uint SampleCount { get; init; }
    public ulong ByteLength { get; init; }
    public string Sha256 { get; init; } = "";
    public string DescriptionSha256 { get; init; } = "";
}
internal sealed record MediaAssetInventory
{
    public int SchemaVersion { get; init; }
    public IReadOnlyList<MediaAssetObservation> Assets { get; init; } = [];
    public string ArtifactSha256 { get; init; } = "";
}

/// <summary>Native observes media structure and payloads; managed code only
/// binds observations to immutable files and projects typed operation results.</summary>
internal static class MediaChangeReports
{
    internal static unsafe MediaAssetInventory CaptureVideo(string path, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var file = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        using var digest = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] chunk = new byte[65536];
        int read;
        while ((read = file.Read(chunk)) != 0)
        {
            token.ThrowIfCancellationRequested();
            digest.AppendData(chunk, 0, read);
        }
        string hash = Convert.ToHexString(digest.GetHashAndReset());
        using var context = NativeContext.Create(token);
        using var lease = context.AcquireOperationLease();
        NativeResult status = NativeMethods.InspectVideoInventory(lease.Handle, path, null, 0, out nuint count);
        token.ThrowIfCancellationRequested();
        if (status != NativeResult.BufferTooSmall || count == 0 || count > 16 * 1024 * 1024)
            throw new InvalidDataException($"Native media inventory failed: {status} ({context.GetLastError()}).");
        byte[] bytes = new byte[checked((int)count)];
        fixed (byte* buffer = bytes)
        {
            status = NativeMethods.InspectVideoInventory(lease.Handle, path, buffer, count, out nuint actual);
            token.ThrowIfCancellationRequested();
            if (status != NativeResult.Ok || actual != count)
                throw new InvalidDataException($"Native media inventory changed or failed: {status}.");
        }
        var result = JsonSerializer.Deserialize<MediaAssetInventory>(bytes, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
            ?? throw new InvalidDataException("Native media inventory is empty.");
        if (result.SchemaVersion != 1 || result.Assets.Count == 0 || result.Assets.GroupBy(x => x.Id).Any(x => x.Count() != 1) ||
            result.Assets.Any(x => x.Sha256.Length != 64))
            throw new InvalidDataException("Native media inventory has an invalid version or asset identity.");
        return result with { ArtifactSha256 = hash };
    }

    internal static IReadOnlyList<MediaAssetChange> Compare(MediaAssetInventory before, MediaAssetInventory after,
        string stage, bool authorizedCleanup = false)
    {
        var changes = new List<MediaAssetChange>();
        foreach (var source in before.Assets)
        {
            var target = after.Assets.SingleOrDefault(x => x.Id == source.Id);
            if (target is null && source.Handler is "vide" or "soun")
            {
                var candidates = after.Assets.Where(x => x.Kind == "Track" && x.Handler == source.Handler).ToArray();
                if (candidates.Length == 1 && before.Assets.Count(x => x.Kind == "Track" && x.Handler == source.Handler) == 1)
                    target = candidates[0];
            }
            if (target is not null && (source.Kind != target.Kind || source.Handler != target.Handler))
                throw new InvalidDataException($"Media asset identity changed its kind/role: {source.Id}.");
            bool same = target is not null && source.Sha256 == target.Sha256 && source.SampleCount == target.SampleCount;
            bool descriptionSame = target is not null && source.DescriptionSha256 == target.DescriptionSha256;
            if (target is null && source.Kind == "Track" && source.Handler is "vide" or "soun" && source.SampleCount > 0)
                throw new InvalidDataException($"Required media track disappeared: {source.Id} ({source.Handler}).");
            if (target is null && !authorizedCleanup)
                throw new InvalidDataException($"Representable Neutral asset disappeared without a format decision: {source.Id}.");
            if (authorizedCleanup && !same && source.Kind == "Track" && source.SampleCount > 0 && source.Handler is "vide" or "soun" or "auxv")
                throw new InvalidDataException($"Source cleanup changed unrelated media payload: {source.Id}.");
            if (!same && !authorizedCleanup && source.Kind == "Metadata")
                throw new InvalidDataException($"Ordinary Neutral metadata changed without a mapping: {source.Id}.");
            changes.Add(new MediaAssetChange
            {
                AssetId = source.Id, Stage = stage,
                Action = same ? descriptionSame ? MediaChangeAction.Copied : MediaChangeAction.Mapped :
                    authorizedCleanup ? target is null ? MediaChangeAction.Omitted : MediaChangeAction.Mapped :
                    source.Kind == "Geometry" ? MediaChangeAction.Mapped : MediaChangeAction.Reencoded,
                ReasonCode = same ? descriptionSame ? MediaChangeReason.None : MediaChangeReason.RepresentationChanged :
                    authorizedCleanup ? source.Kind == "Track" && source.SampleCount == 0 ? MediaChangeReason.EmptyTrackWithoutSamples : MediaChangeReason.AuthorizedSourceCleanup : MediaChangeReason.RepresentationChanged,
                Reason = same ? descriptionSame ? "Actual ordered sample/metadata bytes match." : "Media payload matches; sample representation was mapped." :
                    authorizedCleanup ? "Changed by the identity-bound source protocol cleanup plan; see removedProtocolFacts." : "Selected media was transcoded; source and prepared payload hashes are recorded.",
                SourceRepresentation = source.Kind + "/" + source.Handler + "/" + source.Codec,
                TargetRepresentation = target is null ? null : target.Kind + "/" + target.Handler + "/" + target.Codec,
                SourceSha256 = source.Sha256, TargetSha256 = target?.Sha256,
                SourceArtifactSha256 = before.ArtifactSha256, TargetArtifactSha256 = after.ArtifactSha256
            });
        }
        return changes;
    }

    internal static MediaChangeReport Finish(IEnumerable<MediaAssetChange> observed, IEnumerable<NotCarriedItem> legacy,
        IReadOnlyList<ProductCommittedArtifact> outputs, IReadOnlyList<RemovedProtocolFact> removedFacts)
    {
        var items = observed.ToList();
        foreach (var loss in legacy)
            items.Add(new MediaAssetChange { AssetId = loss.Item, Stage = "TargetPreparation", Action = loss.Outcome switch
            {
                PreservationOutcome.Reencoded or PreservationOutcome.TranscodedLossless => MediaChangeAction.Reencoded,
                PreservationOutcome.DegradedToSdr => MediaChangeAction.Degraded,
                PreservationOutcome.DiscardedNotApplicable or PreservationOutcome.Unsupported => MediaChangeAction.Omitted,
                _ => MediaChangeAction.Unverified
            }, ReasonCode = MediaChangeReason.EvidenceUnavailable, Reason = loss.Reason });
        return new MediaChangeReport { Items = items, Outputs = outputs, RemovedProtocolFacts = removedFacts };
    }
}
