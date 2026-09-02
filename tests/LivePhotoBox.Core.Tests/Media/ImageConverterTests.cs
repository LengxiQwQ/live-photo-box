using System;
using System.IO;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ImageConverterTests
{
    [Fact]
    public async Task Convert_PngSource_IsRejectedAsUnsupportedProductInput()
    {
        using var workspace = new MediaWorkspace();
        string source = Path.Combine(workspace.RootDirectory, "source.png");
        File.WriteAllBytes(source,
        [
            0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A,
            0x00, 0x00, 0x00, 0x0D, 0x49, 0x48, 0x44, 0x52,
            0x00, 0x00, 0x00, 0x01, 0x00, 0x00, 0x00, 0x01
        ]);

        var result = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = source,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/png",
                ImageContainer = ImageContainer.Unknown,
                ByteLength = new FileInfo(source).Length
            },
            TargetContainer = ImageContainer.Jpeg,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.False(result.Success);
        Assert.Contains("JPEG and HEIC", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.False(result.ExecutionRecord.PixelReencoded);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task AppleMakerNoteInjection_CreatesOrUpdatesHeifExifItem()
    {
        string sample = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        var result = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = sample,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = new FileInfo(sample).Length
            },
            TargetContainer = ImageContainer.Heic,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.True(result.Success, result.ErrorMessage);
        byte[] heic = await File.ReadAllBytesAsync(result.OutputArtifact!.Path);
        const string contentId = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEFFFF0000";
        byte[] makerNote = NativeAppleMakerNoteWriter.BuildContentIdentifierMakerNote(contentId);

        Assert.True(
            NativeAppleMakerNoteWriter.TryInjectMakerNoteIntoHeic(
                heic, makerNote, out byte[]? rewritten, out string? error), error);
        Assert.NotNull(rewritten);
        Assert.True(
            NativeHeifBoxParser.TryLocateExifItem(
                rewritten!, out long exifOffset, out long exifLength, out string? parserError),
            parserError);
        Assert.True(exifOffset >= 0);
        Assert.True(exifLength > 0);
        Assert.Contains("Apple iOS", System.Text.Encoding.ASCII.GetString(rewritten!), StringComparison.Ordinal);
        Assert.Contains(contentId, System.Text.Encoding.ASCII.GetString(rewritten!), StringComparison.Ordinal);
    }

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

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        Assert.True(File.Exists(result.OutputArtifact.Path));
        Assert.False(result.ExecutionRecord.PixelReencoded);
        Assert.True(result.ExecutionRecord.MetadataCopied);
        Assert.Equal(PreservationOutcome.Preserved, result.ExecutionRecord.PreservationOutcome);
        Assert.Equal(ImageContainer.Jpeg, result.ExecutionRecord.InputContainer);
        Assert.Equal(ImageContainer.Jpeg, result.ExecutionRecord.OutputContainer);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_HeicToHeic_PerformsStructureCopyWithoutReencoding()
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
            TargetContainer = ImageContainer.Heic,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        Assert.True(File.Exists(result.OutputArtifact.Path));
        Assert.False(result.ExecutionRecord.PixelReencoded);
        Assert.True(result.ExecutionRecord.MetadataCopied);
        Assert.Equal(PreservationOutcome.Preserved, result.ExecutionRecord.PreservationOutcome);
        Assert.Equal(ImageContainer.Heic, result.ExecutionRecord.InputContainer);
        Assert.Equal(ImageContainer.Heic, result.ExecutionRecord.OutputContainer);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_HeicToJpeg_ConvertsPixelsAndEmitsTruthfulRecord()
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
            Quality = 90,
            PreservationPolicy = PreservationPolicy.BestEffort
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        Assert.True(File.Exists(result.OutputArtifact.Path));
        Assert.True(result.ExecutionRecord.PixelReencoded);
        Assert.False(result.ExecutionRecord.MetadataCopied);
        Assert.Equal(PreservationOutcome.PartiallyPreserved, result.ExecutionRecord.PreservationOutcome);
        Assert.Equal(ImageContainer.Heic, result.ExecutionRecord.InputContainer);
        Assert.Equal(ImageContainer.Jpeg, result.ExecutionRecord.OutputContainer);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_JpegToHeic_ConvertsPixelsAndEmitsTruthfulRecord()
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
            TargetContainer = ImageContainer.Heic,
            TargetDirectory = workspace.RootDirectory,
            PreservationPolicy = PreservationPolicy.BestEffort
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        Assert.True(File.Exists(result.OutputArtifact.Path));
        Assert.True(result.ExecutionRecord.PixelReencoded);
        Assert.False(result.ExecutionRecord.MetadataCopied);
        Assert.Equal(PreservationOutcome.PartiallyPreserved, result.ExecutionRecord.PreservationOutcome);
        Assert.Equal(ImageContainer.Jpeg, result.ExecutionRecord.InputContainer);
        Assert.Equal(ImageContainer.Heic, result.ExecutionRecord.OutputContainer);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_CrossContainerStrictPolicy_FailsExplicitly()
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
            TargetContainer = ImageContainer.Heic,
            TargetDirectory = workspace.RootDirectory,
            PreservationPolicy = PreservationPolicy.Strict
        });

        Assert.False(result.Success);
        Assert.Contains("Strict", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(PreservationOutcome.PartiallyPreserved, result.ExecutionRecord.PreservationOutcome);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_Mp4AsInput_IsNotFalselyIdentifiedAsHeic()
    {
        string sample = ResolveSample("vivo双文件.mp4");
        using var workspace = new MediaWorkspace();

        var artifact = new MediaArtifact
        {
            Path = sample,
            Kind = MediaArtifactKind.PrimaryImage,
            MimeType = "video/mp4",
            ImageContainer = ImageContainer.Unknown,
            ByteLength = new FileInfo(sample).Length
        };

        var converter = new ImageConverter();
        // Requesting HEIC target for an MP4 video file will fail during container validation if WIC or native fails, or if container doesn't match
        var result = await converter.ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = ImageContainer.Heic,
            TargetDirectory = workspace.RootDirectory,
            PreservationPolicy = PreservationPolicy.AllowDiscard
        });

        Assert.False(result.Success);
    }
}
