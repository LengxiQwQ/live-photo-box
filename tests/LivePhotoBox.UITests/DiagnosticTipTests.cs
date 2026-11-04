using System.Text.Json;
using Flawright;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace LivePhotoBox.UITests;

public sealed partial class ScanPairingInteractionTests
{
    [Theory]
    [InlineData("Merge")]
    [InlineData("Split")]
    public async Task DiagnosticTip_RetargetsWithoutDriftAndCountsAllSevenUnselectedRows(string workflow)
    {
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        string evidence = NewOutputDirectory(root, "diagnostic-tip-" + workflow);
        string input = Path.Combine(evidence, "input"); Directory.CreateDirectory(input);
        string ordinary = Path.Combine(root, "ordinary");
        if (workflow == "Merge")
            foreach (string ext in new[] { ".jpg", ".mp4" }) File.Copy(Path.Combine(ordinary, "pair" + ext), Path.Combine(input, "pair" + ext));
        else File.Copy(Path.Combine(RequiredEnvironment("LIVEPHOTOBOX_TEST_SAMPLES_DIR"), "oppo.jpg"), Path.Combine(input, "green.jpg"));
        File.Copy(Path.Combine(ordinary, "pair.jpg"), Path.Combine(input, "unmatched.jpg"));
        File.Copy(Path.Combine(ordinary, "pair.mp4"), Path.Combine(input, "video-only.mp4"));
        // Sort the gray and red row next to each other in the visible viewport.
        for (int i = 0; i < 5; i++) File.WriteAllText(Path.Combine(input, "z-broken-" + i + ".jpg"), "not media");
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        await page.BringToFrontAsync();
        // Maximize animates the navigation coordinates. Clicking before it
        // settles can hit History instead of the intended workflow.
        var previousBounds = window.BoundingRectangle;
        await Task.Delay(400);
        await WaitForAsync(() =>
        {
            var currentBounds = window.BoundingRectangle;
            bool settled = currentBounds == previousBounds;
            previousBounds = currentBounds;
            return settled;
        });
        try
        {
            await page.GetByTestId("Nav" + workflow).ClickAsync();
            await page.GetByTestId("ScanInputDirectory").FillAsync(input);
            await page.GetByTestId(workflow + "TaskListView").Locator("controltype:ListItem").Expect().ToHaveCountAsync(1);
            await Task.Delay(500);
            await WaitForAsync(() => FindById(window, "QueueScanStatsText").Name == "Tasks: 1  •  Unselected: 7");
            await ExpandDiagnosticsAsync(window);
            var diagnostics = FindById(window, workflow + "DiagnosticListView");
            async Task ScrollToAsync(double percent)
            {
                diagnostics.Patterns.Scroll.Pattern.SetScrollPercent(-1, percent);
                await WaitForAsync(() => Math.Abs(diagnostics.Patterns.Scroll.Pattern.VerticalScrollPercent.Value - percent) < 0.1);
                await Task.Delay(400); // Wait for virtualized row bindings after the viewport settles.
            }
            await ScrollToAsync(100);
            AutomationElement Row(string filename) => diagnostics.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem))
                .First(row => row.FindFirstDescendant(cf => cf.ByName(filename)) is not null);
            var gray = FindById(Row("unmatched.jpg"), "ScanDiagnosticStatus");
            // Errors precede the two unselected media members. At the bottom,
            // the last red row and the gray image row are both actually visible.
            var redRow = Row("z-broken-4.jpg");
            var red = FindById(redRow, "ScanDiagnosticStatus");
            var observations = new List<object>();
            for (int round = 0; round < 3; round++)
            {
                gray.Click(); red.Click(); gray.Click();
                await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId("ErrorDetailText")) is { IsOffscreen: false } text && text.Name.Contains("unmatched.jpg"));
                // The content changes before the old popup's close animation
                // finishes. Measure and toggle the settled new popup, not that
                // transient old popup displaying the newly assigned text.
                await Task.Delay(400);
                await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId("ErrorDetailText")) is { IsOffscreen: false } text && text.Name.Contains("unmatched.jpg"));
                var detail = FindById(window, "ErrorDetailText");
                var anchor = gray.BoundingRectangle;
                var box = detail.BoundingRectangle;
                double centerX = anchor.Left + anchor.Width / 2, centerY = anchor.Top + anchor.Height / 2;
                double dx = Math.Max(box.Left - centerX, Math.Max(0, centerX - box.Right));
                double dy = Math.Max(box.Top - centerY, Math.Max(0, centerY - box.Bottom));
                Assert.InRange(Math.Sqrt(dx * dx + dy * dy), 0, 160);
                // The detail content must stay to the left or above the clicked
                // status; right/bottom fallback is forbidden.
                Assert.True(box.Right <= anchor.Left || box.Bottom <= anchor.Top);
                observations.Add(new { round, anchor = anchor.ToString(), box = box.ToString(), dx, dy });
                gray.Click();
                await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId("ErrorDetailText")) is not { IsOffscreen: false });
            }
            gray.Click();
            await page.GetByTestId("ErrorDetailText").WaitForAsync();
            await Task.Delay(1000); // A second hover ToolTip would have opened by now.
            Assert.Empty(automation.GetDesktop().FindAllDescendants(cf => cf.ByProcessId(window.Properties.ProcessId.Value).And(cf.ByControlType(ControlType.ToolTip))));
            await page.ScreenshotAsync(Path.Combine(evidence, "anchored.png"));
            File.WriteAllText(Path.Combine(evidence, "ready"), workflow);
            if (Environment.GetEnvironmentVariable("LPB_TIP_OBSERVE") == "1")
                await WaitForAsync(() => File.Exists(Path.Combine(evidence, "observed")), 60);
            await ScrollToAsync(0);
            await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId("ErrorDetailText")) is not { IsOffscreen: false });
            await ScrollToAsync(100);
            gray = FindById(Row("unmatched.jpg"), "ScanDiagnosticStatus");
            gray.Click();
            await page.GetByTestId("ErrorDetailText").WaitForAsync();
            await page.GetByTestId("DiagnosticToggleButton").ClickAsync();
            await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId("ErrorDetailText")) is not { IsOffscreen: false });
            Assert.Equal("Tasks: 1  •  Unselected: 7", FindById(window, "QueueScanStatsText").Name);
            File.WriteAllText(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(observations));
        }
        catch (Exception error)
        {
            File.WriteAllText(Path.Combine(evidence, "failure.txt"), error.ToString());
            try { await page.ScreenshotAsync(Path.Combine(evidence, "failure.png")); }
            catch (Exception capture) { File.WriteAllText(Path.Combine(evidence, "capture-failure.txt"), capture.ToString()); }
            throw;
        }
    }
}
