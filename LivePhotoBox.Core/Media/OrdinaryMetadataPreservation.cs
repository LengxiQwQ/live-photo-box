using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;

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
            throw new InvalidDataException("Ordinary metadata has incomplete identity, ownership or actual materialized bytes.");
        return await VerifyArtifactAsync(artifact,carrier.SourceLength,carrier.SourceSha256,token).ConfigureAwait(false);
    }

    // Final correctness consumers bind a final artifact and semantic row,
    // independently of source protocol, source offset or provenance.
    internal static async Task<MediaArtifact> VerifyArtifactAsync(MediaArtifact artifact,long expectedLength,string expectedSha256,CancellationToken token)
    {
        if(artifact.Kind!=MediaArtifactKind.MetadataCarrier||artifact.FileIdentity is null||artifact.FileIdentity.LinkCount!=1||
            artifact.FileIdentity.IsReparsePoint||expectedLength<=0||artifact.ByteLength!=expectedLength||artifact.Sha256!=expectedSha256||
            expectedSha256.Length!=64||artifact.ImageContainer!=ImageContainer.Unknown||artifact.VideoContainer!=VideoContainer.Unknown)
            throw new InvalidDataException("Ordinary metadata final artifact differs from its semantic identity.");
        using var leases=new NeutralArtifactReadLeaseSet();
        leases.Pin(artifact);
        using var stream=new FileStream(artifact.Path,FileMode.Open,FileAccess.Read,FileShare.Read,65536,FileOptions.Asynchronous|FileOptions.SequentialScan);
        var identity=WindowsFileIdentity.Capture(stream.SafeFileHandle);
        if(identity.IsReparsePoint||identity.LinkCount!=1||identity.VolumeSerialNumber!=artifact.FileIdentity.VolumeSerialNumber||
            identity.FileIndex!=artifact.FileIdentity.FileIndex||stream.Length!=artifact.ByteLength)
            throw new InvalidDataException("Ordinary metadata artifact was replaced, linked or resized.");
        string hash=Convert.ToHexString(await SHA256.HashDataAsync(stream,token).ConfigureAwait(false));
        var after=WindowsFileIdentity.Capture(stream.SafeFileHandle);
        if(after.IsReparsePoint||after.LinkCount!=1||after.VolumeSerialNumber!=identity.VolumeSerialNumber||
            after.FileIndex!=identity.FileIndex||stream.Length!=artifact.ByteLength||hash!=expectedSha256)
            throw new InvalidDataException("Ordinary metadata bytes no longer match their Native extraction receipt.");
        return artifact;
    }
}
