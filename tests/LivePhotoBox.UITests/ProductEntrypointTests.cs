using System.Security.Cryptography;
using System.Text.Json;
using Flawright;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace LivePhotoBox.UITests;

public sealed partial class ScanPairingInteractionTests
{
    [Fact]
    public async Task ProductEntrypoints_SplitHlgToH264ExportsSdrWithHdrWarning()
    {
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        string samples = RequiredEnvironment("LIVEPHOTOBOX_TEST_SAMPLES_DIR");
        string evidence = NewOutputDirectory(root, "hlg-h264");
        string input = Path.Combine(evidence, "input"); Directory.CreateDirectory(input);
        string output = Path.Combine(evidence, "output"); Directory.CreateDirectory(output);
        foreach (string source in new[] { "vivo.jpg", "oppo.jpg" }) File.Copy(Path.Combine(samples, source), Path.Combine(input, source));
        var before = Directory.GetFiles(input).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        await page.GetByTestId("NavSplit").ClickAsync();
        FindById(window, "ProtocolComboBox").AsComboBox().Select(0);
        FindById(window, "SplitOutputFormatComboBox").AsComboBox().Select(3);
        FindById(window, "AfterCompletionCombo").AsComboBox().Select(0);
        await page.GetByTestId("ScanInputDirectory").FillAsync(input);
        await page.GetByTestId("SplitTaskListView").Locator("controltype:ListItem").Expect().ToHaveCountAsync(2);
        await page.GetByTestId("ScanOutputDirectory").FillAsync(output);
        await page.GetByTestId("StartSplitButton").ClickAsync();
        await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")) is { IsOffscreen: false }, 180);
        await page.ScreenshotAsync(Path.Combine(evidence, "summary.png"));
        window.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton"))!.Click();
        var statuses = page.GetByTestId("SplitTaskListView").GetByTestId("ScanDiagnosticStatus");
        var texts = await statuses.AllInnerTextsAsync();
        Assert.Contains("Success", texts);
        int warning = Array.FindIndex(texts.ToArray(), text => text.Contains("isolation", StringComparison.OrdinalIgnoreCase));
        Assert.True(warning >= 0);
        await statuses.Nth(warning).ClickAsync();
        string details = await page.GetByTestId("ErrorDetailText").InnerTextAsync();
        Assert.Contains("HDR", details); Assert.Contains("Unverified", details);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(output, "Unverified"), "*", SearchOption.AllDirectories).Length);
        Assert.Equal(2, Directory.GetFiles(output).Length);
        foreach (var (path, hash) in before) Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        await page.ScreenshotAsync(Path.Combine(evidence, "hdr-warning.png"));
        File.WriteAllText(Path.Combine(evidence, "evidence.json"), JsonSerializer.Serialize(new { texts, details, before }));
    }

    [Theory]
    [InlineData("Merge", 2)]
    [InlineData("Split", 1)]
    public async Task ProductEntrypoints_ExplicitSourceActionCompletesBeforeFinalDialog(string workflow, int action)
    {
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        string evidence = NewOutputDirectory(root, "source-action-" + workflow);
        string input = Path.Combine(evidence, "input"); Directory.CreateDirectory(input);
        string output = Path.Combine(evidence, "output");
        string archive = Path.Combine(evidence, "archive");
        if (workflow == "Merge")
            foreach (string extension in new[] { ".jpg", ".mp4" })
                File.Copy(Path.Combine(root, "ordinary", "pair" + extension), Path.Combine(input, "pair" + extension));
        else File.Copy(Path.Combine(RequiredEnvironment("LIVEPHOTOBOX_TEST_SAMPLES_DIR"), "oppo.jpg"), Path.Combine(input, "green.jpg"));
        var before = Directory.GetFiles(input).ToDictionary(p => Path.GetFileName(p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        await page.GetByTestId("Nav" + workflow).ClickAsync();
        await page.GetByTestId("ScanInputDirectory").FillAsync(input);
        await page.GetByTestId(workflow + "TaskListView").Locator("controltype:ListItem").Expect().ToHaveCountAsync(1);
        await page.GetByTestId("ScanOutputDirectory").FillAsync(output);
        FindById(window, "AfterCompletionCombo").AsComboBox().Select(action);
        if (action == 1) await page.GetByTestId("OriginalDirTextBox").FillAsync(archive);
        await page.GetByTestId("Start" + workflow + "Button").ClickAsync();
        await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")) is { IsOffscreen: false }, 120);
        Assert.Empty(Directory.GetFiles(input));
        if (action == 1)
            Assert.Equal(before, Directory.GetFiles(archive).ToDictionary(p => Path.GetFileName(p), p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)))));
        window.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton"))!.Click();
        await page.GetByTestId(workflow + "TaskListView").GetByTestId("ScanDiagnosticStatus").First.ClickAsync();
        string details = await page.GetByTestId("ErrorDetailText").InnerTextAsync();
        Assert.Contains(action == 1 ? "move/completed" : "recycle/completed", details);
        File.WriteAllText(Path.Combine(evidence, "evidence.json"), JsonSerializer.Serialize(new { workflow, action, input, output, archive, before, details }));
        await page.ScreenshotAsync(Path.Combine(evidence, "source-action.png"));
    }

    [Fact]
    public async Task ProductEntrypoints_HistoryShowsYellowAndSourceActionDetails()
    {
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        await page.GetByTestId("NavHistory").ClickAsync();
        await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByName("Operation history · App and CLI")) is not null);
        var text = string.Join("\n", window.FindAllDescendants().Select(e => e.Properties.Name.ValueOrDefault));
        Assert.Contains("Source post-processing: move/failed", text);
        Assert.Contains("Exported to isolation (review needed)", text);
        Assert.Contains("Unverified", text);
        File.WriteAllText(Path.Combine(root, "history-text.txt"), text);
        await page.ScreenshotAsync(Path.Combine(root, "history.png"));
    }

    [Fact]
    public async Task ProductEntrypoints_PauseResumeCancelKeepsApplicationUsable()
    {
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        string input = Path.Combine(root, "pause-input");
        string output = NewOutputDirectory(root, "pause-output");
        var before = Directory.GetFiles(input).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        await page.GetByTestId("NavSplit").ClickAsync();
        FindById(window, "ProtocolComboBox").AsComboBox().Select(0);
        FindById(window, "SplitOutputFormatComboBox").AsComboBox().Select(3);
        await page.GetByTestId("ScanInputDirectory").FillAsync(input);
        // Offscreen ListView rows are virtualized. The visible scan summary
        // reports the full queue, independently of the realized UIA children.
        await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByName("Tasks: 12  •  Unselected: 0")) is not null);
        await page.GetByTestId("ScanOutputDirectory").FillAsync(output);
        await page.GetByTestId("StartSplitButton").ClickAsync();
        await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByName("Pause")) is { IsOffscreen: false });
        window.FindFirstDescendant(cf => cf.ByName("Pause"))!.Click();
        await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByName("Resume")) is { IsOffscreen: false }, 60);
        int pausedFiles = Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length;
        await Task.Delay(700);
        Assert.Equal(pausedFiles, Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length);
        await page.ScreenshotAsync(Path.Combine(root, "paused.png"));
        window.FindFirstDescendant(cf => cf.ByName("Resume"))!.Click();
        await page.GetByTestId("StartSplitButton").ClickAsync();
        await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")) is { IsOffscreen: false }, 90);
        window.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton"))!.Click();
        var statuses = await page.GetByTestId("SplitTaskListView").GetByTestId("ScanDiagnosticStatus").AllInnerTextsAsync();
        Assert.Contains(statuses, status => status.Contains("cancel", StringComparison.OrdinalIgnoreCase));
        foreach (var (path, hash) in before) Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        File.WriteAllText(Path.Combine(root, "pause-cancel.json"), JsonSerializer.Serialize(new { pausedFiles, statuses, before }));
        await page.ScreenshotAsync(Path.Combine(root, "cancelled.png"));
        await page.GetByTestId("NavHome").ClickAsync();
        await page.GetByTestId("NavSplit").ClickAsync();
        await page.GetByTestId("ScanInputDirectory").FillAsync(Path.Combine(root, "split-mixed"));
        await page.GetByTestId("SplitTaskListView").Locator("controltype:ListItem").Expect().ToHaveCountAsync(2);
        Assert.True(await page.GetByTestId("StartSplitButton").IsEnabledAsync());
    }

    [Theory]
    [InlineData(7, -1, "AppleLivePhoto", 2)]
    [InlineData(8, -1, "VivoLegacyDualFile", 2)]
    [InlineData(5, 0, "SamsungMotionPhoto", 1)]
    [InlineData(6, 1, "HuaweiMovingPhoto", 1)]
    public async Task ProductEntrypoints_MergeTargetAndVersionReachExecution(int target, int version, string expected, int outputCount)
    {
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        string input = Path.Combine(root, "ordinary");
        string output = NewOutputDirectory(root, "merge-target-" + target);
        var before = Directory.GetFiles(input).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        try
        {
            await page.GetByTestId("NavMerge").ClickAsync();
            var protocol = FindById(window, "ProtocolComboBox").AsComboBox();
            protocol.Expand();
            string label = target switch { 7 => "Apple Live Photo", 8 => "vivo legacy dual file",
                5 => "Samsung - Motion Photo", 6 => "HUAWEI - Moving Photo", _ => throw new ArgumentOutOfRangeException(nameof(target)) };
            var item = protocol.Items.Single(i => i.FindFirstDescendant(cf => cf.ByName(label)) is not null);
            item.Select();
            protocol.Collapse();
            // The collapsed WinUI selection reparents its visual children. Verify
            // the visible version control and the actual terminal target below,
            // rather than a stale UIA child under the former popup item.
            if (version >= 0)
                await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId(target == 5
                    ? "SamsungVersionComboBox" : "HuaweiVersionComboBox")) is { IsOffscreen: false });
            if (version >= 0)
                FindById(window, target == 5 ? "SamsungVersionComboBox" : "HuaweiVersionComboBox").AsComboBox().Select(version);
            if (target == 7)
            {
                var formats = FindById(window, "OutputFormatComboBox").AsComboBox();
                formats.Expand();
                await WaitForAsync(() => formats.Items.Length == 2);
                var choices = formats.Items;
                Assert.Equal(2, choices.Length);
                Assert.All(choices, item => Assert.True(item.IsEnabled));
                Assert.Contains(choices, item => item.FindFirstDescendant(cf => cf.ByName("JPG [Embedded MOV (H.265)]")) is not null);
                Assert.Contains(choices, item => item.FindFirstDescendant(cf => cf.ByName("HEIC [Embedded MOV (H.265)]")) is not null);
                formats.Collapse();
            }
            await page.GetByTestId("ScanInputDirectory").FillAsync(input);
            await page.GetByTestId("MergeTaskListView").Locator("controltype:ListItem").Expect().ToHaveCountAsync(1);
            await page.GetByTestId("ScanOutputDirectory").FillAsync(output);
            await page.GetByTestId("StartMergeButton").ClickAsync();
            await WaitForAsync(() => Directory.GetFiles(output, "*", SearchOption.AllDirectories).Length >= outputCount, 180);
            await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")) is { IsOffscreen: false }, 60);
            window.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton"))!.Click();
            await page.GetByTestId("MergeTaskListView").GetByTestId("ScanDiagnosticStatus").First.ClickAsync();
            string details = await page.GetByTestId("ErrorDetailText").InnerTextAsync();
            Assert.Contains(expected, details);
            if (version >= 0) Assert.Contains(" / 2", details);
            var outputs = Directory.GetFiles(output, "*", SearchOption.AllDirectories);
            Assert.Equal(outputCount, outputs.Length);
            foreach (var (path, hash) in before) Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            File.WriteAllText(Path.Combine(output, "ui-evidence.json"), JsonSerializer.Serialize(new { target, version, details, outputs, before }));
            await page.ScreenshotAsync(Path.Combine(output, "ui.png"));
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString());
            await page.ScreenshotAsync(Path.Combine(output, "failure.png"));
            File.WriteAllText(Path.Combine(output, "tree.json"), JsonSerializer.Serialize(window.FindAllDescendants().Select(e => new
            { Id = e.Properties.AutomationId.ValueOrDefault, Name = e.Properties.Name.ValueOrDefault, Type = e.ControlType.ToString() })));
            throw;
        }
    }

    [Fact]
    public async Task ProductEntrypoints_SplitMixedResultsUseExistingTipAndKeepYellowSource()
    {
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        string input = Path.Combine(root, "split-mixed");
        string output = NewOutputDirectory(root, "split-mixed-output");
        var before = Directory.GetFiles(input).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        try
        {
            await page.GetByTestId("NavSplit").ClickAsync();
            FindById(window, "ProtocolComboBox").AsComboBox().Select(0);
            FindById(window, "SplitOutputFormatComboBox").AsComboBox().Select(0);
            await page.GetByTestId("ScanInputDirectory").FillAsync(input);
            await page.GetByTestId("SplitTaskListView").Locator("controltype:ListItem").Expect().ToHaveCountAsync(2);
            await page.GetByTestId("ScanOutputDirectory").FillAsync(output);
            await page.GetByTestId("StartSplitButton").ClickAsync();
            await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton")) is { IsOffscreen: false }, 180);
            await page.ScreenshotAsync(Path.Combine(output, "summary.png"));
            window.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton"))!.Click();
            var statuses = page.GetByTestId("SplitTaskListView").GetByTestId("ScanDiagnosticStatus");
            var texts = await statuses.AllInnerTextsAsync();
            Assert.Contains("Success", texts);
            int yellow = Array.FindIndex(texts.ToArray(), text => text.Contains("isolation", StringComparison.OrdinalIgnoreCase));
            Assert.True(yellow >= 0);
            await statuses.Nth(yellow).ClickAsync();
            string details = await page.GetByTestId("ErrorDetailText").InnerTextAsync();
            Assert.Contains("Unverified", details); Assert.Contains("direction", details);
            await page.ScreenshotAsync(Path.Combine(output, "yellow-tip.png"));
            if (Environment.GetEnvironmentVariable("LPB_UI_OBSERVATION_GATE") == "1")
            {
                File.WriteAllText(Path.Combine(root, "yellow-observation-ready"), details);
                await WaitForAsync(() => File.Exists(Path.Combine(root, "yellow-observed")), 45);
            }
            await ExpandDiagnosticsAsync(window);
            var diagnostic = page.GetByTestId("SplitDiagnosticListView").GetByTestId("ScanDiagnosticStatus");
            await diagnostic.Expect().ToHaveCountAsync(1);
            await diagnostic.First.ClickAsync();
            Assert.Contains("broken.jpg", await page.GetByTestId("ErrorDetailText").InnerTextAsync());
            await page.ScreenshotAsync(Path.Combine(output, "red-tip.png"));
            Assert.Equal(2, Directory.GetFiles(output, "*", SearchOption.TopDirectoryOnly).Count(p => Path.GetExtension(p) is ".JPG" or ".MP4"));
            Assert.Equal(2, Directory.GetFiles(Path.Combine(output, "Unverified"), "*", SearchOption.AllDirectories).Length);
            foreach (var (path, hash) in before) Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            File.WriteAllText(Path.Combine(output, "evidence.json"), JsonSerializer.Serialize(new { texts, details, before }));
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(output, "failure.txt"), error.ToString());
            await page.ScreenshotAsync(Path.Combine(output, "failure.png"));
            throw;
        }
    }
}
