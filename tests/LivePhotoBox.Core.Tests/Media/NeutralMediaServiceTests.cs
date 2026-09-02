using System;
using System.IO;
using System.Threading.Tasks;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class NeutralMediaServiceTests
{
    private static string ResolveSample(string filename)
    {
        string dir = AppContext.BaseDirectory;
        while (!string.IsNullOrEmpty(dir))
        {
            string candidate = Path.Combine(dir, "designs", "各个机型测试", filename);
            if (File.Exists(candidate)) return candidate;
            string? parent = Directory.GetParent(dir)?.FullName;
            if (parent == null || parent == dir) break;
            dir = parent;
        }
        throw new FileNotFoundException($"Sample file '{filename}' not found.");
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task CreateNeutralBundle_Apple_ExtractsCleansAndProducesValidBundle()
    {
        string primary = ResolveSample("苹果双文件.HEIC");
        string secondary = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();

        var service = new NeutralMediaService();
        var bundle = await service.CreateNeutralBundleAsync(primary, secondary, workspace);

        Assert.NotNull(bundle);
        Assert.NotNull(bundle.PrimaryImage);
        Assert.NotNull(bundle.MotionVideo);
        Assert.Equal(ImageContainer.Heic, bundle.PrimaryImage.ImageContainer);
        Assert.Equal(VideoContainer.Mov, bundle.MotionVideo.VideoContainer);
        Assert.NotEmpty(bundle.RemovedProtocolFacts);
        Assert.NotEmpty(bundle.Manifest);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task CreateNeutralBundle_WithFormatConversion_ConvertsCorrectly()
    {
        string primary = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();

        var service = new NeutralMediaService();
        var bundle = await service.CreateNeutralBundleAsync(primary, null, workspace, new MediaFormatRequirement
        {
            ImageContainer = ImageContainer.Heic,
            VideoContainer = VideoContainer.Mov,
            VideoCodec = VideoCodec.Copy
        });

        Assert.NotNull(bundle);
        Assert.NotNull(bundle.PrimaryImage);
        Assert.Equal(ImageContainer.Heic, bundle.PrimaryImage.ImageContainer);
        Assert.NotNull(bundle.MotionVideo);
        Assert.Equal(VideoContainer.Mov, bundle.MotionVideo.VideoContainer);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task CreateNeutralBundle_ReencodedVideo_IsNotReportedLossless()
    {
        string primary = ResolveSample("vivo双文件.jpg");
        string secondary = ResolveSample("vivo双文件.mp4");
        using var workspace = new MediaWorkspace();

        var service = new NeutralMediaService();
        var bundle = await service.CreateNeutralBundleAsync(primary, secondary, workspace, new MediaFormatRequirement
        {
            ImageContainer = ImageContainer.Unknown,
            VideoContainer = VideoContainer.Mp4,
            VideoCodec = VideoCodec.Hevc
        });

        var videoManifest = Assert.Single(bundle.Manifest, x => x.Role == "MotionVideo");
        Assert.Equal(PreservationOutcome.Reencoded, videoManifest.PreservationOutcome);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task CreateNeutralBundle_XiaomiWithGainMap_PreservesGainMapInBundle()
    {
        string primary = ResolveSample("小米.jpg");
        using var workspace = new MediaWorkspace();

        var service = new NeutralMediaService();
        var bundle = await service.CreateNeutralBundleAsync(primary, null, workspace);

        Assert.NotNull(bundle);
        Assert.NotNull(bundle.PrimaryImage);
        Assert.NotNull(bundle.MotionVideo);
        Assert.NotNull(bundle.GainMap);
        Assert.True(File.Exists(bundle.GainMap.Path));

        byte[] primaryBytes = await File.ReadAllBytesAsync(bundle.PrimaryImage.Path);
        int jpegCount = 0;
        for (int i = 0; i + 1 < primaryBytes.Length; i++)
        {
            if (primaryBytes[i] == 0xFF && primaryBytes[i + 1] == 0xD8)
                jpegCount++;
        }

        Assert.True(jpegCount >= 2, "Neutral JPEG must retain the primary and GainMap JPEG payloads.");
    }
}
