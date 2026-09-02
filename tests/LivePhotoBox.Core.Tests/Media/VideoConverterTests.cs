using System;
using System.IO;
using System.Threading.Tasks;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class VideoConverterTests
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
    public async Task Probe_AppleMov_ExtractsVideoFacts()
    {
        string sample = ResolveSample("苹果双文件.MOV");
        var converter = new VideoConverter();
        var facts = await converter.ProbeAsync(sample);

        Assert.NotNull(facts);
        Assert.True(facts.IsPresent);
        Assert.Equal(VideoContainer.Mov, facts.Container);
        Assert.Equal(VideoCodec.Hevc, facts.Codec);
        Assert.True(facts.Width > 0);
        Assert.True(facts.Height > 0);
        Assert.True(facts.DurationSeconds > 0);
        Assert.True(facts.Fps > 0);
        Assert.True(facts.HasAudio);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Probe_VivoMp4_ExtractsVideoFacts()
    {
        string sample = ResolveSample("vivo双文件.mp4");
        var converter = new VideoConverter();
        var facts = await converter.ProbeAsync(sample);

        Assert.NotNull(facts);
        Assert.True(facts.IsPresent);
        Assert.Equal(VideoContainer.Mp4, facts.Container);
        Assert.Equal(VideoCodec.H264, facts.Codec);
        Assert.True(facts.Width > 0);
        Assert.True(facts.Height > 0);
        Assert.True(facts.DurationSeconds > 0);
        Assert.True(facts.Fps > 0);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_MovToMp4_PrioritizesStreamRemux()
    {
        string sample = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();

        var artifact = new MediaArtifact
        {
            Path = sample,
            Kind = MediaArtifactKind.MotionVideo,
            MimeType = "video/quicktime",
            VideoContainer = VideoContainer.Mov,
            VideoCodec = VideoCodec.Hevc,
            ByteLength = new FileInfo(sample).Length
        };

        var converter = new VideoConverter();
        var result = await converter.ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = VideoContainer.Mp4,
            TargetCodec = VideoCodec.Copy,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        Assert.True(File.Exists(result.OutputArtifact.Path));
        Assert.True(result.ExecutionRecord.RemuxUsed);
        Assert.Equal(VideoContainer.Mov, result.ExecutionRecord.InputContainer);
        Assert.Equal(VideoContainer.Mp4, result.ExecutionRecord.OutputContainer);
        Assert.Equal(VideoCodec.Hevc, result.ExecutionRecord.OutputCodec);
        Assert.True(result.ExecutionRecord.AudioPreserved);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_Mp4ToMov_PrioritizesStreamRemux()
    {
        string sample = ResolveSample("vivo双文件.mp4");
        using var workspace = new MediaWorkspace();

        var artifact = new MediaArtifact
        {
            Path = sample,
            Kind = MediaArtifactKind.MotionVideo,
            MimeType = "video/mp4",
            VideoContainer = VideoContainer.Mp4,
            VideoCodec = VideoCodec.H264,
            ByteLength = new FileInfo(sample).Length
        };

        var converter = new VideoConverter();
        var result = await converter.ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = VideoContainer.Mov,
            TargetCodec = VideoCodec.Copy,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        Assert.True(File.Exists(result.OutputArtifact.Path));
        Assert.True(result.ExecutionRecord.RemuxUsed);
        Assert.Equal(VideoContainer.Mp4, result.ExecutionRecord.InputContainer);
        Assert.Equal(VideoContainer.Mov, result.ExecutionRecord.OutputContainer);
        Assert.Equal(VideoCodec.H264, result.ExecutionRecord.OutputCodec);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_HevcToH264_PerformsRealTranscode()
    {
        string sample = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();

        var artifact = new MediaArtifact
        {
            Path = sample,
            Kind = MediaArtifactKind.MotionVideo,
            MimeType = "video/quicktime",
            VideoContainer = VideoContainer.Mov,
            VideoCodec = VideoCodec.Hevc,
            ByteLength = new FileInfo(sample).Length
        };

        var converter = new VideoConverter();
        var result = await converter.ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = VideoContainer.Mp4,
            TargetCodec = VideoCodec.H264,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        Assert.True(File.Exists(result.OutputArtifact.Path));
        Assert.False(result.ExecutionRecord.RemuxUsed);
        Assert.Equal(VideoCodec.H264, result.ExecutionRecord.OutputCodec);
        Assert.Equal(VideoContainer.Mp4, result.ExecutionRecord.OutputContainer);

        // Verify output file independently
        var reProbed = await converter.ProbeAsync(result.OutputArtifact.Path);
        Assert.Equal(VideoContainer.Mp4, reProbed.Container);
        Assert.Equal(VideoCodec.H264, reProbed.Codec);
        Assert.True(reProbed.DurationSeconds > 0);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_H264ToHevc_PerformsRealTranscode()
    {
        string sample = ResolveSample("vivo双文件.mp4");
        using var workspace = new MediaWorkspace();

        var artifact = new MediaArtifact
        {
            Path = sample,
            Kind = MediaArtifactKind.MotionVideo,
            MimeType = "video/mp4",
            VideoContainer = VideoContainer.Mp4,
            VideoCodec = VideoCodec.H264,
            ByteLength = new FileInfo(sample).Length
        };

        var converter = new VideoConverter();
        var result = await converter.ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = VideoContainer.Mp4,
            TargetCodec = VideoCodec.Hevc,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        Assert.True(File.Exists(result.OutputArtifact.Path));
        Assert.False(result.ExecutionRecord.RemuxUsed);
        Assert.Equal(VideoCodec.Hevc, result.ExecutionRecord.OutputCodec);
        Assert.Equal(VideoContainer.Mp4, result.ExecutionRecord.OutputContainer);

        // Verify output file independently
        var reProbed = await converter.ProbeAsync(result.OutputArtifact.Path);
        Assert.Equal(VideoContainer.Mp4, reProbed.Container);
        Assert.Equal(VideoCodec.Hevc, reProbed.Codec);
        Assert.True(reProbed.DurationSeconds > 0);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_TargetFps_ReturnsExplicitUnsupported()
    {
        string sample = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();

        var artifact = new MediaArtifact
        {
            Path = sample,
            Kind = MediaArtifactKind.MotionVideo,
            MimeType = "video/quicktime",
            VideoContainer = VideoContainer.Mov,
            VideoCodec = VideoCodec.Hevc,
            ByteLength = new FileInfo(sample).Length
        };

        var converter = new VideoConverter();
        var result = await converter.ConvertAsync(new VideoConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = VideoContainer.Mp4,
            TargetCodec = VideoCodec.Copy,
            TargetDirectory = workspace.RootDirectory,
            TargetFps = 60
        });

        Assert.False(result.Success);
        Assert.Contains("TargetFps", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ProtocolMediaRequirements_MapsMergeMatricesCorrectly()
    {
        // Google V1 (1): JPG+MP4 (0), JPG+MOV (1)
        var g1Mp4 = ProtocolMediaRequirements.GetMergeRequirement(1, 0);
        Assert.Equal(ImageContainer.Jpeg, g1Mp4.ImageContainer);
        Assert.Equal(VideoContainer.Mp4, g1Mp4.VideoContainer);

        var g1Mov = ProtocolMediaRequirements.GetMergeRequirement(1, 1);
        Assert.Equal(ImageContainer.Jpeg, g1Mov.ImageContainer);
        Assert.Equal(VideoContainer.Mov, g1Mov.VideoContainer);

        // Google V2 (2): HEIC+MOV (3)
        var g2Heic = ProtocolMediaRequirements.GetMergeRequirement(2, 3);
        Assert.Equal(ImageContainer.Heic, g2Heic.ImageContainer);
        Assert.Equal(VideoContainer.Mov, g2Heic.VideoContainer);

        // Huawei (6): HEIC+MP4(H265) (4)
        var hwH265 = ProtocolMediaRequirements.GetMergeRequirement(6, 4);
        Assert.Equal(ImageContainer.Heic, hwH265.ImageContainer);
        Assert.Equal(VideoContainer.Mp4, hwH265.VideoContainer);
        Assert.Equal(VideoCodec.Hevc, hwH265.VideoCodec);
    }

    [Fact]
    public void ProtocolMediaRequirements_MapsSplitMatricesCorrectly()
    {
        var keep = ProtocolMediaRequirements.GetSplitRequirement(0, 0);
        Assert.Equal(ImageContainer.Unknown, keep.ImageContainer);

        var appleJpgMov = ProtocolMediaRequirements.GetSplitRequirement(1, 1);
        Assert.Equal(ImageContainer.Jpeg, appleJpgMov.ImageContainer);
        Assert.Equal(VideoContainer.Mov, appleJpgMov.VideoContainer);
        Assert.Equal(VideoCodec.Hevc, appleJpgMov.VideoCodec);

        var appleHeicMov = ProtocolMediaRequirements.GetSplitRequirement(1, 2);
        Assert.Equal(ImageContainer.Heic, appleHeicMov.ImageContainer);
        Assert.Equal(VideoContainer.Mov, appleHeicMov.VideoContainer);
        Assert.Equal(VideoCodec.Hevc, appleHeicMov.VideoCodec);

        var vivoJpgMp4 = ProtocolMediaRequirements.GetSplitRequirement(2, 3);
        Assert.Equal(ImageContainer.Jpeg, vivoJpgMp4.ImageContainer);
        Assert.Equal(VideoContainer.Mp4, vivoJpgMp4.VideoContainer);
        Assert.Equal(VideoCodec.H264, vivoJpgMp4.VideoCodec);
    }

    [Theory]
    [InlineData(1, 0)] // Apple + Keep -> Invalid
    [InlineData(1, 3)] // Apple + JPG+MP4 -> Invalid
    [InlineData(2, 0)] // vivo + Keep -> Invalid
    [InlineData(2, 1)] // vivo + JPG+MOV -> Invalid
    [InlineData(2, 2)] // vivo + HEIC+MOV -> Invalid
    [InlineData(99, 1)] // Unknown protocol -> Invalid
    [InlineData(1, 99)] // Unknown format -> Invalid
    public void ProtocolMediaRequirements_InvalidSplitCombinations_ThrowsArgumentException(int protocol, int format)
    {
        Assert.Throws<ArgumentException>(() =>
            ProtocolMediaRequirements.GetSplitRequirement(protocol, format));
    }

    [Fact]
    public async Task Probe_NonExistentFile_ThrowsFileNotFoundException()
    {
        var converter = new VideoConverter();
        await Assert.ThrowsAsync<FileNotFoundException>(() =>
            converter.ProbeAsync(@"C:\non_existent_video_path_xyz123.mp4"));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_WhenCancelled_ThrowsOperationCanceledException()
    {
        string sample = ResolveSample("苹果双文件.MOV");
        using var workspace = new MediaWorkspace();

        var artifact = new MediaArtifact
        {
            Path = sample,
            Kind = MediaArtifactKind.MotionVideo,
            MimeType = "video/quicktime",
            VideoContainer = VideoContainer.Mov,
            VideoCodec = VideoCodec.Hevc,
            ByteLength = new FileInfo(sample).Length
        };

        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel(); // pre-cancelled

        var converter = new VideoConverter();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            converter.ConvertAsync(new VideoConversionRequest
            {
                SourceArtifact = artifact,
                TargetContainer = VideoContainer.Mp4,
                TargetCodec = VideoCodec.H264,
                TargetDirectory = workspace.RootDirectory
            }, cts.Token));
    }
}
