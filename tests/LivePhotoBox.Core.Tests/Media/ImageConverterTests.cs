using System;
using System.IO;
using System.Threading.Tasks;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ImageConverterTests
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
    public async Task Convert_JpegToJpeg_PerformsStructureCopyWithoutReencoding()
    {
        string sample = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();

        var artifact = new MediaArtifact
        {
            Path = sample,
            Kind = MediaArtifactKind.PrimaryImage,
            MimeType = "image/jpeg",
            ImageContainer = ImageContainer.Jpeg,
            ByteLength = new FileInfo(sample).Length
        };

        var converter = new ImageConverter();
        var result = await converter.ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = ImageContainer.Jpeg,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.True(result.Success);
        Assert.NotNull(result.OutputArtifact);
        Assert.True(File.Exists(result.OutputArtifact.Path));
        Assert.False(result.ExecutionRecord.PixelReencoded);
        Assert.Equal(ImageContainer.Jpeg, result.ExecutionRecord.InputContainer);
        Assert.Equal(ImageContainer.Jpeg, result.ExecutionRecord.OutputContainer);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_HeicToJpeg_ConvertsPixelsAndEmitsRecord()
    {
        string sample = ResolveSample("苹果双文件.HEIC");
        using var workspace = new MediaWorkspace();

        var artifact = new MediaArtifact
        {
            Path = sample,
            Kind = MediaArtifactKind.PrimaryImage,
            MimeType = "image/heic",
            ImageContainer = ImageContainer.Heic,
            ByteLength = new FileInfo(sample).Length
        };

        var converter = new ImageConverter();
        var result = await converter.ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = ImageContainer.Jpeg,
            TargetDirectory = workspace.RootDirectory,
            Quality = 90
        });

        Assert.True(result.Success);
        Assert.NotNull(result.OutputArtifact);
        Assert.True(File.Exists(result.OutputArtifact.Path));
        Assert.True(result.ExecutionRecord.PixelReencoded);
        Assert.Equal(ImageContainer.Heic, result.ExecutionRecord.InputContainer);
        Assert.Equal(ImageContainer.Jpeg, result.ExecutionRecord.OutputContainer);
        Assert.True(result.ExecutionRecord.MetadataCopied);
    }
}
