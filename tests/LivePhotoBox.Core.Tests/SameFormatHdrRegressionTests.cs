using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
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
    [InlineData("一加.jpg")]
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
                "-s", "-AuxiliaryImageType", "-HDRHeadroom", "-HDRGain",
                "-HDRGainMapVersion", "-GainMapMax");

            Assert.Contains("urn:com:apple:photo:2020:aux:hdrgainmap", tags);
            Assert.Contains("HDRHeadroom", tags);
            Assert.Contains("HDRGain", tags);
            Assert.Contains("0.1.0.0", tags);
            Assert.DoesNotContain("GainMapMax", tags);
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

    // 回归：vivo/一加/小米/三星 的增益图藏在 XMP 容器清单里（Item:Length），
    // exiftool -GainMapImage 会错取成追加的视频；这里直接对原始多段文件转换，
    // 必须成功且 MakerNote 读回的 headroom 与源 hdrgm:HDRCapacityMax 一致。
    [Theory]
    [InlineData("vivo.jpg")]
    [InlineData("一加.jpg")]
    [InlineData("小米.jpg")]
    [InlineData("三星.jpg")]
    [InlineData("荣耀.jpg")]
    public async Task StandardConversion_BrandJpegToHeic_ExtractsGainMapAndWritesMatchingHeadroom(string sampleName)
    {
        string source = ResolveSample(sampleName);
        string outputDir = CreateTempDirectory();

        try
        {
            double? sourceHeadroom = ReadSourceHdrCapacityHeadroom(source);
            Assert.NotNull(sourceHeadroom);

            string converted = await StandardHdrConversionService.ConvertJpegToHeicAsync(
                source, outputDir, CancellationToken.None);

            string tags = await ReadExifTagsAsync(
                converted,
                "-s", "-n", "-AuxiliaryImageType", "-HDRHeadroom", "-HDRGain");

            Assert.Contains("urn:com:apple:photo:2020:aux:hdrgainmap", tags);

            double? maker33 = ParseTagDouble(tags, "HDRHeadroom");
            double? maker48 = ParseTagDouble(tags, "HDRGain");
            Assert.NotNull(maker33);
            Assert.NotNull(maker48);

            double readback = ComputeAppleHeadroom(maker33!.Value, maker48!.Value);
            Assert.True(
                Math.Abs(readback - sourceHeadroom!.Value) < 0.05,
                $"MakerNote headroom readback {readback:F3} does not match source headroom "
                + $"{sourceHeadroom.Value:F3} for {sampleName}.");
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

    private static double? ReadSourceHdrCapacityHeadroom(string path)
    {
        // hdrgm 属性可能在主 XMP 或增益图 JPEG 自带 XMP 里，全文件字节扫描。
        byte[] data = File.ReadAllBytes(path);
        string text = Encoding.Latin1.GetString(data);
        Match m = Regex.Match(text, @"hdrgm:HDRCapacityMax\s*=\s*""([^""]+)""");
        if (!m.Success
            || !double.TryParse(
                m.Groups[1].Value,
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double capMax))
        {
            return null;
        }

        return Math.Pow(2.0, capMax);
    }

    private static double? ParseTagDouble(string tags, string tagName)
    {
        foreach (string line in tags.Split('\n'))
        {
            int sep = line.IndexOf(':');
            if (sep < 0)
            {
                continue;
            }

            if (!line[..sep].Trim().Equals(tagName, StringComparison.Ordinal))
            {
                continue;
            }

            if (double.TryParse(
                line[(sep + 1)..].Trim(),
                NumberStyles.Float,
                CultureInfo.InvariantCulture,
                out double value))
            {
                return value;
            }
        }

        return null;
    }

    private static double ComputeAppleHeadroom(double maker33, double maker48)
    {
        double stops = maker33 < 1.0
            ? (maker48 <= 0.01 ? -20.0 * maker48 + 1.8 : -0.101 * maker48 + 1.601)
            : (maker48 <= 0.01 ? -70.0 * maker48 + 3.0 : -0.303 * maker48 + 2.303);
        return Math.Pow(2.0, Math.Max(stops, 0.0));
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
