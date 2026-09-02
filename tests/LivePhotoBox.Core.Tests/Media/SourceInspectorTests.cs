using System;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Core.Tests.Protocols;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class SourceInspectorTests
{
    private static string ResolveSample(string fileName)
    {
        string[] candidates = [
            Path.Combine(AppContext.BaseDirectory, "samples", fileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "designs", "各个机型测试", fileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "designs", "各个机型测试", fileName),
            Path.Combine(AppContext.BaseDirectory, "designs", "各个机型测试", fileName)
        ];
        foreach (var c in candidates)
        {
            string full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        throw new FileNotFoundException($"Sample not found: {fileName}");
    }

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

        string afterSha = await ComputeSha256Async(sample);
        Assert.Equal(beforeSha, afterSha);
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
    public async Task Inspect_WrongNamespaceMotionPhoto_IsNonLive()
    {
        using var ws = new LivePhotoBox.Media.Workspace.MediaWorkspace();
        string inputPath = ws.AllocateFilePath("wrong_namespace", ".jpg");
        SyntheticProtocolFixtures.CreateWrongNamespaceMotionPhotoJpeg(inputPath);

        var inspector = new SourceInspector();
        var facts = await inspector.InspectAsync(inputPath);

        Assert.Equal(SourceProtocol.NonLive, facts.Protocol);
        Assert.Null(facts.MotionVideo);
        Assert.Equal(ImageContainer.Jpeg, facts.PrimaryImage?.Container);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Inspect_AppleDualFileHeic_IdentifiesAppleLivePhoto()
    {
        string img = ResolveSample("苹果双文件.HEIC");
        string mov = ResolveSample("苹果双文件.MOV");

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
    public async Task Inspect_AppleDualFileQuickTimeWithoutFtyp_IdentifiesAppleLivePhoto()
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
            var facts = await inspector.InspectAsync(img, tempMov);

            Assert.Equal(SourceProtocol.AppleLivePhoto, facts.Protocol);
            Assert.NotNull(facts.MotionVideo);
            Assert.Equal(VideoContainer.Mov, facts.MotionVideo.Container);
        }
        finally
        {
            try { File.Delete(tempMov); } catch { }
        }
    }

    private static async Task<string> ComputeSha256Async(string filePath)
    {
        using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite, 64 * 1024, useAsync: true);
        byte[] hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash);
    }
}
