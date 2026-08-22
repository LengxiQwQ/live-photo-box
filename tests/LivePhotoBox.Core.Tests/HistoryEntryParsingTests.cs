using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests;

/// <summary>
/// 历史条目统一解析（XmpMarkerService.ParseHistoryEntry）与详情转义回归测试：
/// 详情字段中的 '\' ';' '=' 不再被静默丢弃，写入后能原样读回；
/// 旧版未转义条目仍可解析（向后兼容）。
/// </summary>
public sealed class HistoryEntryParsingTests
{
    [Fact]
    public void ParseHistoryEntry_FullEntry_ParsesAllFields()
    {
        const string subject =
            "LivePhotoBox:Merge@2026-06-25T14:30:22+07:00@v2.2.1@" +
            "Source=OPPO;Target=MotionPhotoV2;Format=JPG+MP4;KeyPhoto=1.5";

        HistoryRecord? record = XmpMarkerService.ParseHistoryEntry(subject);

        Assert.NotNull(record);
        Assert.Equal("Merge", record!.Action);
        Assert.Equal(new DateTime(2026, 6, 25, 14, 30, 22), record.Timestamp);
        Assert.Equal("2.2.1", record.Version);
        Assert.Equal("OPPO", record.Details["Source"]);
        Assert.Equal("MotionPhotoV2", record.Details["Target"]);
        Assert.Equal("JPG+MP4", record.Details["Format"]);
        Assert.Equal("1.5", record.Details["KeyPhoto"]);
    }

    [Fact]
    public void ParseHistoryEntry_LightweightEntry_HasNoTimestampOrDetails()
    {
        HistoryRecord? record = XmpMarkerService.ParseHistoryEntry(
            "LivePhotoBox:Split@@v2.2.1@");

        Assert.NotNull(record);
        Assert.Equal("Split", record!.Action);
        Assert.Null(record.Timestamp);
        Assert.Equal("2.2.1", record.Version);
        Assert.Empty(record.Details);
    }

    [Fact]
    public void ParseHistoryEntry_UnknownOrMalformed_ReturnsNull()
    {
        Assert.Null(XmpMarkerService.ParseHistoryEntry("not a history entry"));
        Assert.Null(XmpMarkerService.ParseHistoryEntry("LivePhotoBox:"));
        Assert.Null(XmpMarkerService.ParseHistoryEntry(string.Empty));
        Assert.Null(XmpMarkerService.ParseHistoryEntry(null!));
    }

    [Fact]
    public void BuildDetails_EscapesSpecialChars_RoundTripsThroughParser()
    {
        const string trickyValue = "a=b;c\\d"; // 同时包含 = ; 和反斜杠
        string details = XmpMarkerService.BuildDetails(
            ("Path", trickyValue),
            ("Name", "photo.jpg"));

        // 不再静默丢弃：字段仍在。
        Assert.Contains("Path=", details);
        Assert.Contains("Name=photo.jpg", details);

        HistoryRecord? record = XmpMarkerService.ParseHistoryEntry(
            $"LivePhotoBox:Cover@2026-06-25T14:30:22+07:00@v2.2.1@{details}");

        Assert.NotNull(record);
        Assert.Equal(trickyValue, record!.Details["Path"]);
        Assert.Equal("photo.jpg", record.Details["Name"]);
        // 值里的 ; 不会把字段拆散。
        Assert.Equal(2, record.Details.Count);
    }

    [Fact]
    public void ParseHistoryEntry_LegacyUnescapedDetails_StillParses()
    {
        // 旧版写入的详情未转义（值里没有 ; = 所以本来也安全），必须向后兼容。
        HistoryRecord? record = XmpMarkerService.ParseHistoryEntry(
            "LivePhotoBox:Repair@2026-06-25T14:30:22@v1.2.0@Source=None;Target=None;Fix=Rotation+Thumbnail");

        Assert.NotNull(record);
        Assert.Equal("Rotation+Thumbnail", record!.Details["Fix"]);
        Assert.Equal("None", record.Details["Target"]);
    }

    [Fact]
    public async Task WriteAndReadBack_DetailsWithSpecialChars_SurviveRoundTrip()
    {
        string source = ResolveSample("荣耀.jpg");
        string outputDir = CreateTempDirectory();
        string target = Path.Combine(outputDir, "roundtrip.jpg");

        try
        {
            File.Copy(source, target, overwrite: true);

            string details = XmpMarkerService.BuildDetails(
                ("Source", "OPPO"),
                ("Custom", "a=b;c\\d"));
            bool ok = await XmpMarkerService.TryWriteUnifiedMarkerAsync(
                target, "Repair", details, CancellationToken.None);
            Assert.True(ok);

            var records = await XmpMarkerService.ReadHistoryRecordsAsync(
                target, CancellationToken.None);
            var repair = records.SingleOrDefault(r => r.Action == "Repair");
            Assert.NotNull(repair);
            Assert.Equal("OPPO", repair!.Details["Source"]);
            Assert.Equal("a=b;c\\d", repair.Details["Custom"]);
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
        string path = Path.Combine(Path.GetTempPath(), $"lpb_parse_tests_{Guid.NewGuid():N}");
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
