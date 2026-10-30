using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8NativeInlineTransformCopyTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("non-copy")]
    [InlineData("version")]
    [InlineData("matrix-count")]
    [InlineData("orientation")]
    [InlineData("size")]
    [InlineData("crop-scale")]
    [InlineData("matrix")]
    [InlineData("duplicate")]
    [InlineData("unknown")]
    [InlineData("missing")]
    [InlineData("overflow")]
    [InlineData("array-size")]
    [InlineData("escaped-string")]
    [InlineData("oversized")]
    [InlineData("implicit")]
    [Trait("Category","RealSamplesAndDerived")]
    public async Task InlineTransformValuesAreCarriedOnlyByCompleteCopyWithClosedGrammar(string defect)
    {
        string original=Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples/一加.jpg"));
        string sourceHash=Hash(File.ReadAllBytes(original));using var workspace=new MediaWorkspace();
        var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(original,null,workspace);
        byte[] b=File.ReadAllBytes(Assert.IsType<MediaArtifact>(bundle.MotionVideo).Path);
        var movie=One(Children(b,0,b.Length),"moov");var extension=One(Children(b,movie.Body,movie.End),"lpex");
        Assert.Equal(b.Length,movie.End);Assert.Equal(movie.End,extension.End);
        string text=Encoding.UTF8.GetString(b,extension.Body,extension.End-extension.Body),changed=text;
        (string Before,string After) edit=defect switch
        {
            "valid" or "non-copy" or "oversized" or "implicit"=>("",""),
            "version"=>("\"version\":1","\"version\":2"),
            "matrix-count"=>("\"matrixCount\":0","\"matrixCount\":1"),
            "orientation"=>("\"videoOrientation\":90","\"videoOrientation\":91"),
            "size"=>("\"videoSize\":[1728,1296]","\"videoSize\":[0,1296]"),
            "crop-scale"=>("[1.11,1.11]","[0,1.11]"),
            "matrix"=>("[1.0,0.0,-3.0E-6","[0.0,0.0,-3.0E-6"),
            "duplicate"=>("\"version\":1","\"version\":1,\"version\":1"),
            "unknown"=>("\"version\":1","\"version\":1,\"offset\":1"),
            "missing"=>("\"version\":1,",""),
            "overflow"=>("\"photoCropFactor\":0.0","\"photoCropFactor\":1e999"),
            "array-size"=>("[0,0,0,0]","[0,0,0]"),
            "escaped-string"=>("OppoMotionVideoExt","Oppo\\u004dotionVideoExt"),
            _=>throw new ArgumentOutOfRangeException(nameof(defect))
        };
        if(edit.Before.Length>0)
        {
            Assert.Contains(edit.Before,text);changed=text.Replace(edit.Before,edit.After,StringComparison.Ordinal);Assert.NotEqual(text,changed);
        }
        else if(defect=="oversized")changed=text+new string(' ',16384);
        if(changed!=text)
        {
            byte[] payload=Encoding.UTF8.GetBytes(changed);int delta=payload.Length-(extension.End-extension.Body);
            b=b[..extension.Body].Concat(payload).ToArray();
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(extension.Body-8),checked((uint)(payload.Length+8)));
            BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(movie.Body-8),checked((uint)(movie.End-movie.Body+8+delta)));
        }
        if(defect=="implicit")b.AsSpan(extension.Body-8,4).Clear();
        using var folder=new P9TargetTestInputs.Folder(nameof(InlineTransformValuesAreCarriedOnlyByCompleteCopyWithClosedGrammar)+defect);
        string input=Path.Combine(folder.Path,"source.mp4"),output=Path.Combine(folder.Path,"target.mov");File.WriteAllBytes(input,b);
        using var context=NativeContext.Create();var request=new NativeVideoPreservationRequestV1{StructSize=16,ApiVersion=1,Policy=1};
        var diagnostic=new NativeVideoBackendDiagnostics{StructSize=208};var proof=new NativeVideoPreservationResultV2{StructSize=192,ApiVersion=2,ProofKind=1,ProofFlags=1};
        var result=NativeMethods.TranscodeVideoV4(context.Handle,input,output,targetContainer:2,targetCodec:defect=="non-copy"?2:1,crf:23,in request,ref diagnostic,ref proof);
        if(defect=="valid")
        {
            Assert.Equal(NativeResult.Ok,result);Assert.Equal(1U,proof.ProofKind);Assert.Equal(1U,proof.ProofFlags);
            Assert.Equal((ulong)b.Length,proof.SourceLength);Assert.Equal((ulong)new FileInfo(output).Length,proof.OutputLength);
            Assert.Equal(Hash(b),ProofHash(proof,true));Assert.Equal(Hash(File.ReadAllBytes(output)),ProofHash(proof,false));
            Assert.Equal(2,Directory.GetFiles(folder.Path).Length);
        }
        else
        {
            Assert.NotEqual(NativeResult.Ok,result);Assert.Equal(0U,proof.ProofKind);Assert.Equal(0U,proof.ProofFlags);Assert.Equal(0U,proof.Metadata.Status);
            var error=Record.Exception(()=>context.ThrowIfFailed(result));Assert.NotNull(error);Assert.Contains("movie static metadata",error.Message);
            Assert.False(File.Exists(output));Assert.Equal(input,Assert.Single(Directory.GetFiles(folder.Path)));
        }
        Assert.Equal(b,File.ReadAllBytes(input));Assert.Equal(sourceHash,Hash(File.ReadAllBytes(original)));
    }
    private static string Hash(byte[] b)=>Convert.ToHexString(SHA256.HashData(b));
    private static unsafe string ProofHash(NativeVideoPreservationResultV2 proof,bool source){byte* b=source?proof.SourceFileSha256:proof.OutputFileSha256;return Convert.ToHexString(new ReadOnlySpan<byte>(b,32));}
    private sealed record Box(int Body,int End,string Kind);
    private static Box One(IReadOnlyList<Box> rows,string kind)=>Assert.Single(rows,x=>x.Kind==kind);
    private static List<Box> Children(byte[] b,int at,int end)
    {
        var rows=new List<Box>();while(at<end){int n=checked((int)BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at))),header=8;if(n==1){n=checked((int)BinaryPrimitives.ReadUInt64BigEndian(b.AsSpan(at+8)));header=16;}Assert.InRange(n,header,end-at);rows.Add(new(at+header,at+n,System.Text.Encoding.ASCII.GetString(b,at+4,4)));at+=n;}Assert.Equal(end,at);return rows;
    }
}
