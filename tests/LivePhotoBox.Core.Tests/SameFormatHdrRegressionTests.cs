using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System.Diagnostics;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class SameFormatHdrRegressionTests
{
    [Fact]
    public async Task SplitJpeg_KeepFormat_PreservesGoogleGainMap()
    {
        string source = ResolveSample("荣耀.jpg");
        string outputDir = CreateTempDirectory();

        LivePhotoSplitResult result = await LivePhotoSplitService.SplitAsync(
            source, outputDir, protocolIndex: 0, outputFormatIndex: 0, CancellationToken.None);

        try
        {
            Assert.True(File.Exists(result.ImageOutputPath), "Split did not produce an image output.");

            string tags = await ReadExifTagsAsync(
                result.ImageOutputPath,
                "-s", "-GainMapImage", "-DirectoryItemSemantic", "-DirectoryItemMime");

            Assert.Contains("GainMapImage", tags);
            Assert.Contains("Primary, GainMap", tags);
            Assert.Contains("image/jpeg", tags);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Theory]
    [InlineData("ONEPLUS_test.jpg")]
    [InlineData("vivo.jpg")]
    public async Task SplitJpeg_HdrPlusMotionPhoto_RemovesMotionPhotoButKeepsGainMap(string sampleName)
    {
        string source = ResolveSample(sampleName);
        string outputDir = CreateTempDirectory();

        LivePhotoSplitResult result = await LivePhotoSplitService.SplitAsync(
            source, outputDir, protocolIndex: 0, outputFormatIndex: 0, CancellationToken.None);

        try
        {
            Assert.True(File.Exists(result.ImageOutputPath), "Split did not produce an image output.");

            string tags = await ReadExifTagsAsync(
                result.ImageOutputPath,
                "-s", "-GainMapImage", "-DirectoryItemSemantic", "-DirectoryItemMime");

            Assert.Contains("GainMapImage", tags);
            Assert.Contains("Primary, GainMap", tags);
            Assert.DoesNotContain("MotionPhoto", tags);
            Assert.DoesNotContain("video/mp4", tags);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task SplitHeic_KeepFormat_PreservesAppleHdrGainMap()
    {
        string source = ResolveSample("谷歌自己合成的.heic");
        string outputDir = CreateTempDirectory();

        LivePhotoSplitResult result = await LivePhotoSplitService.SplitAsync(
            source, outputDir, protocolIndex: 0, outputFormatIndex: 0, CancellationToken.None);

        try
        {
            Assert.True(File.Exists(result.ImageOutputPath), "Split did not produce an image output.");

            string tags = await ReadExifTagsAsync(
                result.ImageOutputPath,
                "-s", "-AuxiliaryImageType");

            Assert.Contains("urn:com:apple:photo:2020:aux:hdrgainmap", tags);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task SplitHeic_AppleTargetHeicOutput_PreservesHdrGainMap()
    {
        string source = ResolveSample("谷歌自己合成的.heic");
        string outputDir = CreateTempDirectory();

        LivePhotoSplitResult result = await LivePhotoSplitService.SplitAsync(
            source, outputDir, protocolIndex: 1, outputFormatIndex: 2, CancellationToken.None);

        try
        {
            Assert.True(File.Exists(result.ImageOutputPath), "Split did not produce an image output.");

            string tags = await ReadExifTagsAsync(
                result.ImageOutputPath,
                "-s", "-AuxiliaryImageType", "-ContentIdentifier");

            Assert.Contains("urn:com:apple:photo:2020:aux:hdrgainmap", tags);
            Assert.Contains("ContentIdentifier", tags);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task MergeJpeg_MotionPhotoV2_PreservesGoogleGainMap()
    {
        string source = ResolveSample("荣耀.jpg");
        string outputDir = CreateTempDirectory();

        LivePhotoSplitResult split = await LivePhotoSplitService.SplitAsync(
            source, outputDir, protocolIndex: 0, outputFormatIndex: 0, CancellationToken.None);

        try
        {
            string mergedPath = Path.Combine(outputDir, "merged_hdr.jpg");
            await LivePhotoMergeService.WriteLivePhotoAsync(
                split.ImageOutputPath,
                split.VideoOutputPath,
                mergedPath,
                selectedModeIndex: 2,
                CancellationToken.None,
                outputFormatIndex: ProtocolFormatMatrix.FormatJpgMp4);

            string tags = await ReadExifTagsAsync(
                mergedPath,
                "-s", "-GainMapImage", "-DirectoryItemSemantic");

            Assert.Contains("GainMapImage", tags);
            Assert.Contains("Primary, GainMap", tags);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task MergeHeic_MotionPhotoV2_PreservesAppleHdrGainMap()
    {
        string source = ResolveSample("谷歌自己合成的.heic");
        string outputDir = CreateTempDirectory();

        LivePhotoSplitResult split = await LivePhotoSplitService.SplitAsync(
            source, outputDir, protocolIndex: 0, outputFormatIndex: 0, CancellationToken.None);

        try
        {
            string mergedPath = Path.Combine(outputDir, "merged_hdr.heic");
            await LivePhotoMergeService.WriteLivePhotoAsync(
                split.ImageOutputPath,
                split.VideoOutputPath,
                mergedPath,
                selectedModeIndex: 2,
                CancellationToken.None,
                outputFormatIndex: ProtocolFormatMatrix.FormatHeicMov);

            string tags = await ReadExifTagsAsync(
                mergedPath,
                "-s", "-AuxiliaryImageType");

            Assert.Contains("urn:com:apple:photo:2020:aux:hdrgainmap", tags);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task StandardConversion_JpegUltraHdrToHeic_WritesAppleHdrGainMap()
    {
        string source = ResolveSample("荣耀.jpg");
        string outputDir = CreateTempDirectory();

        LivePhotoSplitResult split = await LivePhotoSplitService.SplitAsync(
            source, outputDir, protocolIndex: 0, outputFormatIndex: 0, CancellationToken.None);

        try
        {
            string converted = await StandardHdrConversionService.ConvertJpegToHeicAsync(
                split.ImageOutputPath, outputDir, CancellationToken.None);

            string tags = await ReadExifTagsAsync(
                converted,
                "-s", "-AuxiliaryImageType");

            Assert.Contains("urn:com:apple:photo:2020:aux:hdrgainmap", tags);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task StandardConversion_AppleHeicToJpeg_WritesGoogleUltraHdrGainMap()
    {
        string source = ResolveSample("苹果双文件.HEIC");
        string outputDir = CreateTempDirectory();

        try
        {
            string converted = await StandardHdrConversionService.ConvertHeicToJpegAsync(
                source, outputDir, CancellationToken.None);

            string tags = await ReadExifTagsAsync(
                converted,
                "-s", "-GainMapImage", "-DirectoryItemSemantic", "-DirectoryItemMime");

            Assert.Contains("GainMapImage", tags);
            Assert.Contains("Primary, GainMap", tags);
            Assert.Contains("image/jpeg", tags);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    private static string ResolveSample(string fileName)
    {
        string path = Path.Combine(AppContext.BaseDirectory, "samples", fileName);
        if (!File.Exists(path))
        {
            throw new FileNotFoundException($"Sample not found: {path}");
        }

        return path;
    }

    private static string CreateTempDirectory()
    {
        string path = Path.Combine(Path.GetTempPath(), $"lpb_hdr_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
    }

    private static async Task<string> ReadExifTagsAsync(string filePath, params string[] args)
    {
        string? exifToolPath = ExternalToolLocator.FindExifTool();
        if (string.IsNullOrEmpty(exifToolPath))
        {
            throw new InvalidOperationException("exiftool.exe was not found.");
        }

        var psi = new ProcessStartInfo
        {
            FileName = exifToolPath,
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        };

        foreach (string arg in args)
        {
            psi.ArgumentList.Add(arg);
        }

        psi.ArgumentList.Add(filePath);

        using var process = Process.Start(psi)
            ?? throw new InvalidOperationException("Failed to start exiftool.");

        string stdout = await process.StandardOutput.ReadToEndAsync();
        string stderr = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();

        if (process.ExitCode != 0 && stderr.Contains("Error", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"exiftool failed: {stderr.Trim()}");
        }

        return stdout;
    }

    private static void TryDeleteDirectory(string path)
    {
        try
        {
            if (Directory.Exists(path))
            {
                Directory.Delete(path, recursive: true);
            }
        }
        catch
        {
            // Best-effort cleanup; test runners may hold file handles briefly.
        }
    }
}
