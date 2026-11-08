using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Collections.Generic;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;

namespace LivePhotoBox.Services;

// Explicit internal target selection; never inferred from Neutral provenance,
// Make, or an ordinary stabilization UUID. Product choices still await R8.
internal enum HuaweiTargetVariant : uint { HuaweiV6 = 1, HonorV2 = 2 }
internal enum SamsungTargetVariant : uint { Mpv2 = 2, Mpv3 = 3 }
// A newly created target may establish its initial cover at the known current
// cover. This describes the new file, never an inferred source capture history.
internal enum OppoInitialCoverPolicy { RequireKnownOriginal = 0, InitializeNewTargetAtCurrentCover = 1 }

/// <summary>
/// Experimental structured Native target path. Product adapters migrate only after
/// their complete target lifecycle; this service has no fallback or selectable validator.
/// </summary>
internal sealed record StructuredTargetWriteReceipt(
    string FinalPath,
    WindowsFileIdentity FileIdentity,
    long ByteLength,
    string Sha256,
    IReadOnlyList<NotCarriedItem> NotCarried)
{
    internal string? VideoPath { get; init; }
}

internal static class StructuredTargetWriterService
{
    internal sealed record WriteAttempt(StructuredTargetWriteReceipt? Receipt, Exception? Failure)
    {
        public static implicit operator WriteAttempt(StructuredTargetWriteReceipt receipt) => new(receipt, null);
    }

    internal static async Task<StructuredTargetWriteReceipt> WriteAsync(NeutralMediaBundle bundle,
        OutputProfile profile, string finalPath, string stagingDirectory,
        bool overwriteExisting, CancellationToken token,
        HuaweiTargetVariant huaweiVariant = HuaweiTargetVariant.HuaweiV6,
        SamsungTargetVariant samsungVariant = SamsungTargetVariant.Mpv3,
        OppoInitialCoverPolicy oppoInitialCoverPolicy = OppoInitialCoverPolicy.RequireKnownOriginal,
        PreservationPolicy preservationPolicy = PreservationPolicy.BestEffort)
    {
        var attempt = await TryWriteAsync(bundle, profile, finalPath, stagingDirectory,
            overwriteExisting, token, huaweiVariant, samsungVariant, oppoInitialCoverPolicy, preservationPolicy).ConfigureAwait(false);
        if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return attempt.Receipt!;
    }

    internal static async Task<WriteAttempt> TryWriteAsync(NeutralMediaBundle bundle,
        OutputProfile profile, string finalPath, string stagingDirectory,
        bool overwriteExisting, CancellationToken token,
        HuaweiTargetVariant huaweiVariant = HuaweiTargetVariant.HuaweiV6,
        SamsungTargetVariant samsungVariant = SamsungTargetVariant.Mpv3,
        OppoInitialCoverPolicy oppoInitialCoverPolicy = OppoInitialCoverPolicy.RequireKnownOriginal,
        PreservationPolicy preservationPolicy = PreservationPolicy.BestEffort,
        Func<IReadOnlyList<NotCarriedItem>,string,string>? publicationPathResolver = null)
    {
        ArgumentNullException.ThrowIfNull(bundle);
        ArgumentNullException.ThrowIfNull(profile);
        if (token.IsCancellationRequested) return new(null,new OperationCanceledException(token));
        if (!overwriteExisting && File.Exists(finalPath))
            return new(null,new IOException("The requested target already exists and overwrite was not authorized."));
        uint target = profile.TargetProtocol switch
        {
            TargetProtocol.GoogleMicroVideoV1 => 1,
            TargetProtocol.GoogleMotionPhotoV2 => 2,
            TargetProtocol.OppoLivePhoto => 3,
            TargetProtocol.VivoLivePhoto => 4,
            TargetProtocol.SamsungMotionPhoto => 5,
            TargetProtocol.HuaweiMovingPhoto => 6,
            TargetProtocol.AppleLivePhoto => 7,
            TargetProtocol.VivoLegacyDualFile => 8,
            _ => 0
        };
        if(target==0) return new(null,new NotSupportedException("This structured path does not implement the requested target family."));
        if(bundle.MotionVideo is not { } video) return new(null,new InvalidDataException("A prepared motion video is required."));
        if (!Enum.IsDefined(huaweiVariant) || (target != 6 && huaweiVariant != HuaweiTargetVariant.HuaweiV6))
            return new(null,new ArgumentException("The explicit Huawei/Honor variant does not apply to this target.", nameof(huaweiVariant)));
        if (!Enum.IsDefined(samsungVariant) || (target != 5 && samsungVariant != SamsungTargetVariant.Mpv3))
            return new(null,new ArgumentException("Samsung variant does not apply to the selected target.", nameof(samsungVariant)));
        if (!Enum.IsDefined(oppoInitialCoverPolicy) || (target != 3 && oppoInitialCoverPolicy != OppoInitialCoverPolicy.RequireKnownOriginal))
            return new(null,new ArgumentException("OPPO initial cover policy does not apply to the selected target.", nameof(oppoInitialCoverPolicy)));
        uint targetVersion = target == 6 ? (uint)huaweiVariant : target == 5 ? (uint)samsungVariant : 1u;
        if (target == 6 && profile.Format == OutputMediaFormat.HeicMp4Hevc && video.VideoCodec != VideoCodec.Hevc)
            return new(null,new NotSupportedException("The explicit Huawei HEVC profile requires prepared HEVC video."));
        if (bundle.PrimaryImage.Kind != MediaArtifactKind.PrimaryImage || video.Kind != MediaArtifactKind.MotionVideo ||
            bundle.PrimaryImage.ImageContainer != profile.MediaRequirement.ImageContainer ||
            video.VideoContainer != profile.MediaRequirement.VideoContainer ||
            video.VideoCodec is not (VideoCodec.H264 or VideoCodec.Hevc))
            return new(null,new InvalidDataException("Prepared artifact roles and formats do not match the structured target profile."));
        if(ManifestFailure(bundle,bundle.PrimaryImage) is { } imageManifestFailure) return new(null,imageManifestFailure);
        if(ManifestFailure(bundle,video) is { } videoManifestFailure) return new(null,videoManifestFailure);
        if(SourceAliasFailure(finalPath,bundle.PrimaryImage) is { } imageAliasFailure) return new(null,imageAliasFailure);
        if(SourceAliasFailure(finalPath,video) is { } videoAliasFailure) return new(null,videoAliasFailure);
        long? cover = bundle.Semantics.Timing.CoverTimestampUs;
        long? primaryTime = target == 3 ? bundle.Semantics.Timing.PrimaryTimestampUs : null;
        if (target == 3 && !primaryTime.HasValue && oppoInitialCoverPolicy == OppoInitialCoverPolicy.InitializeNewTargetAtCurrentCover)
            primaryTime = cover;
        if (target == 3 && !primaryTime.HasValue)
            return new(null,new NotSupportedException("OPPO original capture time is unknown; current cover cannot invent that history."));
        var notCarried = new List<NotCarriedItem>();
        var ordinaryAttempt = (target is 1 or 2 or 3)
            ? await BindOrdinaryMetadataAsync(bundle, finalPath, preservationPolicy, notCarried, token).ConfigureAwait(false)
            : new OrdinaryBindingAttempt(Array.Empty<OrdinaryBinding>(), null);
        if (ordinaryAttempt.Failure is { } ordinaryFailure) return new(null, ordinaryFailure);
        OrdinaryBinding[] ordinary = ordinaryAttempt.Bindings!;
        if (target is not (1 or 2 or 3) && (bundle.Semantics.OrdinaryMetadata.Count > 0 || bundle.Manifest.Any(x => x.Role == "OrdinaryMetadata")))
        {
            notCarried.Add(new NotCarriedItem
            {
                Item = "OrdinaryMetadata",
                Reason = $"{profile.TargetProtocol} cannot represent preserved ordinary metadata records",
                Outcome = PreservationOutcome.DiscardedNotApplicable
            });
        }
        var auxiliaryAttempt = await BindAuxiliariesAsync(bundle, finalPath, preservationPolicy, target, notCarried, token).ConfigureAwait(false);
        if (auxiliaryAttempt.Failure is { } auxiliaryFailure) return new(null, auxiliaryFailure);
        AuxiliaryBinding[] auxiliaries = auxiliaryAttempt.Bindings!;
        NativeCompoundPrimaryObservation? compound = null;
        if (bundle.PrimaryImage.CompoundPrimary is { } compoundOwner)
        {
            var observation = compoundOwner.TryInspect(bundle.PrimaryImage.Path, token);
            if (observation.Failure is { } compoundFailure) return new(null, compoundFailure);
            compound = observation.Observation;
        }
        if (compound.HasValue && preservationPolicy == PreservationPolicy.Strict &&
            (target != 6 || profile.Format != OutputMediaFormat.JpegMp4))
            return new(null,new NotSupportedException("Opaque compound primary lineage permits this Strict target only after an explicit transport mapping."));
        var expected = new TargetValidationExpectations(target, (uint)profile.Format, (uint)video.VideoCodec, cover)
        {
            UseV2 = true, TargetVersion = targetVersion, Compound = compound, PrimaryTimestampUs = primaryTime,
            Auxiliaries = auxiliaries.Select(row => new TargetAuxiliaryExpectation(
                AuxiliarySemanticCode(row.Row.Semantic), (uint)row.Row.ImageContainer,
                checked((ulong)row.Row.ByteLength), row.Row.Sha256)).ToArray(),
            OrdinaryMetadata = ordinary.Select(x => new TargetOrdinaryMetadataExpectation(checked((ulong)x.Row.ByteLength), x.Row.Sha256, (uint)bundle.PrimaryImage.ImageContainer)).ToArray(),
            ImageRotationDegrees = KnownRotation(bundle.Semantics.Orientation.PrimaryImage),
            ImageReflection = KnownReflection(bundle.Semantics.Orientation.PrimaryImage),
            VideoRotationDegrees = KnownRotation(bundle.Semantics.Orientation.MotionVideo),
            VideoReflection = KnownReflection(bundle.Semantics.Orientation.MotionVideo)
        };
        using var session = new Session(bundle.PrimaryImage, video, profile, target, targetVersion, cover, primaryTime,
            auxiliaries, ordinary, stagingDirectory, token, preservationPolicy);
        if (session.Failure is { } sessionFailure) return new(null, sessionFailure);
        if(target is 7 or 8)
        {
            foreach (var binding in auxiliaries.Where(x => x.SelectorKind == 3 &&
                x.Row.Semantic is "MiCameraSubimage" or "MiCameraLensWatermark"))
                notCarried.Add(new NotCarriedItem { Item = binding.Row.Semantic,
                    Reason = "Optional MiCamera camera record omitted from the new dual-file target after its exact source ownership was validated.",
                    Outcome = PreservationOutcome.DiscardedNotApplicable });
        }
        // Optional assets omitted by this target are known before any final
        // publication. Product orchestration can redirect the sealed output
        // to isolation without changing any validator or source authority.
        if(publicationPathResolver is not null)
        {
            string requestedPath=finalPath;
            finalPath=publicationPathResolver(notCarried,finalPath);
            if(!string.Equals(Path.GetFullPath(requestedPath),Path.GetFullPath(finalPath),StringComparison.OrdinalIgnoreCase))overwriteExisting=false;
            if(OutputDirectoryAttempt.Ensure(Path.GetDirectoryName(Path.GetFullPath(finalPath))!) is { } directoryFailure)
                return new(null,directoryFailure);
            if(SourceAliasFailure(finalPath,bundle.PrimaryImage) is { } primaryAlias)return new(null,primaryAlias);
            if(SourceAliasFailure(finalPath,video) is { } motionAlias)return new(null,motionAlias);
        }
        if (target is 7 or 8)
        {
            string videoPath = Path.ChangeExtension(finalPath, target == 7 ? ".MOV" : ".MP4");
            if (!overwriteExisting && File.Exists(videoPath))
                return new(null,new IOException("The requested companion target already exists and overwrite was not authorized."));
            if(SourceAliasFailure(videoPath,bundle.PrimaryImage) is { } imageAliasFailure2) return new(null,imageAliasFailure2);
            if(SourceAliasFailure(videoPath,video) is { } videoAliasFailure2) return new(null,videoAliasFailure2);
            if(session.PairProof is not { } pairProof) return new(null,new InvalidDataException("Native did not validate the target pair."));
            var pairPublication=await WindowsSplitOutputPublisher.TryPublishPairTargetAsync(finalPath, videoPath, overwriteExisting,
                (stream, ct) => session.CopyMemberToAttemptAsync(0, stream, ct),
                (stream, ct) => session.CopyMemberToAttemptAsync(1, stream, ct),
                pairProof,
                session.CompleteTransportAttempt, token).ConfigureAwait(false);
            if(pairPublication.Failure is { } pairFailure)return new(null,pairFailure);
            return new StructuredTargetWriteReceipt(finalPath, WindowsFileIdentity.Capture(finalPath),
                new FileInfo(finalPath).Length, session.MemberHash(0), notCarried) { VideoPath = videoPath };
        }
        var publication = await WindowsSplitOutputPublisher.TryPublishTargetAsync(finalPath, overwriteExisting,
            session.CopyToAttemptAsync, expected, token).ConfigureAwait(false);
        if(publication.Failure is { } publicationFailure) return new(null,publicationFailure);
        var published=publication.Receipt!;
        return new StructuredTargetWriteReceipt(published.FinalPath, published.FileIdentity, published.ByteLength, published.Sha256, notCarried);
    }

    private sealed record AuxiliaryBinding(NeutralArtifactManifest Row, uint Representation,
        uint SelectorKind, uint SelectorId, MediaArtifact? Materialized);
    private sealed record AuxiliaryBindingAttempt(AuxiliaryBinding[]? Bindings, Exception? Failure);
    private sealed record OrdinaryBinding(NeutralArtifactManifest Row,MediaArtifact Artifact);
    private sealed record OrdinaryBindingAttempt(OrdinaryBinding[]? Bindings, Exception? Failure);

    private static async Task<OrdinaryBindingAttempt> BindOrdinaryMetadataAsync(NeutralMediaBundle bundle, string finalPath,
        PreservationPolicy policy, List<NotCarriedItem> notCarried, CancellationToken token)
    {
        var semantics = bundle.Semantics.OrdinaryMetadata;
        if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
        if (semantics.Count == 0) return new(Array.Empty<OrdinaryBinding>(), null);
        var rows = bundle.Manifest.Where(x => x.Role == "OrdinaryMetadata").ToArray();
        var declared = bundle.PreservationCarriers.Where(x => x.Semantic == OrdinaryMetadataPreservation.Semantic || x.MaterializedArtifact?.Kind == MediaArtifactKind.MetadataCarrier).ToArray();

        // Ordinary metadata ownership checks apply to every preservation policy.

        {
            if (semantics.Count > 1 || rows.Length != semantics.Count || declared.Length != semantics.Count ||
                semantics.Select(x => x.StableIdentity).Distinct(StringComparer.Ordinal).Count() != semantics.Count)
                return new(null, new NotSupportedException("Every ordinary metadata artifact requires one unique represented semantic owner."));
            var bindings = new List<OrdinaryBinding>();
            foreach (var semantic in semantics)
            {
                if (semantic.Semantic != OrdinaryMetadataPreservation.Semantic || semantic.Relationship != OrdinaryMetadataPreservation.Relationship ||
                    semantic.OwnerIdentity != "primary:0" || semantic.Representation != NeutralAuxiliaryRepresentation.Materialized ||
                    semantic.EvidenceState != NeutralEvidenceState.VerifiedPreserved || semantic.PreservationOutcome != PreservationOutcome.Preserved)
                    return new(null, new NotSupportedException("Ordinary metadata semantics are not verified complete materialized records."));
                var matching = rows.Where(x => x.StableIdentity == semantic.StableIdentity && x.Semantic == semantic.Semantic &&
                    x.OwnerIdentity == semantic.OwnerIdentity && x.Relationship == semantic.Relationship && x.ByteLength == semantic.ByteLength && x.Sha256 == semantic.Sha256 &&
                    x.SemanticRepresentation == semantic.Representation && x.Ownership == AuxiliaryOwnership.Primary && x.SourceOffset == 0 && x.OwnerByteOffset is null &&
                    x.ImageContainer == ImageContainer.Unknown && x.VideoContainer == VideoContainer.Unknown && x.AuxiliaryCodec == AuxiliaryCodec.Copy &&
                    x.PreservationOutcome == PreservationOutcome.Preserved).ToArray();
                if (matching.Length != 1) return new(null, new InvalidDataException("Ordinary metadata does not match its final Neutral manifest."));
                var row = matching[0];
                var artifacts = declared.Where(x => x.StableIdentity == semantic.StableIdentity && x.Semantic == semantic.Semantic &&
                    x.OwnerIdentity == semantic.OwnerIdentity && x.Relationship == semantic.Relationship && x.MaterializedArtifact is not null)
                    .Select(x => x.MaterializedArtifact!).Where(x => Path.GetFullPath(x.Path) == Path.GetFullPath(row.Path)).ToArray();
                if (artifacts.Length != 1) return new(null, new InvalidDataException("Ordinary metadata has no unique actual final artifact."));
                var artifactAttempt = await OrdinaryMetadataPreservation.TryVerifyArtifactAsync(artifacts[0], row.ByteLength, row.Sha256, token).ConfigureAwait(false);
                if (artifactAttempt.Failure is { } artifactFailure) return new(null, artifactFailure);
                var artifact = artifactAttempt.Artifact!;
                if (SourceAliasFailure(finalPath, artifact) is { } aliasFailure) return new(null, aliasFailure);
                bindings.Add(new(row, artifact));
            }
            return new(bindings.ToArray(), null);
        }
    }

    private static uint AuxiliarySemanticCode(string semantic) => semantic switch
    {
        "GainMap" => 1,
        "Original" => 2,
        "MiCameraSubimage" => 3,
        "MiCameraLensWatermark" => 4,
        "MpfAuxiliaryImage" => 5,
        _ => throw new NotSupportedException("The target cannot represent this auxiliary semantic asset.")
    };

    private static async Task<(SourceMediaFacts? Facts, Exception? Failure)> ObserveAuxiliaryPrimaryAsync(
        MediaArtifact primary, CancellationToken token)
    {
        if (primary.CompoundPrimary is { } compound)
        {
            var observed = compound.TryInspect(primary.Path, token);
            return (observed.Facts, observed.Failure);
        }
        var attempt = await NativeMediaService.TryInspectMediaWithPlanAsync(primary.Path,
            cancellationToken: token).ConfigureAwait(false);
        if (attempt.Failure is { } failure) return (null, failure);
        using var source = attempt.Source!;
        return (source.Facts, null);
    }

    private static async Task<AuxiliaryBindingAttempt> BindAuxiliariesAsync(NeutralMediaBundle bundle,
        string finalPath, PreservationPolicy policy, uint target, List<NotCarriedItem> notCarried, CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
        var semantics = bundle.Semantics.AuxiliaryMedia.ToList();
        if (bundle.Semantics.GainMap is { } gainMap) semantics.Add(gainMap);
        var rows = bundle.Manifest.Where(row => row.Role is not ("PrimaryImage" or "MotionVideo" or "OrdinaryMetadata")).ToArray();

        if (policy == PreservationPolicy.Strict)
        {
            if (semantics.Count > 2 || semantics.GroupBy(row => row.Semantic).Any(group => group.Count() != 1))
                return new(null, new NotSupportedException("The R3 target requires at most two unique supported auxiliary semantic assets."));
            if (bundle.AuxiliaryMedia.GroupBy(row => row.Semantic).Any(group => group.Count() != 1) ||
                bundle.AuxiliaryMedia.Any(descriptor => !semantics.Any(semantic =>
                    descriptor.Semantic == semantic.Semantic && descriptor.OwnerIdentity == semantic.OwnerIdentity &&
                    descriptor.Relationship == semantic.Relationship)))
                return new(null, new InvalidDataException("Auxiliary descriptors do not map uniquely to the final semantic inventory."));
            if (rows.Length != semantics.Count ||
                (bundle.GainMap is not null && !semantics.Any(row => row.Semantic == "GainMap")) ||
                (bundle.GainMapRepresentation != GainMapRepresentation.None && !semantics.Any(row => row.Semantic == "GainMap")))
                return new(null, new NotSupportedException("Unrepresented auxiliary/carrier bytes cannot be silently discarded."));
            if (semantics.Count == 0)
            {
                if (bundle.AuxiliaryMedia.Count != 0)
                    return new(null, new NotSupportedException("Unbound auxiliary/carrier descriptors cannot authorize target writing."));
            }
        // Only prepared Neutral bytes are re-observed to bind structural selectors.
        // Source provenance never selects a target layout.
        SourceMediaFacts? facts = null;
        if (bundle.PrimaryImage.ImageContainer == ImageContainer.Heic &&
            semantics.Any(row => row.Representation is NeutralAuxiliaryRepresentation.Embedded or NeutralAuxiliaryRepresentation.Both))
        {
            var observation = await ObserveAuxiliaryPrimaryAsync(bundle.PrimaryImage, token).ConfigureAwait(false);
            if (observation.Failure is { } observationFailure) return new(null, observationFailure);
            facts = observation.Facts!;
            if (facts.Protocol != SourceProtocol.NonLive)
                return new(null, new InvalidDataException("Prepared primary still contains source live binding."));
        }
        var embeddedCarriers=bundle.PreservationCarriers.Where(x=>!OrdinaryMetadataPreservation.IsRecordCarrier(x)).ToArray();
        if (embeddedCarriers.Length != 0)
        {
            if (facts is null)
            {
                var observation = await ObserveAuxiliaryPrimaryAsync(bundle.PrimaryImage, token).ConfigureAwait(false);
                if (observation.Failure is { } observationFailure) return new(null, observationFailure);
                facts = observation.Facts!;
            }
            if (facts.Protocol != SourceProtocol.NonLive)
                return new(null, new InvalidDataException("Prepared carrier owner retains a source live binding."));
            foreach (var carrier in embeddedCarriers)
            {
                // Descriptive evidence never authorizes an append. Only an
                // actually observed embedded ordinary camera region is copied
                // as part of the independently leased whole primary artifact.
                bool ordinary = carrier.Kind == PreservationCarrierKind.ContainerMetadata && carrier.Semantic == "CameraMetadata" && carrier.Relationship == "opaque-camera-metadata";
                bool opaque = bundle.PrimaryImage.CompoundPrimary is not null && carrier.Kind == PreservationCarrierKind.OpaqueFragment &&
                    carrier.Semantic == "OpaqueSourcePrefix" && carrier.Relationship == "preserved-source-prefix";
                bool sef = carrier.Kind == PreservationCarrierKind.SamsungSef &&
                    carrier.StableIdentity.StartsWith("samsung:sef:", StringComparison.Ordinal) &&
                    carrier.Relationship.StartsWith("SEFH/SEFT", StringComparison.Ordinal);
                bool index = sef && carrier.StableIdentity == "samsung:sef:index-trailer" && carrier.Semantic == "SamsungSEFIndexTrailer";
                if (carrier.ArtifactRole != MediaArtifactKind.PrimaryImage || (!ordinary && !opaque && !sef) || carrier.OwnerIdentity != "primary:0" ||
                    facts.PreservationCarriers.Count(actual => actual.StableIdentity == carrier.StableIdentity &&
                        actual.Kind == carrier.Kind && (sef || actual.SourceOffset == carrier.SourceOffset) &&
                        (index || (actual.SourceLength == carrier.SourceLength && actual.SourceSha256 == carrier.SourceSha256)) && actual.Semantic == carrier.Semantic &&
                        actual.OwnerIdentity == carrier.OwnerIdentity && actual.Relationship == carrier.Relationship) != 1)
                    return new(null, new NotSupportedException("Carrier is not the observed embedded ordinary primary camera metadata."));
            }
        }
        var result = new List<AuxiliaryBinding>();

        foreach (var semantic in semantics)
        {
            if (semantic.Semantic is not ("GainMap" or "Original" or "MiCameraSubimage" or "MiCameraLensWatermark" or "MpfAuxiliaryImage"))
                return new(null, new NotSupportedException("The target cannot represent this auxiliary semantic asset."));
            if (
                semantic.EvidenceState is NeutralEvidenceState.Unknown or NeutralEvidenceState.NotApplicable ||
                semantic.OwnerIdentity != "primary:0" || semantic.Ownership != AuxiliaryOwnership.Primary)
                return new(null, new NotSupportedException("Auxiliary semantic identity, owner and evidence must be established."));
            var matches = rows.Where(row => row.StableIdentity == semantic.StableIdentity &&
                row.Semantic == semantic.Semantic && row.OwnerIdentity == semantic.OwnerIdentity &&
                row.Relationship == semantic.Relationship && row.SemanticRepresentation == semantic.Representation &&
                row.ImageContainer == semantic.ImageContainer && row.Ownership == semantic.Ownership).ToArray();
            if (matches.Length != 1 || matches[0].ByteLength <= 0 || matches[0].Sha256.Length != 64)
                return new(null, new InvalidDataException("Auxiliary semantic asset does not match its unique final Neutral manifest row."));
            var row = matches[0];
            uint representation = semantic.Representation switch
            {
                NeutralAuxiliaryRepresentation.Embedded => 1,
                NeutralAuxiliaryRepresentation.Detached or NeutralAuxiliaryRepresentation.Materialized => 2,
                NeutralAuxiliaryRepresentation.Both => 3,
                _ => 0
            };
            if (representation == 0) return new(null, new NotSupportedException("Auxiliary representation is unknown."));
            uint selectorKind = 0, selectorId = 0;
            if (representation == 1 && (bundle.AuxiliaryMedia.Any(item => item.Semantic == row.Semantic && item.MaterializedArtifact is not null) ||
                (row.Semantic == "GainMap" && bundle.GainMap is not null)))
                return new(null, new InvalidDataException("An embedded asset with a materialized mirror must declare Both representation."));
            if (representation != 2)
            {
                if (row.OwnerByteOffset is not long embeddedOffset || embeddedOffset < 0 ||
                    embeddedOffset > bundle.PrimaryImage.ByteLength || row.ByteLength > bundle.PrimaryImage.ByteLength - embeddedOffset)
                    return new(null, new InvalidDataException("Embedded auxiliary is outside its whole-file primary owner."));
                if (bundle.PrimaryImage.ImageContainer == ImageContainer.Heic)
                {
                    var observed = facts!.AuxiliaryItems.Where(item => item.IsPresent && item.GraphComplete &&
                        item.StableIdentity == row.StableIdentity && item.Semantic == row.Semantic &&
                        item.OwnerIdentity == row.OwnerIdentity && item.Relationship == row.Relationship &&
                        item.Container == row.ImageContainer && item.ByteOffset == row.OwnerByteOffset &&
                        item.ByteLength == row.ByteLength && string.Equals(item.Sha256, row.Sha256, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (observed.Length != 1 || observed[0].ItemId == 0)
                        return new(null, new InvalidDataException("Embedded auxiliary selector does not match the observed prepared primary graph."));
                    selectorKind = 2;
                    selectorId = observed[0].ItemId;
                }
                // JPEG kind/id zero requests Native resolution against its actual
                // semantic/relationship graph. Native freezes the unique observed
                // selector before fingerprinting; range/hash alone grant no authority.
            }
            MediaArtifact? materialized = null;
            if (representation != 1)
            {
                var candidates = bundle.AuxiliaryMedia.Where(item => item.Semantic == row.Semantic &&
                    item.OwnerIdentity == row.OwnerIdentity && item.Relationship == row.Relationship &&
                    item.MaterializedArtifact is not null).Select(item => item.MaterializedArtifact!).ToList();
                if (row.Semantic == "GainMap" && bundle.GainMap is { } image) candidates.Add(image);
                var matching = candidates.Where(item =>
                    string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(row.Path), StringComparison.OrdinalIgnoreCase) &&
                    item.ByteLength == row.ByteLength && item.ImageContainer == row.ImageContainer &&
                    string.Equals(item.Sha256, row.Sha256, StringComparison.OrdinalIgnoreCase)).ToArray();
                if (matching.Length == 0 || matching.Length != candidates.Count || matching.Any(item => item.FileIdentity is null))
                    return new(null, new InvalidDataException("Detached auxiliary is not a uniquely identified complete Neutral artifact."));
                MediaArtifact first = matching[0];
                if (matching.Any(item => item.FileIdentity != first.FileIdentity ||
                    item.Kind != first.Kind || item.ImageContainer != first.ImageContainer ||
                    item.ImageCodec != first.ImageCodec || item.VideoContainer != first.VideoContainer ||
                    item.VideoCodec != first.VideoCodec))
                    return new(null, new InvalidDataException("Detached auxiliary references disagree on their materialized file identity or media representation."));
                materialized = first;
                // SourceExtractor retains the original source-range offset on an
                // independently materialized artifact. That diagnostic offset is
                // not a slice into this file. Native pins the whole file and checks
                // its complete identity, actual size and SHA against the final row.
                if (SourceAliasFailure(finalPath, materialized) is { } aliasFailure) return new(null, aliasFailure);
            }
            result.Add(new AuxiliaryBinding(row, representation, selectorKind, selectorId, materialized));
        }
        return new(result.ToArray(), null);
    }
    else
    {
        // Give the existing B-level GainMap capability priority over optional
        // history/private assets. Strict keeps its original complete inventory.
        semantics = semantics.OrderBy(item => item.Semantic == "GainMap" ? 0 : 1).ToList();
        var result = new List<AuxiliaryBinding>();
        SourceMediaFacts? facts = null;
        try
        {
            if (bundle.PrimaryImage.ImageContainer == ImageContainer.Heic &&
                semantics.Any(row => row.Representation is NeutralAuxiliaryRepresentation.Embedded or NeutralAuxiliaryRepresentation.Both))
            {
                var observation = await ObserveAuxiliaryPrimaryAsync(bundle.PrimaryImage, token).ConfigureAwait(false);
                if (observation.Failure is { } observationFailure) return new(null, observationFailure);
                facts = observation.Facts!;
            }
            var embeddedCarriers = bundle.PreservationCarriers.Where(x => !OrdinaryMetadataPreservation.IsRecordCarrier(x)).ToArray();
            if (embeddedCarriers.Length != 0)
            {
                if (facts is null)
                {
                    var observation = await ObserveAuxiliaryPrimaryAsync(bundle.PrimaryImage, token).ConfigureAwait(false);
                    if (observation.Failure is { } observationFailure) return new(null, observationFailure);
                    facts = observation.Facts!;
                }
            }
        }
        catch (OperationCanceledException ex) { return new(null, ex); }
        catch (Exception ex) { return new(null, new InvalidDataException("Prepared auxiliary observation failed; no loss decision is authorized.", ex)); }

        foreach (var semantic in semantics)
        {
            if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
            try
            {
                if (semantic.Semantic is not ("GainMap" or "Original" or "MiCameraSubimage" or "MiCameraLensWatermark" or "MpfAuxiliaryImage"))
                return new(null, new NotSupportedException("The target cannot represent this auxiliary semantic asset."));
                if (semantic.EvidenceState is NeutralEvidenceState.Unknown or NeutralEvidenceState.NotApplicable ||
                    semantic.OwnerIdentity != "primary:0" || semantic.Ownership != AuxiliaryOwnership.Primary)
                {
                    return new(null, new InvalidDataException("Auxiliary evidence/ownership is invalid; omission is not authorized."));
                }

                var matches = rows.Where(row => row.StableIdentity == semantic.StableIdentity &&
                    row.Semantic == semantic.Semantic && row.OwnerIdentity == semantic.OwnerIdentity &&
                    row.Relationship == semantic.Relationship && row.SemanticRepresentation == semantic.Representation &&
                    row.ImageContainer == semantic.ImageContainer && row.Ownership == semantic.Ownership).ToArray();

                if (matches.Length != 1 || matches[0].ByteLength <= 0 || matches[0].Sha256.Length != 64)
                {
                    return new(null, new InvalidDataException("Auxiliary semantic asset does not match its unique final Neutral manifest row."));
                }

                var row = matches[0];
                uint representation = semantic.Representation switch
                {
                    NeutralAuxiliaryRepresentation.Embedded => 1,
                    NeutralAuxiliaryRepresentation.Detached or NeutralAuxiliaryRepresentation.Materialized => 2,
                    NeutralAuxiliaryRepresentation.Both => 3,
                    _ => 0
                };
                if (representation == 0)
                {
                    return new(null, new InvalidDataException("Auxiliary representation is unknown."));
                }

                uint selectorKind = 0, selectorId = 0;
                if (representation != 2)
                {
                    if (row.OwnerByteOffset is not long embeddedOffset || embeddedOffset < 0 ||
                        embeddedOffset > bundle.PrimaryImage.ByteLength || row.ByteLength > bundle.PrimaryImage.ByteLength - embeddedOffset)
                    {
                        return new(null, new InvalidDataException("Embedded auxiliary is outside its whole-file primary owner."));
                    }
                    if (bundle.PrimaryImage.ImageContainer == ImageContainer.Heic)
                    {
                        if (facts is null)
                        {
                            return new(null, new InvalidDataException("Carrier facts unavailable."));
                        }
                        var observed = facts.AuxiliaryItems.Where(item => item.IsPresent && item.GraphComplete &&
                            item.StableIdentity == row.StableIdentity && item.Semantic == row.Semantic &&
                            item.OwnerIdentity == row.OwnerIdentity && item.Relationship == row.Relationship &&
                            item.Container == row.ImageContainer && item.ByteOffset == row.OwnerByteOffset &&
                            item.ByteLength == row.ByteLength && string.Equals(item.Sha256, row.Sha256, StringComparison.OrdinalIgnoreCase)).ToArray();
                        if (observed.Length != 1 || observed[0].ItemId == 0)
                        {
                            return new(null, new InvalidDataException("Embedded auxiliary selector does not match the observed prepared primary graph."));
                        }
                        selectorKind = 2;
                        selectorId = observed[0].ItemId;
                    }
                }

                MediaArtifact? materialized = null;
                if (representation != 1)
                {
                    // Detached final manifests use a content identity; the extractor's
                    // descriptor retains its source graph identity. Accept only
                    // that explicit canonical mapping, with the full path/hash/
                    // length/file-object comparison below still mandatory.
                    var candidates = bundle.AuxiliaryMedia.Where(item => item.MaterializedArtifact is not null &&
                        (item.StableIdentity == row.StableIdentity || (item.MaterializedArtifact.Sha256 is { } digest && row.StableIdentity ==
                            NeutralMediaService.AuxiliaryStableIdentity(item.Semantic, digest))) && item.Semantic == row.Semantic &&
                        item.OwnerIdentity == row.OwnerIdentity && item.Relationship == row.Relationship &&
                        item.MaterializedArtifact is not null).Select(item => item.MaterializedArtifact!).ToList();
                    if (row.Semantic == "GainMap" && bundle.GainMap is { } image) candidates.Add(image);
                    var matching = candidates.Where(item =>
                        string.Equals(Path.GetFullPath(item.Path), Path.GetFullPath(row.Path), StringComparison.OrdinalIgnoreCase) &&
                        item.ByteLength == row.ByteLength && item.ImageContainer == row.ImageContainer &&
                        string.Equals(item.Sha256, row.Sha256, StringComparison.OrdinalIgnoreCase)).ToArray();
                    if (matching.Length == 0 || matching.Length != candidates.Count || matching.Any(item => item.FileIdentity is null))
                    {
                        return new(null, new InvalidDataException("Detached auxiliary is not a uniquely identified complete Neutral artifact."));
                    }
                    MediaArtifact first = matching[0];
                    if (matching.Any(item => item.FileIdentity != first.FileIdentity || item.Kind != first.Kind ||
                        item.ImageContainer != first.ImageContainer || item.ImageCodec != first.ImageCodec ||
                        item.VideoContainer != first.VideoContainer || item.VideoCodec != first.VideoCodec))
                        return new(null, new InvalidDataException("Auxiliary mirrors disagree on their actual file identity or representation."));
                    // SourceOffset is extraction provenance, not an offset into
                    // this independently materialized file. Bind the complete
                    // file through its manifest length/hash, as Strict does.
                    materialized = first;
                    if (SourceAliasFailure(finalPath, materialized) is { } aliasFailure) return new(null, aliasFailure);
                }

                bool targetCannotCarry = (target is 7 or 8 && representation == 2 && row.Semantic is "Original" or "MiCameraSubimage" or "MiCameraLensWatermark") || (target == 4 && row.Semantic is not ("GainMap" or "MpfAuxiliaryImage")) ||
                    (target == 6 && row.Semantic is "MiCameraSubimage" or "MiCameraLensWatermark") ||
                    (target is 5 or 6 && representation == 2 && row.Semantic == "Original");
                if (targetCannotCarry || result.Count >= 2 || result.Any(item => item.Row.Semantic == row.Semantic))
                {
                    // This is a transport limit, not a reason to extend the
                    // public ABI for C-level assets or to fail ordinary A media.
                    // Unknown semantics and ambiguous ownership still fail closed.
                    bool optionalPrivateAsset = (row.Semantic, row.Relationship) is
                        ("Original", "Original") or
                        ("MiCameraSubimage", "micamera-xmpmeta-subimage") or
                        ("MiCameraLensWatermark", "micamera-xmpmeta-lenswatermark");
                    if (!optionalPrivateAsset)
                        return new(null, new NotSupportedException("Auxiliary transport capacity cannot safely omit this semantic asset."));
                    if (materialized is not null)
                    {
                        using var leases = new NeutralArtifactReadLeaseSet();
                        if (leases.TryPin(materialized) is { } leaseFailure) return new(null, leaseFailure);
                        var hashAttempt = await MediaFileHashAttempt.ComputeAsync(materialized.Path, token).ConfigureAwait(false);
                        if (hashAttempt.Failure is { } hashFailure) return new(null, hashFailure);
                        string actualHash = hashAttempt.Sha256!;
                        if (!string.Equals(actualHash, row.Sha256, StringComparison.OrdinalIgnoreCase))
                            return new(null, new InvalidDataException("Optional auxiliary bytes changed before the loss decision."));
                    }
                    notCarried.Add(new NotCarriedItem
                    {
                        Item = row.Semantic,
                        Reason = targetCannotCarry
                            ? "Selected target cannot transport this optional history/private asset; no separate preservation is claimed"
                            : "Optional history/private asset exceeds existing unique target transport capacity; no separate preservation is claimed",
                        Outcome = PreservationOutcome.DiscardedNotApplicable
                    });
                    continue;
                }
                result.Add(new AuxiliaryBinding(row, representation, selectorKind, selectorId, materialized));
            }
            catch (OperationCanceledException ex) { return new(null, ex); }
            catch (Exception ex) { return new(null, new InvalidDataException("Auxiliary binding failed; no target was authorized: " + ex.Message, ex)); }
        }

        return new(result.ToArray(), null);
    }
}

    private static void RequireManifest(NeutralMediaBundle bundle, MediaArtifact artifact)
    {
        if(ManifestFailure(bundle,artifact) is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static void RejectSourceAlias(string finalPath, MediaArtifact artifact)
    {
        if(SourceAliasFailure(finalPath,artifact) is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
    }
    private static Exception? ManifestFailure(NeutralMediaBundle bundle, MediaArtifact artifact)
    {
        if (artifact.FileIdentity is null || artifact.ByteLength <= 0 || artifact.SourceOffset != 0 ||
            artifact.Sha256 is null || artifact.Sha256.Length != 64)
            return new InvalidDataException("Prepared artifacts require whole-file identity, length and SHA-256 evidence.");
        var matches = bundle.Manifest.Where(row => row.Role == artifact.Kind.ToString() &&
            string.Equals(Path.GetFullPath(row.Path), Path.GetFullPath(artifact.Path), StringComparison.OrdinalIgnoreCase)).ToArray();
        if (matches.Length != 1 || matches[0].ByteLength != artifact.ByteLength ||
            !string.Equals(matches[0].Sha256, artifact.Sha256, StringComparison.OrdinalIgnoreCase))
            return new InvalidDataException("Prepared artifact does not match its unique Neutral manifest member.");
        return null;
    }

    private static Exception? SourceAliasFailure(string finalPath, MediaArtifact artifact)
    {
        if (string.Equals(Path.GetFullPath(finalPath), Path.GetFullPath(artifact.Path), StringComparison.OrdinalIgnoreCase))
            return new IOException("Target publication cannot overwrite a prepared input artifact.");
        if (File.Exists(finalPath))
        {
            if(!WindowsFileIdentity.TryCapture(finalPath,out WindowsFileIdentity? actual,out int error))
                return new System.ComponentModel.Win32Exception(error,"Cannot inspect target identity.");
            if (actual!.VolumeSerialNumber == artifact.FileIdentity!.VolumeSerialNumber &&
                actual.FileIndex == artifact.FileIdentity.FileIndex)
                return new IOException("Target publication resolves to a prepared input object.");
        }
        return null;
    }

    private static int? KnownRotation(NeutralOrientationTransform? transform)
    {
        if (transform?.ClockwiseRotationDegrees is not int angle ||
            transform.RotationEvidence is NeutralEvidenceState.Unknown or NeutralEvidenceState.NotApplicable) return null;
        if (angle is not (0 or 90 or 180 or 270))
            throw new NotSupportedException("Known orientation rotation is outside the canonical quarter-turn model.");
        return angle;
    }

    private static NeutralReflection? KnownReflection(NeutralOrientationTransform? transform) =>
        transform is null || transform.Reflection == NeutralReflection.Unknown ||
        transform.ReflectionEvidence is NeutralEvidenceState.Unknown or NeutralEvidenceState.NotApplicable
            ? null : transform.Reflection;

    private sealed class Session : IDisposable
    {
        private readonly NativeContext _context;
        private readonly NativeContextLease _operation;
        private nint _plan;
        private NativeTargetWriterOutputV1 _output;
        private NativeTargetWriterOutputV1 _videoOutput;
        internal NativeTargetPairProof? PairProof { get; private set; }
        private NativeCompoundPrimary? _compound;
        private bool _compoundRetained;

        internal unsafe Session(MediaArtifact image, MediaArtifact video, OutputProfile profile, uint target, uint targetVersion,
            long? cover, long? primaryTime, AuxiliaryBinding[] bindings, OrdinaryBinding[] ordinary, string stagingDirectory, CancellationToken token,
            PreservationPolicy preservationPolicy = PreservationPolicy.BestEffort)
        {
            _context = NativeContext.Create(token);
            _operation = _context.AcquireOperationLease();
            try
            {
                NativeTargetWriterArtifactV1* artifacts = stackalloc NativeTargetWriterArtifactV1[2];
                artifacts[0] = Bind(image, 0);
                artifacts[1] = Bind(video, 1);
                var request = new NativeTargetWriterRequestV1
                {
                    StructSize = (uint)sizeof(NativeTargetWriterRequestV1), Version = 1,
                    TargetProfile = target,
                    TargetVersion = targetVersion, MediaFormat = (uint)profile.Format,
                    ArtifactCount = 2, Artifacts = (nint)artifacts,
                    CoverTimestampPresent = cover.HasValue ? 1u : 0, CoverTimestampUs = cover ?? 0,
                    Reserved0 = (uint)preservationPolicy
                };
                EncodePath(stagingDirectory, request.StagingDirectoryUtf8, 1024);
                NativeTargetAuxiliaryV2* auxiliaries = stackalloc NativeTargetAuxiliaryV2[Math.Max(1, bindings.Length)];
                for (int i = 0; i < bindings.Length; i++)
                {
                    var binding = bindings[i]; var row = binding.Row;
                    auxiliaries[i] = new NativeTargetAuxiliaryV2
                    {
                        StructSize = (uint)sizeof(NativeTargetAuxiliaryV2), Version = 2,
                        Representation = binding.Representation, SelectorKind = binding.SelectorKind,
                        SelectorId = binding.SelectorId, OwnerIndex = 0,
                        EmbeddedOffset = binding.Representation == 2 ? 0 : checked((ulong)row.OwnerByteOffset!.Value),
                        ByteLength = checked((ulong)row.ByteLength),
                        Materialized = binding.Materialized is null ? default : Bind(binding.Materialized, 0)
                    };
                    byte[] hash = Convert.FromHexString(row.Sha256);
                    for (int j = 0; j < 32; j++) auxiliaries[i].Sha256[j] = hash[j];
                    EncodeText(row.StableIdentity, auxiliaries[i].StableIdentity, 128);
                    EncodeText(row.OwnerIdentity, auxiliaries[i].OwnerIdentity, 128);
                    EncodeText(row.Semantic, auxiliaries[i].Semantic, 32);
                    EncodeText(row.Relationship, auxiliaries[i].Relationship, 96);
                }
                var requestV2 = new NativeTargetWriterRequestV2
                {
                    StructSize = (uint)sizeof(NativeTargetWriterRequestV2), Version = 2, Base = request,
                    PrimaryTimestampPresent = primaryTime.HasValue ? 1u : 0, PrimaryTimestampUs = primaryTime ?? 0,
                    AuxiliaryCount = (uint)bindings.Length, Auxiliaries = bindings.Length == 0 ? 0 : (nint)auxiliaries
                };
                EncodeText("primary:0", requestV2.PrimaryIdentity, 128);
                _compound = ((target == 6 && profile.Format == OutputMediaFormat.JpegMp4) || preservationPolicy == PreservationPolicy.BestEffort) ? image.CompoundPrimary : null;
                if(ordinary.Length!=0)
                {
                    if(ordinary.Length!=1||_compound is not null) { Failure = new NotSupportedException("Ordinary metadata cannot be combined with an unrepresented compound primary."); return; }
                    var binding=ordinary[0];var metadata=new NativeTargetOrdinaryMetadataV1
                    {StructSize=(uint)sizeof(NativeTargetOrdinaryMetadataV1),Version=1,Kind=1,Materialized=Bind(binding.Artifact,5)};
                    EncodeText(binding.Row.StableIdentity,metadata.StableIdentity,128);EncodeText(binding.Row.OwnerIdentity,metadata.OwnerIdentity,128);
                    EncodeText(binding.Row.Semantic,metadata.Semantic,32);EncodeText(binding.Row.Relationship,metadata.Relationship,96);
                    var requestV4=new NativeTargetWriterRequestV4{StructSize=(uint)sizeof(NativeTargetWriterRequestV4),Version=4,
                        Base=new NativeTargetWriterRequestV3{StructSize=(uint)sizeof(NativeTargetWriterRequestV3),Version=3,Base=requestV2},
                        MetadataCount=1,Metadata=(nint)(&metadata)};
                    if (!TryRequire(NativeMethods.TargetWriterPrepareV4(_operation.Handle,&requestV4,out _plan),"prepare ordinary metadata")) return;
                }
                else if (_compound is not null)
                {
                    _compound.DangerousAddRef(ref _compoundRetained);
                    var requestV3 = new NativeTargetWriterRequestV3 { StructSize = (uint)sizeof(NativeTargetWriterRequestV3),
                        Version = 3, Base = requestV2, CompoundPrimaryToken = unchecked((ulong)_compound.DangerousGetHandle()) };
                    if (!TryRequire(NativeMethods.TargetWriterPrepareV3(_operation.Handle, &requestV3, out _plan), "prepare")) return;
                }
                else if (!TryRequire(NativeMethods.TargetWriterPrepareV2(_operation.Handle, &requestV2, out _plan), "prepare")) return;
                if (_plan == 0) { Failure = new IOException("Native Writer did not issue a plan."); return; }
                if (!TryRequire(NativeMethods.TargetWriterStage(_operation.Handle, _plan), "stage")) return;
                var status = new NativeTargetWriterStatusV1
                { StructSize = (uint)sizeof(NativeTargetWriterStatusV1), Version = 1 };
                NativeTargetWriterOutputV1* outputs = stackalloc NativeTargetWriterOutputV1[2];
                for (int i = 0; i < 2; i++) outputs[i] = new NativeTargetWriterOutputV1
                    { StructSize = (uint)sizeof(NativeTargetWriterOutputV1), Version = 1 };
                uint members = target is 7 or 8 ? 2u : 1u;
                if (!TryRequire(NativeMethods.TargetWriterGetOutputs(_operation.Handle, _plan, outputs, members,
                    out uint count, ref status), "query")) return;
                var output = outputs[0];
                if (count != members || status.State != 2 || status.OutputCount != members || status.FailureCategory != 0 ||
                    output.StructSize != sizeof(NativeTargetWriterOutputV1) || output.Version != 1 ||
                    output.ArtifactRole != 0 || output.ArtifactIndex != 0 || output.TargetProfile != request.TargetProfile ||
                    output.TargetVersion != request.TargetVersion || output.Generation != status.Generation || output.StageId == 0 ||
                    output.ByteLength == 0 || output.ByteLength > long.MaxValue || output.Identity.LinkCount != 1 ||
                    output.Identity.FileSize != output.ByteLength)
                    { Failure = new IOException("Native Writer did not provide one complete sealed target artifact."); return; }
                _output = output;
                if (members == 2)
                {
                    var pairObservation = NativeTargetPairProof.TryObserve(_operation.Handle, _plan);
                    if (pairObservation.Failure is { } pairFailure) { Failure = pairFailure; return; }
                    PairProof = pairObservation.Proof!;
                    _videoOutput = outputs[1];
                    if (_videoOutput.ArtifactIndex != 1 || _videoOutput.ArtifactRole != 1 ||
                        _videoOutput.Generation != output.Generation || _videoOutput.TargetProfile != target ||
                        _videoOutput.TargetVersion != targetVersion || _videoOutput.StageId == 0 || _videoOutput.StageId == output.StageId ||
                        _videoOutput.ByteLength == 0 || _videoOutput.ByteLength > long.MaxValue ||
                        _videoOutput.Identity.LinkCount != 1 || _videoOutput.Identity.FileSize != _videoOutput.ByteLength)
                        { Failure = new IOException("Native Writer did not seal both members of the target pair."); return; }
                }
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        internal Task<Exception?> CopyToAttemptAsync(Stream writer, CancellationToken token)
            => Task.Run<Exception?>(() =>
        {
            byte[] buffer = new byte[128 * 1024];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (ulong offset = 0; offset < _output.ByteLength;)
            {
                if(token.IsCancellationRequested)return new OperationCanceledException(token);
                int count = checked((int)Math.Min((ulong)buffer.Length, _output.ByteLength - offset));
                if(Read(offset,buffer.AsSpan(0,count)) is { } readFailure)return readFailure;
                hash.AppendData(buffer.AsSpan(0, count));
                if(TransactionStreamWriteAttempt.Write(writer,buffer.AsSpan(0,count),token,(long)offset) is { } writeFailure)return writeFailure;
                offset += (uint)count;
            }
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), OutputHash()))
                return new IOException("Transported target bytes do not match the Native sealed artifact.");
            // Retire Native staging before the publisher can commit the target.
            // A cleanup failure must not turn an already committed output into
            // a reported failure with no committed-artifact receipt.
            return TryReleasePlan();
        },CancellationToken.None);

        internal Exception? CompleteTransportAttempt() => TryReleasePlan();

        internal unsafe string MemberHash(int member)
        {
            var output = member == 0 ? _output : _videoOutput;
            return Convert.ToHexString(new ReadOnlySpan<byte>(output.Sha256, 32));
        }

        internal Task<Exception?> CopyMemberToAttemptAsync(int member, Stream writer, CancellationToken token)
            => Task.Run<Exception?>(() =>
        {
            var output = member == 0 ? _output : _videoOutput;
            byte[] buffer = new byte[128 * 1024];
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            for (ulong offset = 0; offset < output.ByteLength;)
            {
                if(token.IsCancellationRequested)return new OperationCanceledException(token);
                int count = (int)Math.Min((ulong)buffer.Length, output.ByteLength - offset);
                if(ReadMember(output.StageId,offset,buffer.AsSpan(0,count)) is { } readFailure)return readFailure;
                hash.AppendData(buffer.AsSpan(0, count));
                if(TransactionStreamWriteAttempt.Write(writer,buffer.AsSpan(0,count),token,(long)offset) is { } writeFailure)return writeFailure;
                offset += (uint)count;
            }
            if (Convert.ToHexString(hash.GetHashAndReset()) != MemberHash(member))
                return new IOException("Target pair transport changed the sealed member bytes.");
            return null;
        },CancellationToken.None);

        private unsafe Exception? ReadMember(ulong stage, ulong offset, Span<byte> buffer)
        {
            fixed (byte* bytes = buffer)
            {
                if(!TryRequire(NativeMethods.TargetWriterReadStage(_operation.Handle, _plan, stage,
                    offset, bytes, (uint)buffer.Length, out uint read), "read pair member"))return Failure;
                if (read != buffer.Length) return new EndOfStreamException("Target pair transport was incomplete.");
            }
            return null;
        }

        private unsafe Exception? Read(ulong offset, Span<byte> buffer)
        {
            fixed (byte* bytes = buffer)
            {
                if(!TryRequire(NativeMethods.TargetWriterReadStage(_operation.Handle, _plan, _output.StageId,
                    offset, bytes, (uint)buffer.Length, out uint read), "read"))return Failure;
                if (read != buffer.Length) return new EndOfStreamException("Native target stage transport was incomplete.");
            }
            return null;
        }

        private unsafe byte[] OutputHash()
        {
            byte[] hash = new byte[32];
            for (int i = 0; i < hash.Length; i++) hash[i] = _output.Sha256[i];
            return hash;
        }

        internal Exception? Failure { get; private set; }
        private bool TryRequire(NativeResult result, string stage)
        {
            if (result == NativeResult.Ok) return true;
            string message = $"Native target Writer {stage}: {result}; {_context.GetLastError()}";
            Failure = result == NativeResult.Cancelled ? new OperationCanceledException(message)
                : result == NativeResult.Unsupported ? new NotSupportedException(message)
                : new IOException(message);
            return false;
        }

        private void Require(NativeResult result, string stage)
        {
            if (result == NativeResult.Ok) return;
            if (result == NativeResult.Cancelled) throw new OperationCanceledException("Native target Writer cancelled at " + stage);
            if (result == NativeResult.Unsupported) throw new NotSupportedException($"Native target Writer unsupported at {stage}: {_context.GetLastError()}");
            throw new IOException($"Native target Writer failed at {stage}: {result}; {_context.GetLastError()}");
        }

        private static unsafe NativeTargetWriterArtifactV1 Bind(MediaArtifact artifact, uint role)
        {
            var identity = artifact.FileIdentity!;
            var native = new NativeTargetWriterArtifactV1
            {
                StructSize = (uint)sizeof(NativeTargetWriterArtifactV1), Version = 1, Role = role,
                Container = role == 0 ? (uint)artifact.ImageContainer : (uint)artifact.VideoContainer,
                VideoCodec = role == 0 ? 0 : (uint)artifact.VideoCodec,
                ExpectedIdentity = new NativeFileIdentity
                {
                    VolumeSerial = identity.VolumeSerialNumber, FileIndex = identity.FileIndex,
                    FileSize = checked((ulong)artifact.ByteLength), LinkCount = identity.LinkCount
                }
            };
            byte[] hash = Convert.FromHexString(artifact.Sha256!);
            for (int i = 0; i < 32; i++) native.Sha256[i] = hash[i];
            EncodePath(artifact.Path, native.PathUtf8, 1024);
            return native;
        }

        private static unsafe void EncodePath(string path, byte* output, int capacity)
        {
            string full = Path.GetFullPath(path);
            if (full.Contains('\0')) throw new ArgumentException("A path cannot contain NUL.", nameof(path));
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(full);
            if (bytes.Length == 0 || bytes.Length >= capacity) throw new PathTooLongException("Native target path exceeds the explicit UTF-8 transport capacity.");
            new Span<byte>(output, capacity).Clear();
            bytes.CopyTo(new Span<byte>(output, bytes.Length));
        }

        private static unsafe void EncodeText(string text, byte* output, int capacity)
        {
            if (string.IsNullOrWhiteSpace(text) || text.Contains('\0')) throw new InvalidDataException("Semantic identity text is empty or contains NUL.");
            byte[] bytes = new UTF8Encoding(false, true).GetBytes(text);
            if (bytes.Length >= capacity) throw new InvalidDataException("Semantic identity exceeds its explicit ABI capacity.");
            new Span<byte>(output, capacity).Clear();
            bytes.CopyTo(new Span<byte>(output, bytes.Length));
        }

        private Exception? TryReleasePlan()
        {
            if(_plan==0)return null;
            nint plan=_plan;_plan=0;
            NativeResult release=NativeMethods.TargetWriterRelease(_operation.Handle,plan);
            return release==NativeResult.Ok?null:new IOException("Native target stage cleanup did not complete: "+release);
        }
        private void ReleasePlan()
        {
            if(TryReleasePlan() is { } failure)System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        public void Dispose()
        {
            if (_compoundRetained) { _compoundRetained = false; _compound!.DangerousRelease(); }
            try
            {
                ReleasePlan();
            }
            finally { _operation.Dispose(); _context.Dispose(); }
        }
    }
}
