using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests;

/// <summary>
/// 输出自检（OutputVerifier）回归测试：
/// 浅查验证 XMP 可读/单份、结构完整、实况数据存在；深查额外验证解码与视频播放。
/// </summary>
public sealed class OutputVerifierTests
{
    private const string LevelKey = "OutputCheckLevel";

    [Fact]
    public async Task LightCheck_MergedJpeg_Passes()
    {
        string outputDir = CreateTempDirectory();
        try
        {
            string merged = Path.Combine(outputDir, "merged_v2.jpg");
            await LivePhotoMergeService.WriteLivePhotoAsync(
                ResolveSample("vivo双文件.jpg"),
                ResolveSample("vivo双文件.mp4"),
                merged,
                selectedModeIndex: 2, // MotionPhoto V2
                CancellationToken.None,
                outputFormatIndex: ProtocolFormatMatrix.FormatJpgMp4);

            SetLevel((int)OutputCheckLevel.Light);
            try
            {
                var result = await OutputVerifier.VerifyAsync(
                    merged, CancellationToken.None, LivePhotoProtocolType.GoogleV2);
                Assert.True(result.Passed, string.Join("; ", result.Problems));
                Assert.Contains(result.Notes, n => n.Contains("内嵌视频 ftyp"));
            }
            finally { SetLevel((int)OutputCheckLevel.Light); }
        }
        finally { TryDeleteDirectory(outputDir); }
    }

    [Fact]
    public async Task LightCheck_MergedHuaweiHeic_Passes()
    {
        string outputDir = CreateTempDirectory();
        try
        {
            LivePhotoSplitResult split = await LivePhotoSplitService.SplitAsync(
                ResolveSample("华为Mate80.heic"), outputDir, 0, 0, CancellationToken.None);
            string merged = Path.Combine(outputDir, "merged_huawei.heic");
            await LivePhotoMergeService.WriteLivePhotoAsync(
                split.ImageOutputPath, split.VideoOutputPath, merged,
                selectedModeIndex: 6, // HUAWEI Moving Photo
                CancellationToken.None,
                outputFormatIndex: ProtocolFormatMatrix.FormatHeicMp4);

            SetLevel((int)OutputCheckLevel.Light);
            try
            {
                var result = await OutputVerifier.VerifyAsync(
                    merged, CancellationToken.None, LivePhotoProtocolType.Huawei);
                Assert.True(result.Passed, string.Join("; ", result.Problems));
            }
            finally { SetLevel((int)OutputCheckLevel.Light); }
        }
        finally { TryDeleteDirectory(outputDir); }
    }

    [Fact]
    public async Task LightCheck_HuaweiSplitVideo_Passes()
    {
        string outputDir = CreateTempDirectory();
        try
        {
            LivePhotoSplitResult split = await LivePhotoSplitService.SplitAsync(
                ResolveSample("华为Mate80.heic"), outputDir, 0, 0, CancellationToken.None);

            SetLevel((int)OutputCheckLevel.Light);
            try
            {
                // 拆分产物：图片/视频不带内嵌视频，也不断言实况标记。
                var img = await OutputVerifier.VerifyAsync(
                    split.ImageOutputPath, CancellationToken.None,
                    expectedProtocol: null, expectEmbeddedVideo: false);
                Assert.True(img.Passed, string.Join("; ", img.Problems));

                var vid = await OutputVerifier.VerifyAsync(
                    split.VideoOutputPath, CancellationToken.None,
                    expectedProtocol: null, expectEmbeddedVideo: false);
                Assert.True(vid.Passed, string.Join("; ", vid.Problems));
            }
            finally { SetLevel((int)OutputCheckLevel.Light); }
        }
        finally { TryDeleteDirectory(outputDir); }
    }

    [Fact]
    public async Task FullCheck_MergedJpeg_DecodesAndPlays()
    {
        string outputDir = CreateTempDirectory();
        try
        {
            string merged = Path.Combine(outputDir, "merged_full.jpg");
            await LivePhotoMergeService.WriteLivePhotoAsync(
                ResolveSample("vivo双文件.jpg"),
                ResolveSample("vivo双文件.mp4"),
                merged,
                selectedModeIndex: 2,
                CancellationToken.None,
                outputFormatIndex: ProtocolFormatMatrix.FormatJpgMp4);

            SetLevel((int)OutputCheckLevel.Full);
            try
            {
                var result = await OutputVerifier.VerifyAsync(
                    merged, CancellationToken.None, LivePhotoProtocolType.GoogleV2);
                Assert.True(result.Passed, string.Join("; ", result.Problems));
                Assert.Contains(result.Notes, n => n.Contains("内嵌视频 OK"));
            }
            finally { SetLevel((int)OutputCheckLevel.Light); }
        }
        finally { TryDeleteDirectory(outputDir); }
    }

    [Fact]
    public async Task FullCheck_MergedHuaweiHeic_DecodesAndPlays()
    {
        string outputDir = CreateTempDirectory();
        try
        {
            LivePhotoSplitResult split = await LivePhotoSplitService.SplitAsync(
                ResolveSample("华为Mate80.heic"), outputDir, 0, 0, CancellationToken.None);
            string merged = Path.Combine(outputDir, "merged_full_huawei.heic");
            await LivePhotoMergeService.WriteLivePhotoAsync(
                split.ImageOutputPath, split.VideoOutputPath, merged,
                selectedModeIndex: 6,
                CancellationToken.None,
                outputFormatIndex: ProtocolFormatMatrix.FormatHeicMp4);

            SetLevel((int)OutputCheckLevel.Full);
            try
            {
                var result = await OutputVerifier.VerifyAsync(
                    merged, CancellationToken.None, LivePhotoProtocolType.Huawei);
                Assert.True(result.Passed, string.Join("; ", result.Problems));
                Assert.Contains(result.Notes, n => n.Contains("heif-dec 解码 OK"));
                Assert.Contains(result.Notes, n => n.Contains("内嵌视频 OK"));
            }
            finally { SetLevel((int)OutputCheckLevel.Light); }
        }
        finally { TryDeleteDirectory(outputDir); }
    }

    [Fact]
    public async Task CorruptFile_ReportsProblems()
    {
        string outputDir = CreateTempDirectory();
        string corrupt = Path.Combine(outputDir, "corrupt.jpg");
        try
        {
            File.Copy(ResolveSample("荣耀.jpg"), corrupt, overwrite: true);
            // 破坏文件头，使其不再是合法 JPEG。
            byte[] data = await File.ReadAllBytesAsync(corrupt);
            data[0] = 0x00; data[1] = 0x00;
            await File.WriteAllBytesAsync(corrupt, data);

            SetLevel((int)OutputCheckLevel.Light);
            try
            {
                var result = await OutputVerifier.VerifyAsync(
                    corrupt, CancellationToken.None);
                Assert.False(result.Passed);
                Assert.NotEmpty(result.Problems);
            }
            finally { SetLevel((int)OutputCheckLevel.Light); }
        }
        finally { TryDeleteDirectory(outputDir); }
    }

    [Fact]
    public async Task NoneLevel_SkipsChecks()
    {
        SetLevel((int)OutputCheckLevel.None);
        try
        {
            var result = await OutputVerifier.VerifyAsync(
                ResolveSample("荣耀.jpg"), CancellationToken.None);
            Assert.True(result.Passed);
            Assert.Empty(result.Problems);
        }
        finally { SetLevel((int)OutputCheckLevel.Light); }
    }

    [Fact]
    public void SelfCheckMarker_StripAndClean()
    {
        string marked = OutputVerifier.SelfCheckMarker + "问题一\n问题二";
        Assert.True(OutputVerifier.TryStripSelfCheckMarker(marked, out var problems));
        Assert.Equal("问题一\n问题二", problems);
        Assert.Equal("问题一\n问题二", OutputVerifier.CleanMessage(marked));

        Assert.False(OutputVerifier.TryStripSelfCheckMarker("普通错误", out _));
        Assert.Equal("普通错误", OutputVerifier.CleanMessage("普通错误"));
    }

    private static void SetLevel(int level)
        => AppSettingsService.SetValue(LevelKey, level);

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
        string path = Path.Combine(Path.GetTempPath(), $"lpb_verify_tests_{Guid.NewGuid():N}");
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
