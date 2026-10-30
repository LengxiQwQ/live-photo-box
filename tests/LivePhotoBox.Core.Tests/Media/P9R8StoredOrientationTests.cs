using System.Runtime.InteropServices;
using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8StoredOrientationTests
{
    private static string Source=>Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples/小米.jpg"));
    private static string Hash(string path)=>Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Fact]
    [Trait("Category","RealSamples")]
    public async Task ExplicitStoredPixelsChangesOnlyTheAuthorizedExifFieldAndRetainsEveryOtherByte()
    {
        string originalHash=Hash(Source);using var workspace=new MediaWorkspace();
        var neutral=await new NeutralMediaService().CreateNeutralBundleAsync(Source,null,workspace);
        byte[] before=File.ReadAllBytes(neutral.PrimaryImage.Path);byte[] expected=(byte[])before.Clone();
        var field=OrientationField(expected);Assert.Equal(4U,field.Type);Assert.Equal(0U,field.Value);
        Put(expected,field.Entry+2,2,3,field.BigEndian);Put(expected,field.Entry+8,4,field.BigEndian?0x00010000U:1U,field.BigEndian);
        string output=workspace.AllocateFilePath("resolved-test",".jpg");
        using var result=await NativeStoredOrientationResolver.ResolveAsync(neutral.PrimaryImage,output,UnspecifiedImageOrientationHandling.UseStoredPixels,CancellationToken.None);
        Assert.Equal(expected,File.ReadAllBytes(output));Assert.Equal(before,File.ReadAllBytes(neutral.PrimaryImage.Path));
        Assert.Equal(Hash(neutral.PrimaryImage.Path),result.Evidence.SourceSha256);Assert.Equal(Hash(output),result.Evidence.OutputSha256);
        Assert.Equal("Unspecified",result.Evidence.OriginalDirectionState);Assert.False(result.Evidence.PixelRotated);Assert.False(result.Evidence.PixelReencoded);
        var direction=await NativeMediaService.ObserveImageOrientationAsync(output,ImageContainer.Jpeg);
        Assert.Equal(NativeImageOrientationStatus.Verified,direction.Status);Assert.Equal(0,direction.ClockwiseRotationDegrees);
        Assert.Equal(originalHash,Hash(Source));
        await Assert.ThrowsAsync<InvalidOperationException>(()=>NativeStoredOrientationResolver.ResolveAsync(neutral.PrimaryImage,
            workspace.AllocateFilePath("not-selected",".jpg"),UnspecifiedImageOrientationHandling.Reject,CancellationToken.None));
    }

    [Fact]
    [Trait("Category","RealSamples")]
    public async Task SelectedNeutralRequestDoesNotChangeTheDefaultForTheNextRequest()
    {
        string originalHash=Hash(Source);using var workspace=new MediaWorkspace();
        var requirement=new MediaFormatRequirement{ImageContainer=ImageContainer.Jpeg,VideoContainer=VideoContainer.Mp4,
            UnspecifiedImageOrientationHandling=UnspecifiedImageOrientationHandling.UseStoredPixels};
        var resolved=await new NeutralMediaService().CreateNeutralBundleAsync(Source,null,workspace,requirement,PreservationPolicy.Strict);
        Assert.NotNull(resolved.PreparationExecution.OrientationResolution);Assert.Equal(1U,OrientationField(File.ReadAllBytes(resolved.PrimaryImage.Path)).Value);
        var ordinary=await new NeutralMediaService().CreateNeutralBundleAsync(Source,null,workspace);
        Assert.Null(ordinary.PreparationExecution.OrientationResolution);Assert.Equal(0U,OrientationField(File.ReadAllBytes(ordinary.PrimaryImage.Path)).Value);
        Assert.Equal(originalHash,Hash(Source));
    }

    [Theory]
    [InlineData("choice")][InlineData("reserved")][InlineData("identity")][InlineData("hash")]
    [InlineData("valid-direction")][InlineData("other-unknown")][InlineData("alias")][InlineData("existing")]
    [InlineData("missing")][InlineData("duplicate")][InlineData("type")][InlineData("count")]
    [InlineData("replaced-source")][InlineData("cancelled")]
    [Trait("Category","RealSamplesAndDerived")]
    public async Task NativeCannotNormalizeOtherDirectionsOrUnboundOrUnselectedInputs(string fault)
    {
        using var workspace=new MediaWorkspace();var neutral=await new NeutralMediaService().CreateNeutralBundleAsync(Source,null,workspace);
        using var folder=new P9TargetTestInputs.Folder(nameof(NativeCannotNormalizeOtherDirectionsOrUnboundOrUnselectedInputs)+fault);
        string input=Path.Combine(folder.Path,"input.jpg"),output=Path.Combine(folder.Path,"output.jpg");
        byte[] data=File.ReadAllBytes(neutral.PrimaryImage.Path);var field=OrientationField(data);
        if(fault=="missing")Put(data,field.Entry,2,0xc7ff,field.BigEndian);
        if(fault=="duplicate") {
            int neighbour=field.Entry+12;
            // A second IFD0 entry claims the same direction identity; no field is added or removed.
            Array.Copy(data,field.Entry,data,neighbour,12);
        }
        if(fault=="type")Put(data,field.Entry+2,2,7,field.BigEndian);
        if(fault=="count")Put(data,field.Entry+4,4,2,field.BigEndian);
        if(fault is "valid-direction" or "other-unknown") {
            Put(data,field.Entry+2,2,fault=="valid-direction"?3U:4U,field.BigEndian);
            Put(data,field.Entry+8,4,fault=="valid-direction"?(field.BigEndian?0x00010000U:1U):9U,field.BigEndian);
        }
        File.WriteAllBytes(input,data);if(fault=="alias")output=input;
        if(fault=="existing")File.WriteAllText(output,"foreign output");
        string hash=Hash(input);var request=Request(input,fault);var proof=new NativeImageConversionProofV1{StructSize=240,ApiVersion=1};
        if(fault=="replaced-source") {
            File.Move(input,Path.Combine(folder.Path,"replaced-original.jpg"));File.WriteAllBytes(input,data);
        }
        using var context=NativeContext.Create(new CancellationToken(fault=="cancelled"));var result=NativeMethods.ResolveUnspecifiedJpegOrientationV1(context.Handle,input,output,in request,ref proof);
        Assert.NotEqual(NativeResult.Ok,result);Assert.Equal(0U,proof.ProofKind);Assert.Equal(hash,Hash(input));
        if(fault=="cancelled")Assert.Equal(NativeResult.Cancelled,result);
        if(fault=="existing")Assert.Equal("foreign output",File.ReadAllText(output));else if(fault!="alias")Assert.False(File.Exists(output));
        Assert.Equal(fault is "existing" or "replaced-source"?2:1,Directory.GetFiles(folder.Path).Length);
    }

    [Fact]
    [Trait("Category","RealSamples")]
    public async Task CancelledCoreRequestDoesNotCreateAnOutputOrChangeTheSource()
    {
        using var workspace=new MediaWorkspace();var neutral=await new NeutralMediaService().CreateNeutralBundleAsync(Source,null,workspace);
        string hash=Hash(neutral.PrimaryImage.Path),output=workspace.AllocateFilePath("cancelled",".jpg");
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>NativeStoredOrientationResolver.ResolveAsync(neutral.PrimaryImage,output,
            UnspecifiedImageOrientationHandling.UseStoredPixels,new CancellationToken(true)));
        Assert.False(File.Exists(output));Assert.Equal(hash,Hash(neutral.PrimaryImage.Path));
    }

    [Fact]
    public unsafe void InvalidProofCapacitiesNeverWriteOutsideTheDeclaredRegion()
    {
        using var context=NativeContext.Create();byte* memory=stackalloc byte[512];
        foreach(uint capacity in new uint[]{0,1,4,8,16,80,239,240,241,512}) {
            new Span<byte>(memory,512).Fill(0xa5);*(uint*)memory=capacity;*(uint*)(memory+4)=1;var before=new ReadOnlySpan<byte>(memory,512).ToArray();
            var request=new NativeUnspecifiedJpegOrientationRequestV1();ref var proof=ref *(NativeImageConversionProofV1*)memory;
            Assert.NotEqual(NativeResult.Ok,NativeMethods.ResolveUnspecifiedJpegOrientationV1(context.Handle,"missing","missing2",in request,ref proof));
            int untouched=(int)Math.Min(capacity,240U);Assert.Equal(before.AsSpan(untouched).ToArray(),new ReadOnlySpan<byte>(memory+untouched,512-untouched).ToArray());
        }
    }

    [Fact]
    [Trait("Category","RealSamples")]
    public async Task CoreRejectsForgedStoredDirectionProofAndOtherProofScopes()
    {
        using var workspace=new MediaWorkspace();var neutral=await new NeutralMediaService().CreateNeutralBundleAsync(Source,null,workspace);
        CheckProof(neutral.PrimaryImage,workspace.AllocateFilePath("proof-test",".jpg"));
    }

    private static unsafe void CheckProof(MediaArtifact input,string output)
    {
        using var context=NativeContext.Create();var request=Request(input.Path,"");var proof=new NativeImageConversionProofV1{StructSize=240,ApiVersion=1};
        Assert.Equal(NativeResult.Ok,NativeMethods.ResolveUnspecifiedJpegOrientationV1(context.Handle,input.Path,output,in request,ref proof));
        var actual=NativeMediaService.CaptureRegisteredOutputEvidence(context,output,MediaArtifactKind.PrimaryImage);
        var valid=NativeStoredOrientationResolver.ValidateProof(proof,Hash(input.Path),(ulong)input.ByteLength,actual);Assert.Equal(actual.Sha256,valid.OutputSha256);
        Assert.Throws<InvalidDataException>(()=>NativeBoundImageConverter.ValidateProof(proof,Hash(input.Path),(ulong)input.ByteLength,actual));
        for(int i=0;i<24;++i) {
            var bad=proof;
            switch(i) {
                case 0:bad.StructSize=239;break;case 1:bad.ApiVersion=2;break;case 2:bad.ProofKind=1;break;
                case 3:bad.ProofKind=2;break;case 4:bad.ProofFlags=0;break;case 5:bad.Operation=2;break;
                case 6:bad.Reserved=1;break;case 7:bad.SourceOrientation=1;break;case 8:bad.OutputOrientation=0;break;
                case 9:bad.MetadataCount=2;break;case 10:bad.MetadataStatus=0;break;case 11:bad.OrientationStatus=0;break;
                case 12:bad.IccStatus=3;break;case 13:bad.SourceWidth=0;break;case 14:bad.OutputWidth++;break;
                case 15:bad.SourceSha256[0]^=1;break;case 16:bad.OutputSha256[0]^=1;break;
                case 17:bad.MappedSourceMetadataSha256[0]^=1;break;case 18:bad.OutputMetadataSha256[0]^=1;break;
                case 19:bad.SourceLength++;break;case 20:bad.OutputLength++;break;case 21:bad.OutputIdentity.LinkCount=2;break;
                case 22:bad.OutputIdentity.FileIndex++;break;case 23:bad.OutputIdentity.FileSize++;break;
            }
            var invalid=bad;
            Assert.Throws<InvalidDataException>(()=>NativeStoredOrientationResolver.ValidateProof(invalid,Hash(input.Path),(ulong)input.ByteLength,actual));
        }
    }

    private static unsafe NativeUnspecifiedJpegOrientationRequestV1 Request(string input,string fault)
    {
        using var file=File.Open(input,FileMode.Open,FileAccess.Read,FileShare.Read);var id=WindowsFileIdentity.Capture(file.SafeFileHandle);
        var request=new NativeUnspecifiedJpegOrientationRequestV1{StructSize=80,ApiVersion=1,Handling=fault=="choice"?0U:1U,Reserved=fault=="reserved"?1U:0U,
            ExpectedIdentity=new(){VolumeSerial=id.VolumeSerialNumber,FileIndex=id.FileIndex,FileSize=(ulong)file.Length,LinkCount=id.LinkCount}};
        if(fault=="identity")request.ExpectedIdentity.FileIndex++;
        var digest=SHA256.HashData(file);for(int i=0;i<32;++i)request.SourceSha256[i]=digest[i];if(fault=="hash")request.SourceSha256[0]^=1;
        return request;
    }

    // Independent marker/TIFF walk used to assert the full expected output.
    private static (int Entry,bool BigEndian,uint Type,uint Value) OrientationField(byte[] data)
    {
        Assert.True(data[0]==255&&data[1]==216);int at=2;
        while(at<data.Length) {
            Assert.Equal(255,data[at]);while(data[at]==255)at++;int marker=data[at++];Assert.NotEqual(0xda,marker);
            int length=(data[at]<<8)|data[at+1];Assert.True(length>=2&&at+length<=data.Length);
            if(marker==0xe1&&data.AsSpan(at+2,6).SequenceEqual("Exif\0\0"u8)) {
                int tiff=at+8;bool be=data[tiff]=='M';uint Get(int p,int n){uint v=0;for(int i=0;i<n;++i)v|=(uint)data[p+i]<<((be?n-i-1:i)*8);return v;}
                int root=tiff+(int)Get(tiff+4,4);int count=(int)Get(root,2);
                for(int i=0;i<count;++i){int entry=root+2+i*12;if(Get(entry,2)==0x112){uint type=Get(entry+2,2);return(entry,be,type,Get(entry+8,type==3?2:4));}}
                throw new InvalidDataException("Expected unique source orientation was absent.");
            }
            at+=length;
        }
        throw new InvalidDataException("Expected Exif was absent.");
    }
    private static void Put(byte[] data,int at,int size,uint value,bool big){for(int i=0;i<size;++i)data[at+i]=(byte)(value>>((big?size-i-1:i)*8));}
}
