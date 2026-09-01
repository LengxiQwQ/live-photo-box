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
        Assert.True(facts.Width > 0);
        Assert.True(facts.Height > 0);
        Assert.True(facts.DurationSeconds > 0);
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
    }
}
