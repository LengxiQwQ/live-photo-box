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

            // 6. 单 XMP 回归：HEIC meta 内只能有一个 uuid（嵌入视频自带一个
            //    顶层 uuid，不计入 meta 范围）。
            byte[] mergedBytes = await File.ReadAllBytesAsync(
                mergedPath, CancellationToken.None);
            byte[] usertype = new byte[]
            {
                0xBE, 0x7A, 0xCF, 0xCB, 0x97, 0xA9, 0x42, 0xE8,
                0x9C, 0x71, 0x99, 0x94, 0x91, 0xE3, 0xAF, 0xAC
            };
            int metaPos = FindMetaBox(mergedBytes);
            Assert.True(metaPos >= 0, "merged HEIC has no meta box");
            int metaSize = (mergedBytes[metaPos] << 24) | (mergedBytes[metaPos + 1] << 16)
                | (mergedBytes[metaPos + 2] << 8) | mergedBytes[metaPos + 3];
            Assert.Equal(1, CountBytesInRange(
                mergedBytes, usertype, metaPos, metaPos + metaSize));

            // 7. exiftool 读到的必须是完整新历史（含 Merge），不是被替换前的旧记录。
            string? exifReadback = await ReadExifXmpAsync(
                mergedPath, CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(exifReadback));
            Assert.Contains("LivePhotoBox:Merge@", exifReadback!);
            Assert.Contains("LivePhotoBox:Split@", exifReadback);
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
    public async Task HuaweiSplit_VideoOutput_HasXmpMarker()
    {
        // 华为拆分产物的视频带华为 moov/meta（covertime），exiftool 解析不了；
        // 必须通过字节级顶层 uuid box 写入并读回本软件标识（回归：视频缺 XMP）。
        string source = ResolveSample("华为.heic");
        string outputDir = CreateTempDirectory();

        try
        {
            LivePhotoSplitResult split = await LivePhotoSplitService.SplitAsync(
                source, outputDir, protocolIndex: 0, outputFormatIndex: 0,
                CancellationToken.None);
            Assert.True(File.Exists(split.VideoOutputPath),
                "split did not produce a video output");

            // 1. 历史记录读取（统一入口，含字节级回退）必须读到 Split 条目。
            var records = await XmpMarkerService.ReadHistoryRecordsAsync(
                split.VideoOutputPath, CancellationToken.None);
            Assert.Contains(records, r => r.Action == "Split");

            // 2. exiftool 也能读回（文件顶层 uuid box 是标准 MP4 XMP 位置）。
            string? readback = await ReadExifXmpAsync(
                split.VideoOutputPath, CancellationToken.None);
            Assert.False(string.IsNullOrWhiteSpace(readback));
            Assert.Contains("LivePhotoBox:Split@", readback!);

            // 3. 文件里只追加了一个顶层 uuid box（现有结构不受影响）。
            byte[] videoBytes = await File.ReadAllBytesAsync(
                split.VideoOutputPath, CancellationToken.None);
            Assert.Equal(1, CountBytes(videoBytes, new byte[]
            {
                0xBE, 0x7A, 0xCF, 0xCB, 0x97, 0xA9, 0x42, 0xE8,
                0x9C, 0x71, 0x99, 0x94, 0x91, 0xE3, 0xAF, 0xAC
            }));
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
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

    private static int CountBytes(byte[] haystack, byte[] needle)
    {
        if (needle.Length == 0 || needle.Length > haystack.Length) return 0;
        int count = 0;
        for (int i = 0; i <= haystack.Length - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) count++;
        }
        return count;
    }

    private static int CountBytesInRange(byte[] haystack, byte[] needle, int start, int end)
    {
        if (needle.Length == 0 || end - start < needle.Length) return 0;
        int count = 0;
        for (int i = start; i <= end - needle.Length; i++)
        {
            bool match = true;
            for (int j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j]) { match = false; break; }
            }
            if (match) count++;
        }
        return count;
    }

    private static int FindMetaBox(byte[] bytes)
    {
        int p = 0;
        while (p + 8 <= bytes.Length)
        {
            int size = (bytes[p] << 24) | (bytes[p + 1] << 16)
                | (bytes[p + 2] << 8) | bytes[p + 3];
            if (bytes[p + 4] == (byte)'m' && bytes[p + 5] == (byte)'e' &&
                bytes[p + 6] == (byte)'t' && bytes[p + 7] == (byte)'a')
            {
                return p;
            }
            if (size < 8 || p + size > bytes.Length) break;
            p += size;
        }
        return -1;
    }

    private static async Task<string?> ReadExifXmpAsync(string filePath, CancellationToken token)
    {
        string? exifToolPath = ExternalToolLocator.FindExifTool();
        if (string.IsNullOrEmpty(exifToolPath)) return null;

        var psi = new System.Diagnostics.ProcessStartInfo
        {
            FileName = exifToolPath,
            WorkingDirectory = Path.GetDirectoryName(exifToolPath) ?? AppContext.BaseDirectory,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
            StandardOutputEncoding = System.Text.Encoding.UTF8,
        };
        psi.ArgumentList.Add("-charset");
        psi.ArgumentList.Add("utf8");
        psi.ArgumentList.Add("-xmp");
        psi.ArgumentList.Add("-b");
        psi.ArgumentList.Add(filePath);

        using var process = System.Diagnostics.Process.Start(psi);
        if (process == null) return null;
        string output = await process.StandardOutput.ReadToEndAsync(token);
        string error = await process.StandardError.ReadToEndAsync(token);
        try { await process.WaitForExitAsync(token); }
        catch (OperationCanceledException) { process.Kill(); throw; }
        return string.IsNullOrWhiteSpace(output) ? null : output;
    }
}
