using System.Buffers.Binary;
using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8NativeInlineMetadataTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("playback-literal")]
    [InlineData("camera-literal")]
    [InlineData("model-flags")]
    [InlineData("model-terminator")]
    [InlineData("reference-child")]
    [InlineData("duplicate-field")]
    [InlineData("implicit-container")]
    [InlineData("implicit-model")]
    [Trait("Category","DerivedRealSamples")]
    public async Task PublicNativeCopyValidatesInlineMetadataBeforeAuthorizingProof(string defect)
    {
        string original=Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples/三星.jpg"));
        string sourceHash=Hash(File.ReadAllBytes(original));
        using var workspace=new MediaWorkspace();
        var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(original,null,workspace);
        byte[] b=File.ReadAllBytes(Assert.IsType<LivePhotoBox.Media.Models.MediaArtifact>(bundle.MotionVideo).Path);
        var movie=One(Children(b,0,b.Length),"moov");var user=One(Children(b,movie.Body,movie.End),"udta");var fields=Children(b,user.Body,user.End);
        Box play=One(fields,"SDLN"),camera=One(fields,"smrd"),modelContainer=One(fields,"smta");
        var model=One(Children(b,modelContainer.Body+4,modelContainer.End),"mdln");
        switch(defect)
        {
            case "valid":break;
            case "playback-literal":b[play.Body]^=1;break;
            case "camera-literal":b[camera.Body]^=1;break;
            case "model-flags":b[modelContainer.Body+1]=1;break;
            case "model-terminator":b[model.Body]=0;break;
            case "reference-child":"iloc"u8.CopyTo(b.AsSpan(model.Body-4));break;
            case "duplicate-field":"SDLN"u8.CopyTo(b.AsSpan(camera.Body-4));"SEQ_PLAY"u8.CopyTo(b.AsSpan(camera.Body));break;
            case "implicit-container":b.AsSpan(modelContainer.Body-8,4).Clear();break;
            case "implicit-model":b.AsSpan(model.Body-8,4).Clear();break;
            default:throw new ArgumentOutOfRangeException(nameof(defect));
        }
        using var folder=new P9TargetTestInputs.Folder(nameof(PublicNativeCopyValidatesInlineMetadataBeforeAuthorizingProof)+defect);
        string input=Path.Combine(folder.Path,"derived.mp4"),output=Path.Combine(folder.Path,"must-not-publish.mov");File.WriteAllBytes(input,b);
        using var context=NativeContext.Create();
        var request=new NativeVideoPreservationRequestV1{StructSize=16,ApiVersion=1,Policy=1};
        var diagnostics=new NativeVideoBackendDiagnostics{StructSize=208};
        var proof=new NativeVideoPreservationResultV2{StructSize=192,ApiVersion=2,ProofKind=1,ProofFlags=1};
        // C ABI order is container then codec: MOV=2, explicit Copy=1.
        var result=NativeMethods.TranscodeVideoV4(context.Handle,input,output,targetContainer:2,targetCodec:1,crf:23,in request,ref diagnostics,ref proof);
        if(defect=="valid")
        {
            Assert.Equal(NativeResult.Ok,result);Assert.Equal(1U,proof.ProofKind);Assert.Equal(1U,proof.ProofFlags);
            Assert.True(File.Exists(output));Assert.Equal(2,Directory.GetFiles(folder.Path).Length);
            Assert.Equal((ulong)b.Length,proof.SourceLength);Assert.Equal((ulong)new FileInfo(output).Length,proof.OutputLength);
            Assert.Equal(Hash(b),ProofHash(proof,true));Assert.Equal(Hash(File.ReadAllBytes(output)),ProofHash(proof,false));
            Assert.Equal(b,File.ReadAllBytes(input));Assert.Equal(sourceHash,Hash(File.ReadAllBytes(original)));return;
        }
        Assert.NotEqual(NativeResult.Ok,result);Assert.Equal(0U,proof.ProofKind);Assert.Equal(0U,proof.ProofFlags);Assert.Equal(0U,proof.Metadata.Status);
        var failure=Record.Exception(()=>context.ThrowIfFailed(result));Assert.NotNull(failure);Assert.Contains("movie static metadata",failure.Message);
        Assert.False(File.Exists(output));Assert.Equal(input,Assert.Single(Directory.GetFiles(folder.Path)));
        Assert.Equal(b,File.ReadAllBytes(input));Assert.Equal(sourceHash,Hash(File.ReadAllBytes(original)));
    }
    private static string Hash(byte[] b)=>Convert.ToHexString(SHA256.HashData(b));
    private static unsafe string ProofHash(NativeVideoPreservationResultV2 proof,bool source)
    {
        byte* bytes=source?proof.SourceFileSha256:proof.OutputFileSha256;return Convert.ToHexString(new ReadOnlySpan<byte>(bytes,32));
    }
    private sealed record Box(int Body,int End,string Kind);
    private static Box One(IReadOnlyList<Box> rows,string kind)=>Assert.Single(rows,x=>x.Kind==kind);
    private static List<Box> Children(byte[] b,int at,int end)
    {
        var result=new List<Box>();
        while(at<end)
        {
            int n=checked((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at))),header=8;
            if(n==1){n=checked((int)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(at+8)));header=16;}
            Assert.InRange(n,header,end-at);result.Add(new(at+header,at+n,System.Text.Encoding.ASCII.GetString(b,at+4,4)));at+=n;
        }
        Assert.Equal(end,at);return result;
    }
}
