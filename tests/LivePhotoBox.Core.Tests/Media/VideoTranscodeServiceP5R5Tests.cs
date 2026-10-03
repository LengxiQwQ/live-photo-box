using System;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class VideoTranscodeServiceP5R5Tests
{
    private static string ResolveAppleSample() => TestSampleResolver.ResolveSample("苹果双文件.MOV");

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_RemuxAsync_UsesCopyAndPublishesCanonicalRemux()
    {
        string sample = ResolveAppleSample();
        using var workspace = new MediaWorkspace();
        string output = Path.Combine(workspace.RootDirectory, "apple-remux.mp4");

        var result = await VideoTranscodeService.RemuxAsync(sample, output);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(output, result.OutputPath);
        Assert.True(result.WasRemux);
        Assert.True(File.Exists(output));
        VideoFacts outputFacts = await new VideoConverter().ProbeAsync(output);
        Assert.Equal(VideoContainer.Mp4, outputFacts.Container);
        Assert.Equal(VideoCodec.Hevc, outputFacts.Codec);
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "vid-conv-*"));
    }

    [Theory]
    [InlineData("苹果双文件.MOV", "apple-same-container.mov", true, false)]
    [InlineData("vivo双文件.mp4", "vivo-same-container.mp4", false, true)]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_RemuxAsync_SameContainerLegacyFlagsFollowByteIdentity(
        string sampleName,
        string outputName,
        bool expectedByteIdentical,
        bool expectedWasRemux)
    {
        string sample = TestSampleResolver.ResolveSample(sampleName);
        using var workspace = new MediaWorkspace();
        string output = Path.Combine(workspace.RootDirectory, outputName);
        byte[] sourceBytes = await File.ReadAllBytesAsync(sample);

        VideoTranscodeService.TranscodeResult result = await VideoTranscodeService.RemuxAsync(sample, output);

        Assert.True(result.Success, result.ErrorMessage);
        Assert.Equal(output, result.OutputPath);
        Assert.Equal(expectedWasRemux, result.WasRemux);
        Assert.False(result.WasTranscoded);
        byte[] outputBytes = await File.ReadAllBytesAsync(output);
        Assert.Equal(expectedByteIdentical, sourceBytes.AsSpan().SequenceEqual(outputBytes));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "vid-conv-*"));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_TranscodeToMp4_CopyNeverBecomesH264()
    {
        string sample = ResolveAppleSample();
        using var workspace = new MediaWorkspace();
        string output = Path.Combine(workspace.RootDirectory, "apple-copy.mp4");

        var result = await VideoTranscodeService.TranscodeToMp4Async(sample, output, videoCodec: "copy");

        Assert.True(result.Success, result.ErrorMessage);
        Assert.True(result.WasRemux);
        VideoFacts outputFacts = await new VideoConverter().ProbeAsync(output);
        Assert.Equal(VideoCodec.Hevc, outputFacts.Codec);
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "vid-conv-*"));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_EnsureMp4_CopyReportsNotTranscoded()
    {
        string sample = ResolveAppleSample();
        using var workspace = new MediaWorkspace();

        (string path, bool wasTranscoded) = await VideoTranscodeService.EnsureMp4Async(
            sample, workspace.RootDirectory, videoCodec: "copy");

        Assert.False(wasTranscoded);
        Assert.True(File.Exists(path));
        VideoFacts outputFacts = await new VideoConverter().ProbeAsync(path);
        Assert.Equal(VideoCodec.Hevc, outputFacts.Codec);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_EnsureMp4_ReportsTranscodedOnlyAfterRealH264Reencode()
    {
        string sample = ResolveAppleSample();
        using var workspace = new MediaWorkspace();

        (string path, bool wasTranscoded) = await VideoTranscodeService.EnsureMp4Async(
            sample, workspace.RootDirectory, videoCodec: "h264");

        Assert.True(wasTranscoded);
        Assert.True(File.Exists(path));
        VideoFacts outputFacts = await new VideoConverter().ProbeAsync(path);
        Assert.Equal(VideoCodec.H264, outputFacts.Codec);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_UnknownCodecFailsClosed()
    {
        string sample = ResolveAppleSample();
        using var workspace = new MediaWorkspace();
        string output = Path.Combine(workspace.RootDirectory, "unknown-codec.mp4");

        var result = await VideoTranscodeService.TranscodeToMp4Async(sample, output, videoCodec: "h264-or-something-else");

        Assert.False(result.Success);
        Assert.False(result.WasRemux);
        Assert.False(File.Exists(output));
        Assert.Contains("Unsupported video codec selection", result.ErrorMessage, StringComparison.Ordinal);
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "vid-conv-*"));
    }

    [Theory]
    [InlineData("remux")]
    [InlineData("transcode-copy")]
    [Trait("Category", "RealSamples")]
    public async Task P5R5_PreexistingCallerDestinationIsNotOverwrittenOrPartiallyPublished(string operation)
    {
        string sample = ResolveAppleSample();
        using var workspace = new MediaWorkspace();
        string output = Path.Combine(workspace.RootDirectory, "caller-owned.mp4");
        byte[] originalBytes = [0x43, 0x41, 0x4C, 0x4C, 0x45, 0x52, 0x01, 0x02];
        await File.WriteAllBytesAsync(output, originalBytes);

        VideoTranscodeService.TranscodeResult result = operation switch
        {
            "remux" => await VideoTranscodeService.RemuxAsync(sample, output),
            "transcode-copy" => await VideoTranscodeService.TranscodeToMp4Async(sample, output, videoCodec: "copy"),
            _ => throw new InvalidOperationException($"Unknown test operation: {operation}")
        };

        Assert.False(result.Success);
        Assert.False(result.WasRemux);
        Assert.Equal(originalBytes, await File.ReadAllBytesAsync(output));
        Assert.Equal(new[] { output }, Directory.GetFiles(workspace.RootDirectory).OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
    }
}
