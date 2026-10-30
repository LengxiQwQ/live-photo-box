using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Interop;

internal sealed record NativeStoredOrientationResolution(MediaArtifact Output,
    ImageOrientationResolutionEvidence Evidence,NeutralArtifactReadLeaseSet Lease):IDisposable
{
    public void Dispose()=>Lease.Dispose();
}

internal static class NativeStoredOrientationResolver
{
    internal static Task<NativeStoredOrientationResolution> ResolveAsync(MediaArtifact source,string output,
        UnspecifiedImageOrientationHandling handling,CancellationToken token)=>Task.Run(()=>Resolve(source,output,handling,token),token);

    private static unsafe NativeStoredOrientationResolution Resolve(MediaArtifact source,string output,
        UnspecifiedImageOrientationHandling handling,CancellationToken token)
    {
        if(handling!=UnspecifiedImageOrientationHandling.UseStoredPixels)throw new InvalidOperationException("Explicit stored-pixel direction selection is required.");
        token.ThrowIfCancellationRequested();
        if(source.Kind!=MediaArtifactKind.PrimaryImage||source.ImageContainer!=ImageContainer.Jpeg||source.CompoundPrimary is not null)
            throw new NotSupportedException("Stored-pixel direction resolution requires a complete neutral JPEG artifact.");
        using var sourceLease=new NeutralArtifactReadLeaseSet();sourceLease.Pin(source);
        using var input=File.Open(source.Path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var identity=WindowsFileIdentity.Capture(input.SafeFileHandle);string hash=Convert.ToHexString(SHA256.HashData(input));
        if(source.Sha256 is not {Length:64}||source.Sha256!=hash||source.ByteLength!=input.Length)
            throw new InvalidDataException("Stored-direction source artifact bytes changed.");
        var request=new NativeUnspecifiedJpegOrientationRequestV1{StructSize=80,ApiVersion=1,Handling=1,
            ExpectedIdentity=new(){VolumeSerial=identity.VolumeSerialNumber,FileIndex=identity.FileIndex,FileSize=(ulong)input.Length,LinkCount=identity.LinkCount}};
        byte[] digest=Convert.FromHexString(hash);for(int i=0;i<32;++i)request.SourceSha256[i]=digest[i];
        using var context=NativeContext.Create(token);var proof=new NativeImageConversionProofV1{StructSize=240,ApiVersion=1};
        var result=NativeMethods.ResolveUnspecifiedJpegOrientationV1(context.Handle,source.Path,output,in request,ref proof);
        if(result==NativeResult.Unsupported)throw new NotSupportedException(context.GetLastError());context.ThrowIfFailed(result);
        NativeMediaService.NativeRegisteredOutputEvidence? actual=null;NeutralArtifactReadLeaseSet? outputLease=null;
        try {
            actual=NativeMediaService.CaptureRegisteredOutputEvidence(context,output,MediaArtifactKind.PrimaryImage);
            var artifact=new MediaArtifact{Path=output,Kind=MediaArtifactKind.PrimaryImage,MimeType="image/jpeg",ImageContainer=ImageContainer.Jpeg,
                ImageCodec=ImageCodec.Jpeg,ByteLength=actual.ByteLength,Sha256=actual.Sha256,FileIdentity=actual.FileIdentity};
            outputLease=new NeutralArtifactReadLeaseSet();outputLease.Pin(artifact);
            var evidence=ValidateProof(proof,hash,(ulong)input.Length,actual);
            return new(artifact,evidence,outputLease);
        }catch {
            outputLease?.Dispose();
            if(!NativeMediaService.TryDeleteRegisteredOutput(output,actual))throw new IOException("Exact cleanup of the failed stored-direction output could not be proved.");
            throw;
        }
    }

    internal static unsafe ImageOrientationResolutionEvidence ValidateProof(NativeImageConversionProofV1 proof,string sourceHash,
        ulong sourceLength,NativeMediaService.NativeRegisteredOutputEvidence actual)
    {
        string source=Convert.ToHexString(new ReadOnlySpan<byte>(proof.SourceSha256,32));
        string output=Convert.ToHexString(new ReadOnlySpan<byte>(proof.OutputSha256,32));
        string mapped=Convert.ToHexString(new ReadOnlySpan<byte>(proof.MappedSourceMetadataSha256,32));
        string observed=Convert.ToHexString(new ReadOnlySpan<byte>(proof.OutputMetadataSha256,32));
        if(proof.StructSize!=240||proof.ApiVersion!=1||proof.ProofKind!=3||proof.ProofFlags!=1||proof.Operation!=4||proof.Reserved!=0||
            proof.SourceOrientation!=0||proof.OutputOrientation!=1||proof.OrientationStatus!=1||proof.MetadataCount!=1||proof.MetadataStatus!=1||
            proof.IccStatus is not (1 or 2)||proof.SourceWidth is 0 or >65535||proof.SourceHeight is 0 or >65535||
            proof.OutputWidth!=proof.SourceWidth||proof.OutputHeight!=proof.SourceHeight||
            source!=sourceHash||proof.SourceLength!=sourceLength||proof.OutputLength!=sourceLength||source==output||
            output!=actual.Sha256||proof.OutputLength!=(ulong)actual.ByteLength||mapped!=observed||mapped==new string('0',64)||
            proof.OutputIdentity.LinkCount!=1||proof.OutputIdentity.VolumeSerial!=actual.FileIdentity.VolumeSerialNumber||
            proof.OutputIdentity.FileIndex!=actual.FileIdentity.FileIndex||proof.OutputIdentity.FileSize!=(ulong)actual.ByteLength)
            throw new InvalidDataException("Stored-direction proof does not bind the explicit operation, exact source/output, geometry or mapped Exif.");
        return new(source,output,mapped,observed,sourceLength,proof.OutputWidth,proof.OutputHeight);
    }
}
