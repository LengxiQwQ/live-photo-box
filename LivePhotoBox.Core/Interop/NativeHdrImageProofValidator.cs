using System;
using System.IO;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Interop;

internal static class NativeHdrImageProofValidator
{
    // Separate from ordinary kind1: both image owners, eight metadata carriers,
    // and the producer's separately verified original JPEG padding context.
    internal static unsafe ImageHdrConversionEvidence Validate(NativeImageConversionProofV1 proof,
        string sourceHash,ulong sourceLength,uint sourceOrientation,
        NativeMediaService.NativeRegisteredOutputEvidence actual)
    {
        string source=Convert.ToHexString(new ReadOnlySpan<byte>(proof.SourceSha256,32));
        string output=Convert.ToHexString(new ReadOnlySpan<byte>(proof.OutputSha256,32));
        string mapped=Convert.ToHexString(new ReadOnlySpan<byte>(proof.MappedSourceMetadataSha256,32));
        string observed=Convert.ToHexString(new ReadOnlySpan<byte>(proof.OutputMetadataSha256,32));
        bool swap=proof.SourceOrientation>=5;
        if(proof.StructSize!=240||proof.ApiVersion!=1||proof.ProofKind!=2||proof.ProofFlags!=1||proof.Operation!=3||proof.Reserved!=0||
            proof.SourceOrientation is <1 or >8||proof.SourceOrientation!=sourceOrientation||proof.OutputOrientation!=1||proof.OrientationStatus!=1||
            proof.SourceWidth is 0 or >65535||proof.SourceHeight is 0 or >65535||
            proof.OutputWidth!=(swap?proof.SourceHeight:proof.SourceWidth)||proof.OutputHeight!=(swap?proof.SourceWidth:proof.SourceHeight)||
            proof.MetadataCount is <1 or >8||proof.MetadataStatus!=1||proof.IccStatus is not (1 or 2)||
            source!=sourceHash||proof.SourceLength!=sourceLength||sourceLength==0||output!=actual.Sha256||
            actual.ByteLength<=0||proof.OutputLength!=(ulong)actual.ByteLength||mapped!=observed||mapped==new string('0',64)||
            proof.OutputIdentity.LinkCount!=1||actual.FileIdentity.LinkCount!=1||actual.FileIdentity.IsReparsePoint||
            proof.OutputIdentity.VolumeSerial!=actual.FileIdentity.VolumeSerialNumber||proof.OutputIdentity.FileIndex!=actual.FileIdentity.FileIndex||
            proof.OutputIdentity.FileSize!=(ulong)actual.ByteLength)
            throw new InvalidDataException("Complete HDR image proof does not bind its source, both-owner inventory, creating output or physical direction.");
        return new(source,output,mapped,observed,proof.SourceOrientation,proof.OutputOrientation,proof.SourceWidth,proof.SourceHeight,
            proof.OutputWidth,proof.OutputHeight,proof.MetadataCount,proof.IccStatus==1)
            {NativeVerifiedOrientation=new(source,output,proof.SourceOrientation,proof.OutputOrientation)};
    }
}
