using System;
using System.Diagnostics;
using System.IO;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
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

    private static string ResolveSample(string fileName) => TestSampleResolver.ResolveSample(fileName);

    private static async Task CopyRangeAsync(string sourcePath, string destinationPath, long offset, long length)
    {
        Assert.True(offset >= 0 && length > 0);
        await using FileStream source = File.OpenRead(sourcePath);
        await using FileStream destination = File.Create(destinationPath);
        source.Position = offset;
        byte[] buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int read = await source.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)));
            Assert.True(read > 0, "The inspector-confirmed JPEG range ended early.");
            await destination.WriteAsync(buffer.AsMemory(0, read));
            remaining -= read;
        }
    }

    // ExifTool is verification-only. It is never invoked by the production
    // encoder; this test ensures an independent reader accepts its JPEG output.
    private static async Task AssertReadableByExifToolAsync(string imagePath)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        string? exiftool = path?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, "exiftool.exe"))
            .FirstOrDefault(File.Exists);
        Assert.False(string.IsNullOrWhiteSpace(exiftool), "exiftool.exe is required for this independent JPEG validation.");

        var startInfo = new ProcessStartInfo(exiftool!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-s3");
        startInfo.ArgumentList.Add("-ImageWidth");
        startInfo.ArgumentList.Add("-ImageHeight");
        startInfo.ArgumentList.Add(imagePath);
        using Process process = Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start exiftool.");
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"ExifTool rejected Native JPEG output: {error}");
        Assert.Equal(2, output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries).Length);
    }

    // Generated once with an independent libjpeg fixture tool, then embedded so
    // the test has no machine-local codec/tool dependency. The production path
    // below is Native libjpeg-turbo decode -> current WIC HEIC boundary.
    [Theory]
    [InlineData("grayscale", "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/wAALCAAIAAgBAREA/8QAHwAAAQUBAQEBAQEAAAAAAAAAAAECAwQFBgcICQoL/8QAtRAAAgEDAwIEAwUFBAQAAAF9AQIDAAQRBRIhMUEGE1FhByJxFDKBkaEII0KxwRVS0fAkM2JyggkKFhcYGRolJicoKSo0NTY3ODk6Q0RFRkdISUpTVFVWV1hZWmNkZWZnaGlqc3R1dnd4eXqDhIWGh4iJipKTlJWWl5iZmqKjpKWmp6ipqrKztLW2t7i5usLDxMXGx8jJytLT1NXW19jZ2uHi4+Tl5ufo6erx8vP09fb3+Pn6/9oACAEBAAA/AOf/AOSX/wDFpvhN/wAjr/x6694ms/8AmCdntbZx/wAvfUPIP+PflV/f5Nv/AP/Z")]
    [InlineData("progressive", "/9j/4AAQSkZJRgABAQAAAQABAAD/2wBDAAMCAgMCAgMDAwMEAwMEBQgFBQQEBQoHBwYIDAoMDAsKCwsNDhIQDQ4RDgsLEBYQERMUFRUVDA8XGBYUGBIUFRT/2wBDAQMEBAUEBQkFBQkUDQsNFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBQUFBT/wgARCAAIAAgDASIAAhEBAxEB/8QAFQABAQAAAAAAAAAAAAAAAAAAAAT/xAAVAQEBAAAAAAAAAAAAAAAAAAADBf/aAAwDAQACEAMQAAABvDyf/8QAFRABAQAAAAAAAAAAAAAAAAAAADT/2gAIAQEAAQUClf/EAB4RAAIBAwUAAAAAAAAAAAAAAAECBQMRMQBBQlHB/9oACAEDAQE/AZCRqxzhKQzfdxgkcGXrwWFhr//EABwRAAEDBQAAAAAAAAAAAAAAAAECA0EAERNRcf/aAAgBAgEBPwFhkPoyK3aI6DX/xAAgEAAABQMFAAAAAAAAAAAAAAABAgMRIRIiYRMjMUJD/9oACAEBAAY/AtBCxQsmOPlkeLowzdadr//EABcQAAMBAAAAAAAAAAAAAAAAAABRcfD/2gAIAQEAAT8hwaKFhH//2gAMAwEAAgADAAAAEPf/xAAZEQEAAgMAAAAAAAAAAAAAAAABESExQVH/2gAIAQMBAT8QnUFnKtDaGRjQh//EABgRAQEBAQEAAAAAAAAAAAAAAAERIQAx/9oACAECAQE/EKckQAQEQCD1u1da73//xAAZEAEAAgMAAAAAAAAAAAAAAAABACFBUbH/2gAIAQEAAT8Q6MOqhZqDBLGf/9k=")]
    public async Task Convert_GrayAndProgressiveJpegs_DecodeThroughNativeBackend(string name, string fixture)
    {
        using var workspace = new MediaWorkspace();
        string input = Path.Combine(workspace.RootDirectory, $"{name}.jpg");
        string output = Path.Combine(workspace.RootDirectory, $"{name}.heic");
        await File.WriteAllBytesAsync(input, Convert.FromBase64String(fixture));

        bool reencoded = await NativeMediaService.ConvertImageAsync(input, output, ImageContainer.Heic, quality: 90);

        Assert.True(reencoded);
        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length > 0);
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("苹果-双文件.JPG")]
    [InlineData("红米老款-GV1.JPG")]
    [InlineData("oppo.jpg")]
    [InlineData("vivo.jpg")]
    [InlineData("三星.jpg")]
    [InlineData("华为-Mate80.jpg")]
    [InlineData("荣耀.jpg")]
    [InlineData("小米.jpg")]
    [InlineData("一加.jpg")]
    public async Task Convert_RepresentativeVendorJpegs_DecodesThroughNativeBackend(string sampleName)
    {
        string source = ResolveSample(sampleName);
        string sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(source)));
        using var workspace = new MediaWorkspace();
        string output = Path.Combine(workspace.RootDirectory, "native-decode.heic");

        bool reencoded = await NativeMediaService.ConvertImageAsync(
            source, output, ImageContainer.Heic, quality: 90);

        Assert.True(reencoded);
        Assert.True(File.Exists(output));
        Assert.True(new FileInfo(output).Length > 0);
        Assert.Equal(sourceHash, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(source))));
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
    public async Task Convert_HeicToJpeg_UsesNativeCodecAndSupportsPerfectDctTransform()
    {
        string sample = ResolveSample("苹果双文件.HEIC");
        using var workspace = new MediaWorkspace();
        var converted = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = sample,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/heic",
                ImageContainer = ImageContainer.Heic,
                ByteLength = new FileInfo(sample).Length
            },
            TargetContainer = ImageContainer.Jpeg,
            TargetDirectory = workspace.RootDirectory,
            Quality = 90,
            PreservationPolicy = PreservationPolicy.BestEffort
        });

        Assert.True(converted.Success, converted.ErrorMessage);
        await AssertReadableByExifToolAsync(converted.OutputArtifact!.Path);
        string transformed = Path.Combine(workspace.RootDirectory, "rotated-lossless.jpg");
        await NativeMediaService.TransformJpegLosslesslyAsync(
            converted.OutputArtifact.Path, transformed, transform: 1);

        byte[] bytes = await File.ReadAllBytesAsync(transformed);
        Assert.True(bytes.Length > 4);
        Assert.Equal(0xFF, bytes[0]);
        Assert.Equal(0xD8, bytes[1]);
        Assert.Equal(0xFF, bytes[^2]);
        Assert.Equal(0xD9, bytes[^1]);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Transform_HuaweiPrimaryJpeg_PreservesProjectObservedMetadataAndDoesNotMutateSource()
    {
        string source = ResolveSample("华为-Mate80.jpg");
        string beforeHash = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(source)));
        var facts = await NativeMediaService.InspectMediaAsync(source);
        Assert.Equal(SourceProtocol.HuaweiMovingPhoto, facts.Protocol);
        Assert.Equal(ImageContainer.Jpeg, facts.PrimaryImage.Container);

        using var workspace = new MediaWorkspace();
        string primary = Path.Combine(workspace.RootDirectory, "huawei-primary.jpg");
        await CopyRangeAsync(source, primary, facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength);
        PreservationObservation before = await NativeMediaService.CapturePreservationObservationAsync(
            primary, SourceProtocol.HuaweiMovingPhoto, ImageContainer.Jpeg);
        Assert.Equal((ushort)1, before.Orientation);
        Assert.True(before.HasExif);
        Assert.True(before.HasIcc);

        string output = Path.Combine(workspace.RootDirectory, "huawei-lossless-180.jpg");
        await NativeMediaService.TransformJpegLosslesslyAsync(primary, output, transform: 1);

        PreservationObservation after = await NativeMediaService.CapturePreservationObservationAsync(
            output, SourceProtocol.HuaweiMovingPhoto, ImageContainer.Jpeg);
        Assert.Equal(before.Orientation, after.Orientation);
        Assert.Equal(before.HasExif, after.HasExif);
        Assert.Equal(before.HasIcc, after.HasIcc);
        Assert.Equal(before.HasXmp, after.HasXmp);
        Assert.Equal(before.ExifIfd0NonPtrSha256, after.ExifIfd0NonPtrSha256);
        Assert.Equal(before.ExifExifIfdSha256, after.ExifExifIfdSha256);
        Assert.Equal(before.IccSha256, after.IccSha256);
        Assert.Equal(before.XmpNonprotocolSha256, after.XmpNonprotocolSha256);
        Assert.Equal(before.MakernoteNonliveSha256, after.MakernoteNonliveSha256);
        Assert.Equal(beforeHash, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(source))));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Transform_ProtocolTailedJpeg_FailsClosedWithoutPublishingOutput()
    {
        string source = ResolveSample("华为-Mate80.jpg");
        using var workspace = new MediaWorkspace();
        string output = Path.Combine(workspace.RootDirectory, "must-not-drop-tail.jpg");

        await Assert.ThrowsAnyAsync<Exception>(() => NativeMediaService.TransformJpegLosslesslyAsync(
            source, output, transform: 1));
        Assert.False(File.Exists(output));
    }

    [Fact]
    public async Task Convert_TruncatedJpeg_FailsWithoutPublishingOutput()
    {
        string source = ResolveSample("华为-Mate80.jpg");
        using var workspace = new MediaWorkspace();
        string truncated = Path.Combine(workspace.RootDirectory, "truncated.jpg");
        string output = Path.Combine(workspace.RootDirectory, "must-not-publish.heic");
        await using (FileStream input = File.OpenRead(source))
        await using (FileStream destination = File.Create(truncated))
        {
            byte[] prefix = new byte[1024];
            int count = await input.ReadAsync(prefix);
            await destination.WriteAsync(prefix.AsMemory(0, count));
        }

        await Assert.ThrowsAnyAsync<Exception>(() => NativeMediaService.ConvertImageAsync(
            truncated, output, ImageContainer.Heic, quality: 90));
        Assert.False(File.Exists(output));
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
