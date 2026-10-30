using System.Buffers.Binary;
using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8NativeTimedOpaqueCopyTests
{
    [Theory]
    [InlineData("valid")]
    [InlineData("non-copy")]
    [InlineData("first-mime")]
    [InlineData("second-mime")]
    [InlineData("description-count")]
    [InlineData("external-reference")]
    [InlineData("media-flags")]
    [InlineData("timebase")]
    [InlineData("duration")]
    [InlineData("null-header-flags")]
    [InlineData("track-matrix")]
    [InlineData("track-size")]
    [InlineData("implicit-mett")]
    [InlineData("implicit-mdia")]
    [InlineData("implicit-minf")]
    [InlineData("implicit-stbl")]
    [InlineData("implicit-co64")]
    [InlineData("implicit-dinf")]
    [InlineData("implicit-dref")]
    [InlineData("implicit-url")]
    [InlineData("implicit-nmhd-last")]
    [InlineData("table-flags")]
    [InlineData("chunk-outside-owner")]
    [Trait("Category","RealSamplesAndDerived")]
    public async Task OpaqueTimedMetadataRequiresActualCopyAndACompleteLocalGraph(string defect)
    {
        string original=Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples/vivo.jpg"));
        string sourceHash=Hash(File.ReadAllBytes(original));using var workspace=new MediaWorkspace();
        var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(original,null,workspace);
        byte[] b=File.ReadAllBytes(Assert.IsType<MediaArtifact>(bundle.MotionVideo).Path);
        var movie=One(Children(b,0,b.Length),"moov");
        var track=Assert.Single(Children(b,movie.Body,movie.End).Where(x=>x.Kind=="trak"),x=>
        {
            var md=One(Children(b,x.Body,x.End),"mdia");var hd=One(Children(b,md.Body,md.End),"hdlr");return b.AsSpan(hd.Body+8,4).SequenceEqual("meta"u8);
        });
        var tc=Children(b,track.Body,track.End);var tkhd=One(tc,"tkhd");var mdia=One(tc,"mdia");var media=Children(b,mdia.Body,mdia.End);var mdhd=One(media,"mdhd");
        var minf=One(media,"minf");var mc=Children(b,minf.Body,minf.End);var nmhd=One(mc,"nmhd");var dinf=One(mc,"dinf");var dref=One(Children(b,dinf.Body,dinf.End),"dref");var url=Assert.Single(Children(b,dref.Body+8,dref.End));
        var stbl=One(mc,"stbl");var tables=Children(b,stbl.Body,stbl.End);var stsd=One(tables,"stsd");var entry=One(Children(b,stsd.Body+8,stsd.End),"mett");
        int mimeLength="application/livePhoto-meta".Length+1;
        switch(defect)
        {
            case "valid":case "non-copy":break;
            case "first-mime":b[entry.Body]^=1;break;
            case "second-mime":b[entry.Body+mimeLength]^=1;break;
            case "description-count":BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(stsd.Body+4),2);break;
            case "external-reference":b[url.Body+3]=0;break;
            case "media-flags":b[mdhd.Body+1]=1;break;
            case "timebase":BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(mdhd.Body+(b[mdhd.Body]==0?12:20)),0);break;
            case "duration":b.AsSpan(mdhd.Body+(b[mdhd.Body]==0?16:24),b[mdhd.Body]==0?4:8).Clear();break;
            case "null-header-flags":b[nmhd.Body+1]=1;break;
            case "track-matrix":BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(tkhd.Body+(b[tkhd.Body]==0?40:52)),123);break;
            case "track-size":BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(tkhd.End-8),65536);break;
            case "implicit-mett":b.AsSpan(entry.Body-8,4).Clear();break;
            case "implicit-mdia":b.AsSpan(mdia.Body-8,4).Clear();break;
            case "implicit-minf":b.AsSpan(minf.Body-8,4).Clear();break;
            case "implicit-stbl":b.AsSpan(stbl.Body-8,4).Clear();break;
            case "implicit-co64":b.AsSpan(One(tables,"co64").Body-8,4).Clear();break;
            case "implicit-dinf":b.AsSpan(dinf.Body-8,4).Clear();break;
            case "implicit-dref":b.AsSpan(dref.Body-8,4).Clear();break;
            case "implicit-url":b.AsSpan(url.Body-8,4).Clear();break;
            case "implicit-nmhd-last":
                byte[] reordered=mc.Where(x=>x!=nmhd).SelectMany(x=>b[(x.Body-8)..x.End]).Concat(b[(nmhd.Body-8)..nmhd.End]).ToArray();
                Assert.Equal(minf.End-minf.Body,reordered.Length);reordered.CopyTo(b,minf.Body);b.AsSpan(minf.End-12,4).Clear();break;
            case "table-flags":b[One(tables,"stts").Body+1]=1;break;
            case "chunk-outside-owner":BinaryPrimitives.WriteUInt64BigEndian(b.AsSpan(One(tables,"co64").Body+8),ulong.MaxValue);break;
            default:throw new ArgumentOutOfRangeException(nameof(defect));
        }
        using var folder=new P9TargetTestInputs.Folder(nameof(OpaqueTimedMetadataRequiresActualCopyAndACompleteLocalGraph)+defect);
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
