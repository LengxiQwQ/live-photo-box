using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using System.Security.Cryptography;
using System.Text;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class RepairThumbnailTests
{
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task LegacyDualJpeg_DiagnosesAndExportsWithRenamedConfirmedCompanion()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(LegacyDualJpeg_DiagnosesAndExportsWithRenamedConfirmedCompanion));
        string source = Path.Combine(folder.Path, "photo.jpg"), video = Path.Combine(folder.Path, "renamed-video.mp4");
        File.Copy(TestSampleResolver.ResolveSample("vivo双文件.jpg"), source);
        File.Copy(TestSampleResolver.ResolveSample("vivo双文件.mp4"), video);
        byte[] imageBytes = File.ReadAllBytes(source), videoBytes = File.ReadAllBytes(video);
        var diagnosis = await LivePhotoBox.Services.LivePhotoRepairService.AnalyzeFileAsync(source);
        Assert.NotEqual(LivePhotoBox.Models.RepairIssueType.Error, diagnosis.IssueType);
        Assert.NotEqual(LivePhotoBox.Models.RepairIssueType.Limited, diagnosis.IssueType);
        var repair = await LivePhotoBox.Services.LivePhotoRepairService.RepairImageFileAsync(
            source, folder.Path, true, false, new LivePhotoBox.Models.RepairOptions(), default, copyPerfect: true);
        Assert.True(repair.Status is "success" or "copied", repair.Message);
        Assert.NotNull(repair.Image);
        Assert.NotNull(repair.Video);
        Assert.Equal(videoBytes, File.ReadAllBytes(repair.Video!));
        var confirmed = await NativeMediaService.InspectMediaForDiscoveryAsync(repair.Image!, repair.Video, default);
        Assert.Equal(LivePhotoBox.Media.Models.SourceProtocol.VivoLegacyDualFile, confirmed.Facts?.Protocol);
        Assert.Equal(imageBytes, File.ReadAllBytes(source));
        Assert.Equal(videoBytes, File.ReadAllBytes(video));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task LegacyDualJpeg_MissingOrDuplicateCompanionIsNotAnImageReadFailure()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(LegacyDualJpeg_MissingOrDuplicateCompanionIsNotAnImageReadFailure));
        string source = Path.Combine(folder.Path, "photo.jpg");
        File.Copy(TestSampleResolver.ResolveSample("vivo双文件.jpg"), source);
        Assert.Equal(LivePhotoBox.Models.RepairIssueType.Limited,
            (await LivePhotoBox.Services.LivePhotoRepairService.AnalyzeFileAsync(source)).IssueType);
        File.Copy(TestSampleResolver.ResolveSample("vivo双文件.mp4"), Path.Combine(folder.Path, "first.mp4"));
        File.Copy(TestSampleResolver.ResolveSample("vivo双文件.mp4"), Path.Combine(folder.Path, "second.mp4"));
        Assert.Equal(LivePhotoBox.Models.RepairIssueType.Limited,
            (await LivePhotoBox.Services.LivePhotoRepairService.AnalyzeFileAsync(source)).IssueType);
        var repair = await LivePhotoBox.Services.LivePhotoRepairService.RepairImageFileAsync(
            source, folder.Path, true, false, new LivePhotoBox.Models.RepairOptions(), default);
        Assert.Equal("failed", repair.Status);
        Assert.Null(repair.Image);
        Assert.Null(repair.Video);
        Assert.Equal(3, Directory.GetFiles(folder.Path).Length);
    }
    [Fact]
    public async Task AppendedMedia_PreviewCleanupPreservesEveryOffsetAndTailByte()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(AppendedMedia_PreviewCleanupPreservesEveryOffsetAndTailByte));
        byte[] primary = P9TargetTestInputs.Image;
        byte[] payload = Encoding.ASCII.GetBytes("QVGA\0").Concat(primary).ToArray();
        int size = payload.Length + 2;
        byte[] tail = P9TargetTestInputs.Video;
        byte[] bytes = primary[..2].Concat(new byte[] {255,226,(byte)(size >> 8),(byte)size})
            .Concat(payload).Concat(primary[2..]).Concat(tail).ToArray();
        string source = Path.Combine(folder.Path, "source.jpg"), output = Path.Combine(folder.Path, "output.jpg");
        File.WriteAllBytes(source, bytes);
        Assert.True((await NativeMediaService.ProbeImageExifRepairAsync(source, 1, default)).Supported);
        Assert.Equal(1u, (await NativeMediaService.RepairImageExifAsync(source, output, 1, default)).Changes);
        byte[] actual = File.ReadAllBytes(output);
        byte[] expected = bytes.ToArray();
        expected[3] = 0xfe;
        Array.Clear(expected, 6, payload.Length);
        Assert.Equal(expected, actual);
        Assert.Equal(tail, actual[^tail.Length..]);
        Assert.Equal(0u, (await NativeMediaService.ProbeImageExifRepairAsync(output, 1, default)).Changes);
        Assert.Equal(bytes, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData("vivo.jpg")]
    [InlineData("小米.jpg")]
    [InlineData("OPPO.jpg")]
    [InlineData("一加.jpg")]
    [InlineData("红米老款-GV1.JPG")]
    [InlineData("华为-Mate80.jpg")]
    [InlineData("荣耀.jpg")]
    [Trait("Category", "RealSamples")]
    public async Task AllDevicesScan_DoesNotRejectCompositeJpegAsStandalone(string name)
    {
        string source = TestSampleResolver.ResolveSample(name);
        byte[] hash = SHA256.HashData(File.ReadAllBytes(source));
        var analysis = await LivePhotoBox.Services.LivePhotoRepairService.AnalyzeFileAsync(source);
        Assert.NotEqual(LivePhotoBox.Models.RepairIssueType.Error, analysis.IssueType);
        Assert.NotEqual(LivePhotoBox.Models.RepairIssueType.NonApple, analysis.IssueType);
        Assert.DoesNotContain("standalone", analysis.IssueDescription);
        Assert.Equal(hash, SHA256.HashData(File.ReadAllBytes(source)));
    }

    [Fact]
    public async Task UnsupportedPreview_IsAProbeResultRatherThanAnException()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(UnsupportedPreview_IsAProbeResultRatherThanAnException));
        byte[] primary = P9TargetTestInputs.Image;
        byte[] bytes = primary[..2].Concat(new byte[] {255,226,0,7,255,216,255,219,0}).Concat(primary[2..]).ToArray();
        string source = Path.Combine(folder.Path, "source.jpg");
        File.WriteAllBytes(source, bytes);
        var probe = await NativeMediaService.ProbeImageExifRepairAsync(source, 1, default);
        Assert.False(probe.Supported);
        Assert.NotEmpty(probe.Diagnostic!);
        var analysis = await LivePhotoBox.Services.LivePhotoRepairService.AnalyzeFileAsync(source);
        Assert.Equal(LivePhotoBox.Models.RepairIssueType.Limited, analysis.IssueType);
        Assert.Equal(bytes, File.ReadAllBytes(source));
    }
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task AppleHeif_RemovesThumbnailGraphButRetainsEveryOtherItemByte()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(AppleHeif_RemovesThumbnailGraphButRetainsEveryOtherItemByte));
        string source = Path.Combine(folder.Path, "source.heic"), output = Path.Combine(folder.Path, "output.heic");
        byte[] bytes = File.ReadAllBytes(TestSampleResolver.ResolveSample("苹果双文件.HEIC"));
        File.WriteAllBytes(source, bytes);
        var before = W3IndependentHeifParser.Parse(bytes);
        uint thumbnail = Assert.Single(before.References, r => r.Type == "thmb" && r.Targets.Contains(before.PrimaryItemId)).From;
        Assert.Equal(1u, (await NativeMediaService.RepairImageExifAsync(source, null, 1, default)).Changes);
        Assert.False(File.Exists(output));
        var result = await NativeMediaService.RepairImageExifAsync(source, output, 1, default);
        Assert.Equal(1u, result.Changes);
        byte[] repaired = File.ReadAllBytes(output);
        var after = W3IndependentHeifParser.Parse(repaired);
        Assert.Equal(before.PrimaryItemId, after.PrimaryItemId);
        Assert.Equal(bytes.Length, repaired.Length);
        Assert.DoesNotContain(after.Items, i => i.ItemId == thumbnail);
        Assert.DoesNotContain(after.References, r => r.Type == "thmb" && r.Targets.Contains(before.PrimaryItemId));
        foreach (var item in before.Items.Where(i => i.ItemId != thumbnail))
        {
            var retained = Assert.Single(after.Items, i => i.ItemId == item.ItemId);
            Assert.Equal(item.ItemType, retained.ItemType);
            Assert.Equal(item.Ranges.Count, retained.Ranges.Count);
            for (int i = 0; i < item.Ranges.Count; ++i)
            {
                var a = item.Ranges[i]; var b = retained.Ranges[i];
                Assert.Equal(a.Length, b.Length);
                Assert.Equal(bytes.AsSpan((int)a.Offset, (int)a.Length).ToArray(),
                    repaired.AsSpan((int)b.Offset, (int)b.Length).ToArray());
            }
        }
        Assert.Equal(0u, (await NativeMediaService.RepairImageExifAsync(output, null, 1, default)).Changes);
        Assert.Equal(bytes, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData("", 0xe2)]
    [InlineData("QVGA\0", 0xe2)]
    [InlineData("BGTH", 0xe2)]
    [InlineData("XMTH12345", 0xe2)]
    [InlineData("JFXX\0\u0010", 0xe0)]
    public async Task JpegPreview_IsRemovedWithoutChangingPrimaryOrOrdinaryMarkers(string prefix, int marker)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(JpegPreview_IsRemovedWithoutChangingPrimaryOrOrdinaryMarkers) + marker + Convert.ToHexString(Encoding.ASCII.GetBytes(prefix)));
        byte[] primary = P9TargetTestInputs.Image;
        byte[] payload = Encoding.ASCII.GetBytes(prefix).Concat(primary).ToArray();
        int size = payload.Length + 2;
        Assert.InRange(size, 2, 65535);
        byte[] bytes = primary[..2].Concat(new byte[] {255, (byte)marker, (byte)(size >> 8), (byte)size})
            .Concat(payload).Concat(primary[2..]).ToArray();
        string source = Path.Combine(folder.Path, "source.jpg"), output = Path.Combine(folder.Path, "output.jpg");
        File.WriteAllBytes(source, bytes);
        Assert.Equal(1u, (await NativeMediaService.RepairImageExifAsync(source, null, 1, default)).Changes);
        Assert.Equal(1u, (await NativeMediaService.RepairImageExifAsync(source, output, 1, default)).Changes);
        Assert.Equal(primary, File.ReadAllBytes(output));
        Assert.Equal(bytes, File.ReadAllBytes(source));
        Assert.Equal(0u, (await NativeMediaService.RepairImageExifAsync(output, null, 1, default)).Changes);
    }

    [Fact]
    public async Task MalformedPreview_DoesNotPublishOrAlterSource()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(MalformedPreview_DoesNotPublishOrAlterSource));
        byte[] primary = P9TargetTestInputs.Image;
        byte[] bytes = primary[..2].Concat(new byte[] {255,226,0,7,255,216,255,219,0}).Concat(primary[2..]).ToArray();
        string source = Path.Combine(folder.Path, "source.jpg"), output = Path.Combine(folder.Path, "output.jpg");
        File.WriteAllBytes(source, bytes);
        await Assert.ThrowsAsync<InvalidOperationException>(() => NativeMediaService.RepairImageExifAsync(source, output, 1, default));
        Assert.False(File.Exists(output));
        Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(File.ReadAllBytes(source)));
    }
    [Theory]
    [InlineData("auxl")]
    [InlineData("dimg")]
    public async Task HeifSharedOrAuxiliaryThumbnail_RefusesWithoutPublishing(string extraRelation)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(HeifSharedOrAuxiliaryThumbnail_RefusesWithoutPublishing) + extraRelation);
        byte[] bytes = W3HeifSyntheticBuilder.Build(
            [new(1,extraRelation == "dimg" ? "grid" : "hvc1",0,8), new(2,"hvc1",8,8)],
            [new("thmb",2,[1]), new(extraRelation, extraRelation == "auxl" ? (ushort)2 : (ushort)1,
                extraRelation == "auxl" ? [(ushort)1] : [(ushort)2])], primaryDimensions: true);
        string source = Path.Combine(folder.Path,"source.heic"), output = Path.Combine(folder.Path,"output.heic");
        File.WriteAllBytes(source,bytes);
        var failure = await Assert.ThrowsAsync<InvalidOperationException>(() => NativeMediaService.RepairImageExifAsync(source,output,1,default));
        Assert.Contains("relationship",failure.Message);
        Assert.False(File.Exists(output)); Assert.Equal(bytes,File.ReadAllBytes(source));
    }

    [Fact]
    public async Task HeifThumbnailCleanup_RetainsUnrelatedGainMapRelationshipAndPayload()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(HeifThumbnailCleanup_RetainsUnrelatedGainMapRelationshipAndPayload));
        byte[] bytes = W3HeifSyntheticBuilder.Build(
            [new(1,"hvc1",0,8),new(2,"hvc1",8,8),new(3,"hvc1",16,8,"urn:com:apple:photo:2020:aux:hdrgainmap")],
            [new("thmb",2,[1]),new("auxl",3,[1])], primaryDimensions: true);
        string source = Path.Combine(folder.Path,"source.heic"), output = Path.Combine(folder.Path,"output.heic");
        File.WriteAllBytes(source,bytes);
        var before = W3IndependentHeifParser.Parse(bytes);
        Assert.Equal(1u,(await NativeMediaService.RepairImageExifAsync(source,output,1,default)).Changes);
        byte[] repaired = File.ReadAllBytes(output);var after = W3IndependentHeifParser.Parse(repaired);
        Assert.DoesNotContain(after.Items,i=>i.ItemId==2);
        var aux = Assert.Single(after.Auxiliaries); Assert.Equal(3u,aux.ItemId); Assert.Equal(1u,aux.OwnerItemId);
        var oldItem=Assert.Single(before.Items,i=>i.ItemId==3);var item=Assert.Single(after.Items,i=>i.ItemId==3);
        var a=Assert.Single(oldItem.Ranges);var b=Assert.Single(item.Ranges);
        Assert.Equal(bytes.AsSpan((int)a.Offset,(int)a.Length).ToArray(),repaired.AsSpan((int)b.Offset,(int)b.Length).ToArray());
        Assert.Equal(bytes,File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task JfifRgbThumbnailAndUnrelatedAppSegment_AreHandledPrecisely(bool strip)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(JfifRgbThumbnailAndUnrelatedAppSegment_AreHandledPrecisely)+strip);
        byte[] primary=P9TargetTestInputs.Image;
        byte[] jfif=[255,224,0,19,74,70,73,70,0,1,1,0,0,1,0,1,1,1,11,22,33];
        byte[] unrelated=[255,226,0,10,85,78,75,78,79,87,78,0];
        // The neutral fixture may already contain JFIF; remove its existing APP0.
        int pos=2;
        while(pos<primary.Length && primary[pos]==255 && primary[pos+1]==224)
            pos+=2+(primary[pos+2]<<8)+primary[pos+3];
        byte[] bytes=primary[..2].Concat(jfif).Concat(unrelated).Concat(primary[pos..]).ToArray();
        string source=Path.Combine(folder.Path,"source.jpg"),output=Path.Combine(folder.Path,"output.jpg");File.WriteAllBytes(source,bytes);
        uint actions=strip?1u:2u;var result=await NativeMediaService.RepairImageExifAsync(source,output,actions,default);
        Assert.Equal(strip?1u:0u,result.Changes);
        if(strip){byte[] expected=bytes[..20].Concat(bytes[23..]).ToArray();expected[5]=16;expected[18]=expected[19]=0;Assert.Equal(expected,File.ReadAllBytes(output));}
        else Assert.False(File.Exists(output));
        Assert.Equal(bytes,File.ReadAllBytes(source));
    }

}
