using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R8BoundImageFailureTests
{
    [Theory]
    [InlineData("request-size")][InlineData("request-version")][InlineData("quality")]
    [InlineData("policy")][InlineData("reserved0")][InlineData("reserved1")]
    [InlineData("identity")][InlineData("hash")][InlineData("source-container")][InlineData("target-container")]
    [InlineData("proof-version")][InlineData("proof-small")][InlineData("proof-large")]
    [InlineData("alias")][InlineData("existing")][InlineData("replacement")][InlineData("cancel")]
    [InlineData("apple-binding")][InlineData("other-subject")][InlineData("anonymous-subject")]
    [InlineData("nested-subject")][InlineData("nested-rdf")][InlineData("duplicate-rdf")]
    [InlineData("thumbnail-direction")]
    [InlineData("hdr")][InlineData("compound")]
    [InlineData("subject-location")][InlineData("crop-recipe")]
    [Trait("Category","RealSamplesAndDerived")]
    public async Task ActualImageApiRejectsUnboundOrAmbiguousInputsWithoutProofOrOutput(string defect)
    {
        using var workspace=new MediaWorkspace();
        string name=defect=="thumbnail-direction"?"vivo双文件.jpg":defect=="hdr"?"一加.jpg":defect=="compound"?"荣耀.jpg":"oppo.jpg";
        string original=Cached(name),originalHash=Hash(File.ReadAllBytes(original));
        var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(original,defect=="thumbnail-direction"?Cached("vivo双文件.mp4"):null,workspace);
        using var folder=new P9TargetTestInputs.Folder(nameof(ActualImageApiRejectsUnboundOrAmbiguousInputsWithoutProofOrOutput)+defect);
        string input=Path.Combine(folder.Path,"source.jpg"),output=Path.Combine(folder.Path,"output.heic");
        byte[] source=File.ReadAllBytes(defect=="apple-binding"?Cached("苹果-双文件.JPG"):bundle.PrimaryImage.Path);
        if(defect.EndsWith("subject",StringComparison.Ordinal)||defect.EndsWith("rdf",StringComparison.Ordinal)||defect=="crop-recipe") {
            source=SetOrientation(source,6);
            const string body=" xmlns:t='http://ns.adobe.com/tiff/1.0/' t:Orientation='6'";
            string description=defect switch {
                "other-subject"=>"<r:Description r:about='urn:other'"+body+"/>",
                "anonymous-subject"=>"<r:Description"+body+"/>",
                "nested-subject"=>"<r:Description r:about=''><o:Nested xmlns:o='urn:ordinary'><r:Description r:about=''"+body+"/></o:Nested></r:Description>",
                "nested-rdf"=>"<r:Description r:about=''><o:Nested xmlns:o='urn:ordinary'><r:RDF><r:Description r:about=''"+body+"/></r:RDF></o:Nested></r:Description>",
                "duplicate-rdf"=>"<r:Description r:about=''"+body+"/>",
                "crop-recipe"=>"<r:Description r:about='' xmlns:c='http://ns.adobe.com/camera-raw-settings/1.0/' c:CropTop='0.1'/>",
                _=>throw new ArgumentException(defect)};
            string xml="<x:xmpmeta xmlns:x='adobe:ns:meta/'><r:RDF xmlns:r='http://www.w3.org/1999/02/22-rdf-syntax-ns#'>"+description+"</r:RDF>"+
                (defect=="duplicate-rdf"?"<r:RDF xmlns:r='http://www.w3.org/1999/02/22-rdf-syntax-ns#'><r:Description r:about=''/></r:RDF>":"")+"</x:xmpmeta>";
            source=ReplaceXmp(source,xml);
        }
        if(defect=="thumbnail-direction")source=WithThumbnailOrientation(source,6);
        if(defect=="subject-location")source=WithSubjectLocation(SetOrientation(source,6));
        File.WriteAllBytes(input,source);var request=Request(input);
        if(defect=="request-size")request.StructSize--;
        if(defect=="request-version")request.ApiVersion=1;
        if(defect=="quality")request.Quality=0;
        if(defect=="policy")request.OrientationPolicy=0;
        if(defect=="reserved0")request.Reserved0=1;
        if(defect=="reserved1")request.Reserved1=1;
        if(defect=="identity")request.ExpectedIdentity.FileIndex^=1;
        if(defect=="hash")CorruptHash(ref request);
        if(defect=="source-container")request.SourceContainer=2;
        if(defect=="target-container")request.TargetContainer=0;
        if(defect=="alias")output=input;
        byte[] collision=Encoding.ASCII.GetBytes("existing destination must remain unchanged");
        if(defect=="existing")File.WriteAllBytes(output,collision);
        if(defect=="replacement") {
            string replacement=Path.Combine(folder.Path,"replacement.jpg");File.WriteAllBytes(replacement,source);
            File.Move(input,Path.Combine(folder.Path,"old-object.jpg"));File.Move(replacement,input);
        }
        using var cancel=new CancellationTokenSource();using var context=NativeContext.Create(cancel.Token);
        if(defect=="cancel")cancel.Cancel();
        var proof=new NativeImageConversionProofV1{StructSize=defect=="proof-small"?239U:defect=="proof-large"?241U:240U,
            ApiVersion=defect=="proof-version"?2U:1U,ProofKind=1,ProofFlags=1,MetadataStatus=1,Operation=3};
        var result=NativeMethods.ConvertImageV2(context.Handle,input,output,in request,ref proof);
        Assert.NotEqual(NativeResult.Ok,result);Assert.Equal(0U,proof.ProofKind);Assert.Equal(0U,proof.ProofFlags);Assert.Equal(0U,proof.MetadataStatus);
        Assert.Equal(0UL,proof.SourceLength);Assert.Equal(0UL,proof.OutputLength);Assert.Equal(0U,proof.OrientationStatus);
        if(defect=="identity"||defect=="hash"||defect=="replacement")Assert.Equal(NativeResult.SourceChanged,result);
        if(defect=="cancel")Assert.Equal(NativeResult.Cancelled,result);
        if(defect=="existing")Assert.Equal(collision,File.ReadAllBytes(output));
        else if(defect!="alias")Assert.False(File.Exists(output));
        Assert.Equal(source,File.ReadAllBytes(input));Assert.Equal(originalHash,Hash(File.ReadAllBytes(original)));
        Assert.Equal(defect is "existing" or "replacement"?2:1,Directory.GetFiles(folder.Path).Length);
    }

    [Theory]
    [InlineData(2)][InlineData(3)][InlineData(4)][InlineData(5)][InlineData(6)][InlineData(7)][InlineData(8)]
    [Trait("Category","DerivedRealSamples")]
    public async Task ActualImageApiPhysicallyNormalizesEveryKnownExifDirection(int orientation)
    {
        using var workspace=new MediaWorkspace();var bundle=await new NeutralMediaService().CreateNeutralBundleAsync(Cached("oppo.jpg"),null,workspace);
        using var folder=new P9TargetTestInputs.Folder(nameof(ActualImageApiPhysicallyNormalizesEveryKnownExifDirection)+orientation);
        string input=Path.Combine(folder.Path,"source.jpg"),output=Path.Combine(folder.Path,"output.heic");
        byte[] source=SetOrientation(File.ReadAllBytes(bundle.PrimaryImage.Path),checked((ushort)orientation));File.WriteAllBytes(input,source);
        var request=Request(input);using var context=NativeContext.Create();var proof=new NativeImageConversionProofV1{StructSize=240,ApiVersion=1};
        var result=NativeMethods.ConvertImageV2(context.Handle,input,output,in request,ref proof);context.ThrowIfFailed(result);
        Assert.Equal(1U,proof.ProofKind);Assert.Equal((uint)orientation,proof.SourceOrientation);Assert.Equal(1U,proof.OutputOrientation);Assert.Equal(3U,proof.Operation);
        Assert.Equal(orientation>=5?proof.SourceHeight:proof.SourceWidth,proof.OutputWidth);Assert.Equal(orientation>=5?proof.SourceWidth:proof.SourceHeight,proof.OutputHeight);
        var actual=await NativeMediaService.DecodeHeicPrimaryAsync(output);
        // The final-orientation ABI accounts for the HEIF item graph and Exif.
        // The older preservation TIFF projection is not that geometry contract.
        var direction=await NativeMediaService.ObserveImageOrientationAsync(output,ImageContainer.Heic);
        Assert.Equal(NativeImageOrientationStatus.Verified,direction.Status);
        Assert.Equal(0,direction.ClockwiseRotationDegrees);Assert.Equal(NativeImageOrientationReflection.None,direction.Reflection);
        Assert.Equal(NativeImageOrientationEvidence.Verified,direction.RotationEvidence);
        Assert.Equal(NativeImageOrientationEvidence.Verified,direction.ReflectionEvidence);
        Assert.Equal(proof.OutputWidth,actual.Width);Assert.Equal(proof.OutputHeight,actual.Height);
        Assert.Equal(source,File.ReadAllBytes(input));Snapshot("orientation-"+orientation,input,output,proof);
    }

    internal static string Cached(string name)=>Path.GetFullPath(Path.Combine(P9TargetTestInputs.Root,"../../../../../cache/samples",name));
    internal static unsafe NativeImageConversionRequestV2 Request(string input)
    {
        var id=WindowsFileIdentity.Capture(input);var request=new NativeImageConversionRequestV2{StructSize=96,ApiVersion=2,SourceContainer=1,TargetContainer=2,
            Quality=95,OrientationPolicy=1,ExpectedIdentity=new NativeCleanService.NativeFileIdentity{VolumeSerial=id.VolumeSerialNumber,FileIndex=id.FileIndex,FileSize=(ulong)new FileInfo(input).Length,LinkCount=id.LinkCount}};
        var hash=SHA256.HashData(File.ReadAllBytes(input));for(int i=0;i<32;i++)request.SourceSha256[i]=hash[i];return request;
    }
    private static unsafe void CorruptHash(ref NativeImageConversionRequestV2 request){fixed(byte* hash=request.SourceSha256)hash[0]^=1;}
    private static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes));
    private sealed record Segment(int Payload,int End);
    private static Segment SegmentOf(byte[] b,byte tag,byte[] prefix)
    {
        var rows=new List<Segment>();for(int at=2;at<b.Length;) {
            Assert.Equal(255,b[at++]);byte marker=b[at++];if(marker==0xda)break;
            int length=BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));Assert.InRange(length,2,b.Length-at);
            if(marker==tag&&b.AsSpan(at+2,length-2).StartsWith(prefix))rows.Add(new(at+2,at+length));at+=length;
        }
        return Assert.Single(rows);
    }
    private static byte[] ReplaceSegment(byte[] b,Segment s,byte[] data)
    {
        Assert.InRange(data.Length,0,65533);var result=b[..s.Payload].Concat(data).Concat(b[s.End..]).ToArray();
        BinaryPrimitives.WriteUInt16BigEndian(result.AsSpan(s.Payload-2),checked((ushort)(data.Length+2)));return result;
    }
    private static byte[] ReplaceXmp(byte[] b,string xml)
    {
        var head=Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");var s=SegmentOf(b,0xe1,head);
        return ReplaceSegment(b,s,head.Concat(Encoding.UTF8.GetBytes(xml)).ToArray());
    }
    private static ushort U16(byte[] b,int at,bool little)=>little?BinaryPrimitives.ReadUInt16LittleEndian(b.AsSpan(at)):BinaryPrimitives.ReadUInt16BigEndian(b.AsSpan(at));
    private static uint U32(byte[] b,int at,bool little)=>little?BinaryPrimitives.ReadUInt32LittleEndian(b.AsSpan(at)):BinaryPrimitives.ReadUInt32BigEndian(b.AsSpan(at));
    private static void W16(byte[] b,int at,ushort n,bool little){if(little)BinaryPrimitives.WriteUInt16LittleEndian(b.AsSpan(at),n);else BinaryPrimitives.WriteUInt16BigEndian(b.AsSpan(at),n);}
    private static void W32(byte[] b,int at,uint n,bool little){if(little)BinaryPrimitives.WriteUInt32LittleEndian(b.AsSpan(at),n);else BinaryPrimitives.WriteUInt32BigEndian(b.AsSpan(at),n);}
    private static byte[] SetOrientation(byte[] input,ushort orientation)
    {
        var b=(byte[])input.Clone();var s=SegmentOf(b,0xe1,"Exif\0\0"u8.ToArray());int tiff=s.Payload+6;bool little=b[tiff]==(byte)'I';int root=tiff+checked((int)U32(b,tiff+4,little)),n=U16(b,root,little);int found=0;
        for(int i=0;i<n;i++){int at=root+2+i*12;if(U16(b,at,little)==0x112){Assert.Equal(3,U16(b,at+2,little));Assert.Equal(1U,U32(b,at+4,little));W16(b,at+8,orientation,little);found++;}}
        Assert.Equal(1,found);return b;
    }
    private static byte[] WithThumbnailOrientation(byte[] b,ushort orientation)
    {
        var s=SegmentOf(b,0xe1,"Exif\0\0"u8.ToArray());byte[] exif=b[s.Payload..s.End];int tiff=6;bool little=exif[tiff]==(byte)'I';int root=tiff+checked((int)U32(exif,tiff+4,little)),count=U16(exif,root,little),next=root+2+count*12;
        int thumb=tiff+checked((int)U32(exif,next,little));Assert.True(thumb>tiff);int n=U16(exif,thumb,little);
        for(int i=0;i<n;i++){int at=thumb+2+i*12;if(U16(exif,at,little)==0x112){W16(exif,at+8,orientation,little);return ReplaceSegment(b,s,exif);}}
        int appended=exif.Length;Array.Resize(ref exif,appended+2+(n+1)*12+4);W32(exif,next,checked((uint)(appended-tiff)),little);W16(exif,appended,checked((ushort)(n+1)),little);
        b.AsSpan(s.Payload+thumb+2,n*12).CopyTo(exif.AsSpan(appended+2));int field=appended+2+n*12;
        W16(exif,field,0x112,little);W16(exif,field+2,3,little);W32(exif,field+4,1,little);W16(exif,field+8,orientation,little);
        return ReplaceSegment(b,s,exif);
    }
    private static byte[] WithSubjectLocation(byte[] b)
    {
        var s=SegmentOf(b,0xe1,"Exif\0\0"u8.ToArray());byte[] exif=b[s.Payload..s.End];int tiff=6;bool little=exif[tiff]==(byte)'I';
        int root=tiff+checked((int)U32(exif,tiff+4,little)),count=U16(exif,root,little),pointer=0;
        for(int i=0;i<count;i++){int at=root+2+i*12;if(U16(exif,at,little)==0x8769)pointer=at+8;}Assert.True(pointer>0);
        int old=tiff+checked((int)U32(exif,pointer,little)),n=U16(exif,old,little),appended=exif.Length;
        Assert.DoesNotContain(Enumerable.Range(0,n),i=>U16(exif,old+2+i*12,little)==0xa214);
        Array.Resize(ref exif,appended+2+(n+1)*12+4);W32(exif,pointer,checked((uint)(appended-tiff)),little);W16(exif,appended,checked((ushort)(n+1)),little);
        b.AsSpan(s.Payload+old+2,n*12).CopyTo(exif.AsSpan(appended+2));int field=appended+2+n*12;
        W16(exif,field,0xa214,little);W16(exif,field+2,3,little);W32(exif,field+4,2,little);W16(exif,field+8,10,little);W16(exif,field+10,20,little);
        return ReplaceSegment(b,s,exif);
    }
    internal static unsafe void Snapshot(string name,string input,string output,NativeImageConversionProofV1 proof)
    {
        if(Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_IMAGE_V2_SNAPSHOTS") is not { Length: > 0 } root)return;
        string folder=Path.Combine(root,name);Assert.False(Directory.Exists(folder));Directory.CreateDirectory(folder);
        File.Copy(input,Path.Combine(folder,"source.jpg"));File.Copy(output,Path.Combine(folder,"output.heic"));
        File.WriteAllBytes(Path.Combine(folder,"proof.bin"),MemoryMarshal.AsBytes(MemoryMarshal.CreateReadOnlySpan(ref proof,1)).ToArray());
        var native=Assert.Single(System.Diagnostics.Process.GetCurrentProcess().Modules.Cast<System.Diagnostics.ProcessModule>(),m=>m.ModuleName=="LivePhotoBox.Native.dll");
        File.WriteAllText(Path.Combine(folder,"identity.json"),System.Text.Json.JsonSerializer.Serialize(new{SourceSha256=Hash(File.ReadAllBytes(input)),OutputSha256=Hash(File.ReadAllBytes(output)),NativePath=native.FileName,NativeSha256=Hash(File.ReadAllBytes(native.FileName))}));
    }
}
