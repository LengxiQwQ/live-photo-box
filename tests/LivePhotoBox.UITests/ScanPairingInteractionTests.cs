using Flawright;
using FlaUI.Core.AutomationElements;
using System.Text.Json;

namespace LivePhotoBox.UITests;

[Trait("Category", "ManualUI")]
public sealed partial class ScanPairingInteractionTests : IDisposable
{
    [Theory]
    [InlineData("Merge", 2)]
    [InlineData("Split", 1)]
    public async Task DirectoryScan_PreservesReadyItemsAndOpensExistingDiagnosticTip(string workflow, int readyCount)
    {
        string executable = RequiredEnvironment("LPB_UI_TEST_APP_PATH");
        string inputRoot = Path.GetFullPath(RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT"));
        string evidence = Path.Combine(inputRoot, "screenshots");
        Directory.CreateDirectory(evidence);
        await using var app = await Flawright.Flawright.LaunchAsync(
            new LaunchOptions { ApplicationPath = executable, WorkingDirectory = Path.GetDirectoryName(executable) },
            new FlawrightOptions { DefaultTimeout = TimeSpan.FromSeconds(20) });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        Assert.NotNull(window);
        window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
        await page.BringToFrontAsync();
        try
        {
            await page.GetByTestId("Nav" + workflow).ClickAsync();
            await page.GetByTestId("ScanInputDirectory").WaitForAsync();
            var mode = page.GetByTestId(workflow == "Merge" ? "PairingMethodComboBox" : "MatchProtocolComboBox");
            await mode.ClickAsync();
            await mode.PressAsync("Home");
            await mode.PressAsync("Enter");
            await page.GetByTestId("ScanInputDirectory").FillAsync(Path.Combine(inputRoot, workflow.ToLowerInvariant()));
            var ready = page.GetByTestId(workflow + "TaskListView").Locator("controltype:ListItem");
            await ready.Expect().ToHaveCountAsync(readyCount);
            await ExpandDiagnosticsAsync(window);
            var statuses = page.GetByTestId(workflow + "DiagnosticListView").GetByTestId("ScanDiagnosticStatus");
            await statuses.Expect().ToHaveCountAsync(2);
            Assert.True(await page.GetByTestId("Start" + workflow + "Button").IsEnabledAsync());
            await statuses.First.ClickAsync();
            string details = await page.GetByTestId("ErrorDetailText").InnerTextAsync();
            Assert.Contains("broken.jpg", details);
            Assert.Contains(Path.Combine(inputRoot, workflow.ToLowerInvariant()), details);
            await page.ScreenshotAsync(Path.Combine(evidence, workflow + "-scan-error-tip.png"));
            await page.GetByTestId("NavHome").ClickAsync();
            await page.GetByTestId("Nav" + workflow).ClickAsync();
            await page.GetByTestId("ScanInputDirectory").FillAsync(Path.Combine(inputRoot, "diagnostics"));
            await ready.Expect().ToHaveCountAsync(0);
            await statuses.Expect().ToHaveCountAsync(1);
            Assert.True(await page.GetByTestId("Start" + workflow + "Button").IsDisabledAsync());
            await statuses.First.ClickAsync();
            Assert.Contains("broken.jpg", await page.GetByTestId("ErrorDetailText").InnerTextAsync());
            await page.ScreenshotAsync(Path.Combine(evidence, workflow + "-diagnostics-only.png"));
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(evidence, workflow + "-failure.txt"), error.ToString());
            File.WriteAllText(Path.Combine(evidence, workflow + "-failure-tree.json"), JsonSerializer.Serialize(
                window.FindAllDescendants().Select(e => new { Id = e.Properties.AutomationId.ValueOrDefault, Name = e.Properties.Name.ValueOrDefault, Type = e.Properties.ControlType.ValueOrDefault.ToString(), Bounds = e.Properties.BoundingRectangle.ValueOrDefault.ToString() }),
                new JsonSerializerOptions { WriteIndented = true }));
            await page.ScreenshotAsync(Path.Combine(evidence, workflow + "-failure.png"));
            throw;
        }
    }

    [Fact]
    public async Task Merge_ModesAreExclusiveAndRuntimeFailurePreservesOtherOutputs()
    {
        string executable = RequiredEnvironment("LPB_UI_TEST_APP_PATH");
        string root = Path.GetFullPath(RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT"));
        await using var app = await Flawright.Flawright.LaunchAsync(
            new LaunchOptions { ApplicationPath = executable, WorkingDirectory = Path.GetDirectoryName(executable) },
            new FlawrightOptions { DefaultTimeout = TimeSpan.FromSeconds(30) });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
        await page.GetByTestId("NavMerge").ClickAsync();
        await page.GetByTestId("ScanInputDirectory").WaitForAsync();
        var mode = FindById(window, "PairingMethodComboBox").AsComboBox();
        Assert.NotNull(mode);
        try
        {
            mode.Select(0);
            mode.Collapse();
            await page.GetByTestId("ScanInputDirectory").FillAsync(Path.Combine(root, "modes"));
            var ready = page.GetByTestId("MergeTaskListView").Locator("controltype:ListItem");
            await ready.Expect().ToHaveCountAsync(1);
            foreach (var (index, expectedName) in new[] { (1, "apple.heic"), (2, "vivo.jpg") })
            {
                mode.Select(index);
                mode.Collapse();
                // The product deliberately debounces scan clicks for 200 ms.
                await Task.Delay(250);
                await page.GetByTestId("ScanButton").ClickAsync();
                await ready.Expect().ToHaveCountAsync(1);
                window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
                await page.BringToFrontAsync();
                var taskList = FindById(window, "MergeTaskListView");
                var deadlineForRow = DateTime.UtcNow.AddSeconds(10);
                while (taskList.FindFirstDescendant(cf => cf.ByName(expectedName)) is null && DateTime.UtcNow < deadlineForRow)
                    await Task.Delay(100);
                var sourceName = taskList.FindFirstDescendant(cf => cf.ByName(expectedName));
                Assert.NotNull(sourceName);
                Assert.False(sourceName.Properties.IsOffscreen.Value);
                await page.ScreenshotAsync(Path.Combine(root, "screenshots", "Merge-mode-" + expectedName + ".png"));
            }
            mode.Select(0);
            mode.Collapse();
            await page.GetByTestId("ScanInputDirectory").FillAsync(Path.Combine(root, "execution"));
            await ready.Expect().ToHaveCountAsync(3);
            string output = NewOutputDirectory(root, "execution-output");
            PreserveTestInput(Path.Combine(root, "execution", "c.jpg"));
            await page.GetByTestId("ScanOutputDirectory").FillAsync(output);
            File.WriteAllText(Path.Combine(root, "execution", "c.jpg"), "actual source damage after completed scan");
            await page.GetByTestId("StartMergeButton").ClickAsync();
            var deadline = DateTime.UtcNow.AddSeconds(60);
            while (DateTime.UtcNow < deadline && (!File.Exists(Path.Combine(output, "a.jpg")) || !File.Exists(Path.Combine(output, "b.jpg"))))
                await Task.Delay(100);
            Assert.True(File.Exists(Path.Combine(output, "a.jpg")));
            Assert.True(File.Exists(Path.Combine(output, "b.jpg")));
            Assert.False(File.Exists(Path.Combine(output, "c.jpg")));
            await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByName("Total: 3 tasks. Succeeded: 2, Failed: 1.")) != null);
            await page.ScreenshotAsync(Path.Combine(root, "screenshots", "Merge-runtime-summary.png"));
            var close = window.FindFirstDescendant(cf => cf.ByAutomationId("CloseButton"));
            if (close is not null && !close.IsOffscreen) close.Click();
            var statuses = page.GetByTestId("MergeTaskListView").GetByTestId("ScanDiagnosticStatus");
            Assert.Equal(new[] { "Success", "Success", "Merge failed" }, await statuses.AllInnerTextsAsync());
            await statuses.Nth(2).ClickAsync();
            Assert.NotNull(FindById(window, "MergeTaskListView").FindFirstDescendant(cf => cf.ByName("c.jpg")));
            Assert.Contains("Primary image container", await page.GetByTestId("ErrorDetailText").InnerTextAsync());
            await page.ScreenshotAsync(Path.Combine(root, "screenshots", "Merge-runtime-final.png"));
        }
        catch
        {
            await page.ScreenshotAsync(Path.Combine(root, "screenshots", "Merge-modes-failure.png"));
            File.WriteAllText(Path.Combine(root, "screenshots", "Merge-modes-tree.json"), JsonSerializer.Serialize(window.FindAllDescendants().Select(e => new
            {
                Id = e.Properties.AutomationId.ValueOrDefault, Name = e.Properties.Name.ValueOrDefault,
                Bounds = e.Properties.BoundingRectangle.ValueOrDefault.ToString()
            }), new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
    }

    [Theory]
    [InlineData("Merge", "files")]
    [InlineData("Merge", "folders")]
    [InlineData("Merge", "mixed")]
    [InlineData("Split", "files")]
    [InlineData("Split", "folders")]
    [InlineData("Split", "mixed")]
    public async Task ExplorerDrop_ImportsWholeSelectionAndDeduplicates(string workflow, string kind)
    {
        string executable = RequiredEnvironment("LPB_UI_TEST_APP_PATH");
        string root = Path.GetFullPath(RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT"));
        string input = Path.Combine(root, workflow.ToLowerInvariant() + "-drop-" + kind);
        Assert.True(Directory.Exists(input));
        Assert.True(File.Exists(Path.Combine(input, kind == "files" || kind == "mixed" ? "b.jpg" : Path.Combine("group-b", "b.jpg"))), "The second group's real fixture is missing.");
        await using var app = await Flawright.Flawright.LaunchAsync(
            new LaunchOptions { ApplicationPath = executable, WorkingDirectory = Path.GetDirectoryName(executable) },
            new FlawrightOptions { DefaultTimeout = TimeSpan.FromSeconds(30) });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Maximized);
        await page.GetByTestId("Nav" + workflow).ClickAsync();
        await page.GetByTestId("ScanInputDirectory").WaitForAsync();
        // Keep the drop scenario direct: no ComboBox interaction before dragging.
        var target = FindById(window, workflow + "TaskListView").BoundingRectangle;
        dynamic shell = Activator.CreateInstance(Type.GetTypeFromProgID("Shell.Application")!)!;
        shell.Open(input);
        dynamic? explorer = null;
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (explorer is null && DateTime.UtcNow < deadline)
        {
            foreach (dynamic candidate in shell.Windows())
            {
                try
                {
                    if (string.Equals((string)candidate.Document.Folder.Self.Path, input, StringComparison.OrdinalIgnoreCase))
                    { explorer = candidate; break; }
                }
                catch (System.Runtime.InteropServices.COMException) { }
            }
            if (explorer is null) await Task.Delay(100);
        }
        Assert.NotNull(explorer);
        try
        {
            var explorerWindow = automation.FromHandle(new IntPtr((long)explorer!.HWND));
            explorerWindow.Patterns.Window.Pattern.SetWindowVisualState(FlaUI.Core.Definitions.WindowVisualState.Normal);
            explorerWindow.Patterns.Transform.Pattern.Move(0, 0);
            explorerWindow.Patterns.Transform.Pattern.Resize(720, 640);
            var ready = page.GetByTestId(workflow + "TaskListView").Locator("controltype:ListItem");
            for (int repeat = 0; repeat < 2; repeat++)
            {
                dynamic items = explorer.Document.Folder.Items();
                Assert.True((int)items.Count >= 2);
                for (int i = 0; i < (int)items.Count; i++) explorer.Document.SelectItem(items.Item(i), i == 0 ? 29 : 1);
                Assert.Equal((int)items.Count, (int)explorer.Document.SelectedItems().Count);
                string firstName = (string)items.Item(0).Name;
                SetForegroundWindow(new IntPtr((long)explorer.HWND));
                await Task.Delay(200);
                var source = explorerWindow.FindFirstDescendant(cf => cf.ByName(firstName));
                Assert.NotNull(source);
                // COM already selected the whole set. A preparatory click can open
                // the image (Explorer single-click activation), stealing the drag.
                Assert.Equal((int)items.Count, (int)explorer.Document.SelectedItems().Count);
                var bounds = source.BoundingRectangle;
                var start = new System.Drawing.Point(bounds.X + bounds.Width / 2, bounds.Y + bounds.Height / 2);
                var end = new System.Drawing.Point(target.X + target.Width * 3 / 4, target.Y + Math.Min(150, target.Height / 2));
                FlaUI.Core.Input.Mouse.MoveTo(start);
                FlaUI.Core.Input.Mouse.Down(FlaUI.Core.Input.MouseButton.Left);
                try
                {
                    await Task.Delay(200);
                    FlaUI.Core.Input.Mouse.MoveTo(start.X + 20, start.Y + 10);
                    FlaUI.Core.Input.Mouse.MoveTo(end);
                    await Task.Delay(700);
                }
                finally { FlaUI.Core.Input.Mouse.Up(FlaUI.Core.Input.MouseButton.Left); }
                await ready.Expect().ToHaveCountAsync(2);
                await ExpandDiagnosticsAsync(window);
                await page.GetByTestId(workflow + "DiagnosticListView").Locator("controltype:ListItem").Expect().ToHaveCountAsync(1);
            }
            await page.BringToFrontAsync();
            await page.ScreenshotAsync(Path.Combine(root, "screenshots", workflow + "-drop-" + kind + ".png"));
        }
        catch (Exception error)
        {
            string prefix = Path.Combine(root, "screenshots", workflow + "-drop-" + kind + "-failure");
            File.WriteAllText(prefix + ".txt", error.ToString());
            await page.ScreenshotAsync(prefix + ".png");
            File.WriteAllText(prefix + "-tree.json", JsonSerializer.Serialize(window.FindAllDescendants().Select(e => new
            {
                Id = e.Properties.AutomationId.ValueOrDefault, Name = e.Properties.Name.ValueOrDefault,
                Bounds = e.Properties.BoundingRectangle.ValueOrDefault.ToString()
            }), new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
        finally { explorer!.Quit(); }
    }

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetForegroundWindow(IntPtr window);

    private static string RequiredEnvironment(string name) => Environment.GetEnvironmentVariable(name)
        ?? throw new InvalidOperationException($"Set {name} to the real application/fixture path before running ManualUI tests.");
}
