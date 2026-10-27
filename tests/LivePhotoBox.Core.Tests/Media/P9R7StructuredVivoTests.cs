using System.Text;
using System.Buffers.Binary;
using System.Text.Json;
using System.Xml.Linq;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9R7StructuredVivoTests
{
    [Theory]
    [InlineData(0L)]
    [InlineData(599999L)]
    [InlineData(null)]
    public async Task ExperimentalSdrHasRawCommentFreshValidationAndNoInventedCameraHistory(long? cover)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(ExperimentalSdrHasRawCommentFreshValidationAndNoInventedCameraHistory) + cover);
        var bundle = P9TargetTestInputs.Bundle(folder.Path, cover);
        var profile = OutputProfile.Create(TargetProtocol.VivoLivePhoto, OutputMediaFormat.JpegMp4);
        string final = Path.Combine(folder.Path, "target.jpg");
        await StructuredTargetWriterService.WriteAsync(bundle, profile, final, folder.Path, false, CancellationToken.None);
        var expected = new TargetValidationExpectations(4, 0, 2, cover) { UseV2 = true };
        using (var reader = File.OpenRead(final))
            NativeTargetValidator.RequirePass(NativeTargetValidator.Observe(reader, expected, CancellationToken.None), expected);
        string bytes = Encoding.UTF8.GetString(File.ReadAllBytes(final));
        Assert.Contains("VMotionPhotoVersion", bytes);
        Assert.Contains("multi-frame: 1;\nispap:1;\nmodule: photo;\0", bytes);
        Assert.DoesNotContain("VMotionPhotoSource", bytes);
        Assert.DoesNotContain("VMediaKitVersion", bytes);
        Assert.DoesNotContain("papproctime", bytes);
        Assert.DoesNotContain("GainMap", bytes);
        string evidence = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R7_OUTPUTS")
            ?? throw new InvalidOperationException("R7 evidence output is required.");
        Directory.CreateDirectory(evidence);
        File.Copy(final, Path.Combine(evidence, "synthetic-sdr-" + (cover?.ToString() ?? "unknown") + ".jpg"), true);

        if(cover==0) {
            byte[] original=File.ReadAllBytes(final);var comment=CommentRange(original);
            int line=Array.IndexOf(original,(byte)'\n',comment.At,comment.Length);
            var cases=new (string Name,Action<byte[]> Change,uint Rule)[] {
                ("comment-ascii-prefix",b=>"ASCII\0\0\0"u8.CopyTo(b.AsSpan(comment.At)),162),
                ("comment-crlf",b=>b[line]=(byte)'\r',162),
                ("comment-no-terminator",b=>b[comment.At+comment.Length-1]=(byte)' ',162),
                ("comment-invalid-utf8",b=>b[comment.At]=255,162),
                ("comment-wrong-key",b=>b[comment.At]=(byte)'X',163),
                ("comment-wrong-flag",b=>b[comment.At+13]=(byte)'0',163)
            };
            var results=new List<object>();
            foreach(var item in cases) {
                byte[] changed=original.ToArray();item.Change(changed);
                string path=Path.Combine(evidence,item.Name+".jpg");File.WriteAllBytes(path,changed);
                using var reader=File.OpenRead(path);var rejected=NativeTargetValidator.Observe(reader,expected,CancellationToken.None);
                Assert.False(rejected.CanPublish);Assert.Equal(item.Rule,rejected.Observed.RuleId);
                results.Add(new{item.Name,rejected.InvocationResult,rejected.Observed.RuleId,rejected.Observed.FailureStage});
            }
            File.WriteAllText(Path.Combine(evidence,"comment-negatives.json"),JsonSerializer.Serialize(results,new JsonSerializerOptions{WriteIndented=true}));
        }

        // A generic V2 claim cannot silently approve a private vivo owner.
        using (var reader = File.OpenRead(final))
            Assert.False(NativeTargetValidator.Observe(reader, expected with { TargetProfile = 2 }, CancellationToken.None).CanPublish);
    }
    [Theory]
    [InlineData("version",160u)] [InlineData("source",160u)]
    [InlineData("mediakit-empty",107u)] [InlineData("flags-empty",107u)]
    [InlineData("primary-length",161u)] [InlineData("primary-padding",161u)]
    [InlineData("video-padding-absent",161u)] [InlineData("wrong-mime",119u)]
    [InlineData("cover",120u)] [InlineData("duplicate-version",107u)]
    public async Task FinalArtifactXmlCannotBypassIndependentVivoRules(string mutation,uint rule) {
        using var folder=new P9TargetTestInputs.Folder(nameof(FinalArtifactXmlCannotBypassIndependentVivoRules)+mutation);
        var bundle=P9TargetTestInputs.Bundle(folder.Path);string final=Path.Combine(folder.Path,"target.jpg");
        await StructuredTargetWriterService.WriteAsync(bundle,OutputProfile.Create(TargetProtocol.VivoLivePhoto,OutputMediaFormat.JpegMp4),final,folder.Path,false,CancellationToken.None);
        byte[] original=File.ReadAllBytes(final),changed=RewritePacket(original,doc=> {
            XNamespace vivo="http://ns.vivo.com/photos/1.0/camera/",item="http://ns.google.com/photos/1.0/container/item/",container="http://ns.google.com/photos/1.0/container/",camera="http://ns.google.com/photos/1.0/camera/";
            var version=doc.Descendants(vivo+"VMotionPhotoVersion").Single();
            var assets=doc.Descendants(container+"Item").ToArray();
            switch(mutation) {
                case "version":version.Value="2";break;
                case "source":version.Parent!.Add(new XElement(vivo+"VMotionPhotoSource","1"));break;
                case "mediakit-empty":version.Parent!.Add(new XElement(vivo+"VMediaKitVersion",""));break;
                case "flags-empty":version.Parent!.Add(new XElement(vivo+"VMotionPhotoFlags",""));break;
                case "primary-length":assets[0].SetAttributeValue(item+"Length","0");break;
                case "primary-padding":assets[0].SetAttributeValue(item+"Padding","0");break;
                case "video-padding-absent":assets[^1].Attribute(item+"Padding")!.Remove();break;
                case "wrong-mime":assets[^1].SetAttributeValue(item+"Mime","video/quicktime");break;
                case "cover":doc.Descendants(camera+"MotionPhotoPresentationTimestampUs").Single().Value="1";break;
                case "duplicate-version":version.AddAfterSelf(new XElement(version));break;
            }
        });
        File.WriteAllBytes(final,changed);var expected=new TargetValidationExpectations(4,0,2,0){UseV2=true};
        using var reader=File.OpenRead(final);var observed=NativeTargetValidator.Observe(reader,expected,CancellationToken.None);
        Assert.False(observed.CanPublish);Assert.Equal(rule,observed.Observed.RuleId);
        string root=Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P9_R7_OUTPUTS")??throw new InvalidOperationException("R7 output required");
        string negative=Path.Combine(root,"xml-negatives");Directory.CreateDirectory(negative);File.Copy(final,Path.Combine(negative,mutation+".jpg"),true);
        File.WriteAllText(Path.Combine(negative,mutation+".json"),JsonSerializer.Serialize(new{mutation,rule,observed.InvocationResult,observed.Observed.RuleId,observed.Observed.FailureStage}));
    }

    private static byte[] RewritePacket(byte[] bytes,Action<XDocument> change) {
        for(int at=2;at<bytes.Length;) {
            int marker=bytes[at+1];if(marker==0xda)break;int n=BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(at+2));
            byte[] prefix="http://ns.adobe.com/xap/1.0/\0"u8.ToArray();
            if(marker==0xe1&&bytes.AsSpan(at+4,prefix.Length).SequenceEqual(prefix)) {
                var document=XDocument.Parse(Encoding.UTF8.GetString(bytes,at+4+prefix.Length,n-2-prefix.Length));change(document);
                byte[] payload=prefix.Concat(Encoding.UTF8.GetBytes(document.ToString(SaveOptions.DisableFormatting))).ToArray();
                Assert.True(payload.Length<=65533);int size=payload.Length+2;
                return bytes.AsSpan(0,at).ToArray().Concat(new byte[]{255,225,(byte)(size>>8),(byte)size}).Concat(payload).Concat(bytes.AsSpan(at+n+2).ToArray()).ToArray();
            }
            at+=n+2;
        }
        throw new InvalidDataException("No structured standard packet");
    }

    [Theory]
    [InlineData("source-share-lock")]
    [InlineData("cancelled")]
    [InlineData("output-conflict")]
    public async Task RealFailureConditionsLeaveInputsAndForeignOutputIntact(string condition) {
        using var folder=new P9TargetTestInputs.Folder(nameof(RealFailureConditionsLeaveInputsAndForeignOutputIntact)+condition);
        var bundle=P9TargetTestInputs.Bundle(folder.Path);string final=Path.Combine(folder.Path,"foreign.jpg");
        byte[] foreign="foreign-owner-must-stay"u8.ToArray();
        if(condition=="output-conflict")File.WriteAllBytes(final,foreign);
        using var cancellation=new CancellationTokenSource();if(condition=="cancelled")cancellation.Cancel();
        FileStream? lease=condition=="source-share-lock"?new FileStream(bundle.PrimaryImage.Path,FileMode.Open,FileAccess.ReadWrite,FileShare.None):null;
        try {
            Task Write()=>StructuredTargetWriterService.WriteAsync(bundle,OutputProfile.Create(TargetProtocol.VivoLivePhoto,OutputMediaFormat.JpegMp4),final,folder.Path,false,cancellation.Token);
            if(condition=="cancelled")await Assert.ThrowsAnyAsync<OperationCanceledException>(Write);
            else await Assert.ThrowsAnyAsync<IOException>(Write);
        }finally{lease?.Dispose();}
        Assert.Equal(P9TargetTestInputs.Image,File.ReadAllBytes(bundle.PrimaryImage.Path));
        Assert.Equal(P9TargetTestInputs.Video,File.ReadAllBytes(bundle.MotionVideo!.Path));
        if(condition=="output-conflict")Assert.Equal(foreign,File.ReadAllBytes(final));else Assert.False(File.Exists(final));
        Assert.Empty(Directory.EnumerateFiles(folder.Path,"*.stage",SearchOption.AllDirectories));
    }

    private static (int At,int Length) CommentRange(byte[] data) {
        for(int marker=2;marker<data.Length;) {
            Assert.Equal(255,data[marker]);int code=data[marker+1];if(code==0xda)break;
            int length=BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(marker+2));int start=marker+4;
            if(code==0xe1&&data.AsSpan(start,6).SequenceEqual("Exif\0\0"u8)) {
                int tiff=start+6;bool big=data[tiff]==(byte)'M';
                uint U32(int p)=>big?BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p)):BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(p));
                ushort U16(int p)=>big?BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p)):BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(p));
                int directory=tiff+checked((int)U32(tiff+4));int exif=0;
                for(int i=0;i<U16(directory);++i){int p=directory+2+12*i;if(U16(p)==0x8769)exif=tiff+checked((int)U32(p+8));}
                Assert.True(exif>tiff);
                for(int i=0;i<U16(exif);++i){int p=exif+2+12*i;if(U16(p)==0x9286){int n=checked((int)U32(p+4));return(n<=4?p+8:tiff+checked((int)U32(p+8)),n);}}
            }
            marker+=2+length;
        }
        throw new InvalidDataException("No unique structured UserComment was found.");
    }

}
