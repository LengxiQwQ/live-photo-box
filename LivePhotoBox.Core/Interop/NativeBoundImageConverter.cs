using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;

namespace LivePhotoBox.Interop;

internal sealed record NativeBoundImageAttempt(
    NativeMediaService.NativeImageConversionAttempt Attempt,
    ImageOrdinaryConversionEvidence? Proof,NeutralArtifactReadLeaseSet? OutputLease) : IDisposable
{
    public void Dispose()=>OutputLease?.Dispose();
}

internal static class NativeBoundImageConverter
{
    internal static Task<NativeBoundImageAttempt> ConvertAsync(MediaArtifact source,string output,int quality,CancellationToken token)=>
        Task.Run(()=>Convert(source,output,quality,token),token);

    private static unsafe NativeBoundImageAttempt Convert(MediaArtifact source,string output,int quality,CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        using var inputLease=new NeutralArtifactReadLeaseSet();inputLease.Pin(source);
        using var input=new FileStream(source.Path,FileMode.Open,FileAccess.Read,FileShare.Read);
        var identity=WindowsFileIdentity.Capture(input.SafeFileHandle);string sourceHash=System.Convert.ToHexString(SHA256.HashData(input));
        if(source.Kind!=MediaArtifactKind.PrimaryImage||source.ImageContainer!=ImageContainer.Jpeg||
            (source.Sha256 is { Length: > 0 } expected&&!string.Equals(expected,sourceHash,StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("Bound image input bytes differ from the requested artifact.");
        var request=new NativeImageConversionRequestV2{StructSize=96,ApiVersion=2,SourceContainer=1,TargetContainer=2,Quality=quality,OrientationPolicy=1,
            ExpectedIdentity=new NativeCleanService.NativeFileIdentity{VolumeSerial=identity.VolumeSerialNumber,FileIndex=identity.FileIndex,FileSize=(ulong)input.Length,LinkCount=identity.LinkCount}};
        byte[] hash=System.Convert.FromHexString(sourceHash);for(int i=0;i<32;++i)request.SourceSha256[i]=hash[i];
        using var context=NativeContext.Create(token);var proof=new NativeImageConversionProofV1{StructSize=240,ApiVersion=1};
        var result=NativeMethods.ConvertImageV2(context.Handle,source.Path,output,in request,ref proof);
        if(result==NativeResult.Unsupported)throw new NotSupportedException(context.GetLastError());
        context.ThrowIfFailed(result);
        NativeMediaService.NativeRegisteredOutputEvidence? evidence=null;NeutralArtifactReadLeaseSet? outputLease=null;
        try {
            evidence=NativeMediaService.CaptureRegisteredOutputEvidence(context,output,MediaArtifactKind.PrimaryImage);
            outputLease=new NeutralArtifactReadLeaseSet();outputLease.Pin(new MediaArtifact{Path=output,Kind=MediaArtifactKind.PrimaryImage,
                ImageContainer=ImageContainer.Heic,ByteLength=evidence.ByteLength,Sha256=evidence.Sha256,FileIdentity=evidence.FileIdentity});
            var verified=ValidateProof(proof,sourceHash,(ulong)input.Length,evidence);
            return new(new(true,evidence,null),verified,outputLease);
        }catch(Exception error) {
            outputLease?.Dispose();return new(new(true,evidence,error),null,null);
        }
    }

    internal static unsafe ImageOrdinaryConversionEvidence ValidateProof(NativeImageConversionProofV1 proof,string sourceHash,ulong sourceLength,
        NativeMediaService.NativeRegisteredOutputEvidence actual)
    {
        string source=System.Convert.ToHexString(new ReadOnlySpan<byte>(proof.SourceSha256,32));
        string output=System.Convert.ToHexString(new ReadOnlySpan<byte>(proof.OutputSha256,32));
        string mapped=System.Convert.ToHexString(new ReadOnlySpan<byte>(proof.MappedSourceMetadataSha256,32));
        string observed=System.Convert.ToHexString(new ReadOnlySpan<byte>(proof.OutputMetadataSha256,32));
        bool swap=proof.SourceOrientation>=5;
        if(proof.StructSize!=240||proof.ApiVersion!=1||proof.ProofKind!=1||proof.ProofFlags!=1||proof.Operation!=3||proof.Reserved!=0||
            proof.SourceOrientation is <1 or >8||proof.OutputOrientation!=1||proof.OrientationStatus!=1||
            proof.SourceWidth is 0 or >65535||proof.SourceHeight is 0 or >65535||
            proof.OutputWidth!=(swap?proof.SourceHeight:proof.SourceWidth)||proof.OutputHeight!=(swap?proof.SourceWidth:proof.SourceHeight)||
            proof.MetadataCount>4||proof.MetadataStatus!=(proof.MetadataCount==0?2U:1U)||proof.IccStatus is not (1 or 2)||
            (proof.IccStatus==1&&proof.MetadataCount==0)||
            source!=sourceHash||proof.SourceLength!=sourceLength||output!=actual.Sha256||proof.OutputLength!=(ulong)actual.ByteLength||
            mapped!=observed||mapped==new string('0',64)||proof.OutputIdentity.LinkCount!=1||
            proof.OutputIdentity.VolumeSerial!=actual.FileIdentity.VolumeSerialNumber||proof.OutputIdentity.FileIndex!=actual.FileIdentity.FileIndex||
            proof.OutputIdentity.FileSize!=(ulong)actual.ByteLength)
            throw new InvalidDataException("Bound image proof does not match its actual source, creating-handle output, inventory or physical direction.");
        return new(source,output,mapped,observed,proof.SourceOrientation,proof.OutputOrientation,proof.SourceWidth,proof.SourceHeight,
            proof.OutputWidth,proof.OutputHeight,proof.MetadataCount,proof.IccStatus==1)
            {NativeVerifiedOrientation=new(source,output,proof.SourceOrientation,proof.OutputOrientation)};
    }
}
