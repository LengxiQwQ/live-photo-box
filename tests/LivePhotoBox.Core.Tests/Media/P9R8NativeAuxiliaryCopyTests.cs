using System.Buffers.Binary;
using System.Security.Cryptography;
using LivePhotoBox.Interop;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8NativeAuxiliaryCopyTests
{
    [Theory]
    [InlineData("codec")]
    [InlineData("width")]
    [InlineData("height")]
    [InlineData("matrix")]
    [InlineData("timebase")]
    [InlineData("media-flags")]
    [InlineData("table-flags")]
    [Trait("Category","DerivedRealSamples")]
    public void PublicNativeCopyRejectsMalformedAuxiliarySourceWithoutSuccessProofOrOutput(string defect)
    {
        string original=Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples/苹果-双文件.MOV"));
        Assert.True(File.Exists(original));
        byte[] bytes=File.ReadAllBytes(original);string originalHash=Convert.ToHexString(SHA256.HashData(bytes));
        var movie=One(Children(bytes,0,bytes.Length),"moov");
        Box auxiliary=Children(bytes,movie.Body,movie.End).Where(x=>x.Kind=="trak").First(x=>
        {
            var md=One(Children(bytes,x.Body,x.End),"mdia");
            var hd=One(Children(bytes,md.Body,md.End),"hdlr");
            return bytes.AsSpan(hd.Body+8,4).SequenceEqual("auxv"u8);
        });
        var track=Children(bytes,auxiliary.Body,auxiliary.End);var mdia=One(track,"mdia");var media=Children(bytes,mdia.Body,mdia.End);
        var minf=One(media,"minf");var stbl=One(Children(bytes,minf.Body,minf.End),"stbl");var table=Children(bytes,stbl.Body,stbl.End);
        var stsd=One(table,"stsd");var entry=Assert.Single(Children(bytes,stsd.Body+8,stsd.End));
        var tkhd=One(track,"tkhd");var mdhd=One(media,"mdhd");
        switch(defect)
        {
            case "codec":bytes[One(Children(bytes,entry.Body+78,entry.End-4),"hvcC").Body]=0;break;
            case "width":bytes[entry.Body+24]=bytes[entry.Body+25]=0;break;
            case "height":bytes[entry.Body+26]=bytes[entry.Body+27]=0;break;
            case "matrix":BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(tkhd.Body+(bytes[tkhd.Body]==0?40:52)),123);break;
            case "timebase":BinaryPrimitives.WriteUInt32BigEndian(bytes.AsSpan(mdhd.Body+(bytes[mdhd.Body]==0?12:20)),0);break;
            case "media-flags":bytes[mdhd.Body+1]=1;break;
            case "table-flags":bytes[One(table,"stts").Body+1]=1;break;
            default:throw new ArgumentOutOfRangeException(nameof(defect));
        }
        using var folder=new P9TargetTestInputs.Folder(nameof(PublicNativeCopyRejectsMalformedAuxiliarySourceWithoutSuccessProofOrOutput)+defect);
        string input=Path.Combine(folder.Path,"derived.mov"),output=Path.Combine(folder.Path,"must-not-publish.mp4");File.WriteAllBytes(input,bytes);
        using var context=NativeContext.Create();
        var request=new NativeVideoPreservationRequestV1{StructSize=16,ApiVersion=1,Policy=1};
        var diagnostics=new NativeVideoBackendDiagnostics{StructSize=208};
        var proof=new NativeVideoPreservationResultV2{StructSize=192,ApiVersion=2,ProofKind=1,ProofFlags=1};
        var result=NativeMethods.TranscodeVideoV4(context.Handle,input,output,1,1,23,in request,ref diagnostics,ref proof);
        Assert.NotEqual(NativeResult.Ok,result);Assert.Equal(0U,proof.ProofKind);Assert.Equal(0U,proof.ProofFlags);Assert.Equal(0U,proof.Metadata.Status);
        Assert.False(File.Exists(output));Assert.Equal(input,Assert.Single(Directory.GetFiles(folder.Path)));
        Assert.Equal(bytes,File.ReadAllBytes(input));Assert.Equal(originalHash,Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(original))));
    }

    private sealed record Box(int Body,int End,string Kind);
    private static Box One(IReadOnlyList<Box> rows,string kind)=>Assert.Single(rows,x=>x.Kind==kind);
    private static IReadOnlyList<Box> Children(byte[] bytes,int start,int end)
    {
        var rows=new List<Box>();
        while(start<end)
        {
            Assert.InRange(end-start,8,int.MaxValue);uint size=BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(start));int header=8;
            if(size==1){size=checked((uint)BinaryPrimitives.ReadUInt64BigEndian(bytes.AsSpan(start+8)));header=16;}
            Assert.InRange(size,(uint)header,(uint)(end-start));int next=checked(start+(int)size);
            rows.Add(new(start+header,next,System.Text.Encoding.ASCII.GetString(bytes,start+4,4)));start=next;
        }
        Assert.Equal(end,start);return rows;
    }
}
