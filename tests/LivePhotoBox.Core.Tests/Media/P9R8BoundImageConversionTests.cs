using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8BoundImageConversionTests
{
    [Fact]
    public void AdditiveImageConversionLayoutsAreExactAndOldProofLayoutsStaySeparate()
    {
        Assert.Equal(96,Marshal.SizeOf<NativeImageConversionRequestV2>());
        Assert.Equal(24,Marshal.OffsetOf<NativeImageConversionRequestV2>(nameof(NativeImageConversionRequestV2.ExpectedIdentity)).ToInt32());
        Assert.Equal(56,Marshal.OffsetOf<NativeImageConversionRequestV2>(nameof(NativeImageConversionRequestV2.SourceSha256)).ToInt32());
        Assert.Equal(240,Marshal.SizeOf<NativeImageConversionProofV1>());
        Assert.Equal(144,Marshal.OffsetOf<NativeImageConversionProofV1>(nameof(NativeImageConversionProofV1.SourceLength)).ToInt32());
        Assert.Equal(208,Marshal.OffsetOf<NativeImageConversionProofV1>(nameof(NativeImageConversionProofV1.OutputIdentity)).ToInt32());
        Assert.Equal(80,Marshal.SizeOf<NativeHdrMetadataProofV2>());
    }
    [Theory]
    [InlineData("oppo.jpg")]
    [InlineData("vivo双文件.jpg")]
    [InlineData("苹果-双文件.JPG")]
    [Trait("Category","RealSamples")]
    public async Task RealOrdinaryJpegCarriesWholeExifXmpAndIccWithBoundReencodeProof(string name)
    {
        string original=Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples",name));
        string? companion=name=="vivo双文件.jpg"?Path.ChangeExtension(original,"mp4"):name=="苹果-双文件.JPG"?Path.ChangeExtension(original,"MOV"):null;
        var sourceHash=Hash(File.ReadAllBytes(original));using var workspace=new MediaWorkspace();
        var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(original,companion,workspace);
        var primary=bundle.PrimaryImage;using var folder=new P9TargetTestInputs.Folder(nameof(RealOrdinaryJpegCarriesWholeExifXmpAndIccWithBoundReencodeProof)+name);
        string output=Path.Combine(folder.Path,"converted.heic");var proof=Invoke(primary.Path,output);
        Assert.Equal(1U,proof.ProofKind);Assert.Equal(1U,proof.ProofFlags);Assert.Equal(3U,proof.Operation);
        Assert.Equal(1U,proof.SourceOrientation);Assert.Equal(1U,proof.OutputOrientation);Assert.Equal(1U,proof.OrientationStatus);
        Assert.Equal((ulong)new FileInfo(primary.Path).Length,proof.SourceLength);Assert.Equal((ulong)new FileInfo(output).Length,proof.OutputLength);
        Assert.Equal(Hash(File.ReadAllBytes(primary.Path)),ProofHash(proof,0));Assert.Equal(Hash(File.ReadAllBytes(output)),ProofHash(proof,1));
        Assert.Equal(ProofHash(proof,2),ProofHash(proof,3));Assert.NotEqual(new string('0',64),ProofHash(proof,2));
        var identity=WindowsFileIdentity.Capture(output);Assert.Equal(identity.FileIndex,proof.OutputIdentity.FileIndex);Assert.Equal(identity.VolumeSerialNumber,proof.OutputIdentity.VolumeSerial);
        Assert.Equal(1U,proof.OutputIdentity.LinkCount);Assert.Equal(proof.OutputLength,proof.OutputIdentity.FileSize);
        Assert.Equal(output,Assert.Single(Directory.GetFiles(folder.Path)));Assert.Equal(sourceHash,Hash(File.ReadAllBytes(original)));
        P9R8BoundImageFailureTests.Snapshot(name,primary.Path,output,proof);
    }
    private static unsafe NativeImageConversionProofV1 Invoke(string input,string output)
    {
        var identity=WindowsFileIdentity.Capture(input);
        var request=new NativeImageConversionRequestV2{StructSize=96,ApiVersion=2,SourceContainer=1,TargetContainer=2,Quality=95,OrientationPolicy=1,
            ExpectedIdentity=new NativeCleanService.NativeFileIdentity{VolumeSerial=identity.VolumeSerialNumber,FileIndex=identity.FileIndex,FileSize=(ulong)new FileInfo(input).Length,LinkCount=identity.LinkCount}};
        var hash=SHA256.HashData(File.ReadAllBytes(input));for(int i=0;i<32;i++)request.SourceSha256[i]=hash[i];
        using var context=NativeContext.Create();var proof=new NativeImageConversionProofV1{StructSize=240,ApiVersion=1};
        var result=NativeMethods.ConvertImageV2(context.Handle,input,output,in request,ref proof);context.ThrowIfFailed(result);return proof;
    }
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private static unsafe string ProofHash(NativeImageConversionProofV1 proof,int index)
    {
        byte* p=index switch{0=>proof.SourceSha256,1=>proof.OutputSha256,2=>proof.MappedSourceMetadataSha256,3=>proof.OutputMetadataSha256,_=>throw new ArgumentOutOfRangeException(nameof(index))};
        return Convert.ToHexString(new ReadOnlySpan<byte>(p,32));
    }
}
