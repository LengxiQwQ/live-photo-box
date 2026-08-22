using System.Text;
using LivePhotoBox.Services;
using LivePhotoBox.Services.Protocols;
using Xunit;

namespace LivePhotoBox.Core.Tests;

/// <summary>
/// 封面操作独立 Cover 历史条目回归测试：
/// 封面不再是"伪装成 Merge"的操作，GUI 与 CLI 都写 LivePhotoBox:Cover@... 条目。
/// </summary>
public sealed class CoverHistoryTests
{
    [Fact]
    public async Task TryWriteUnifiedMarker_CoverAction_WritesAndReadsCoverEntry()
    {
        string source = ResolveSample("荣耀.jpg");
        string outputDir = CreateTempDirectory();
        string target = Path.Combine(outputDir, "cover_copy.jpg");

        try
        {
            File.Copy(source, target, overwrite: true);

            string details = "Source=MotionPhotoV2;Target=MotionPhotoV2;Format=JPG+MP4;KeyPhoto=2.5";
            bool ok = await XmpMarkerService.TryWriteUnifiedMarkerAsync(
                target, "Cover", details, CancellationToken.None);
            Assert.True(ok, "TryWriteUnifiedMarkerAsync failed to write Cover history");

            var entries = await XmpMarkerService.ReadExistingEntriesAsync(
                target, CancellationToken.None);
            var cover = entries.SingleOrDefault(e => e.StartsWith(
                "LivePhotoBox:Cover@", StringComparison.Ordinal));
            Assert.NotNull(cover);
            Assert.Contains("KeyPhoto=2.5", cover);
            Assert.Contains("Target=MotionPhotoV2", cover);
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task EmbedMergeHistory_CoverAction_EmbedsCoverEntry()
    {
        string source = ResolveSample("荣耀.jpg");
        string outputDir = CreateTempDirectory();

        try
        {
            var protocol = new MotionPhotoV2Protocol();
            byte[] xmpBytes = protocol.BuildXmpMetadata(
                1234, 0, "image/jpeg", "0", "video/mp4");

            byte[] result = await XmpMarkerService.EmbedMergeHistoryAsync(
                source, xmpBytes, "Source=OPPO;Target=MotionPhotoV2;KeyPhoto=1.5",
                CancellationToken.None, action: "Cover");

            string xmpText = Encoding.UTF8.GetString(result);
            Assert.Contains("LivePhotoBox:Cover@", xmpText);
            Assert.Contains("KeyPhoto=1.5", xmpText);
            // Cover 不再记成 Merge。
            Assert.DoesNotContain("LivePhotoBox:Merge@", xmpText);
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
        string path = Path.Combine(Path.GetTempPath(), $"lpb_cover_tests_{Guid.NewGuid():N}");
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
