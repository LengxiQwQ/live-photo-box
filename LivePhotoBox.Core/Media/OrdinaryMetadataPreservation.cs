using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Interop;

namespace LivePhotoBox.Media;

// Control-plane identity checks only. Record/container parsing and copying
// remain Native operations; opaque payload paths are never followed.
internal static class OrdinaryMetadataPreservation
{
    internal const string Semantic="OpaqueCameraRecords";
    internal const string Relationship="self-relative-record-directory-v1";
    internal static bool IsRecordCarrier(PreservationCarrier carrier)=>carrier.Semantic==Semantic;

    internal static async Task<MediaArtifact> VerifyAsync(PreservationCarrier carrier,CancellationToken token)
    {
        var attempt = await TryVerifyAsync(carrier, token).ConfigureAwait(false);
        if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return attempt.Artifact!;
    }

    internal static async Task<ArtifactAttempt> TryVerifyAsync(PreservationCarrier carrier,CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
        var artifact=carrier.MaterializedArtifact;
        if(carrier.Kind!=PreservationCarrierKind.OpaqueFragment||carrier.Semantic!=Semantic||
            carrier.Relationship!=Relationship||carrier.ArtifactRole!=MediaArtifactKind.PrimaryImage||
            carrier.OwnerIdentity!="primary:0"||carrier.Ownership!=AuxiliaryOwnership.Primary||
            carrier.Representation!=AuxiliaryRepresentation.Materialized||carrier.SourceIndex!=0||
            string.IsNullOrWhiteSpace(carrier.StableIdentity)||carrier.SourceOffset<0||carrier.SourceLength<=0||
            artifact is null||artifact.Kind!=MediaArtifactKind.MetadataCarrier||artifact.FileIdentity is null||
            artifact.FileIdentity.LinkCount!=1||artifact.FileIdentity.IsReparsePoint||
            artifact.ByteLength!=carrier.SourceLength||artifact.Sha256!=carrier.SourceSha256||
            carrier.SourceSha256.Length!=64||artifact.ImageContainer!=ImageContainer.Unknown||
            artifact.VideoContainer!=VideoContainer.Unknown)
            return new(null, new InvalidDataException("Ordinary metadata has incomplete identity, ownership or actual materialized bytes."));
        return await TryVerifyArtifactAsync(artifact,carrier.SourceLength,carrier.SourceSha256,token).ConfigureAwait(false);
    }

    // Final correctness consumers bind a final artifact and semantic row,
    // independently of source protocol, source offset or provenance.
    internal static async Task<MediaArtifact> VerifyArtifactAsync(MediaArtifact artifact,long expectedLength,string expectedSha256,CancellationToken token)
    {
        var attempt = await TryVerifyArtifactAsync(artifact, expectedLength, expectedSha256, token).ConfigureAwait(false);
        if (attempt.Failure is { } failure) System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        return attempt.Artifact!;
    }

    internal sealed record ArtifactAttempt(MediaArtifact? Artifact, Exception? Failure);

    internal static async Task<ArtifactAttempt> TryVerifyArtifactAsync(MediaArtifact artifact,long expectedLength,string expectedSha256,CancellationToken token)
    {
        if (token.IsCancellationRequested) return new(null, new OperationCanceledException(token));
        if(artifact.Kind!=MediaArtifactKind.MetadataCarrier||artifact.FileIdentity is null||artifact.FileIdentity.LinkCount!=1||
            artifact.FileIdentity.IsReparsePoint||expectedLength<=0||artifact.ByteLength!=expectedLength||artifact.Sha256!=expectedSha256||
            expectedSha256.Length!=64||artifact.ImageContainer!=ImageContainer.Unknown||artifact.VideoContainer!=VideoContainer.Unknown)
            return new(null, new InvalidDataException("Ordinary metadata final artifact differs from its semantic identity."));
        using var leases=new NeutralArtifactReadLeaseSet();
        if (leases.TryPin(artifact) is { } leaseFailure) return new(null, leaseFailure);
        using var source = SourceReadAttempt.Open(artifact.Path);
        if (!source.Succeeded) return new(null, new IOException($"Cannot open ordinary metadata (system error {source.Error})."));
        if (!WindowsFileIdentity.TryCapture(source.Handle, out var identity, out int error))
            return new(null, new IOException($"Cannot verify ordinary metadata identity (system error {error})."));
        if(identity!.IsReparsePoint||identity.LinkCount!=1||identity.VolumeSerialNumber!=artifact.FileIdentity.VolumeSerialNumber||
            identity.FileIndex!=artifact.FileIdentity.FileIndex)
            return new(null, new InvalidDataException("Ordinary metadata artifact was replaced, linked or resized."));
        var hash = await MediaFileHashAttempt.ComputeAsync(artifact.Path, token).ConfigureAwait(false);
        if (hash.Failure is { } hashFailure) return new(null, hashFailure);
        if (!WindowsFileIdentity.TryCapture(source.Handle, out var after, out error))
            return new(null, new IOException($"Cannot recheck ordinary metadata identity (system error {error})."));
        if(after!.IsReparsePoint||after.LinkCount!=1||after.VolumeSerialNumber!=identity.VolumeSerialNumber||
            after.FileIndex!=identity.FileIndex||hash.Sha256!=expectedSha256)
            return new(null, new InvalidDataException("Ordinary metadata bytes no longer match their Native extraction receipt."));
        return new(artifact, null);
    }
}
