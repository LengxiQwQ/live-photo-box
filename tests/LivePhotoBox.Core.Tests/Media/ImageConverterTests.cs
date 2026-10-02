using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Json;
using System.Threading.Tasks;
using LivePhotoBox.Interop;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using LivePhotoBox.Services;
using LivePhotoBox.Core.Tests.Support;
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
    public async Task Convert_WhenCancelled_LeavesNoPublishedImageOutput()
    {
        string sample = ResolveSample("oppo.jpg");
        using var workspace = new MediaWorkspace();
        using var cts = new System.Threading.CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new ImageConverter().ConvertAsync(new ImageConversionRequest
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
            }, cts.Token));

        Assert.Empty(Directory.GetFiles(workspace.RootDirectory, "img-conv-*"));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task AppleMakerNoteInjection_CreatesOrUpdatesHeifExifItem()
    {
        string sample = ResolveSample("oppo.jpg");
        string sourceHashBefore = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sample)));
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
        string rewrittenPath = Path.Combine(workspace.RootDirectory, "apple-makernote.heic");
        await File.WriteAllBytesAsync(rewrittenPath, rewritten!);
        await AssertReadableByExifToolAsync(rewrittenPath);
        Assert.Equal(sourceHashBefore, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sample))));
    }

    [Fact]
    public async Task Convert_MissingHeicForJpegTarget_ReturnsClassifiedSourceInspectionFailure()
    {
        using var workspace = new MediaWorkspace();
        ImageConversionResult result = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = Path.Combine(workspace.RootDirectory, "missing.heic"),
                Kind = MediaArtifactKind.PrimaryImage,
                ImageContainer = ImageContainer.Heic
            },
            TargetContainer = ImageContainer.Jpeg,
            TargetDirectory = workspace.RootDirectory
        });

        Assert.False(result.Success);
        Assert.Null(result.OutputArtifact);
        Assert.Equal(ConversionFailureStage.SourceInspection, result.ExecutionRecord.Truth.FailureStage);
        Assert.Equal(ConversionFailureCategory.SourceInspection, result.ExecutionRecord.Truth.FailureCategory);
    }

    private static string ResolveSample(string fileName) => TestSampleResolver.ResolveSample(fileName);

    private static async Task<string> CreateNeutralHuaweiJpegPrimaryAsync(MediaWorkspace workspace, string fileName)
    {
        string source = ResolveSample("华为-Mate80.jpg");
        SourceMediaFacts facts = await NativeMediaService.InspectMediaAsync(source);
        Assert.Equal(ImageContainer.Jpeg, facts.PrimaryImage.Container);
        Assert.Null(facts.GainMap);
        Assert.DoesNotContain(facts.AuxiliaryItems, item => item.IsPresent);
        string neutralPath = Path.Combine(workspace.RootDirectory, fileName);
        await CopyRangeAsync(source, neutralPath, facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength);
        return neutralPath;
    }

    private static async Task<string> CreateNeutralHuaweiHeicPrimaryAsync(MediaWorkspace workspace, string fileName)
    {
        string source = ResolveSample("华为Mate80.heic");
        SourceMediaFacts facts = await NativeMediaService.InspectMediaAsync(source);
        Assert.Equal(ImageContainer.Heic, facts.PrimaryImage.Container);
        Assert.Null(facts.GainMap);
        Assert.DoesNotContain(facts.AuxiliaryItems, item => item.IsPresent);
        string neutralPath = Path.Combine(workspace.RootDirectory, fileName);
        await CopyRangeAsync(source, neutralPath, facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength);
        return neutralPath;
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public void FormalHeicRealSampleInventory_IsExplicitAndComplete()
    {
        // The cache can contain auxiliary generated files for other tests.
        // Formal evidence is the explicit device allow-list, not every HEIC
        // co-located in that cache; in particular it never includes the
        // synthetic malformed-XMP fixture.
        string[] actual = RealSampleCorpusManifest.R5HeicExpectations
            .Select(expectation => expectation.Filename)
            .Select(ResolveSample)
            .Select(Path.GetFileName)
            .Where(name => name is not null)
            .Select(name => name!)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(
            RealSampleCorpusManifest.R5HeicExpectations
                .Select(expectation => expectation.Filename)
                .OrderBy(name => name, StringComparer.Ordinal),
            actual);
    }

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
        (uint width, uint height) = await ReadDimensionsWithExifToolAsync(imagePath);
        Assert.True(width > 0 && height > 0);
    }

    private static async Task<(uint Width, uint Height)> ReadDimensionsWithExifToolAsync(string imagePath)
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
        string[] dimensions = output.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);
        Assert.Equal(2, dimensions.Length);
        Assert.True(uint.TryParse(dimensions[0], out uint width), "ExifTool did not emit a numeric image width.");
        Assert.True(uint.TryParse(dimensions[1], out uint height), "ExifTool did not emit a numeric image height.");
        return (width, height);
    }

    private static async Task<string> ReadExifToolTextTagAsync(string imagePath, string tag)
    {
        using Process process = StartExifTool("-s3", tag, imagePath);
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"ExifTool could not read {tag}: {error}");
        return output.Trim();
    }

    private static async Task<byte[]> ReadExifToolBinaryTagAsync(string imagePath, string tag)
    {
        using Process process = StartExifTool("-b", tag, imagePath);
        using var output = new MemoryStream();
        await process.StandardOutput.BaseStream.CopyToAsync(output);
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"ExifTool could not read {tag}: {error}");
        return output.ToArray();
    }

    private static async Task AssertExifToolOutcomesMatchAsync(
        string sourcePath,
        string outputPath,
        ConversionExecutionTruth truth)
    {
        await AssertReadableByExifToolAsync(outputPath);

        ImageContainer sourceContainer = Path.GetExtension(sourcePath).Equals(".heic", StringComparison.OrdinalIgnoreCase)
            ? ImageContainer.Heic
            : ImageContainer.Jpeg;
        PreservationObservation sourceObservation = await NativeMediaService.CapturePreservationObservationAsync(
            sourcePath, SourceProtocol.Unknown, sourceContainer);
        PreservationObservation outputObservation = await NativeMediaService.CapturePreservationObservationAsync(
            outputPath,
            SourceProtocol.Unknown,
            Path.GetExtension(outputPath).Equals(".heic", StringComparison.OrdinalIgnoreCase)
                ? ImageContainer.Heic
                : ImageContainer.Jpeg);

        string[] sourceMetadata = await ReadExifToolMetadataTagsAsync(sourcePath);
        string[] outputMetadata = await ReadExifToolMetadataTagsAsync(outputPath);
        ConversionComponentOutcome metadataOutcome = sourceMetadata.Length == 0
            ? ConversionComponentOutcome.NotApplicable
            : sourceMetadata.SequenceEqual(outputMetadata, StringComparer.Ordinal)
                ? ConversionComponentOutcome.Preserved
                : outputMetadata.Length > 0
                    ? ConversionComponentOutcome.Degraded
                    : ConversionComponentOutcome.Lost;
        Assert.True(metadataOutcome == truth.Metadata,
            $"Independent EXIF/XMP outcome was {metadataOutcome}, converter reported {truth.Metadata}; " +
            $"Native source HasExif={sourceObservation.HasExif}, parseError={sourceObservation.ExifParseError}, " +
            $"DateTimeOriginal='{sourceObservation.DateTimeOriginal}', output HasExif={outputObservation.HasExif}, " +
            $"parseError={outputObservation.ExifParseError}, DateTimeOriginal='{outputObservation.DateTimeOriginal}'.");

        string sourceOrientation = await ReadExifToolTextTagAsync(sourcePath, "-Orientation");
        string outputOrientation = await ReadExifToolTextTagAsync(outputPath, "-Orientation");
        ConversionComponentOutcome orientationOutcome = string.IsNullOrEmpty(sourceOrientation)
            ? string.IsNullOrEmpty(outputOrientation)
                ? ConversionComponentOutcome.NotApplicable
                : ConversionComponentOutcome.Lost
            : string.Equals(sourceOrientation, outputOrientation, StringComparison.Ordinal)
                ? ConversionComponentOutcome.Preserved
                : ConversionComponentOutcome.Lost;
        Assert.True(orientationOutcome == truth.Orientation,
            $"Independent orientation outcome was {orientationOutcome} ({sourceOrientation} -> {outputOrientation}), " +
            $"converter reported {truth.Orientation}; Native values {sourceObservation.Orientation} -> {outputObservation.Orientation}.");

        byte[] sourceIcc = await ReadExifToolBinaryTagAsync(sourcePath, "-icc_profile");
        byte[] outputIcc = await ReadExifToolBinaryTagAsync(outputPath, "-icc_profile");
        ConversionComponentOutcome iccOutcome = sourceIcc.Length == 0
            ? ConversionComponentOutcome.NotApplicable
            : SHA256.HashData(sourceIcc).AsSpan().SequenceEqual(SHA256.HashData(outputIcc))
                ? ConversionComponentOutcome.Preserved
                : ConversionComponentOutcome.Lost;
        Assert.True(iccOutcome == truth.ColorIcc,
            $"Independent ICC outcome was {iccOutcome} ({sourceIcc.Length} -> {outputIcc.Length} bytes), " +
            $"converter reported {truth.ColorIcc}; Native flags 0x{sourceObservation.Flags:X8} -> 0x{outputObservation.Flags:X8}, " +
            $"hashes {sourceObservation.IccSha256} -> {outputObservation.IccSha256}.");
    }

    private static async Task<string[]> ReadExifToolMetadataTagsAsync(string imagePath)
    {
        using Process process = StartExifTool(
            "-json", "-G1", "-EXIF:all", "-XMP:all", "-IPTC:all", "-MakerNotes:all", imagePath);
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"ExifTool could not independently read EXIF/XMP metadata: {error}");

        using JsonDocument document = JsonDocument.Parse(output);
        JsonElement tags = document.RootElement[0];
        return tags.EnumerateObject()
            .Where(property => !string.Equals(property.Name, "SourceFile", StringComparison.Ordinal))
            .Select(property => $"{property.Name}={property.Value.GetRawText()}")
            .OrderBy(value => value, StringComparer.Ordinal)
            .ToArray();
    }

    private static Process StartExifTool(params string[] arguments)
    {
        string? path = Environment.GetEnvironmentVariable("PATH");
        string? exiftool = path?
            .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(directory => Path.Combine(directory, "exiftool.exe"))
            .FirstOrDefault(File.Exists);
        Assert.False(string.IsNullOrWhiteSpace(exiftool), "exiftool.exe is required for independent R2 metadata validation.");

        var startInfo = new ProcessStartInfo(exiftool!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        foreach (string argument in arguments) startInfo.ArgumentList.Add(argument);
        return Process.Start(startInfo) ?? throw new InvalidOperationException("Could not start exiftool.");
    }

    // Generated once with an independent libjpeg fixture tool, then embedded so
    // the test has no machine-local codec/tool dependency. The production path
    // below is Native libjpeg-turbo decode -> project-owned libheif HEIC encode.
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
        using var workspace = new MediaWorkspace();
        string sample = await CreateNeutralHuaweiJpegPrimaryAsync(workspace, "neutral-source.jpg");
        string sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sample)));

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
        Assert.Equal(ConversionOperationKind.Passthrough, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionComponentOutcome.Preserved, result.ExecutionRecord.Truth.Pixel);
        Assert.Equal(ConversionComponentOutcome.Preserved, result.ExecutionRecord.Truth.Metadata);
        Assert.Equal(ConversionComponentOutcome.Preserved, result.ExecutionRecord.Truth.Orientation);
        Assert.Equal(ConversionComponentOutcome.Preserved, result.ExecutionRecord.Truth.ColorIcc);
        Assert.Equal(sourceHash, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(result.OutputArtifact.Path))));
        await AssertExifToolOutcomesMatchAsync(sample, result.OutputArtifact.Path, result.ExecutionRecord.Truth);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_HeicToHeic_PerformsStructureCopyWithoutReencoding()
    {
        using var workspace = new MediaWorkspace();
        string neutralJpeg = await CreateNeutralHuaweiJpegPrimaryAsync(workspace, "neutral-source.jpg");
        ImageConversionResult neutralHeic = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = neutralJpeg,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = new FileInfo(neutralJpeg).Length
            },
            TargetContainer = ImageContainer.Heic,
            TargetDirectory = workspace.RootDirectory,
            PreservationPolicy = PreservationPolicy.BestEffort
        });
        Assert.True(neutralHeic.Success, neutralHeic.ErrorMessage);
        string sample = neutralHeic.OutputArtifact!.Path;
        SourceMediaFacts neutralFacts = await NativeMediaService.InspectMediaAsync(sample);
        Assert.Null(neutralFacts.GainMap);
        Assert.DoesNotContain(neutralFacts.AuxiliaryItems, item => item.IsPresent);
        string sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sample)));

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
        Assert.False(result.ExecutionRecord.MetadataCopied);
        Assert.Equal(PreservationOutcome.Preserved, result.ExecutionRecord.PreservationOutcome);
        Assert.Equal(ImageContainer.Heic, result.ExecutionRecord.InputContainer);
        Assert.Equal(ImageContainer.Heic, result.ExecutionRecord.OutputContainer);
        Assert.Equal(ConversionOperationKind.Passthrough, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionComponentOutcome.NotApplicable, result.ExecutionRecord.Truth.Metadata);
        Assert.Equal(ConversionComponentOutcome.NotApplicable, result.ExecutionRecord.Truth.Orientation);
        Assert.Equal(ConversionComponentOutcome.Preserved, result.ExecutionRecord.Truth.ColorIcc);
        Assert.Equal(sourceHash, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(result.OutputArtifact.Path))));
        await AssertReadableByExifToolAsync(result.OutputArtifact.Path);
    }

    [Fact]
    public async Task Convert_TransformOutsideJpegToJpeg_IsRejectedWithoutPublishingOutput()
    {
        string sample = ResolveSample("华为-Mate80.jpg");
        using var workspace = new MediaWorkspace();

        ImageConversionResult result = await new ImageConverter().ConvertAsync(new ImageConversionRequest
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
            TargetDirectory = workspace.RootDirectory,
            Transform = ImageTransformKind.Rotate180
        });

        Assert.False(result.Success);
        Assert.Null(result.OutputArtifact);
        Assert.Equal(ConversionOperationKind.Unknown, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionFailureCategory.Unsupported, result.ExecutionRecord.Truth.FailureCategory);
        Assert.Empty(Directory.EnumerateFiles(workspace.RootDirectory));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_JpegToJpeg_Rotate90_UsesPerfectDctAndPreservesObservedComponents()
    {
        string liveSource = ResolveSample("华为-Mate80.jpg");
        string liveSourceHash = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(liveSource)));
        SourceMediaFacts facts = await NativeMediaService.InspectMediaAsync(liveSource);
        Assert.Equal(ImageContainer.Jpeg, facts.PrimaryImage.Container);

        using var workspace = new MediaWorkspace();
        string neutralPrimary = Path.Combine(workspace.RootDirectory, "huawei-primary.jpg");
        await CopyRangeAsync(liveSource, neutralPrimary, facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength);
        (uint sourceWidth, uint sourceHeight) = await ReadDimensionsWithExifToolAsync(neutralPrimary);

        ImageConversionResult result = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = neutralPrimary,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = new FileInfo(neutralPrimary).Length
            },
            TargetContainer = ImageContainer.Jpeg,
            TargetDirectory = workspace.RootDirectory,
            Transform = ImageTransformKind.Rotate90,
            PreservationPolicy = PreservationPolicy.BestEffort
        });

        Assert.True(result.Success, result.ErrorMessage);
        Assert.NotNull(result.OutputArtifact);
        (uint outputWidth, uint outputHeight) = await ReadDimensionsWithExifToolAsync(result.OutputArtifact.Path);
        Assert.Equal(sourceHeight, outputWidth);
        Assert.Equal(sourceWidth, outputHeight);
        Assert.False(result.ExecutionRecord.PixelReencoded);
        Assert.Equal(ConversionOperationKind.LosslessTransform, result.ExecutionRecord.Truth.RequestedOperationKind);
        Assert.Equal(ConversionOperationKind.LosslessTransform, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionCapability.ImageLosslessTransform, result.ExecutionRecord.Truth.SelectedCapability);
        Assert.Equal(ConversionCapability.ImageLosslessTransform, result.ExecutionRecord.Truth.ActualCapability);
        Assert.Equal(ConversionComponentOutcome.Preserved, result.ExecutionRecord.Truth.Pixel);
        Assert.Equal(ConversionComponentOutcome.Degraded, result.ExecutionRecord.Truth.Metadata);
        Assert.Equal(ConversionComponentOutcome.Preserved, result.ExecutionRecord.Truth.Orientation);
        Assert.Equal(ConversionComponentOutcome.Preserved, result.ExecutionRecord.Truth.ColorIcc);
        Assert.Equal(PreservationOutcome.PartiallyPreserved, result.ExecutionRecord.PreservationOutcome);
        Assert.Equal(
            await ReadExifToolTextTagAsync(neutralPrimary, "-Orientation"),
            await ReadExifToolTextTagAsync(result.OutputArtifact.Path, "-Orientation"));
        Assert.Equal(
            Convert.ToHexString(SHA256.HashData(await ReadExifToolBinaryTagAsync(neutralPrimary, "-icc_profile"))),
            Convert.ToHexString(SHA256.HashData(await ReadExifToolBinaryTagAsync(result.OutputArtifact.Path, "-icc_profile"))));
        Assert.Equal(
            await ReadExifToolTextTagAsync(neutralPrimary, "-DateTimeOriginal"),
            await ReadExifToolTextTagAsync(result.OutputArtifact.Path, "-DateTimeOriginal"));
        Assert.Equal(liveSourceHash, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(liveSource))));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_JpegLosslessTransform_StrictFailsClosedWhenObservationFindsMetadataDegradation()
    {
        string liveSource = ResolveSample("华为-Mate80.jpg");
        SourceMediaFacts facts = await NativeMediaService.InspectMediaAsync(liveSource);
        using var workspace = new MediaWorkspace();
        string neutralPrimary = Path.Combine(workspace.RootDirectory, "huawei-primary.jpg");
        await CopyRangeAsync(liveSource, neutralPrimary, facts.PrimaryImage.ByteOffset, facts.PrimaryImage.ByteLength);

        ImageConversionResult result = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = neutralPrimary,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = "image/jpeg",
                ImageContainer = ImageContainer.Jpeg,
                ByteLength = new FileInfo(neutralPrimary).Length
            },
            TargetContainer = ImageContainer.Jpeg,
            TargetDirectory = workspace.RootDirectory,
            Transform = ImageTransformKind.Rotate90,
            PreservationPolicy = PreservationPolicy.Strict
        });

        Assert.False(result.Success);
        Assert.Equal(ConversionFailureStage.OutputValidation, result.ExecutionRecord.Truth.FailureStage);
        Assert.Equal(ConversionFailureCategory.OutputValidation, result.ExecutionRecord.Truth.FailureCategory);
        Assert.Equal(ConversionOperationKind.LosslessTransform, result.ExecutionRecord.Truth.RequestedOperationKind);
        Assert.Equal(ConversionCapability.ImageLosslessTransform, result.ExecutionRecord.Truth.SelectedCapability);
        Assert.Equal(ConversionOperationKind.Unknown, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Empty(Directory.EnumerateFiles(workspace.RootDirectory, "img-conv-*", SearchOption.TopDirectoryOnly));
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("苹果双文件.HEIC")]
    [InlineData("华为Mate80.heic")]
    // The Samsung source advertises a rotated stored grid. The libheif decode
    // contract reports the orientation-applied visual dimensions.
    [InlineData("三星.heic")]
    public async Task NativeHeicBackend_DecodesRealPrimaryPixelsWithoutMutatingSource(string sampleName)
    {
        string source = ResolveSample(sampleName);
        R5HeicExpectation expectation = RealSampleCorpusManifest.GetR5HeicExpectation(sampleName);
        CorpusSample sample = RealSampleCorpusManifest.GetRequiredSample(sampleName);
        string before = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(source)));
        NativeHeicPrimaryDecodeInfo info = await NativeMediaService.DecodeHeicPrimaryAsync(source);

        Assert.NotEqual(0u, info.PrimaryItemId);
        Assert.Equal(expectation.VisualDimensions[0], info.Width);
        Assert.Equal(expectation.VisualDimensions[1], info.Height);
        Assert.Equal(expectation.PrimaryBitDepth, info.SourceBitDepth);
        Assert.True(info.DecodedSignalBitDepth >= info.SourceBitDepth);
        Assert.Equal(expectation.PrimaryBitDepth, info.DecodedStorageBitDepth);
        Assert.Equal(sample.Sha256, before);
        Assert.Equal(before, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(source))));
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("苹果双文件.HEIC")]
    [InlineData("三星.heic")]
    public async Task NativeHeicBackend_DecodesRealAuxiliaryThroughStructuralItemIdentity(string sampleName)
    {
        string source = ResolveSample(sampleName);
        R5AuxiliaryExpectation expectation = Assert.IsType<R5AuxiliaryExpectation>(
            RealSampleCorpusManifest.GetR5HeicExpectation(sampleName).Auxiliary);
        string before = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(source)));
        (NativeResult graphResult, string? graphError, NativeAuxiliaryItemFacts[] auxiliaries) =
            W3NativeHeif.Enumerate(await File.ReadAllBytesAsync(source));
        Assert.Equal(NativeResult.Ok, graphResult);
        Assert.True(string.IsNullOrWhiteSpace(graphError), graphError);
        NativeAuxiliaryItemFacts auxiliary = Assert.Single(auxiliaries);

        using var context = NativeContext.Create();
        var decoded = new NativeHeicAuxiliaryInfo
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeHeicAuxiliaryInfo>())
        };
        NativeResult result = NativeMethods.DecodeHeicAuxiliaryImage(
            context.Handle, source, auxiliary.ItemId, ref decoded);
        context.ThrowIfFailed(result);

        Assert.Equal(auxiliary.ItemId, decoded.ItemId);
        Assert.Equal(expectation.ItemId, decoded.ItemId);
        Assert.Equal(expectation.ItemId, auxiliary.ItemId);
        Assert.Equal(expectation.VisualDimensions[0], decoded.Width);
        Assert.Equal(expectation.VisualDimensions[1], decoded.Height);
        Assert.Equal(expectation.BitDepth, decoded.SourceBitDepth);
        Assert.Equal(expectation.BitDepth, decoded.DecodedSignalBitDepth);
        Assert.Equal(expectation.BitDepth, decoded.DecodedStorageBitDepth);
        Assert.Equal(before, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(source))));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_NonGainMapHeicToJpeg_ConvertsPixelsAndEmitsTruthfulRecord()
    {
        // The Huawei live-photo source is reduced to its inspector-confirmed
        // primary range before it enters the ordinary R2 image route.
        string livePhotoSource = ResolveSample("华为Mate80.heic");
        string livePhotoSourceHash = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(livePhotoSource)));
        using var workspace = new MediaWorkspace();
        string sample = await CreateNeutralHuaweiHeicPrimaryAsync(workspace, "neutral-huawei-primary.heic");
        string before = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sample)));
        byte[] sampleBytes = await File.ReadAllBytesAsync(sample);
        Assert.True(NativeHeifBoxParser.TryLocateExifItem(
            sampleBytes, out _, out _, out string? exifItemError), exifItemError);
        SourceMediaFacts facts = await NativeMediaService.InspectMediaAsync(sample);
        Assert.Null(facts.GainMap);
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
        Assert.Equal(ConversionOperationKind.LossyReencode, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionComponentOutcome.Reencoded, result.ExecutionRecord.Truth.Pixel);
        Assert.NotEqual(ConversionComponentOutcome.NotEvaluated, result.ExecutionRecord.Truth.Metadata);
        Assert.NotEqual(ConversionComponentOutcome.NotEvaluated, result.ExecutionRecord.Truth.Orientation);
        Assert.NotEqual(ConversionComponentOutcome.NotEvaluated, result.ExecutionRecord.Truth.ColorIcc);
        await AssertExifToolOutcomesMatchAsync(sample, result.OutputArtifact.Path, result.ExecutionRecord.Truth);
        Assert.Equal(before, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sample))));
        Assert.Equal(livePhotoSourceHash, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(livePhotoSource))));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task NativeGainMapMetadata_InspectsAppleAndSamsungRealSampleMappings()
    {
        NativeGainMapMetadataV1 apple = await NativeMediaService.InspectGainMapMetadataAsync(
            ResolveSample("苹果双文件.HEIC"));
        Assert.Equal(1, apple.Kind);
        Assert.Equal(1.568873048, apple.AppleMakerNote33, 6);
        Assert.Equal(0.005930147133, apple.AppleMakerNote48, 8);

        NativeGainMapMetadataV1 samsung = await NativeMediaService.InspectGainMapMetadataAsync(
            ResolveSample("三星.heic"));
        Assert.Equal(2, samsung.Kind);
        Assert.Equal(0.0, samsung.GetGainMapMin(0), 8);
        Assert.Equal(2.3, samsung.GetGainMapMax(0), 5);
        Assert.Equal(1.0, samsung.GetGamma(0), 8);
        Assert.Equal(2.3, samsung.HdrCapacityMax, 5);
        Assert.Equal(0, samsung.BaseRenditionIsHdr);
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("苹果双文件.HEIC", "苹果双文件.MOV")]
    [InlineData("三星.heic", null)]
    public async Task NeutralPipeline_HeicGainMapToJpeg_UsesBoundUltraHdrSemanticAndPreservesInputs(
        string sampleName,
        string? companionName)
    {
        string source = ResolveSample(sampleName);
        string? companion = companionName is null ? null : ResolveSample(companionName);
        string sourceHashBefore = await ComputeSha256Async(source);
        string? companionHashBefore = companion is null ? null : await ComputeSha256Async(companion);
        using var workspace = new MediaWorkspace();
        var imageConverter = new CapturingImageConverter(new ImageConverter());

        NeutralMediaBundle bundle = await new NeutralMediaService(imageConverter: imageConverter)
            .CreateNeutralBundleAsync(
                source,
                companion,
                workspace,
                new MediaFormatRequirement
                {
                    ImageContainer = ImageContainer.Jpeg,
                    VideoContainer = VideoContainer.Unknown
                },
                PreservationPolicy.BestEffort);

        Assert.NotNull(imageConverter.Request);
        Assert.NotNull(imageConverter.Result);
        Assert.True(imageConverter.Result!.Success, imageConverter.Result.ErrorMessage);
        Assert.Equal(ImageHdrGainMapTargetSemantic.JpegIsoGainMap,
            imageConverter.Request!.TargetHdrGainMapSemantic);
        Assert.NotNull(imageConverter.Request.TrustedHdrGainMapBinding);
        Assert.Equal(ConversionOperationKind.HdrGainMapConversion,
            imageConverter.Result.ExecutionRecord.Truth.RequestedOperationKind);
        Assert.Equal(ConversionOperationKind.HdrGainMapConversion,
            imageConverter.Result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionCapability.HdrGainMapConversion,
            imageConverter.Result.ExecutionRecord.Truth.ActualCapability);
        Assert.Equal(ConversionComponentOutcome.Reencoded, imageConverter.Result.ExecutionRecord.Truth.Pixel);
        Assert.Equal(ConversionComponentOutcome.Reencoded, imageConverter.Result.ExecutionRecord.Truth.HdrGainMap);
        Assert.False(imageConverter.Result.ExecutionRecord.Truth.FallbackOccurred);
        Assert.False(imageConverter.Result.ExecutionRecord.Truth.FallbackAllowed);

        Assert.Equal(ImageContainer.Jpeg, bundle.PrimaryImage.ImageContainer);
        Assert.True(File.Exists(bundle.PrimaryImage.Path));
        SourceMediaFacts outputFacts = await NativeMediaService.InspectMediaAsync(bundle.PrimaryImage.Path);
        Assert.NotNull(outputFacts.GainMap);
        Assert.True(outputFacts.GainMap!.IsPresent);
        AuxiliaryMediaFacts outputGainMap = Assert.Single(outputFacts.AuxiliaryItems,
            item => item.Semantic == "GainMap");
        Assert.Equal(AuxiliaryRepresentation.Embedded, outputGainMap.Representation);
        Assert.Equal(AuxiliaryOwnership.Primary, outputGainMap.Ownership);
        Assert.Equal(outputFacts.GainMap.ItemId, outputGainMap.ItemId);
        ImageHdrGainMapSourceBinding outputBinding = Assert.IsType<ImageHdrGainMapSourceBinding>(bundle.HdrGainMapBinding);
        Assert.Equal(Path.GetFullPath(bundle.PrimaryImage.Path), Path.GetFullPath(outputBinding.SourcePath));
        Assert.Equal(ImageContainer.Jpeg, outputBinding.SourceContainer);
        Assert.Equal(await ComputeSha256Async(bundle.PrimaryImage.Path), outputBinding.SourceSha256);
        Assert.Equal(outputFacts.PrimarySha256, outputBinding.PrimaryImageSha256);
        Assert.Equal(outputGainMap.ItemId, outputBinding.ItemId);
        Assert.Equal(outputFacts.GainMap.AuxiliaryIndex, outputBinding.AuxiliaryIndex);
        Assert.Equal(outputGainMap.ByteOffset, outputBinding.ByteOffset);
        Assert.Equal(outputGainMap.ByteLength, outputBinding.ByteLength);
        Assert.Equal(outputGainMap.Sha256, outputBinding.GainMapSha256);
        Assert.Equal(outputGainMap.StableIdentity, outputBinding.StableIdentity);
        Assert.Equal(outputGainMap.StableIdentity, outputBinding.InspectedStableIdentity);
        Assert.Equal(outputGainMap.OwnerIdentity, outputBinding.OwnerIdentity);
        Assert.Equal(outputGainMap.OwnerIdentity, outputBinding.InspectedOwnerIdentity);
        Assert.Equal(outputGainMap.Relationship, outputBinding.Relationship);
        AuxiliaryMediaDescriptor outputDescriptor = Assert.Single(bundle.AuxiliaryMedia,
            item => item.Semantic == "GainMap");
        Assert.Equal(outputGainMap.StableIdentity, outputDescriptor.StableIdentity);
        Assert.Equal(outputGainMap.OwnerIdentity, outputDescriptor.OwnerIdentity);
        Assert.Equal(outputGainMap.Relationship, outputDescriptor.Relationship);
        Assert.Equal(outputGainMap.Container, outputDescriptor.ImageContainer);
        Assert.Equal(outputGainMap.ByteOffset, outputDescriptor.SourceOffset);
        Assert.Equal(outputGainMap.ByteLength, outputDescriptor.SourceLength);
        Assert.Equal(outputGainMap.Sha256, outputDescriptor.SourceSha256);
        NeutralArtifactManifest gainMapManifest = Assert.Single(bundle.Manifest,
            item => item.Role == "GainMap");
        Assert.Equal(outputGainMap.StableIdentity, gainMapManifest.StableIdentity);
        Assert.Equal(outputGainMap.OwnerIdentity, gainMapManifest.OwnerIdentity);
        Assert.Equal(outputGainMap.Relationship, gainMapManifest.Relationship);
        Assert.Equal(outputGainMap.ByteOffset, gainMapManifest.SourceOffset);
        Assert.Equal(outputGainMap.ByteLength, gainMapManifest.SourceLength);
        Assert.Equal(outputGainMap.Sha256, gainMapManifest.SourceSha256);
        Assert.Equal(GainMapRepresentation.Embedded,
            Assert.Single(bundle.Manifest, item => item.Role == "PrimaryImage").GainMapRepresentation);

        NativeGainMapMetadataV1 outputMetadata = await NativeMediaService.InspectGainMapMetadataAsync(
            bundle.PrimaryImage.Path);
        Assert.Equal(2, outputMetadata.Kind);
        Assert.Equal(0, outputMetadata.BaseRenditionIsHdr);
        Assert.True(outputMetadata.HdrCapacityMax > outputMetadata.HdrCapacityMin);
        Assert.Equal(sourceHashBefore, await ComputeSha256Async(source));
        if (companion is not null)
            Assert.Equal(companionHashBefore, await ComputeSha256Async(companion));
        ExportP5R3EvidenceArtifactIfRequested(bundle, sampleName);
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("荣耀.jpg")]
    [InlineData("vivo.jpg")]
    public async Task NeutralPipeline_JpegGainMapToHeic_UsesBoundIsoAuxiliarySemanticAndPreservesInputs(
        string sampleName)
    {
        string source = ResolveSample(sampleName);
        string sourceHashBefore = await ComputeSha256Async(source);
        using var workspace = new MediaWorkspace();
        var imageConverter = new CapturingImageConverter(new ImageConverter());

        NeutralMediaBundle bundle = await new NeutralMediaService(imageConverter: imageConverter)
            .CreateNeutralBundleAsync(
                source,
                secondaryPath: null,
                workspace,
                new MediaFormatRequirement
                {
                    ImageContainer = ImageContainer.Heic,
                    VideoContainer = VideoContainer.Unknown
                },
                PreservationPolicy.BestEffort);

        Assert.NotNull(imageConverter.Request);
        Assert.NotNull(imageConverter.Result);
        Assert.True(imageConverter.Result!.Success, imageConverter.Result.ErrorMessage);
        Assert.Equal(ImageHdrGainMapTargetSemantic.HeicGainMapAuxiliary,
            imageConverter.Request!.TargetHdrGainMapSemantic);
        Assert.NotNull(imageConverter.Request.TrustedHdrGainMapBinding);
        Assert.Equal(ConversionOperationKind.HdrGainMapConversion,
            imageConverter.Result.ExecutionRecord.Truth.RequestedOperationKind);
        Assert.Equal(ConversionOperationKind.HdrGainMapConversion,
            imageConverter.Result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionCapability.HdrGainMapConversion,
            imageConverter.Result.ExecutionRecord.Truth.ActualCapability);
        Assert.Equal(ConversionComponentOutcome.Reencoded, imageConverter.Result.ExecutionRecord.Truth.Pixel);
        Assert.Equal(ConversionComponentOutcome.Reencoded, imageConverter.Result.ExecutionRecord.Truth.HdrGainMap);
        Assert.False(imageConverter.Result.ExecutionRecord.Truth.FallbackOccurred);

        Assert.Equal(ImageContainer.Heic, bundle.PrimaryImage.ImageContainer);
        Assert.True(File.Exists(bundle.PrimaryImage.Path));
        SourceMediaFacts outputFacts = await NativeMediaService.InspectMediaAsync(bundle.PrimaryImage.Path);
        Assert.NotNull(outputFacts.GainMap);
        Assert.True(outputFacts.GainMap!.IsPresent);
        AuxiliaryMediaFacts outputGainMap = Assert.Single(outputFacts.AuxiliaryItems,
            item => item.Semantic == "GainMap");
        Assert.Equal(AuxiliaryRepresentation.Embedded, outputGainMap.Representation);
        Assert.Equal(AuxiliaryOwnership.Primary, outputGainMap.Ownership);
        Assert.Equal("urn:com:photo:aux:hdrgainmap", outputGainMap.Relationship);
        Assert.Equal(outputFacts.GainMap.ItemId, outputGainMap.ItemId);

        ImageHdrGainMapSourceBinding outputBinding = Assert.IsType<ImageHdrGainMapSourceBinding>(bundle.HdrGainMapBinding);
        Assert.Equal(Path.GetFullPath(bundle.PrimaryImage.Path), Path.GetFullPath(outputBinding.SourcePath));
        Assert.Equal(ImageContainer.Heic, outputBinding.SourceContainer);
        Assert.Equal(await ComputeSha256Async(bundle.PrimaryImage.Path), outputBinding.SourceSha256);
        Assert.Equal(outputFacts.PrimarySha256, outputBinding.PrimaryImageSha256);
        Assert.Equal(outputGainMap.ItemId, outputBinding.ItemId);
        Assert.Equal(outputFacts.GainMap.AuxiliaryIndex, outputBinding.AuxiliaryIndex);
        Assert.Equal(outputGainMap.ByteOffset, outputBinding.ByteOffset);
        Assert.Equal(outputGainMap.ByteLength, outputBinding.ByteLength);
        Assert.Equal(outputGainMap.Sha256, outputBinding.GainMapSha256);
        Assert.Equal(outputGainMap.StableIdentity, outputBinding.InspectedStableIdentity);
        Assert.Equal(outputGainMap.OwnerIdentity, outputBinding.InspectedOwnerIdentity);
        Assert.Equal(outputGainMap.Relationship, outputBinding.Relationship);

        NativeGainMapMetadataV1 outputMetadata = await NativeMediaService.InspectGainMapMetadataAsync(
            bundle.PrimaryImage.Path);
        Assert.Equal(2, outputMetadata.Kind);
        Assert.InRange(outputMetadata.BaseRenditionIsHdr, 0, 1);
        Assert.True(outputMetadata.HdrCapacityMax > outputMetadata.HdrCapacityMin);
        Assert.Equal(sourceHashBefore, await ComputeSha256Async(source));
        ExportP5R3EvidenceArtifactIfRequested(bundle, sampleName);
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("source-hash")]
    [InlineData("primary-hash")]
    [InlineData("gainmap-hash")]
    [InlineData("byte-range")]
    [InlineData("byte-length")]
    [InlineData("item-id")]
    [InlineData("auxiliary-index")]
    [InlineData("source-file-identity")]
    [InlineData("inspected-identity")]
    [InlineData("semantic")]
    [InlineData("owner-identity")]
    [InlineData("relationship")]
    [InlineData("representation")]
    [InlineData("ownership")]
    [InlineData("owner-role")]
    public async Task Convert_JpegGainMapToHeic_RejectsStaleBoundIdentityWithoutPublishing(string mutation)
    {
        string sample = ResolveSample("vivo.jpg");
        string sourceHashBefore = await ComputeSha256Async(sample);
        using var workspace = new MediaWorkspace();
        NeutralMediaBundle bundle = await new NeutralMediaService()
            .CreateNeutralBundleAsync(sample, null, workspace);
        ImageHdrGainMapSourceBinding binding = Assert.IsType<ImageHdrGainMapSourceBinding>(bundle.HdrGainMapBinding);
        string neutralHashBefore = await ComputeSha256Async(bundle.PrimaryImage.Path);
        ImageHdrGainMapSourceBinding staleBinding = CloneBindingWithMutation(binding, mutation);

        ImageConversionResult result = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = bundle.PrimaryImage,
            TargetContainer = ImageContainer.Heic,
            TargetDirectory = workspace.RootDirectory,
            PreservationPolicy = PreservationPolicy.BestEffort,
            TrustedSourceFacts = ImageConversionEligibility.ToConversionFacts(
                await NativeMediaService.InspectMediaAsync(bundle.PrimaryImage.Path)),
            TrustedHdrGainMapBinding = staleBinding,
            TargetHdrGainMapSemantic = ImageHdrGainMapTargetSemantic.HeicGainMapAuxiliary
        });

        Assert.False(result.Success);
        Assert.Null(result.OutputArtifact);
        Assert.Equal(ConversionOperationKind.HdrGainMapConversion, result.ExecutionRecord.Truth.RequestedOperationKind);
        Assert.Equal(ConversionFailureCategory.TrustedFactsMismatch, result.ExecutionRecord.Truth.FailureCategory);
        Assert.Empty(Directory.EnumerateFiles(workspace.RootDirectory, "img-hdr-conv-*", SearchOption.TopDirectoryOnly));
        Assert.Equal(sourceHashBefore, await ComputeSha256Async(sample));
        Assert.Equal(neutralHashBefore, await ComputeSha256Async(bundle.PrimaryImage.Path));
    }

    private static ImageHdrGainMapSourceBinding CloneBindingWithMutation(
        ImageHdrGainMapSourceBinding binding,
        string mutation) =>
        new(
            binding.SourcePath,
            mutation == "source-hash" ? new string('0', 64) : binding.SourceSha256,
            mutation == "primary-hash" ? new string('0', 64) : binding.PrimaryImageSha256,
            mutation == "source-file-identity"
                ? binding.SourceFileIdentity with { FileIndex = binding.SourceFileIdentity.FileIndex + 1 }
                : binding.SourceFileIdentity,
            binding.SourceContainer,
            binding.StableIdentity,
            mutation == "inspected-identity" ? binding.InspectedStableIdentity + ":stale" : binding.InspectedStableIdentity,
            mutation == "semantic" ? binding.Semantic + ":stale" : binding.Semantic,
            binding.OwnerIdentity,
            mutation == "owner-identity" ? binding.InspectedOwnerIdentity + ":stale" : binding.InspectedOwnerIdentity,
            mutation == "relationship" ? binding.Relationship + ":stale" : binding.Relationship,
            mutation == "gainmap-hash" ? new string('0', 64) : binding.GainMapSha256,
            mutation == "item-id" ? binding.ItemId + 1 : binding.ItemId,
            mutation == "auxiliary-index" ? binding.AuxiliaryIndex + 1 : binding.AuxiliaryIndex,
            binding.ByteOffset + (mutation == "byte-range" ? 1 : 0),
            binding.ByteLength + (mutation == "byte-length" ? 1 : 0),
            binding.SourceByteOffset,
            binding.SourceByteLength,
            mutation == "representation" ? AuxiliaryRepresentation.Detached : binding.Representation,
            mutation == "ownership" ? AuxiliaryOwnership.Auxiliary : binding.Ownership,
            mutation == "owner-role" ? MediaArtifactKind.AuxiliaryItem : binding.OwnerArtifactRole);

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("苹果双文件.HEIC")]
    [InlineData("三星.heic")]
    public async Task Convert_GainMapHeicToJpeg_FailsClosedWithoutPublishingOrMutatingSource(string sampleName)
    {
        string sample = ResolveSample(sampleName);
        string before = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sample)));
        using var workspace = new MediaWorkspace();

        SourceMediaFacts facts = await NativeMediaService.InspectMediaAsync(sample);
        Assert.NotNull(facts.GainMap);
        Assert.True(facts.GainMap!.IsPresent);

        ImageConversionResult result = await new ImageConverter().ConvertAsync(new ImageConversionRequest
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

        Assert.False(result.Success);
        Assert.Equal(ConversionOperationKind.HdrGainMapConversion, result.ExecutionRecord.Truth.RequestedOperationKind);
        Assert.Equal(ConversionOperationKind.Unsupported, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionFailureCategory.Unsupported, result.ExecutionRecord.Truth.FailureCategory);
        Assert.Null(result.OutputArtifact);
        Assert.False(result.ExecutionRecord.PixelReencoded);
        Assert.Contains("GainMap", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("forbidden", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(workspace.RootDirectory, "*.jpg", SearchOption.TopDirectoryOnly));
        Assert.Equal(before, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sample))));
    }

    [Theory]
    [InlineData("vivo.jpg", ImageContainer.Jpeg)]
    [InlineData("vivo.jpg", ImageContainer.Heic)]
    [InlineData("苹果双文件.HEIC", ImageContainer.Heic)]
    public async Task Convert_GainMapAnyOrdinaryRoute_FailsClosedWithoutPublishingOrMutatingSource(
        string sampleName,
        ImageContainer targetContainer)
    {
        string sample = ResolveSample(sampleName);
        string before = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sample)));
        SourceMediaFacts facts = await NativeMediaService.InspectMediaAsync(sample);
        Assert.True(facts.GainMap is { IsPresent: true }, $"{sampleName} must be an inspected GainMap sample.");
        using var workspace = new MediaWorkspace();

        ImageConversionResult result = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = new MediaArtifact
            {
                Path = sample,
                Kind = MediaArtifactKind.PrimaryImage,
                MimeType = facts.PrimaryImage.Container == ImageContainer.Heic ? "image/heic" : "image/jpeg",
                ImageContainer = facts.PrimaryImage.Container,
                ByteLength = new FileInfo(sample).Length
            },
            TargetContainer = targetContainer,
            TargetDirectory = workspace.RootDirectory,
            PreservationPolicy = PreservationPolicy.BestEffort
        });

        Assert.False(result.Success);
        Assert.Null(result.OutputArtifact);
        Assert.Equal(ConversionOperationKind.HdrGainMapConversion, result.ExecutionRecord.Truth.RequestedOperationKind);
        Assert.Equal(ConversionOperationKind.Unsupported, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionFailureCategory.Unsupported, result.ExecutionRecord.Truth.FailureCategory);
        Assert.Contains("GainMap", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(workspace.RootDirectory));
        Assert.Equal(before, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sample))));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_OrdinaryCallerFactsCannotHideGainMapFromNativeInspection()
    {
        string sample = ResolveSample("vivo.jpg");
        string before = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sample)));
        using var workspace = new MediaWorkspace();

        ImageConversionResult result = await new ImageConverter().ConvertAsync(new ImageConversionRequest
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
            TargetDirectory = workspace.RootDirectory,
            TrustedSourceFacts = new ImageConversionSourceFacts
            {
                Container = ImageContainer.Jpeg,
                Codec = ImageCodec.Jpeg,
                HasGainMap = false,
                HasAuxiliaryMedia = false
            }
        });

        Assert.False(result.Success);
        Assert.Null(result.OutputArtifact);
        Assert.Equal(ConversionOperationKind.HdrGainMapConversion, result.ExecutionRecord.Truth.RequestedOperationKind);
        Assert.Equal(ConversionFailureCategory.Unsupported, result.ExecutionRecord.Truth.FailureCategory);
        Assert.Contains("GainMap", result.ErrorMessage, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(workspace.RootDirectory));
        Assert.Equal(before, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sample))));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_GainMapStrictCrossContainer_PreservesR1SemanticRejectionTruth()
    {
        string sample = ResolveSample("苹果双文件.HEIC");
        string before = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sample)));
        using var workspace = new MediaWorkspace();

        ImageConversionResult result = await new ImageConverter().ConvertAsync(new ImageConversionRequest
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
            PreservationPolicy = PreservationPolicy.Strict
        });

        Assert.False(result.Success);
        Assert.Null(result.OutputArtifact);
        Assert.Equal(ConversionOperationKind.HdrGainMapConversion, result.ExecutionRecord.Truth.RequestedOperationKind);
        Assert.Equal(ConversionOperationKind.Unsupported, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionFailureCategory.Unsupported, result.ExecutionRecord.Truth.FailureCategory);
        Assert.Empty(Directory.EnumerateFiles(workspace.RootDirectory));
        Assert.Equal(before, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sample))));
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("vivo.jpg", ImageContainer.Jpeg, ImageContainer.Heic, false)]
    [InlineData("三星.heic", ImageContainer.Heic, ImageContainer.Jpeg, false)]
    public async Task Convert_BoundGainMapStrict_PreservesHdrOrFailsClosedWithoutFallback(
        string sampleName,
        ImageContainer sourceContainer,
        ImageContainer targetContainer,
        bool expectedSuccess)
    {
        string source = ResolveSample(sampleName);
        string sourceHash = await ComputeSha256Async(source);
        using var workspace = new MediaWorkspace();
        NeutralMediaBundle bundle = await new NeutralMediaService()
            .CreateNeutralBundleAsync(source, secondaryPath: null, workspace);
        ImageHdrGainMapSourceBinding binding = Assert.IsType<ImageHdrGainMapSourceBinding>(bundle.HdrGainMapBinding);
        SourceMediaFacts facts = await NativeMediaService.InspectMediaAsync(bundle.PrimaryImage.Path);

        ImageConversionResult result = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = bundle.PrimaryImage,
            TargetContainer = targetContainer,
            TargetDirectory = workspace.RootDirectory,
            PreservationPolicy = PreservationPolicy.Strict,
            TrustedSourceFacts = ImageConversionEligibility.ToConversionFacts(facts),
            TrustedHdrGainMapBinding = binding,
            TargetHdrGainMapSemantic = targetContainer == ImageContainer.Jpeg
                ? ImageHdrGainMapTargetSemantic.JpegIsoGainMap
                : ImageHdrGainMapTargetSemantic.HeicGainMapAuxiliary
        });

        Assert.Equal(sourceContainer, bundle.PrimaryImage.ImageContainer);
        Assert.False(result.ExecutionRecord.Truth.FallbackAllowed);
        Assert.False(result.ExecutionRecord.Truth.FallbackOccurred);
        Assert.True(result.Success == expectedSuccess,
            $"Strict outcome for {sampleName}: success={result.Success}; stage={result.ExecutionRecord.Truth.FailureStage}; " +
            $"category={result.ExecutionRecord.Truth.FailureCategory}; preservation={result.ExecutionRecord.PreservationOutcome}; " +
            $"metadata={result.ExecutionRecord.Truth.Metadata}; orientation={result.ExecutionRecord.Truth.Orientation}; " +
            $"icc={result.ExecutionRecord.Truth.ColorIcc}; error={result.ErrorMessage}");
        if (expectedSuccess)
        {
            MediaArtifact output = Assert.IsType<MediaArtifact>(result.OutputArtifact);
            Assert.True(File.Exists(output.Path));
            Assert.Equal(ConversionOperationKind.HdrGainMapConversion,
                result.ExecutionRecord.Truth.ActualOperationKind);
            Assert.Equal(ConversionComponentOutcome.Reencoded, result.ExecutionRecord.Truth.HdrGainMap);
            Assert.Equal(PreservationOutcome.Preserved, result.ExecutionRecord.PreservationOutcome);
            SourceMediaFacts outputFacts = await NativeMediaService.InspectMediaAsync(output.Path);
            Assert.True(outputFacts.GainMap is { IsPresent: true });
        }
        else
        {
            Assert.Null(result.OutputArtifact);
            Assert.Equal(ConversionFailureStage.OutputValidation, result.ExecutionRecord.Truth.FailureStage);
            Assert.Equal(ConversionFailureCategory.OutputValidation, result.ExecutionRecord.Truth.FailureCategory);
            Assert.Equal(ConversionOperationKind.Unknown, result.ExecutionRecord.Truth.ActualOperationKind);
            Assert.Equal(ConversionCapability.HdrGainMapConversion, result.ExecutionRecord.Truth.SelectedCapability);
            Assert.Equal(PreservationOutcome.PartiallyPreserved, result.ExecutionRecord.PreservationOutcome);
            Assert.Contains("Strict HDR/GainMap conversion could not prove", result.ErrorMessage, StringComparison.Ordinal);
            Assert.Empty(Directory.EnumerateFiles(workspace.RootDirectory, "img-hdr-conv-*", SearchOption.TopDirectoryOnly));
        }

        Assert.Equal(sourceHash, await ComputeSha256Async(source));
    }

    [Theory]
    [Trait("Category", "RealSamples")]
    [InlineData("GainMapMix", PreservationPolicy.BestEffort, HdrOutputPolicy.AllowSdrDegradation, true)]
    [InlineData("GainMapMix", PreservationPolicy.AllowDiscard, HdrOutputPolicy.PreserveHdr, false)]
    [InlineData("GainMapMix", PreservationPolicy.Strict, HdrOutputPolicy.AllowSdrDegradation, false)]
    [InlineData("GainMapMin", PreservationPolicy.BestEffort, HdrOutputPolicy.PreserveHdr, false)]
    public async Task Convert_IncompleteOrAmbiguousIsoMetadata_FailsWithoutSdrFallbackOrPublication(
        string replacementTag,
        PreservationPolicy preservationPolicy,
        HdrOutputPolicy hdrOutputPolicy,
        bool expectedFallbackAllowed)
    {
        string sample = ResolveSample("vivo.jpg");
        string sampleHash = await ComputeSha256Async(sample);
        using var workspace = new MediaWorkspace();
        string malformedPath = workspace.AllocateFilePath("malformed-vivo-gainmap", ".jpg");
        File.Copy(sample, malformedPath);
        byte[] malformedBytes = await File.ReadAllBytesAsync(malformedPath);
        byte[] originalTag = Encoding.ASCII.GetBytes("GainMapMax");
        int tagOffset = malformedBytes.AsSpan().IndexOf(originalTag);
        Assert.True(tagOffset >= 0, "The canonical GainMap XMP packet must contain its complete Max field.");
        Assert.Equal(-1, malformedBytes.AsSpan(tagOffset + originalTag.Length).IndexOf(originalTag));
        byte[] replacement = Encoding.ASCII.GetBytes(replacementTag);
        Assert.Equal(originalTag.Length, replacement.Length);
        replacement.CopyTo(malformedBytes, tagOffset);
        await File.WriteAllBytesAsync(malformedPath, malformedBytes);

        NeutralMediaBundle bundle = await new NeutralMediaService()
            .CreateNeutralBundleAsync(malformedPath, secondaryPath: null, workspace);
        ImageHdrGainMapSourceBinding binding = Assert.IsType<ImageHdrGainMapSourceBinding>(bundle.HdrGainMapBinding);
        SourceMediaFacts facts = await NativeMediaService.InspectMediaAsync(bundle.PrimaryImage.Path);
        ImageConversionResult result = await new ImageConverter().ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = bundle.PrimaryImage,
            TargetContainer = ImageContainer.Heic,
            TargetDirectory = workspace.RootDirectory,
            PreservationPolicy = preservationPolicy,
            HdrOutputPolicy = hdrOutputPolicy,
            TrustedSourceFacts = ImageConversionEligibility.ToConversionFacts(facts),
            TrustedHdrGainMapBinding = binding,
            TargetHdrGainMapSemantic = ImageHdrGainMapTargetSemantic.HeicGainMapAuxiliary
        });

        Assert.False(result.Success);
        Assert.Null(result.OutputArtifact);
        Assert.Equal(expectedFallbackAllowed, result.ExecutionRecord.Truth.FallbackAllowed);
        Assert.False(result.ExecutionRecord.Truth.FallbackOccurred);
        Assert.NotEqual(ConversionOperationKind.DegradedOutput, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.NotEqual(PreservationOutcome.DegradedToSdr, result.ExecutionRecord.PreservationOutcome);
        Assert.Empty(Directory.EnumerateFiles(workspace.RootDirectory, "img-hdr-conv-*", SearchOption.TopDirectoryOnly));
        Assert.Equal(sampleHash, await ComputeSha256Async(sample));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(malformedBytes)), await ComputeSha256Async(malformedPath));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task NativeHdrGainMapTransaction_AbortCleansStageAndRejectsTokenReplay()
    {
        string sample = ResolveSample("vivo.jpg");
        string sourceHash = await ComputeSha256Async(sample);
        using var workspace = new MediaWorkspace();
        NeutralMediaBundle bundle = await new NeutralMediaService()
            .CreateNeutralBundleAsync(sample, secondaryPath: null, workspace);
        ImageHdrGainMapSourceBinding binding = Assert.IsType<ImageHdrGainMapSourceBinding>(bundle.HdrGainMapBinding);
        string outputPath = Path.Combine(workspace.RootDirectory, "aborted-gainmap.heic");

        using NativeHdrGainMapConversionTransaction transaction = await NativeMediaService.StageJpegGainMapToHeicAsync(
            bundle.PrimaryImage.Path, outputPath, binding, bundle.PrimaryImage.ByteLength, 90,
            HdrOutputPolicy.PreserveHdr);
        string stagePath = transaction.StagingPath;
        ulong token = transaction.Result.TransactionToken;
        Assert.True(File.Exists(stagePath));

        transaction.Abort();

        Assert.False(File.Exists(stagePath));
        Assert.False(File.Exists(outputPath));
        FieldInfo contextField = typeof(NativeHdrGainMapConversionTransaction)
            .GetField("_context", BindingFlags.Instance | BindingFlags.NonPublic)!;
        NativeContext context = Assert.IsType<NativeContext>(contextField.GetValue(transaction));
        NativeResult replayedCommit = NativeMethods.CommitHdrGainMapV1(context.Handle, token);
        Assert.NotEqual(NativeResult.Ok, replayedCommit);
        Assert.Throws<InvalidOperationException>(() => transaction.Inspect());
        Assert.Equal(sourceHash, await ComputeSha256Async(sample));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task NativeHdrGainMapTransaction_NoReplaceCollisionKeepsExistingTargetAndCleansStage()
    {
        string sample = ResolveSample("vivo.jpg");
        string sourceHash = await ComputeSha256Async(sample);
        using var workspace = new MediaWorkspace();
        NeutralMediaBundle bundle = await new NeutralMediaService()
            .CreateNeutralBundleAsync(sample, secondaryPath: null, workspace);
        ImageHdrGainMapSourceBinding binding = Assert.IsType<ImageHdrGainMapSourceBinding>(bundle.HdrGainMapBinding);
        string outputPath = Path.Combine(workspace.RootDirectory, "existing-target.heic");
        byte[] sentinel = Encoding.ASCII.GetBytes("existing-user-target");
        await File.WriteAllBytesAsync(outputPath, sentinel);

        using (NativeHdrGainMapConversionTransaction transaction = await NativeMediaService.StageJpegGainMapToHeicAsync(
            bundle.PrimaryImage.Path, outputPath, binding, bundle.PrimaryImage.ByteLength, 90,
            HdrOutputPolicy.PreserveHdr))
        {
            string stagePath = transaction.StagingPath;
            Assert.True(File.Exists(stagePath));
            Assert.ThrowsAny<Exception>(() => transaction.Commit());
            transaction.Abort();
            Assert.False(File.Exists(stagePath));
        }

        Assert.Equal(sentinel, await File.ReadAllBytesAsync(outputPath));
        Assert.Equal(sourceHash, await ComputeSha256Async(sample));
    }

    [Fact]
    public void ImagePostValidation_OrientationWithoutTag_IsNotApplicableEvenWhenExifExists()
    {
        var before = new PreservationObservation { Flags = 0x00000001u, Orientation = 0 };
        var after = new PreservationObservation { Flags = 0x00000001u, Orientation = 0 };

        Assert.Equal(
            ConversionComponentOutcome.NotApplicable,
            ImagePostValidator.OrientationOutcome(before, after));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_NonGainMapHeicToJpeg_UsesNativeCodecAndSupportsPerfectDctTransform()
    {
        string sample = ResolveSample("华为Mate80.heic");
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
        using var workspace = new MediaWorkspace();
        string sample = await CreateNeutralHuaweiJpegPrimaryAsync(workspace, "neutral-jpeg-to-heic.jpg");
        string sourceHash = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sample)));

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
        Assert.Equal(ConversionFactsOrigin.CompatibilityInspectionOrProbe, result.ExecutionRecord.Truth.FactsOrigin);
        Assert.Equal(ConversionOperationKind.LossyReencode, result.ExecutionRecord.Truth.ActualOperationKind);
        Assert.Equal(ConversionComponentOutcome.Reencoded, result.ExecutionRecord.Truth.Pixel);
        Assert.NotEqual(ConversionComponentOutcome.NotEvaluated, result.ExecutionRecord.Truth.Metadata);
        Assert.NotEqual(ConversionComponentOutcome.NotEvaluated, result.ExecutionRecord.Truth.Orientation);
        Assert.NotEqual(ConversionComponentOutcome.NotEvaluated, result.ExecutionRecord.Truth.ColorIcc);
        await AssertExifToolOutcomesMatchAsync(sample, result.OutputArtifact.Path, result.ExecutionRecord.Truth);

        using var context = NativeContext.Create();
        var facts = new NativeHeicImageInfo
        {
            StructSize = checked((uint)Marshal.SizeOf<NativeHeicImageInfo>())
        };
        NativeResult inspection = NativeMethods.InspectHeicImage(context.Handle, result.OutputArtifact.Path, ref facts);
        context.ThrowIfFailed(inspection);
        Assert.True(facts.Width > 0 && facts.Height > 0);
        Assert.Equal(8u, facts.SourceBitDepth);
        Assert.Equal(1, facts.HasIcc);
        Assert.True(facts.HasNclx == 1 || facts.HasIcc == 1);
        Assert.Equal(sourceHash, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(sample))));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task NativeHeicBackend_EncodesSecondCodecImageForProjectOwnedAuxiliaryAssembly()
    {
        string source = ResolveSample("oppo.jpg");
        string before = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(source)));
        using var workspace = new MediaWorkspace();
        string output = Path.Combine(workspace.RootDirectory, "two-coded-images.heic");

        // This deliberately proves codec staging, not an invented GainMap
        // semantic: a project-owned writer must still validate and establish
        // auxC/auxl before this second image can be called an auxiliary.
        NativeHeicEncodedImagesInfo encoded = await NativeMediaService.EncodeHeicPrimaryAndSecondaryJpegsAsync(
            source, source, output, quality: 90);

        Assert.True(File.Exists(output));
        Assert.NotEqual(0u, encoded.PrimaryItemId);
        Assert.NotEqual(0u, encoded.SecondaryItemId);
        Assert.NotEqual(encoded.PrimaryItemId, encoded.SecondaryItemId);
        await AssertReadableByExifToolAsync(output);

        W3IndependentHeifGraph graph = W3IndependentHeifParser.Parse(await File.ReadAllBytesAsync(output));
        Assert.Equal(encoded.PrimaryItemId, graph.PrimaryItemId);
        W3IndependentHeifItem secondary = Assert.Single(graph.Items, item => item.ItemId == encoded.SecondaryItemId);
        Assert.Equal("hvc1", secondary.ItemType);
        Assert.NotEmpty(secondary.Ranges);
        Assert.All(secondary.Ranges, range => Assert.True(range.Length > 0));
        Assert.Empty(graph.Auxiliaries);
        Assert.Equal(before, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(source))));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Convert_TruncatedHeic_FailsClosedWithoutPublishingOutputOrMutatingSource()
    {
        string source = ResolveSample("苹果双文件.HEIC");
        string before = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(source)));
        using var workspace = new MediaWorkspace();
        string truncated = Path.Combine(workspace.RootDirectory, "truncated.heic");
        string output = Path.Combine(workspace.RootDirectory, "must-not-publish.jpg");
        await using (FileStream input = File.OpenRead(source))
        await using (FileStream destination = File.Create(truncated))
        {
            byte[] prefix = new byte[1024];
            int count = await input.ReadAsync(prefix);
            await destination.WriteAsync(prefix.AsMemory(0, count));
        }

        await Assert.ThrowsAnyAsync<Exception>(() => NativeMediaService.ConvertImageAsync(
            truncated, output, ImageContainer.Jpeg, quality: 90));
        Assert.False(File.Exists(output));
        Assert.Equal(before, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(source))));
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task HeicConverter_HdrGainMapWithoutP5SemanticEngine_FailsClosedInsteadOfPlainSdrFallback()
    {
        string source = ResolveSample("苹果双文件.HEIC");
        string before = Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(source)));
        using var workspace = new MediaWorkspace();

        InvalidOperationException error = await Assert.ThrowsAsync<InvalidOperationException>(
            () => HeicConverterService.ConvertToJpegAsync(source, workspace.RootDirectory));

        Assert.Contains("plain JPEG fallback is forbidden", error.Message, StringComparison.OrdinalIgnoreCase);
        Assert.Empty(Directory.EnumerateFiles(workspace.RootDirectory, "*.jpg", SearchOption.TopDirectoryOnly));
        Assert.Equal(before, Convert.ToHexString(await SHA256.HashDataAsync(File.OpenRead(source))));
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
        Assert.Empty(Directory.EnumerateFiles(workspace.RootDirectory));
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
        // An MP4 video is never a JPEG/HEIC image input for the Native codec boundary.
        var result = await converter.ConvertAsync(new ImageConversionRequest
        {
            SourceArtifact = artifact,
            TargetContainer = ImageContainer.Heic,
            TargetDirectory = workspace.RootDirectory,
            PreservationPolicy = PreservationPolicy.AllowDiscard
        });

        Assert.False(result.Success);
    }

    private static void ExportP5R3EvidenceArtifactIfRequested(NeutralMediaBundle bundle, string sampleName)
    {
        string? outputDirectory = Environment.GetEnvironmentVariable("LPB_P5_R3_OUTPUT_DIR");
        if (string.IsNullOrWhiteSpace(outputDirectory)) return;

        string outputName = sampleName switch
        {
            "荣耀.jpg" => "honor-jpeg-to-heic.heic",
            "vivo.jpg" => "vivo-jpeg-to-heic.heic",
            "苹果双文件.HEIC" => "apple-heic-to-jpeg.jpg",
            "三星.heic" => "samsung-heic-to-jpeg.jpg",
            _ => throw new InvalidOperationException($"Unexpected P5-R3 evidence source '{sampleName}'.")
        };

        Directory.CreateDirectory(outputDirectory);
        string outputPath = Path.Combine(outputDirectory, outputName);
        if (File.Exists(outputPath))
            throw new IOException($"Refusing to overwrite existing P5-R3 evidence artifact '{outputPath}'.");
        File.Copy(bundle.PrimaryImage.Path, outputPath, overwrite: false);
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using FileStream stream = File.OpenRead(path);
        return Convert.ToHexString(await SHA256.HashDataAsync(stream));
    }

    private sealed class CapturingImageConverter(IImageConverter inner) : IImageConverter
    {
        public ImageConversionRequest? Request { get; private set; }
        public ImageConversionResult? Result { get; private set; }

        public async Task<ImageConversionResult> ConvertAsync(
            ImageConversionRequest request,
            System.Threading.CancellationToken cancellationToken = default)
        {
            Request = request;
            Result = await inner.ConvertAsync(request, cancellationToken);
            return Result;
        }
    }
}
