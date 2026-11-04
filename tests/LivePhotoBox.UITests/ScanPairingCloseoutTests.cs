using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flawright;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace LivePhotoBox.UITests;

public sealed partial class ScanPairingInteractionTests
{
    [Theory]
    [InlineData("Merge", "三星.heic", "三星.jpg")]
    [InlineData("Split", "vivo双文件.jpg", "vivo双文件.jpg")]
    public async Task CameraDirectory_DiagnosticTipDoesNotRaiseNativeExceptions(string workflow, string primaryName, string relatedName)
    {
        string input = RequiredEnvironment("LPB_SCAN_REFUSAL_INPUT_ROOT");
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        await page.GetByTestId("Nav" + workflow).ClickAsync();
        await page.GetByTestId("ScanInputDirectory").FillAsync(input);
        await WaitForAsync(() => FindById(window, "ScanInputDirectory").IsEnabled);
        await ExpandDiagnosticsAsync(window);
        var list = FindById(window, workflow + "DiagnosticListView");
        var row = list.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
            .FirstOrDefault(item => item.FindFirstDescendant(cf => cf.ByName(primaryName)) is not null);
        Assert.NotNull(row);
        var status = FindById(row, "ScanDiagnosticStatus");
        Assert.False(status.IsOffscreen);
        string evidence = NewOutputDirectory(root, "camera-conflict-" + workflow);
        string observation = Path.Combine(evidence, "native");
        Process? observer = null;
        try
        {
            if (Environment.GetEnvironmentVariable("LPB_NATIVE_OBSERVER_PATH") is { Length: > 0 } executable)
            {
                observer = Process.Start(new ProcessStartInfo(executable)
                {
                    ArgumentList = { window.Properties.ProcessId.Value.ToString(), observation },
                    UseShellExecute = false, CreateNoWindow = true
                });
                await WaitForAsync(() => File.Exists(observation + ".ready"));
            }
            File.WriteAllText(observation + ".phase", "click-samsung-status");
            status.Click();
            await page.GetByTestId("ErrorDetailText").WaitForAsync();
            string details = await page.GetByTestId("ErrorDetailText").InnerTextAsync();
            File.WriteAllText(Path.Combine(evidence, "details.txt"), details);
            Assert.Contains(primaryName, details);
            Assert.Contains(relatedName, details);
            await page.ScreenshotAsync(Path.Combine(evidence, "tip.png"));
            File.WriteAllText(observation + ".phase", "close-samsung-status");
            status.Click();
            await Task.Delay(300);
            File.WriteAllText(observation + ".phase", "reopen-samsung-status");
            status.Click();
            await page.GetByTestId("ErrorDetailText").WaitForAsync();
            Assert.Contains(relatedName, await page.GetByTestId("ErrorDetailText").InnerTextAsync());
            await Task.Delay(300);
        }
        finally
        {
            File.WriteAllText(observation + ".done", "finished");
            if (observer is not null)
            {
                Assert.True(observer.WaitForExit(10000));
                Assert.Equal(0, observer.ExitCode);
                observer.Dispose();
                string nativeLog = File.ReadAllText(observation + ".log");
                Assert.Contains("DETACHED", nativeLog);
                Assert.DoesNotContain("code=e06d7363", nativeLog);
                Assert.DoesNotContain("code=40080201", nativeLog);
            }
        }
    }

    private readonly HashSet<int> _existingProcesses = Process.GetProcesses().Select(p => p.Id).ToHashSet();
    private readonly string _settingsPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LivePhotoBox", "appsettings.json");
    private readonly byte[]? _settingsBefore;
    private readonly Dictionary<string, byte[]> _inputsBeforeMutation = new();

    public ScanPairingInteractionTests()
    {
        _settingsBefore = File.Exists(_settingsPath) ? File.ReadAllBytes(_settingsPath) : null;
        var settings = _settingsBefore is null ? new JsonObject() : JsonNode.Parse(_settingsBefore)!.AsObject();
        // Configure test prerequisites before launch. Direct-drop tests still do
        // not touch a ComboBox; every user's original setting is restored below.
        foreach (string key in new[] { "PairingMethodIndex", "SplitMatchProtocolIndex", "AfterCompletionActionIndex", "SplitAfterCompletionActionIndex", "SplitProtocolIndex", "OutputFormatIndex", "SplitOutputFormatIndex" })
            settings[key] = 0;
        settings["SelectedModeIndex"] = 2;
        settings["LanguageIndex"] = 1;
        settings["IsHistoryPageVisible"] = true;
        settings["SplitThreadCount"] = 1;
        settings["OverwriteExisting"] = false;
        settings["SplitOverwriteExisting"] = false;
        settings["IsRecursiveScanEnabled"] = false;
        settings["UpdateLastCheckTime"] = DateTimeOffset.Now.ToString("O");
        Directory.CreateDirectory(Path.GetDirectoryName(_settingsPath)!);
        File.WriteAllText(_settingsPath, settings.ToJsonString());
    }

    public void Dispose()
    {
        foreach (var (path, bytes) in _inputsBeforeMutation) File.WriteAllBytes(path, bytes);
        if (_settingsBefore is not null) File.WriteAllBytes(_settingsPath, _settingsBefore);
        else File.Delete(_settingsPath);
    }

    private void PreserveTestInput(string path) => _inputsBeforeMutation.TryAdd(path, File.ReadAllBytes(path));

    private AutomationElement FindLaunchedWindow(FlaUI.UIA3.UIA3Automation automation)
    {
        int[] launched = Process.GetProcessesByName("Live Photo Box").Where(p => !_existingProcesses.Contains(p.Id)).Select(p => p.Id).ToArray();
        int pid = Assert.Single(launched);
        var window = automation.GetDesktop().FindFirstChild(cf => cf.ByProcessId(pid).And(cf.ByControlType(ControlType.Window)));
        Assert.NotNull(window);
        return window;
    }

    private static AutomationElement FindById(AutomationElement parent, string id)
    {
        var element = parent.FindFirstDescendant(cf => cf.ByAutomationId(id));
        Assert.NotNull(element);
        return element;
    }

    private static async Task WaitForAsync(Func<bool> condition, int seconds = 30)
    {
        var timer = Stopwatch.StartNew();
        while (!condition() && timer.Elapsed < TimeSpan.FromSeconds(seconds)) await Task.Delay(100);
        Assert.True(condition(), "The expected real UI state did not arrive before the deadline.");
    }

    private static async Task ExpandDiagnosticsAsync(AutomationElement window)
    {
        await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId("DiagnosticToggleButton")) is { IsOffscreen: false });
        var button = FindById(window, "DiagnosticToggleButton");
        if (button.Patterns.Toggle.Pattern.ToggleState.Value != ToggleState.On) button.Click();
        await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId("MergeDiagnosticListView")) is { IsOffscreen: false }
            || window.FindFirstDescendant(cf => cf.ByAutomationId("SplitDiagnosticListView")) is { IsOffscreen: false });
    }

    private static string NewOutputDirectory(string root, string name)
    {
        string parent = Path.Combine(root, name);
        Directory.CreateDirectory(parent);
        int run = 1;
        while (Directory.Exists(Path.Combine(parent, "run-" + run))) run++;
        string output = Path.Combine(parent, "run-" + run);
        Directory.CreateDirectory(output);
        return output;
    }

    [Fact]
    public async Task Merge_RealInputLockIsIsolatedAndProtectsAllOriginals()
    {
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        string input = Path.Combine(root, "merge-locked-input"); Directory.CreateDirectory(input);
        foreach (string name in new[] { "a", "b", "c" })
            foreach (string extension in new[] { ".jpg", ".mp4" })
                if (!File.Exists(Path.Combine(input, name + extension)))
                    File.Copy(Path.Combine(root, "ordinary", "pair" + extension), Path.Combine(input, name + extension));
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        await page.GetByTestId("NavMerge").ClickAsync();
        await page.GetByTestId("ScanInputDirectory").FillAsync(input);
        var ready = page.GetByTestId("MergeTaskListView").Locator("controltype:ListItem");
        await ready.Expect().ToHaveCountAsync(3);
        string output = NewOutputDirectory(root, "merge-postscan-lock-output");
        await page.GetByTestId("ScanOutputDirectory").FillAsync(output);
        var before = Directory.GetFiles(input).ToDictionary(f => f, f => Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(f))));
        using (var held = new FileStream(Path.Combine(input, "b.jpg"), FileMode.Open, FileAccess.Read, FileShare.None))
        {
            await page.GetByTestId("StartMergeButton").ClickAsync();
            await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByName("Total: 3 tasks. Succeeded: 2, Failed: 1.")) is not null, 60);
            window.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton"))!.Click();
            var statuses = await page.GetByTestId("MergeTaskListView").GetByTestId("ScanDiagnosticStatus").AllInnerTextsAsync();
            Assert.Equal(new[] { "Success", "Merge failed", "Success" }, statuses);
            Assert.Equal(2, Directory.GetFiles(output).Length);
            await page.GetByTestId("MergeTaskListView").GetByTestId("ScanDiagnosticStatus").Nth(1).ClickAsync();
            Assert.Contains("Inspection", await page.GetByTestId("ErrorDetailText").InnerTextAsync());
            await page.ScreenshotAsync(Path.Combine(output, "ui-locked-input.png"));
            File.WriteAllText(Path.Combine(output, "ui-statuses.json"), JsonSerializer.Serialize(statuses));
        }
        foreach (var (file, hash) in before) Assert.Equal(hash, Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(file))));
    }

    [Theory]
    [InlineData("Merge", 3)]
    [InlineData("Split", 2)]
    public async Task Scan_CancelNavigateRescanAndRootFailurePreserveCompleteQueue(string workflow, int count)
    {
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        string initial = Path.Combine(root, workflow == "Merge" ? "execution" : "split-drop-files");
        string slow = Path.Combine(root, "cancel-input");
        Assert.True(Directory.GetFiles(slow).Length >= 20000);
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        await page.GetByTestId("Nav" + workflow).ClickAsync();
        var input = page.GetByTestId("ScanInputDirectory");
        var ready = page.GetByTestId(workflow + "TaskListView").Locator("controltype:ListItem");
        await input.FillAsync(initial);
        await ready.Expect().ToHaveCountAsync(count);
        var previousRows = await ready.AllInnerTextsAsync();
        await input.FillAsync(slow);
        await WaitForAsync(() => !FindById(window, "ScanInputDirectory").IsEnabled);
        Assert.True(await page.GetByTestId("AddFilesButton").IsDisabledAsync());
        Assert.True(await page.GetByTestId("Start" + workflow + "Button").IsDisabledAsync());
        Assert.False(FindById(window, workflow == "Merge" ? "PairingMethodComboBox" : "MatchProtocolComboBox").IsEnabled);
        await Task.Delay(250);
        await page.GetByTestId("ScanButton").ClickAsync();
        await page.GetByTestId("NavHome").ClickAsync();
        await page.GetByTestId("Nav" + workflow).ClickAsync();
        await WaitForAsync(() => FindById(window, "ScanInputDirectory").IsEnabled);
        await ready.Expect().ToHaveCountAsync(count);
        Assert.Equal(previousRows, await ready.AllInnerTextsAsync());
        await input.FillAsync(Path.Combine(root, "missing-root"));
        await Task.Delay(250);
        await page.GetByTestId("ScanButton").ClickAsync();
        await ExpandDiagnosticsAsync(window);
        await page.GetByTestId(workflow + "DiagnosticListView").GetByTestId("ScanDiagnosticStatus").Expect().ToHaveCountAsync(workflow == "Merge" ? 1 : 2);
        await ready.Expect().ToHaveCountAsync(count);
        await input.FillAsync(initial);
        await WaitForAsync(() => FindById(window, "ScanInputDirectory").IsEnabled);
        await ready.Expect().ToHaveCountAsync(count);
        await Task.Delay(300);
        Assert.Equal(previousRows, await ready.AllInnerTextsAsync());
        await page.ScreenshotAsync(Path.Combine(root, "screenshots", workflow + "-cancel-rescan.png"));
    }

    [Fact]
    public async Task Merge_UnmatchedAndConflictRemainVisibleWithoutExecutableTasks()
    {
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        await page.GetByTestId("NavMerge").ClickAsync();
        var input = page.GetByTestId("ScanInputDirectory");
        var ready = page.GetByTestId("MergeTaskListView").Locator("controltype:ListItem");
        var statuses = page.GetByTestId("MergeDiagnosticListView").GetByTestId("ScanDiagnosticStatus");
        await input.FillAsync(Path.Combine(root, "unmatched-only"));
        await ExpandDiagnosticsAsync(window);
        await statuses.Expect().ToHaveCountAsync(1);
        await ready.Expect().ToHaveCountAsync(0);
        Assert.True(await page.GetByTestId("StartMergeButton").IsDisabledAsync());
        await statuses.First.ClickAsync();
        string details = await page.GetByTestId("ErrorDetailText").InnerTextAsync();
        Assert.Contains("orphan.jpg", details);
        Assert.Contains("Unmatched image", details);
        await page.ScreenshotAsync(Path.Combine(root, "screenshots", "Merge-unmatched-only.png"));
        await page.GetByTestId("NavHome").ClickAsync();
        await page.GetByTestId("NavMerge").ClickAsync();
        await input.FillAsync(Path.Combine(root, "append-conflict"));
        await WaitForAsync(() => FindById(window, "ScanInputDirectory").IsEnabled);
        await WaitForAsync(() => FindById(window, "MergeDiagnosticListView").FindFirstDescendant(cf => cf.ByName("a.jpg")) != null ||
            FindById(window, "MergeDiagnosticListView").FindFirstDescendant(cf => cf.ByName("a.jpeg")) != null);
        await statuses.Expect().ToHaveCountAsync(1);
        await ready.Expect().ToHaveCountAsync(0);
        Assert.True(await page.GetByTestId("StartMergeButton").IsDisabledAsync());
        await statuses.First.ClickAsync();
        details = await page.GetByTestId("ErrorDetailText").InnerTextAsync();
        Assert.Contains("a.jpg", details);
        Assert.Contains("a.jpeg", details);
        Assert.Contains("a.mp4", details);
        await page.ScreenshotAsync(Path.Combine(root, "screenshots", "Merge-conflict-disabled.png"));
    }

    [Fact]
    public async Task Split_RuntimeFailureKeepsOtherOutputsAndTaskTip()
    {
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        string input = Path.Combine(root, "split-execution");
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        await page.GetByTestId("NavSplit").ClickAsync();
        await page.GetByTestId("ScanInputDirectory").FillAsync(input);
        await page.GetByTestId("SplitTaskListView").Locator("controltype:ListItem").Expect().ToHaveCountAsync(3);
        string output = NewOutputDirectory(root, "split-execution-output");
        await page.GetByTestId("ScanOutputDirectory").FillAsync(output);
        string broken = Path.Combine(input, "c.jpg");
        PreserveTestInput(broken);
        File.WriteAllText(broken, "actual damage after scanning");
        await page.GetByTestId("StartSplitButton").ClickAsync();
        await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByName("Total: 3 tasks. Succeeded: 2, Failed: 1.")) != null);
        await page.ScreenshotAsync(Path.Combine(root, "screenshots", "Split-runtime-summary.png"));
        var close = window.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton"));
        if (close is not null && !close.IsOffscreen) close.Click();
        var statuses = page.GetByTestId("SplitTaskListView").GetByTestId("ScanDiagnosticStatus");
        Assert.Equal(new[] { "Success", "Success", "Split failed" }, await statuses.AllInnerTextsAsync());
        Assert.Equal(4, Directory.GetFiles(output).Length);
        Assert.Equal(new[] { "a.jpg", "a.mp4", "b.jpg", "b.mp4" }, Directory.GetFiles(output).Select(Path.GetFileName).OrderBy(n => n), StringComparer.OrdinalIgnoreCase);
        Assert.NotNull(FindById(window, "SplitTaskListView").FindFirstDescendant(cf => cf.ByName("c.jpg")));
        await statuses.Nth(2).ClickAsync();
        string details = await page.GetByTestId("ErrorDetailText").InnerTextAsync();
        Assert.Contains("Unsupported request", details);
        Assert.Contains("Primary file format", details);
        await page.ScreenshotAsync(Path.Combine(root, "screenshots", "Split-runtime-task-tip.png"));
    }
}
