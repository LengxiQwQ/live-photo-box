using System.Security.Cryptography;
using System.Text.Json;
using Flawright;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace LivePhotoBox.UITests;

public sealed partial class ScanPairingInteractionTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(3)]
    public async Task SplitInputLoop_PairedAndSingleSourcesExecuteSelectedFormat(int format)
    {
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        string samples = RequiredEnvironment("LIVEPHOTOBOX_TEST_SAMPLES_DIR");
        string input = Path.Combine(root, "input"); Directory.CreateDirectory(input);
        foreach (var (source, name) in new[] { ("苹果-双文件.JPG", "apple.jpg"), ("苹果-双文件.MOV", "renamed.MOV"),
            ("苹果双文件.HEIC", "heic.HEIC"), ("苹果双文件.MOV", "other.MOV"),
            ("vivo双文件.jpg", "vivo.jpg"), ("vivo双文件.mp4", "video.mp4"), ("oppo.jpg", "single.jpg") })
            if (!File.Exists(Path.Combine(input, name))) File.Copy(Path.Combine(samples, source), Path.Combine(input, name));
        var hashes = Directory.GetFiles(input).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        string output = NewOutputDirectory(root, "format-" + format);
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        await page.GetByTestId("NavSplit").ClickAsync();
        FindById(window, "SplitOutputFormatComboBox").AsComboBox().Select(format);
        await page.GetByTestId("ScanInputDirectory").FillAsync(input);
        await page.GetByTestId("SplitTaskListView").Locator("controltype:ListItem").Expect().ToHaveCountAsync(4);
        await page.GetByTestId("ScanOutputDirectory").FillAsync(output);
        await page.GetByTestId("StartSplitButton").ClickAsync();
        await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByName("Total: 4 tasks. Succeeded: 4, Failed: 0.")) != null, 180);
        await page.ScreenshotAsync(Path.Combine(output, "summary.png"));
        var close = window.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton"));
        if (close is not null && !close.IsOffscreen) close.Click();
        var statuses = await page.GetByTestId("SplitTaskListView").GetByTestId("ScanDiagnosticStatus").AllInnerTextsAsync();
        Assert.Equal(new[] { "Success", "Success", "Success", "Success" }, statuses);
        var files = Directory.GetFiles(output).Where(p => Path.GetExtension(p) != ".png").ToArray();
        Assert.Equal(8, files.Length);
        if (format == 3) { Assert.Equal(4, files.Count(p => Path.GetExtension(p).Equals(".JPG", StringComparison.OrdinalIgnoreCase))); Assert.Equal(4, files.Count(p => Path.GetExtension(p).Equals(".MP4", StringComparison.OrdinalIgnoreCase))); }
        foreach (var (path, hash) in hashes) Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        File.WriteAllText(Path.Combine(output, "evidence.json"), JsonSerializer.Serialize(new { format, files, statuses, hashes }, new JsonSerializerOptions { WriteIndented = true }));
    }
    [Fact]
    public async Task SplitInputLoop_AppleFilterSelectsOnlyConfirmedPairs()
    {
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        string samples = RequiredEnvironment("LIVEPHOTOBOX_TEST_SAMPLES_DIR");
        string input = Path.Combine(root, "apple-filter"); Directory.CreateDirectory(input);
        foreach (string name in new[] { "苹果-双文件.JPG", "苹果-双文件.MOV", "vivo双文件.jpg", "vivo双文件.mp4", "oppo.jpg" })
            if (!File.Exists(Path.Combine(input, name))) File.Copy(Path.Combine(samples, name), Path.Combine(input, name));
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        await page.GetByTestId("NavSplit").ClickAsync();
        // UI Automation excludes the hidden Fusion row; Apple is visible item 7 (persisted index 8).
        var matching = FindById(window, "MatchProtocolComboBox").AsComboBox();
        matching.Select(7); matching.Collapse();
        await page.GetByTestId("ScanInputDirectory").FillAsync(input);
        await page.GetByTestId("SplitTaskListView").Locator("controltype:ListItem").Expect().ToHaveCountAsync(1);
        Assert.NotNull(FindById(window, "SplitTaskListView").FindFirstDescendant(cf => cf.ByName("苹果-双文件.JPG")));
        await page.ScreenshotAsync(Path.Combine(root, "apple-filter.png"));
        var row = FindById(window, "SplitTaskListView").FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem));
        row.FindFirstDescendant(cf => cf.ByControlType(ControlType.Button)).Click();
        await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByName("LIVE")) is not null);
        window.FindFirstDescendant(cf => cf.ByName("LIVE")).Click();
        await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByName("0:01")) is not null, 15);
        await page.ScreenshotAsync(Path.Combine(root, "windows-media-player-preview.png"));
    }

}
