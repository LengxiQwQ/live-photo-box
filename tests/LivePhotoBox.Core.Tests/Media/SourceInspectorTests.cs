using System;
using System.IO;
using System.Security.Cryptography;
using System.Linq;
using System.Threading.Tasks;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Services;
using LivePhotoBox.Core.Tests.Protocols;
using LivePhotoBox.Interop;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class SourceInspectorTests
{
    private static string ResolveSample(string fileName) => TestSampleResolver.ResolveSample(fileName);

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_OppoRealSample_IdentifiesProtocolAndRanges()
    {
        string sample = ResolveSample("oppo.jpg");
        string beforeSha = await ComputeSha256Async(sample);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(sample);

        Assert.Equal(SourceProtocol.OppoLivePhoto, facts.Protocol);
        Assert.NotNull(facts.PrimaryImage);
        Assert.NotNull(facts.MotionVideo);
        Assert.Equal(ImageContainer.Jpeg, facts.PrimaryImage.Container);
        Assert.Equal(VideoContainer.Mp4, facts.MotionVideo.Container);
        Assert.True(facts.PrimaryImage.ByteLength > 0);
        Assert.True(facts.MotionVideo.ByteLength > 0);
        Assert.Equal(0, facts.PrimaryImage.ByteOffset);
        Assert.True(facts.MotionVideo.ByteOffset > 0);

        string afterSha = await ComputeSha256Async(sample);
        Assert.Equal(beforeSha, afterSha);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_VivoX300RealSample_Identifies3ItemGainMapAndVideo()
    {
        string sample = ResolveSample("vivo.jpg");
        string beforeSha = await ComputeSha256Async(sample);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(sample);

        Assert.Equal(SourceProtocol.VivoLivePhoto, facts.Protocol);
        Assert.NotNull(facts.PrimaryImage);
        Assert.NotNull(facts.MotionVideo);
        Assert.NotNull(facts.GainMap);
        Assert.True(facts.GainMap.IsPresent);
        Assert.True(facts.GainMap.ByteLength > 0);
        Assert.True(facts.PrimaryImage.ByteLength > 0);
        Assert.True(facts.MotionVideo.ByteLength > 0);

        Assert.Equal(0, facts.PrimaryImage.ByteOffset);
        Assert.True(facts.GainMap.ByteOffset > 0);
        Assert.Equal(facts.GainMap.ByteOffset + facts.GainMap.ByteLength, facts.MotionVideo.ByteOffset);

        string afterSha = await ComputeSha256Async(sample);
        Assert.Equal(beforeSha, afterSha);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_VivoLegacyDualFile_IdentifiesProtocolAndSecondaryVideo()
    {
        string img = ResolveSample("vivo双文件.jpg");
        string vid = ResolveSample("vivo双文件.mp4");

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(img, vid);

        Assert.Equal(SourceProtocol.VivoLegacyDualFile, facts.Protocol);
        Assert.NotNull(facts.PrimaryImage);
        Assert.NotNull(facts.MotionVideo);
        Assert.Equal(ImageContainer.Jpeg, facts.PrimaryImage.Container);
        Assert.Equal(VideoContainer.Mp4, facts.MotionVideo.Container);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_SamsungJpegRealSample_IdentifiesTrailerVideoRange()
    {
        string sample = ResolveSample("三星.jpg");
        string beforeSha = await ComputeSha256Async(sample);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(sample);

        Assert.Equal(SourceProtocol.SamsungMotionPhotoJpeg, facts.Protocol);
        Assert.NotNull(facts.PrimaryImage);
        Assert.NotNull(facts.MotionVideo);
        Assert.Equal(ImageContainer.Jpeg, facts.PrimaryImage.Container);
        Assert.Equal(VideoContainer.Mp4, facts.MotionVideo.Container);
        Assert.True(facts.MotionVideo.ByteOffset > facts.PrimaryImage.ByteOffset);
        Assert.NotEmpty(facts.PreservationCarriers);
        Assert.Contains(facts.PreservationCarriers, carrier =>
            carrier.Kind == PreservationCarrierKind.SamsungSef &&
            carrier.StableIdentity == "samsung:sef:index-trailer" &&
            carrier.Semantic == "SamsungSEFIndexTrailer" &&
            carrier.ArtifactRole == MediaArtifactKind.PrimaryImage);
        Assert.All(facts.PreservationCarriers, carrier =>
        {
            Assert.Equal(PreservationCarrierKind.SamsungSef, carrier.Kind);
            Assert.Equal(MediaArtifactKind.PrimaryImage, carrier.ArtifactRole);
            Assert.NotEqual("GainMap", carrier.Semantic);
            Assert.False(string.IsNullOrWhiteSpace(carrier.SourceSha256));
            Assert.Equal(64, carrier.SourceSha256.Length);
        });

        string afterSha = await ComputeSha256Async(sample);
        Assert.Equal(beforeSha, afterSha);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_SamsungHeicRealSample_IdentifiesMpvdVideoRange()
    {
        string sample = ResolveSample("三星.heic");
        string beforeSha = await ComputeSha256Async(sample);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(sample);

        Assert.Equal(SourceProtocol.SamsungMotionPhotoHeic, facts.Protocol);
        Assert.NotNull(facts.PrimaryImage);
        Assert.NotNull(facts.MotionVideo);
        Assert.Equal(ImageContainer.Heic, facts.PrimaryImage.Container);
        Assert.Equal(VideoContainer.Mp4, facts.MotionVideo.Container);
        Assert.True(facts.MotionVideo.ByteOffset > 0);

        byte[] heic = await File.ReadAllBytesAsync(sample);
        AssertValidHeifAuxiliaryFacts(facts, heic);
        AuxiliaryMediaFacts samsungGainMap = Assert.Single(facts.AuxiliaryItems,
            item => item.Relationship == "urn:com:samsung:photo:2024:aux:hdrgainmap");
        Assert.Equal("GainMap", samsungGainMap.Semantic);
        Assert.Equal(AuxiliaryRepresentation.Embedded, samsungGainMap.Representation);
        Assert.Equal(AuxiliaryOwnership.Primary, samsungGainMap.Ownership);
        Assert.Equal(AuxiliaryCodec.Hevc, samsungGainMap.Codec);
        Assert.NotNull(facts.GainMap);
        Assert.Equal(samsungGainMap.ItemId, facts.GainMap.ItemId);
        Assert.True(NativeHeifBoxParser.TryLocateXmpItem(
            heic, out long xmpOffset, out long xmpLength, out string? xmpError), xmpError);
        Assert.True(xmpLength > 0);
        Assert.True(xmpOffset + xmpLength <= heic.Length);

        string afterSha = await ComputeSha256Async(sample);
        Assert.Equal(beforeSha, afterSha);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_SamsungHeicWithMalformedAuxC_FailsClosed()
    {
        byte[] bytes = await File.ReadAllBytesAsync(ResolveSample("三星.heic"));
        int auxcType = bytes.AsSpan().IndexOf("auxC"u8);
        Assert.True(auxcType >= 4 && auxcType + 8 <= bytes.Length);
        bytes[auxcType + 4] = 1; // unsupported auxC FullBox version

        using var workspace = new MediaWorkspace();
        string path = workspace.AllocateFilePath("samsung-malformed-auxc", ".heic");
        await File.WriteAllBytesAsync(path, bytes);

        SourceInspectionException error = await Assert.ThrowsAsync<SourceInspectionException>(
            () => new SourceInspector().InspectAsync(path));
        Assert.Equal(SourceInspectionFailureCategory.Malformed, error.Category);
        Assert.Equal(SourceInspectionStage.Container, error.Stage);
        Assert.Equal(NativeRuntime.SamsungHeicCapability, error.Capability);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_SamsungHeicWithUnknownPrimaryItem_FailsClosed()
    {
        byte[] bytes = await File.ReadAllBytesAsync(ResolveSample("三星.heic"));
        int pitmType = bytes.AsSpan().IndexOf("pitm"u8);
        Assert.True(pitmType >= 4 && pitmType + 10 <= bytes.Length);
        Assert.Equal(0, bytes[pitmType + 4]); // fixture uses pitm version 0
        bytes[pitmType + 8] = 0xFF;
        bytes[pitmType + 9] = 0xFF;

        using var workspace = new MediaWorkspace();
        string path = workspace.AllocateFilePath("samsung-unknown-primary", ".heic");
        await File.WriteAllBytesAsync(path, bytes);

        SourceInspectionException error = await Assert.ThrowsAsync<SourceInspectionException>(
            () => new SourceInspector().InspectAsync(path));
        Assert.Equal(SourceInspectionFailureCategory.Malformed, error.Category);
        Assert.Equal(SourceInspectionStage.Container, error.Stage);
        Assert.Equal(NativeRuntime.SamsungHeicCapability, error.Capability);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_HuaweiMate80Jpeg_IdentifiesEmbeddedMp4AndLiveTail()
    {
        string sample = ResolveSample("华为-Mate80.jpg");
        string beforeSha = await ComputeSha256Async(sample);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(sample);

        Assert.Equal(SourceProtocol.HuaweiMovingPhoto, facts.Protocol);
        Assert.NotNull(facts.PrimaryImage);
        Assert.NotNull(facts.MotionVideo);
        Assert.Equal(ImageContainer.Jpeg, facts.PrimaryImage.Container);
        Assert.Equal(VideoContainer.Mp4, facts.MotionVideo.Container);
        Assert.True(facts.MotionVideo.ByteOffset > 0);

        string afterSha = await ComputeSha256Async(sample);
        Assert.Equal(beforeSha, afterSha);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_HuaweiMate80Heic_IdentifiesEmbeddedMp4AndLiveTail()
    {
        string sample = ResolveSample("华为Mate80.heic");
        string beforeSha = await ComputeSha256Async(sample);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(sample);

        Assert.Equal(SourceProtocol.HuaweiMovingPhoto, facts.Protocol);
        Assert.NotNull(facts.PrimaryImage);
        Assert.NotNull(facts.MotionVideo);
        Assert.Equal(ImageContainer.Heic, facts.PrimaryImage.Container);
        Assert.Equal(VideoContainer.Mp4, facts.MotionVideo.Container);

        string afterSha = await ComputeSha256Async(sample);
        Assert.Equal(beforeSha, afterSha);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_HonorRealSample_IdentifiesHonorProtocol()
    {
        string sample = ResolveSample("荣耀.jpg");
        string beforeSha = await ComputeSha256Async(sample);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(sample);

        Assert.Equal(SourceProtocol.HonorMovingPhoto, facts.Protocol);
        Assert.NotNull(facts.PrimaryImage);
        Assert.NotNull(facts.MotionVideo);

        string afterSha = await ComputeSha256Async(sample);
        Assert.Equal(beforeSha, afterSha);
    }

    
    [Fact]
    public async Task Inspect_HuaweiSyntheticWithMultipleVendorMakerNotes_IdentifiesHuaweiNotAppleConflict()
    {
        // Regression: Huawei/Honor firmware writes several non-Apple 0x927C
        // MakerNote entries in ExifIFD.  The Inspector must count only
        // Apple-shaped MakerNotes and must not report an Apple CID conflict.
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("huawei_multi_makernote", ".jpg");
        SyntheticProtocolFixtures.CreateHuaweiJpegWithMultipleVendorMakerNotes(inputPath);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(inputPath);

        Assert.Equal(SourceProtocol.HuaweiMovingPhoto, facts.Protocol);
        Assert.NotNull(facts.PrimaryImage);
        Assert.NotNull(facts.MotionVideo);
        Assert.Equal(ImageContainer.Jpeg, facts.PrimaryImage.Container);
        Assert.Equal(VideoContainer.Mp4, facts.MotionVideo.Container);
    }
[Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_XiaomiRealSample_IdentifiesGoogleMotionPhotoV2()
    {
        string sample = ResolveSample("小米.jpg");
        string beforeSha = await ComputeSha256Async(sample);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(sample);

        Assert.Equal(SourceProtocol.GoogleMotionPhotoV2, facts.Protocol);
        Assert.NotNull(facts.PrimaryImage);
        Assert.NotNull(facts.MotionVideo);

        string afterSha = await ComputeSha256Async(sample);
        Assert.Equal(beforeSha, afterSha);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_RedmiMicroVideoV1_IdentifiesGoogleMicroVideoV1()
    {
        string sample = ResolveSample("红米老款-GV1.JPG");
        string beforeSha = await ComputeSha256Async(sample);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(sample);

        Assert.Equal(SourceProtocol.GoogleMicroVideoV1, facts.Protocol);
        Assert.NotNull(facts.PrimaryImage);
        Assert.NotNull(facts.MotionVideo);

        string afterSha = await ComputeSha256Async(sample);
        Assert.Equal(beforeSha, afterSha);
    }

    [Fact]
    public async Task Inspect_WrongNamespaceMotionPhotoWithUnclaimedTrailer_FailsClosed()
    {
        using var ws = new LivePhotoBox.Media.Workspace.MediaWorkspace();
        string inputPath = ws.AllocateFilePath("wrong_namespace", ".jpg");
        SyntheticProtocolFixtures.CreateWrongNamespaceMotionPhotoJpeg(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Equal(SourceInspectionFailureCategory.Ambiguous, ex.Category);
        Assert.Equal(SourceInspectionStage.Pairing, ex.Stage);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_AppleDualFileHeic_IdentifiesAppleLivePhoto()
    {
        string img = ResolveSample("苹果双文件.HEIC");
        string mov = ResolveSample("苹果双文件.MOV");

        byte[] imageBytes = await File.ReadAllBytesAsync(img);
        Assert.True(NativeHeifBoxParser.TryLocateExifItem(imageBytes, out long exifOffset, out long exifLength, out string? exifError), exifError);
        Assert.Contains("0BCBD05C-F9F4-4D99-A40D-96D3C6CA8F9C", System.Text.Encoding.ASCII.GetString(imageBytes, checked((int)exifOffset), checked((int)exifLength)));

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(img, mov);

        Assert.Equal(SourceProtocol.AppleLivePhoto, facts.Protocol);
        Assert.NotNull(facts.PrimaryImage);
        Assert.NotNull(facts.MotionVideo);
        Assert.Equal(ImageContainer.Heic, facts.PrimaryImage.Container);
        Assert.Equal(VideoContainer.Mov, facts.MotionVideo.Container);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_AppleDualFileQuickTimeWithoutFtypAndStaleChunkOffsets_FailsClosed()
    {
        string img = ResolveSample("苹果双文件.HEIC");
        string sourceMov = ResolveSample("苹果双文件.MOV");
        string tempMov = Path.Combine(Path.GetTempPath(), $"lpb-apple-no-ftyp-{Guid.NewGuid():N}.mov");

        try
        {
            byte[] mov = await File.ReadAllBytesAsync(sourceMov);
            Assert.True(mov.Length >= 20);
            Assert.Equal("ftyp", System.Text.Encoding.ASCII.GetString(mov, 4, 4));
            uint ftypSize = System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(mov.AsSpan(0, 4));
            Assert.InRange(ftypSize, 8u, (uint)mov.Length);
            await File.WriteAllBytesAsync(tempMov, mov[(int)ftypSize..]);

            var inspector = new SourceInspector();
            var exception = await Assert.ThrowsAsync<SourceInspectionException>(() =>
                inspector.InspectAsync(img, tempMov));

            Assert.Equal(SourceInspectionFailureCategory.Malformed, exception.Category);
            Assert.Equal(SourceInspectionStage.Container, exception.Stage);
        }
        finally
        {
            try { File.Delete(tempMov); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_AppleDualFileMismatch_ThrowsInvalidArgument()
    {
        string img = ResolveSample("苹果双文件.HEIC");
        string mov = ResolveSample("苹果-双文件.MOV");

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(img, mov));
        Assert.Contains("pairing identifier mismatch", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_AppleSingleHeic_PopulatesPairingIdentifierAndReportsNonLive()
    {
        string img = ResolveSample("苹果双文件.HEIC");
        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(img);

        Assert.Equal(SourceProtocol.NonLive, facts.Protocol);
        Assert.NotNull(facts.PairingIdentifier);
        Assert.Equal("0BCBD05C-F9F4-4D99-A40D-96D3C6CA8F9C", facts.PairingIdentifier);
        AssertValidHeifAuxiliaryFacts(facts, await File.ReadAllBytesAsync(img));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_AppleSingleMov_PopulatesPairingIdentifierAndReportsNonLive()
    {
        string mov = ResolveSample("苹果双文件.MOV");
        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(mov);

        Assert.Equal(SourceProtocol.NonLive, facts.Protocol);
        Assert.NotNull(facts.PairingIdentifier);
        Assert.Equal("0BCBD05C-F9F4-4D99-A40D-96D3C6CA8F9C", facts.PairingIdentifier);
    }

    [Fact]
    public async Task Inspect_MalformedXmp_UnclosedTag_ThrowsInvalidArgument()
    {
        using var ws = new LivePhotoBox.Media.Workspace.MediaWorkspace();
        string inputPath = ws.AllocateFilePath("malformed_xmp", ".jpg");
        string badXmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\"><GCamera:MotionPhoto>1<unclosed></rdf:Description></rdf:RDF></x:xmpmeta>";
        byte[] jpeg = SyntheticProtocolFixtures.CreateJpegWithXmp(badXmp);
        await File.WriteAllBytesAsync(inputPath, jpeg);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Equal(SourceInspectionFailureCategory.Ambiguous, ex.Category);
        Assert.Equal(SourceInspectionStage.Metadata, ex.Stage);
    }

    [Fact]
    public async Task Inspect_ConflictingXmpAttributes_ThrowsInvalidArgument()
    {
        using var ws = new LivePhotoBox.Media.Workspace.MediaWorkspace();
        string inputPath = ws.AllocateFilePath("conflicting_xmp", ".jpg");
        string confXmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\" GCamera:MotionPhoto=\"1\"><GCamera:MotionPhoto>0</GCamera:MotionPhoto></rdf:Description></rdf:RDF></x:xmpmeta>";
        byte[] jpeg = SyntheticProtocolFixtures.CreateJpegWithXmp(confXmp);
        await File.WriteAllBytesAsync(inputPath, jpeg);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Equal(SourceInspectionFailureCategory.Malformed, ex.Category);
        Assert.Equal(SourceInspectionStage.Protocol, ex.Stage);
        Assert.Equal(NativeRuntime.GoogleV2Capability, ex.Capability);
        Assert.Contains("Conflicting or malformed MotionPhoto attributes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_HuaweiCorruptedTrailer_ThrowsInvalidArgument()
    {
        string sample = ResolveSample("华为-Mate80.jpg");
        byte[] bytes = await File.ReadAllBytesAsync(sample);
        for (int i = bytes.Length - 15; i < bytes.Length; i++)
        {
            bytes[i] = (byte)'X';
        }

        using var ws = new LivePhotoBox.Media.Workspace.MediaWorkspace();
        string corruptPath = ws.AllocateFilePath("huawei_corrupt", ".jpg");
        await File.WriteAllBytesAsync(corruptPath, bytes);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(corruptPath));
        Assert.Contains("Huawei/Honor Moving Photo trailer is malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_SamsungCorruptedSef_ThrowsInvalidArgument()
    {
        string sample = ResolveSample("三星.jpg");
        byte[] bytes = await File.ReadAllBytesAsync(sample);
        bytes[^8] = 0xFF;
        bytes[^7] = 0xFF;
        bytes[^6] = 0xFF;
        bytes[^5] = 0x7F;

        using var ws = new LivePhotoBox.Media.Workspace.MediaWorkspace();
        string corruptPath = ws.AllocateFilePath("samsung_corrupt", ".jpg");
        await File.WriteAllBytesAsync(corruptPath, bytes);

        var inspector = new SourceInspector();
        await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(corruptPath));
    }

    [Fact]
    public async Task Inspect_TruncatedJpeg_ThrowsInvalidArgument()
    {
        using var ws = new LivePhotoBox.Media.Workspace.MediaWorkspace();
        string inputPath = ws.AllocateFilePath("truncated", ".jpg");
        await File.WriteAllBytesAsync(inputPath, [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46]);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Equal(SourceInspectionFailureCategory.Malformed, ex.Category);
        Assert.Equal(SourceInspectionStage.Container, ex.Stage);
    }

    [Fact]
    public async Task Inspect_FtypOnlyMp4_ReportsMalformedContainer()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("ftyp_only", ".mp4");
        await File.WriteAllBytesAsync(inputPath, [
            0, 0, 0, 20, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0,
            (byte)'i', (byte)'s', (byte)'o', (byte)'m']);

        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => new SourceInspector().InspectAsync(inputPath));
        Assert.Equal(SourceInspectionFailureCategory.Malformed, ex.Category);
        Assert.Equal(SourceInspectionStage.Container, ex.Stage);
    }

    [Fact]
    public async Task Inspect_TracklessMp4_ReportsMalformedContainer()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("trackless", ".mp4");
        static byte[] Box(string type, params byte[] payload) =>
            [.. BitConverter.GetBytes(System.Net.IPAddress.HostToNetworkOrder(8 + payload.Length)),
             .. System.Text.Encoding.ASCII.GetBytes(type), .. payload];
        byte[] bytes = [
            .. Box("ftyp", (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0),
            .. Box("mdat", 1, 2, 3, 4),
            .. Box("moov")];
        await File.WriteAllBytesAsync(inputPath, bytes);

        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => new SourceInspector().InspectAsync(inputPath));
        Assert.Equal(SourceInspectionFailureCategory.Malformed, ex.Category);
        Assert.Equal(SourceInspectionStage.Container, ex.Stage);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_OnePlusVendorTail_IdentifiesOffsetAndTailRange()
    {
        string sample = ResolveSample("一加.jpg");
        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(sample);

        Assert.Equal(SourceProtocol.OppoLivePhoto, facts.Protocol);
        Assert.NotNull(facts.MotionVideo);
        Assert.True(facts.ProtocolTailLength > 0);
        long fileSize = new FileInfo(sample).Length;
        Assert.Equal(fileSize, facts.MotionVideo.ByteOffset + facts.MotionVideo.ByteLength + facts.ProtocolTailLength);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_All17RealSamples_NeverMutateSha()
    {
        string[] allSampleNames = [
            "oppo.jpg",
            "vivo.jpg",
            "vivo双文件.jpg",
            "vivo双文件.mp4",
            "一加-改了封面照片.jpg",
            "一加.jpg",
            "三星.heic",
            "三星.jpg",
            "华为-Mate80.jpg",
            "华为Mate80.heic",
            "小米.jpg",
            "红米老款-GV1.JPG",
            "苹果-双文件.JPG",
            "苹果-双文件.MOV",
            "苹果双文件.HEIC",
            "苹果双文件.MOV",
            "荣耀.jpg"
        ];

        var inspector = new SourceInspector();
        foreach (var name in allSampleNames)
        {
            string path = ResolveSample(name);
            string shaBefore = await ComputeSha256Async(path);

            try
            {
                await inspector.InspectAsync(path);
            }
            catch
            {
                // Single file inspect
            }

            string shaAfter = await ComputeSha256Async(path);
            Assert.Equal(shaBefore, shaAfter);
        }
    }

    [Fact]
    public async Task Inspect_GoogleV1_MissingMicroVideo_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("v1_missing_mv", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV1JpegMissingMicroVideo(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("MicroVideo", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_GoogleV1_DisabledMicroVideo_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("v1_disabled_mv", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV1JpegDisabledMicroVideo(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("MicroVideo must be 1", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_GoogleV1_UnsupportedVersion_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("v1_bad_version", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV1JpegUnsupportedVersion(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("MicroVideo version", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_GoogleV1_ConflictingXmpDeclarations_FailClosed()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("v1_conflicting_offset", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV1JpegConflictingOffset(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Equal(SourceInspectionFailureCategory.Ambiguous, ex.Category);
        Assert.Equal(SourceInspectionStage.Metadata, ex.Stage);
        Assert.Contains("rdf:Description owner", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_GoogleV1_StaleInV2_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("v1_stale_in_v2", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV1JpegStaleInV2(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("Container:Directory", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_GoogleV2_MissingVersion_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("v2_missing_ver", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegMissingVersion(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("MotionPhotoVersion", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_GoogleV2_PrimaryNotFirst_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("v2_primary_not_first", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegPrimaryNotFirst(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("Primary", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_GoogleV2_DuplicateMotionPhoto_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("v2_dup_motion", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegDuplicateMotionPhoto(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("exactly one MotionPhoto", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_GoogleV2_CompatibleNamespaceUrisAndPrefixes_Succeeds()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("v2_namespace_compat", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegWithCompatibleNamespaces(inputPath);

        var facts = await new SourceInspector().InspectAsync(inputPath);

        Assert.Equal(SourceProtocol.GoogleMotionPhotoV2, facts.Protocol);
        Assert.NotNull(facts.MotionVideo);
    }

    [Fact]
    public async Task Inspect_GoogleV2_CrossDescriptionPropertyAndDirectory_FailsClosed()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("v2_cross_description", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegWithCrossDescriptionOwner(inputPath);

        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() =>
            new SourceInspector().InspectAsync(inputPath));

        Assert.Equal(SourceInspectionFailureCategory.Ambiguous, ex.Category);
        Assert.Equal(SourceInspectionStage.Metadata, ex.Stage);
    }

    [Fact]
    public async Task Inspect_DuplicateStandardXmpPacket_FailsClosed()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("duplicate_standard_xmp", ".jpg");
        const string xmp = "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\"><rdf:Description xmlns:GCamera=\"http://ns.google.com/photos/1.0/camera/\" GCamera:MotionPhoto=\"1\" GCamera:MotionPhotoVersion=\"1\" /></rdf:RDF></x:xmpmeta>";
        byte[] one = SyntheticProtocolFixtures.CreateJpegWithXmp(xmp);
        byte[] app1 = ExtractFirstApp1(one);
        int app1Offset = FindFirstApp1(one);
        byte[] duplicate = [.. one.AsSpan(0, app1Offset), .. app1, .. app1,
            .. one.AsSpan(app1Offset + app1.Length)];
        await File.WriteAllBytesAsync(inputPath, duplicate);

        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => new SourceInspector().InspectAsync(inputPath));
        Assert.Equal(SourceInspectionFailureCategory.Ambiguous, ex.Category);
        Assert.Equal(SourceInspectionStage.Metadata, ex.Stage);
    }

    [Fact]
    public async Task Inspect_MultipleContainerItemsUnderOneLi_FailsClosed()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("multiple_items_same_li", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegMultipleItemsInSameLi(inputPath);

        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => new SourceInspector().InspectAsync(inputPath));
        Assert.Equal(SourceInspectionFailureCategory.Malformed, ex.Category);
        Assert.Equal(SourceInspectionStage.Protocol, ex.Stage);
    }

    [Fact]
    public async Task Inspect_ExtendedXmpShadowDirectory_FailsClosed()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("extended_xmp_shadow", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegExtendedXmpShadow(inputPath);

        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => new SourceInspector().InspectAsync(inputPath));
        Assert.Equal(SourceInspectionFailureCategory.Ambiguous, ex.Category);
        Assert.Equal(SourceInspectionStage.Metadata, ex.Stage);
        Assert.Contains("rdf:Description owner", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_GoogleV2_WrongMotionMime_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("v2_wrong_motion_mime", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegWrongMotionMime(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("MotionPhoto item MIME", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_GoogleV2_DuplicateGainMap_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("v2_dup_gainmap", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegDuplicateGainMap(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("duplicate GainMap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_GoogleV2_WrongGainMapMime_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("v2_wrong_gainmap_mime", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegWrongGainMapMime(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("GainMap item MIME", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_Oppo_VideoLengthOnly_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("oppo_videolength_only", ".jpg");
        SyntheticProtocolFixtures.CreateOppoVideoLengthOnlyJpeg(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("OPPO Live Photo candidate missing", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_Oppo_WrongOwner_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("oppo_wrong_owner", ".jpg");
        SyntheticProtocolFixtures.CreateOppoWrongOwnerJpeg(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("MotionPhotoOwner", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_Oppo_ItemLengthSmallerThanVideoLength_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("oppo_bad_item_len", ".jpg");
        SyntheticProtocolFixtures.CreateOppoItemLengthSmallerThanVideoLength(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("smaller than VideoLength", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_VivoX300_MissingVersion_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("vivo_missing_version", ".jpg");
        SyntheticProtocolFixtures.CreateVivoX300MissingVersionJpeg(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("missing required VCamera:VMotionPhotoVersion", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_VivoX300_UnsupportedVersion_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("vivo_unsupported_version", ".jpg");
        SyntheticProtocolFixtures.CreateVivoX300UnsupportedVersionJpeg(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("unsupported VCamera:VMotionPhotoVersion", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_VivoX300_NonThreeItems_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("vivo_non_3_items", ".jpg");
        SyntheticProtocolFixtures.CreateVivoX300NonThreeItemsJpeg(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("Vivo X300+", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_VivoX300_TwoItems_IdentifiesVideoWithoutGainMap()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("vivo_two_items", ".jpg");
        SyntheticProtocolFixtures.CreateVivoX300TwoItemJpeg(inputPath);

        var facts = await new SourceInspector().InspectAsync(inputPath);

        Assert.Equal(SourceProtocol.VivoLivePhoto, facts.Protocol);
        Assert.NotNull(facts.PrimaryImage);
        Assert.NotNull(facts.MotionVideo);
        Assert.False(facts.GainMap?.IsPresent ?? false);
        Assert.True(facts.MotionVideo.ByteLength > 0);
    }

    [Fact]
    public async Task Inspect_VivoX300_WrongMime_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("vivo_wrong_mime", ".jpg");
        SyntheticProtocolFixtures.CreateVivoX300WrongMimeJpeg(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("Vivo X300+", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_AppleDual_CorruptPrimaryJpeg_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string imgPath = ws.AllocateFilePath("corrupt_apple", ".jpg");
        string movPath = ws.AllocateFilePath("valid_apple", ".mov");
        SyntheticProtocolFixtures.CreateAppleCorruptDualJpeg(imgPath);
        SyntheticProtocolFixtures.CreateAppleMov(movPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(imgPath, movPath));
        Assert.Contains("structurally malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_AppleDual_CorruptSecondaryMov_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string imgPath = ws.AllocateFilePath("valid_apple", ".jpg");
        string movPath = ws.AllocateFilePath("corrupt_apple", ".mov");
        SyntheticProtocolFixtures.CreateAppleJpeg(imgPath);
        SyntheticProtocolFixtures.CreateAppleCorruptDualMov(movPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(imgPath, movPath));
        Assert.Contains("structurally malformed", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Theory]
    [InlineData(1, 0)]
    [InlineData(0, 1)]
    public async Task Inspect_AppleDual_MovMetadataFullBoxVersionOrFlagsFailsClosed(byte version, byte flagByte)
    {
        using var ws = new MediaWorkspace();
        string imgPath = ws.AllocateFilePath("valid_apple", ".jpg");
        string movPath = ws.AllocateFilePath("malformed_apple_fullbox", ".mov");
        SyntheticProtocolFixtures.CreateAppleJpeg(imgPath);
        SyntheticProtocolFixtures.CreateAppleMov(movPath);
        byte[] mov = await File.ReadAllBytesAsync(movPath);
        int hdlrType = mov.AsSpan().IndexOf("hdlr"u8);
        Assert.True(hdlrType >= 0);
        mov[hdlrType + 4] = version;
        mov[hdlrType + 5] = flagByte;
        await File.WriteAllBytesAsync(movPath, mov);

        var inspector = new SourceInspector();
        await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(imgPath, movPath));
    }

    [Fact]
    public void MatchVivo_SyntheticPairs_SuccessfullyMatchesAndConfirms()
    {
        using var ws = new MediaWorkspace();
        string imgPath = ws.AllocateFilePath("vivo_match", ".jpg");
        string vidPath = ws.AllocateFilePath("vivo_match", ".mp4");
        string dummyImgPath = ws.AllocateFilePath("dummy_unmatched", ".jpg");

        SyntheticProtocolFixtures.CreateVivoLegacyDualJpeg(imgPath);
        SyntheticProtocolFixtures.CreateVivoLegacyDualMp4(vidPath);
        File.WriteAllBytes(dummyImgPath, SyntheticProtocolFixtures.CreateMinimalJpeg());

        var result = LivePhotoMetadataMatcher.MatchVivo([imgPath, dummyImgPath], [vidPath]);
        Assert.Single(result.Pairs);
        Assert.Equal(imgPath, result.Pairs[0].ImagePath);
        Assert.Equal(vidPath, result.Pairs[0].VideoPath);
        Assert.Equal(MatchSource.VivoLivePhoto, result.Pairs[0].Source);
        Assert.Equal(1, result.RemainingImages);
        Assert.Equal(0, result.RemainingVideos);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public void MatchVivo_RealSamples_SuccessfullyMatchesAndConfirms()
    {
        string img = ResolveSample("vivo双文件.jpg");
        string vid = ResolveSample("vivo双文件.mp4");

        var result = LivePhotoMetadataMatcher.MatchVivo([img], [vid]);
        Assert.Single(result.Pairs);
        Assert.Equal(img, result.Pairs[0].ImagePath);
        Assert.Equal(vid, result.Pairs[0].VideoPath);
        Assert.Equal(MatchSource.VivoLivePhoto, result.Pairs[0].Source);
        Assert.Equal(0, result.RemainingImages);
        Assert.Equal(0, result.RemainingVideos);
    }

    [Fact]
    public async Task Inspect_GoogleV2_MissingPrimaryMime_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("googlev2_no_mime", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegMissingPrimaryMime(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Equal(SourceInspectionFailureCategory.Malformed, ex.Category);
        Assert.Equal(SourceInspectionStage.Protocol, ex.Stage);
    }

    [Fact]
    public async Task Inspect_GoogleV2_WrongPrimaryMime_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("googlev2_wrong_mime", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegWrongPrimaryMime(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("MIME", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_GoogleV2_NonzeroPadding_ExcludesPaddingFromPrimaryImage()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("googlev2_padding", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegNonzeroPadding(inputPath);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(inputPath);

        Assert.Equal(SourceProtocol.GoogleMotionPhotoV2, facts.Protocol);
        Assert.NotNull(facts.PrimaryImage);
        Assert.NotNull(facts.MotionVideo);
        Assert.True(facts.MotionVideo.IsPresent);
        Assert.True(facts.PrimaryImage.ByteLength > 0);
        // PrimaryImage.ByteLength strictly excludes the 16 bytes of padding
        Assert.Equal((ulong)facts.PrimaryImage.ByteLength + 16, (ulong)facts.MotionVideo.ByteOffset);
    }

    [Fact]
    public async Task Inspect_GoogleV2_MalformedPadding_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("googlev2_bad_pad", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegMalformedPadding(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("Padding", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_GoogleV2_HeicCandidate_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("googlev2_heic", ".heic");
        SyntheticProtocolFixtures.CreateGoogleV2HeicCandidate(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("HEIC", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_GoogleV2_UndeclaredGapToMotionPhoto_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("googlev2_gap_mp", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegUndeclaredGapToMotionPhoto(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("undeclared bytes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_GoogleV2_UndeclaredGapToGainMap_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("googlev2_gap_gm", ".jpg");
        SyntheticProtocolFixtures.CreateGoogleV2JpegUndeclaredGapToGainMap(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("undeclared bytes", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_Oppo_WrongMotionMime_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("oppo_bad_motion_mime", ".jpg");
        SyntheticProtocolFixtures.CreateOppoWrongMotionMimeJpeg(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("MIME", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_Oppo_WrongPrimaryMime_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("oppo_bad_pri_mime", ".jpg");
        SyntheticProtocolFixtures.CreateOppoWrongPrimaryMimeJpeg(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("MIME", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_Oppo_WrongGainMapMime_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("oppo_bad_gm_mime", ".jpg");
        SyntheticProtocolFixtures.CreateOppoWrongGainMapMimeJpeg(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("GainMap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_Oppo_GainMapMissingLength_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("oppo_gm_missing_len", ".jpg");
        SyntheticProtocolFixtures.CreateOppoGainMapMissingLengthJpeg(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("GainMap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_Oppo_GainMapZeroLength_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("oppo_gm_zero_len", ".jpg");
        SyntheticProtocolFixtures.CreateOppoGainMapZeroLengthJpeg(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("GainMap", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_Oppo_UnsupportedVersion_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("oppo_bad_ver", ".jpg");
        SyntheticProtocolFixtures.CreateOppoUnsupportedVersionJpeg(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Equal(SourceInspectionFailureCategory.Malformed, ex.Category);
        Assert.Equal(SourceInspectionStage.Protocol, ex.Stage);
        Assert.Equal(NativeRuntime.OppoCapability, ex.Capability);
        Assert.Contains("OLivePhotoVersion", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Inspect_Oppo_ConflictingVersion_ThrowsInvalidArgument()
    {
        using var ws = new MediaWorkspace();
        string inputPath = ws.AllocateFilePath("oppo_conflict_ver", ".jpg");
        SyntheticProtocolFixtures.CreateOppoConflictingVersionJpeg(inputPath);

        var inspector = new SourceInspector();
        var ex = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(inputPath));
        Assert.Contains("OLivePhotoVersion", ex.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_AppleDualFile_NeverMutatesSha()
    {
        string img = ResolveSample("苹果双文件.HEIC");
        string mov = ResolveSample("苹果双文件.MOV");
        string beforeImgSha = await ComputeSha256Async(img);
        string beforeMovSha = await ComputeSha256Async(mov);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(img, mov);
        Assert.Equal(SourceProtocol.AppleLivePhoto, facts.Protocol);

        string afterImgSha = await ComputeSha256Async(img);
        string afterMovSha = await ComputeSha256Async(mov);
        Assert.Equal(beforeImgSha, afterImgSha);
        Assert.Equal(beforeMovSha, afterMovSha);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_VivoDualFile_NeverMutatesSha()
    {
        string img = ResolveSample("vivo双文件.jpg");
        string vid = ResolveSample("vivo双文件.mp4");
        string beforeImgSha = await ComputeSha256Async(img);
        string beforeVidSha = await ComputeSha256Async(vid);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(img, vid);
        Assert.Equal(SourceProtocol.VivoLegacyDualFile, facts.Protocol);

        string afterImgSha = await ComputeSha256Async(img);
        string afterVidSha = await ComputeSha256Async(vid);
        Assert.Equal(beforeImgSha, afterImgSha);
        Assert.Equal(beforeVidSha, afterVidSha);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_OnePlusModifiedCover_IdentifiesOppoLivePhotoAndTiming()
    {
        string sample = ResolveSample("一加-改了封面照片.jpg");
        string beforeSha = await ComputeSha256Async(sample);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(sample);

        Assert.Equal(SourceProtocol.OppoLivePhoto, facts.Protocol);
        Assert.NotNull(facts.PrimaryImage);
        Assert.NotNull(facts.MotionVideo);
        Assert.True(facts.Timing.CoverTimestampUs != 0);
        AuxiliaryMediaFacts original = Assert.Single(facts.AuxiliaryItems,
            item => item.Semantic == "Original");
        Assert.Equal(3176955, original.ByteOffset);
        Assert.Equal(11355276 - 3176955, original.ByteLength);
        Assert.Equal(AuxiliaryRepresentation.Materialized, original.Representation);
        Assert.Equal(AuxiliaryOwnership.Primary, original.Ownership);
        Assert.Equal(AuxiliaryCodec.Jpeg, original.Codec);
        Assert.Equal("oppo:container:item:Original", original.StableIdentity);
        Assert.Equal("primary:0", original.OwnerIdentity);
        Assert.Equal("Original", original.Relationship);
        Assert.Equal("Original", original.Semantic);
        Assert.Equal(original.Sha256, await ComputeSliceSha256Async(sample, original.ByteOffset, original.ByteLength));
        Assert.NotEqual(facts.PrimaryImage.ByteOffset, original.ByteOffset);
        Assert.DoesNotContain(facts.AuxiliaryItems.Where(item => item.Semantic != "Original"),
            item => item.StableIdentity == original.StableIdentity ||
                (item.ByteOffset == original.ByteOffset && item.ByteLength == original.ByteLength));

        string afterSha = await ComputeSha256Async(sample);
        Assert.Equal(beforeSha, afterSha);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_AppleDualFileJpeg_IdentifiesAppleLivePhoto()
    {
        string img = ResolveSample("苹果-双文件.JPG");
        string mov = ResolveSample("苹果-双文件.MOV");
        string beforeImgSha = await ComputeSha256Async(img);
        string beforeMovSha = await ComputeSha256Async(mov);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(img, mov);

        Assert.Equal(SourceProtocol.AppleLivePhoto, facts.Protocol);
        Assert.False(string.IsNullOrWhiteSpace(facts.PairingIdentifier));

        string afterImgSha = await ComputeSha256Async(img);
        string afterMovSha = await ComputeSha256Async(mov);
        Assert.Equal(beforeImgSha, afterImgSha);
        Assert.Equal(beforeMovSha, afterMovSha);
    }

    [Fact]
    public async Task MatchAsync_AppleCandidate_PairsSuccessfully()
    {
        using var ws = new MediaWorkspace();
        string imgPath = ws.AllocateFilePath("apple_match", ".jpg");
        string movPath = ws.AllocateFilePath("apple_match", ".mov");
        SyntheticProtocolFixtures.CreateAppleJpeg(imgPath);
        SyntheticProtocolFixtures.CreateAppleMov(movPath);

        var result = await LivePhotoMetadataMatcher.MatchAsync([imgPath], [movPath]);
        Assert.Single(result.Pairs);
        Assert.Equal(imgPath, result.Pairs[0].ImagePath);
        Assert.Equal(movPath, result.Pairs[0].VideoPath);
        Assert.Equal(MatchSource.ContentIdentifier, result.Pairs[0].Source);
        Assert.Equal(0, result.RemainingImages);
        Assert.Equal(0, result.RemainingVideos);
    }

    [Fact]
    public async Task Inspect_AppleDual_ShadowMakerNoteOwner_FailsClosed()
    {
        using var ws = new MediaWorkspace();
        string imagePath = ws.AllocateFilePath("apple_shadow_makernote", ".jpg");
        string videoPath = ws.AllocateFilePath("apple_shadow_makernote", ".mov");
        SyntheticProtocolFixtures.CreateAppleJpegWithShadowMakerNote(imagePath);
        SyntheticProtocolFixtures.CreateAppleMov(videoPath);

        var error = await Assert.ThrowsAsync<SourceInspectionException>(() =>
            new SourceInspector().InspectAsync(imagePath, videoPath));

        Assert.Equal(SourceInspectionFailureCategory.Ambiguous, error.Category);
        Assert.Equal(SourceInspectionStage.Pairing, error.Stage);
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("meta")]
    [InlineData("keys")]
    [InlineData("ilst")]
    [InlineData("data")]
    public async Task Inspect_AppleDual_NestedMetadataShadow_FailsClosed(string shadowType)
    {
        string imagePath = ResolveSample("苹果-双文件.JPG");
        string sourceVideoPath = ResolveSample("苹果-双文件.MOV");
        using var workspace = new MediaWorkspace();
        string videoPath = workspace.AllocateFilePath("apple-shadow-" + shadowType, ".mov");

        byte[] video = await File.ReadAllBytesAsync(sourceVideoPath);
        InjectNestedMetadataShadow(video, shadowType);
        await File.WriteAllBytesAsync(videoPath, video);

        var error = await Assert.ThrowsAsync<SourceInspectionException>(() =>
            new SourceInspector().InspectAsync(imagePath, videoPath));
        Assert.Equal(SourceInspectionFailureCategory.Ambiguous, error.Category);
        Assert.Equal(SourceInspectionStage.Pairing, error.Stage);
    }

    [Fact]
    public async Task MatchAsync_VivoLegacyCandidate_DoesNotPairAsContentIdentifier()
    {
        using var ws = new MediaWorkspace();
        string imgPath = ws.AllocateFilePath("vivo_legacy_cid", ".jpg");
        string vidPath = ws.AllocateFilePath("vivo_legacy_cid", ".mp4");
        SyntheticProtocolFixtures.CreateVivoLegacyDualJpeg(imgPath);
        SyntheticProtocolFixtures.CreateVivoLegacyDualMp4(vidPath);

        // An orphan vivo marker is a typed ambiguous inspection result; it is
        // not silently downgraded to a no-match CID result.
        var error = await Assert.ThrowsAsync<SourceInspectionException>(() =>
            LivePhotoMetadataMatcher.MatchAsync([imgPath], [vidPath]));
        Assert.Equal(SourceInspectionFailureCategory.Ambiguous, error.Category);
        Assert.Equal(SourceInspectionStage.Pairing, error.Stage);
        Assert.Equal(LivePhotoBox.Interop.NativeRuntime.VivoLegacyCapability, error.Capability);

        // But MatchVivo DOES pair them as VivoLivePhoto
        var vivoResult = LivePhotoMetadataMatcher.MatchVivo([imgPath], [vidPath]);
        Assert.Single(vivoResult.Pairs);
        Assert.Equal(MatchSource.VivoLivePhoto, vivoResult.Pairs[0].Source);
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }

    private static async Task<string> ComputeSliceSha256Async(string filePath, long offset, long length)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        stream.Position = offset;
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int read = await stream.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            Assert.True(read > 0);
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    private static void AssertValidHeifAuxiliaryFacts(SourceMediaFacts facts, byte[] sourceBytes)
    {
        Assert.NotEmpty(facts.AuxiliaryItems);
        Assert.Equal(
            facts.AuxiliaryItems.Count,
            facts.AuxiliaryItems.Select(item => item.ItemId).Distinct().Count());

        var ordered = facts.AuxiliaryItems.OrderBy(item => item.ByteOffset).ToArray();
        for (int i = 0; i < ordered.Length; i++)
        {
            AuxiliaryMediaFacts item = ordered[i];
            Assert.True(item.IsPresent);
            Assert.Equal(ImageContainer.Heic, item.Container);
            Assert.Equal(AuxiliaryRepresentation.Embedded, item.Representation);
            Assert.Equal(AuxiliaryOwnership.Primary, item.Ownership);
            Assert.True(item.ItemId > 0);
            Assert.InRange(item.ByteOffset, 0, sourceBytes.LongLength - 1);
            Assert.InRange(item.ByteLength, 1, sourceBytes.LongLength - item.ByteOffset);
            Assert.False(string.IsNullOrWhiteSpace(item.Relationship));
            Assert.False(string.IsNullOrWhiteSpace(item.StableIdentity));
            Assert.False(string.IsNullOrWhiteSpace(item.Semantic));
            Assert.False(string.IsNullOrWhiteSpace(item.OwnerIdentity));
            Assert.Equal("primary:0", item.OwnerIdentity);
            if (item.ItemType is "grid" or "tmap")
            {
                Assert.True(item.GraphComplete);
                Assert.NotEmpty(item.Dependencies);
            }
            Assert.Equal(64, item.Sha256.Length);
            byte[] itemBytes = sourceBytes.AsSpan(
                checked((int)item.ByteOffset), checked((int)item.ByteLength)).ToArray();
            Assert.Equal(Convert.ToHexString(SHA256.HashData(itemBytes)), item.Sha256);
            if (i > 0)
            {
                AuxiliaryMediaFacts previous = ordered[i - 1];
                Assert.True(previous.ByteOffset + previous.ByteLength <= item.ByteOffset,
                    "HEIF auxiliary item ranges must not overlap.");
            }
        }
    }

    private static byte[] ExtractFirstApp1(byte[] jpeg)
    {
        int offset = FindFirstApp1(jpeg);
        int length = (jpeg[offset + 2] << 8) | jpeg[offset + 3];
        return jpeg[offset..checked(offset + 2 + length)];
    }

    private static int FindFirstApp1(byte[] jpeg)
    {
        for (int i = 2; i + 4 <= jpeg.Length; i++)
        {
            if (jpeg[i] == 0xFF && jpeg[i + 1] == 0xE1)
            {
                int length = (jpeg[i + 2] << 8) | jpeg[i + 3];
                Assert.InRange(i + 2 + length, i + 4, jpeg.Length);
                return i;
            }
        }
        throw new InvalidDataException("Synthetic JPEG did not contain APP1 XMP.");
    }

    private static void InjectNestedMetadataShadow(byte[] mov, string shadowType)
    {
        int moov = FindDirectBox(mov, 0, mov.Length, "moov");
        Assert.True(moov >= 0);
        int moovEnd = moov + ReadBe32(mov, moov);
        int udta = FindDirectBox(mov, moov + 8, moovEnd, "udta");
        Assert.True(udta >= 0);
        int originalFreeStart = udta + ReadBe32(mov, udta);
        int free = FindDirectBox(mov, originalFreeStart, moovEnd, "free");
        Assert.True(free >= 0);
        Assert.Equal(originalFreeStart, free);

        byte[] shadowPayload = new byte[8];
        WriteBe32(shadowPayload, 0, 0);
        WriteBe32(shadowPayload, 4, 0);
        byte[] shadow = shadowType switch
        {
            "meta" => Box("meta", Box("keys", shadowPayload)),
            "keys" => Box("keys", shadowPayload),
            "ilst" => Box("ilst", []),
            "data" => Box("data", shadowPayload),
            _ => throw new ArgumentOutOfRangeException(nameof(shadowType))
        };

        int freeSize = ReadBe32(mov, free);
        Assert.True(shadow.Length < freeSize - 8);
        int newFreeStart = udta + 8 + shadow.Length;
        int newFreeSize = freeSize - shadow.Length;

        WriteBe32(mov, udta, checked((uint)(8 + shadow.Length)));
        Buffer.BlockCopy(shadow, 0, mov, udta + 8, shadow.Length);
        WriteBe32(mov, newFreeStart, checked((uint)newFreeSize));
        Buffer.BlockCopy(mov, free + 4, mov, newFreeStart + 4, 4);
        Buffer.BlockCopy(mov, free + 8, mov, newFreeStart + 8, newFreeSize - 8);
    }

    private static int FindDirectBox(byte[] data, int start, int end, string type)
    {
        int position = start;
        while (position + 8 <= end)
        {
            int size = ReadBe32(data, position);
            Assert.InRange(size, 8, end - position);
            if (System.Text.Encoding.ASCII.GetString(data, position + 4, 4) == type)
                return position;
            position += size;
        }
        return -1;
    }

    private static byte[] Box(string type, byte[] payload)
    {
        byte[] box = new byte[payload.Length + 8];
        WriteBe32(box, 0, checked((uint)box.Length));
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(box, 4);
        payload.CopyTo(box, 8);
        return box;
    }

    private static int ReadBe32(byte[] data, int offset) =>
        checked((data[offset] << 24) | (data[offset + 1] << 16) |
            (data[offset + 2] << 8) | data[offset + 3]);

    private static void WriteBe32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
