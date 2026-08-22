using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests;

/// <summary>
/// 华为合并型 HEIC 历史记录字节级读取回归测试。
/// 华为 meta 布局（iloc 在 iinf 前）exiftool 读/写都报错，XMP 只有字节级保证，
/// 历史页与历史继承必须依赖 HeicXmpInjector 的反向读取（uuid box 解析）。
/// </summary>
public sealed class HuaweiHeicHistoryReadTests
{
    [Fact]
    public async Task HuaweiHeic_ByteLevelReader_ReadsInjectedMergeHistory()
    {
        string source = ResolveSample("华为.heic");
        string outputDir = CreateTempDirectory();

        try
        {
            // 1. 拆分真机华为源，得到可用的图片 + 视频对。
            LivePhotoSplitResult split = await LivePhotoSplitService.SplitAsync(
                source, outputDir, protocolIndex: 0, outputFormatIndex: 0,
                CancellationToken.None);

            // 2. 合成华为合并型 HEIC（字节级注入 XMP 历史）。
            string mergedPath = Path.Combine(outputDir, "merged_huawei.heic");
            await LivePhotoMergeService.WriteLivePhotoAsync(
                split.ImageOutputPath,
                split.VideoOutputPath,
                mergedPath,
                selectedModeIndex: 6, // HUAWEI Moving Photo
                CancellationToken.None,
                outputFormatIndex: ProtocolFormatMatrix.FormatHeicMp4);

            // 3. 字节级读取器必须能读回注入的 Merge 历史。
            string? xmp = await HeicXmpInjector.TryReadXmpTextAsync(
                mergedPath, CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(xmp),
                "byte-level reader returned no XMP for HUAWEI merged HEIC");
            Assert.Contains("LivePhotoBox:Merge@", xmp!);

            // 4. XmpMarkerService 统一读取入口也要能读到（历史继承依赖它）。
            string? viaMarker = await XmpMarkerService.ReadXmpTextAsync(
                mergedPath, CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(viaMarker));
            Assert.Contains("LivePhotoBox:Merge@", viaMarker!);

            // 5. 历史条目读取（拆分/修复继承历史时使用）。
            var entries = await XmpMarkerService.ReadExistingEntriesAsync(
                mergedPath, CancellationToken.None);
            Assert.Contains(entries,
                e => e.Contains("LivePhotoBox:Merge@", StringComparison.Ordinal));
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task HuaweiRealPhoneSource_ByteLevelReader_ReturnsNullWhenNoXmp()
    {
        // 真机华为源没有 Adobe XMP uuid box（用 LIVE_ 尾标），不应误读。
        string source = ResolveSample("华为.heic");
        string? xmp = await HeicXmpInjector.TryReadXmpTextAsync(
            source, CancellationToken.None);
        Assert.Null(xmp);
    }

    [Fact]
    public async Task AppleHeic_ByteLevelReader_ReturnsNullWhenNoXmpUuidBox()
    {
        // 标准 Apple HEIC 的 XMP 走 exiftool 通道，无 Adobe XMP uuid box 时字节级读取器返回 null。
        string source = ResolveSample("苹果双文件.HEIC");
        string? xmp = await HeicXmpInjector.TryReadXmpTextAsync(
            source, CancellationToken.None);
        Assert.Null(xmp);
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
        string path = Path.Combine(Path.GetTempPath(), $"lpb_huawei_tests_{Guid.NewGuid():N}");
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
