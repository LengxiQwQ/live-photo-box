using System.Text;
using LivePhotoBox.Services;
using LivePhotoBox.Services.Protocols;
using Xunit;

namespace LivePhotoBox.Core.Tests;

/// <summary>
/// 调试工具区"写入本软件 XMP 命名空间"总开关（IsLpbNamespaceWriteEnabled）回归测试：
/// 关闭后所有操作都不再写入 LivePhotoBox 私有命名空间 / 版本 / 时间戳 / 历史条目，
/// 但协议自身要求的 XMP（如 Google Motion Photo）必须保留。
/// </summary>
public sealed class XmpNamespaceWriteToggleTests
{
    private const string ToggleKey = "IsLpbNamespaceWriteEnabled";

    [Fact]
    public async Task Disabled_UnifiedMarker_SkipsWritingEntirely()
    {
        string source = ResolveSample("荣耀.jpg");
        string outputDir = CreateTempDirectory();
        string target = Path.Combine(outputDir, "plain.jpg");

        try
        {
            File.Copy(source, target, overwrite: true);

            AppSettingsService.SetValue(ToggleKey, false);
            try
            {
                bool ok = await XmpMarkerService.TryWriteUnifiedMarkerAsync(
                    target, "Split", "Source=OPPO;Target=None",
                    CancellationToken.None);
                Assert.True(ok, "marker write should be treated as success when disabled");

                string? xmp = await XmpMarkerService.ReadXmpTextAsync(
                    target, CancellationToken.None);
                // 源图可能自带协议 XMP；关闭后必须不含本软件私有命名空间。
                if (!string.IsNullOrWhiteSpace(xmp))
                {
                    Assert.DoesNotContain("xmlns:LivePhotoBox=", xmp);
                    Assert.DoesNotContain("LivePhotoBox:Split@", xmp);
                }
            }
            finally
            {
                AppSettingsService.SetValue(ToggleKey, true);
            }
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public async Task Disabled_MergeEmbed_ReturnsProtocolXmpUntouched()
    {
        string source = ResolveSample("荣耀.jpg");
        string outputDir = CreateTempDirectory();

        try
        {
            AppSettingsService.SetValue(ToggleKey, false);
            try
            {
                var protocol = new MotionPhotoV2Protocol();
                byte[] protocolXmp = protocol.BuildXmpMetadata(
                    1234, 0, "image/jpeg", "0", "video/mp4");

                byte[] result = await XmpMarkerService.EmbedMergeHistoryAsync(
                    source, protocolXmp, "Source=OPPO;Target=MotionPhotoV2",
                    CancellationToken.None);

                string xmpText = Encoding.UTF8.GetString(result);
                // 协议标记保留，本软件私有命名空间不写。
                Assert.Contains("MotionPhoto", xmpText);
                Assert.DoesNotContain("xmlns:LivePhotoBox=", xmpText);
                Assert.DoesNotContain("LivePhotoBox:Merge@", xmpText);
            }
            finally
            {
                AppSettingsService.SetValue(ToggleKey, true);
            }
        }
        finally
        {
            TryDeleteDirectory(outputDir);
        }
    }

    [Fact]
    public void Disabled_WrapXmp_OmitsNamespaceButKeepsProtocolFields()
    {
        AppSettingsService.SetValue(ToggleKey, false);
        try
        {
            var protocol = new MotionPhotoV2Protocol();
            byte[] xmp = protocol.BuildXmpMetadata(
                1234, 0, "image/jpeg", "0", "video/mp4");
            string xmpText = Encoding.UTF8.GetString(xmp);

            // 协议字段保留，本软件命名空间不注入。
            Assert.Contains("Container:Directory", xmpText);
            Assert.Contains("MotionPhoto", xmpText);
            Assert.DoesNotContain("xmlns:LivePhotoBox=", xmpText);
            Assert.DoesNotContain("LivePhotoBox:Version=", xmpText);
            Assert.DoesNotContain("LivePhotoBox:Timestamp=", xmpText);
        }
        finally
        {
            AppSettingsService.SetValue(ToggleKey, true);
        }
    }

    [Fact]
    public void Enabled_Default_WritesNamespace()
    {
        AppSettingsService.SetValue(ToggleKey, true);
        try
        {
            var protocol = new MotionPhotoV2Protocol();
            byte[] xmp = protocol.BuildXmpMetadata(
                1234, 0, "image/jpeg", "0", "video/mp4");
            string xmpText = Encoding.UTF8.GetString(xmp);

            Assert.Contains("xmlns:LivePhotoBox=", xmpText);
            Assert.Contains("LivePhotoBox:Version=", xmpText);
            Assert.Contains("LivePhotoBox:Timestamp=", xmpText);
        }
        finally
        {
            AppSettingsService.SetValue(ToggleKey, true);
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
        string path = Path.Combine(Path.GetTempPath(), $"lpb_xmp_toggle_{Guid.NewGuid():N}");
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
            // best-effort cleanup
        }
    }
}
