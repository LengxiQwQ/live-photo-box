using System.Text;
using LivePhotoBox.Services;
using LivePhotoBox.Services.Protocols;
using Xunit;

namespace LivePhotoBox.Core.Tests;

/// <summary>
/// "写入操作历史记录"开关（IsDetailedHistoryEnabled）回归测试：
/// 开关只控制条目详细程度（完整 vs 简略），命名空间/版本/时间标识始终写入，
/// 历史页仍能识别本软件处理的照片。
/// </summary>
public sealed class HistoryDetailLevelTests
{
    [Fact]
    public async Task DetailedHistoryDisabled_UnifiedMarker_WritesLightweightButKeepsNamespace()
    {
        string source = ResolveSample("荣耀.jpg");
        string outputDir = CreateTempDirectory();
        string target = Path.Combine(outputDir, "light.jpg");

        try
        {
            File.Copy(source, target, overwrite: true);

            AppSettingsService.SetValue("IsDetailedHistoryEnabled", false);
            try
            {
                bool ok = await XmpMarkerService.TryWriteUnifiedMarkerAsync(
                    target, "Split", "Source=OPPO;Target=None;Image=JPG;Video=MP4",
                    CancellationToken.None);
                Assert.True(ok, "TryWriteUnifiedMarkerAsync failed to write lightweight marker");

                string? xmp = await XmpMarkerService.ReadXmpTextAsync(
                    target, CancellationToken.None);
                Assert.False(string.IsNullOrWhiteSpace(xmp));

                // 命名空间/版本/时间标识始终写入。
                Assert.Contains("xmlns:LivePhotoBox=", xmp!);
                Assert.Contains("LivePhotoBox:Version=", xmp);
                Assert.Contains("LivePhotoBox:Timestamp=", xmp);

                // 关闭开关只写简略标记：无时间戳、无详情字段。
                Assert.Contains("LivePhotoBox:Split@@v", xmp);
                Assert.DoesNotContain("Source=OPPO", xmp);
                Assert.DoesNotContain("Image=JPG", xmp);
            }
            finally
            {
                AppSettingsService.SetValue("IsDetailedHistoryEnabled", true);
            }
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task DetailedHistoryDisabled_MergeEmbed_WritesLightweightButKeepsNamespace()
    {
        string source = ResolveSample("荣耀.jpg");
        string outputDir = CreateTempDirectory();

        try
        {
            AppSettingsService.SetValue("IsDetailedHistoryEnabled", false);
            try
            {
                var protocol = new MotionPhotoV2Protocol();
                byte[] xmpBytes = protocol.BuildXmpMetadata(
                    1234, 0, "image/jpeg", "0", "video/mp4");

                byte[] result = await XmpMarkerService.EmbedMergeHistoryAsync(
                    source, xmpBytes, "Source=OPPO;Target=MotionPhotoV2;KeyPhoto=1.5",
                    CancellationToken.None);

                string xmpText = Encoding.UTF8.GetString(result);
                Assert.Contains("LivePhotoBox:Merge@@v", xmpText);
                Assert.DoesNotContain("Target=MotionPhotoV2", xmpText);
                // 命名空间标识始终写入。
                Assert.Contains("xmlns:LivePhotoBox=", xmpText);
            }
            finally
            {
                AppSettingsService.SetValue("IsDetailedHistoryEnabled", true);
            }
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task DetailedHistoryEnabled_StillWritesFullEntries()
    {
        string source = ResolveSample("荣耀.jpg");
        string outputDir = CreateTempDirectory();
        string target = Path.Combine(outputDir, "full.jpg");

        try
        {
            File.Copy(source, target, overwrite: true);

            AppSettingsService.SetValue("IsDetailedHistoryEnabled", true);
            bool ok = await XmpMarkerService.TryWriteUnifiedMarkerAsync(
                target, "Repair", "Source=None;Target=None;Fix=Rotation+Thumbnail",
                CancellationToken.None);
            Assert.True(ok);

            var entries = await XmpMarkerService.ReadExistingEntriesAsync(
                target, CancellationToken.None);
            var repair = entries.SingleOrDefault(e => e.StartsWith(
                "LivePhotoBox:Repair@", StringComparison.Ordinal));
            Assert.NotNull(repair);
            Assert.Contains("Fix=Rotation+Thumbnail", repair);
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
        string path = Path.Combine(Path.GetTempPath(), $"lpb_detail_tests_{Guid.NewGuid():N}");
        Directory.CreateDirectory(path);
        return path;
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
