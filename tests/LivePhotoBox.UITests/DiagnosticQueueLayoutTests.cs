using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flawright;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace LivePhotoBox.UITests;

public sealed partial class ScanPairingInteractionTests
{
    [Theory]
    [InlineData("Merge", 1, 2)]
    [InlineData("Split", 2, 1)]
    public async Task DiagnosticQueue_CollapsesAlignsAndLoadsRealThumbnails(string workflow, int language, int theme)
    {
        var settings = JsonNode.Parse(File.ReadAllText(_settingsPath))!.AsObject();
        settings["LanguageIndex"] = language;
        settings["ElementTheme"] = theme;
        File.WriteAllText(_settingsPath, settings.ToJsonString());
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        string evidence = NewOutputDirectory(root, "diagnostic-layout-" + workflow);
        string input = Path.Combine(evidence, "input"); Directory.CreateDirectory(input);
        string ordinary = Path.Combine(root, "ordinary");
        if (workflow == "Merge")
            foreach (string ext in new[] { ".jpg", ".mp4" }) File.Copy(Path.Combine(ordinary, "pair" + ext), Path.Combine(input, "pair" + ext));
        else File.Copy(Path.Combine(RequiredEnvironment("LIVEPHOTOBOX_TEST_SAMPLES_DIR"), "oppo.jpg"), Path.Combine(input, "green.jpg"));
        File.Copy(Path.Combine(ordinary, "pair.jpg"), Path.Combine(input, "unmatched.jpg"));
        File.Copy(Path.Combine(ordinary, "pair.mp4"), Path.Combine(input, "video-only.mp4"));
        File.WriteAllText(Path.Combine(input, "broken.jpg"), "not media");
        var before = Directory.GetFiles(input).ToDictionary(p => p, p => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p))));
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        try
        {
            await page.GetByTestId("Nav" + workflow).ClickAsync();
            await page.GetByTestId("ScanInputDirectory").FillAsync(input);
            await page.GetByTestId(workflow + "TaskListView").Locator("controltype:ListItem").Expect().ToHaveCountAsync(1);
            await WaitForAsync(() => FindById(window, "ScanInputDirectory").IsEnabled);
            var tasks = FindById(window, workflow + "TaskListView");
            double collapsedHeight = tasks.BoundingRectangle.Height;
            Assert.True(window.FindFirstDescendant(cf => cf.ByAutomationId(workflow + "DiagnosticListView")) is not { IsOffscreen: false });
            await page.ScreenshotAsync(Path.Combine(evidence, "collapsed.png"));

            await page.GetByTestId("DiagnosticToggleButton").ClickAsync();
            var diagnostics = FindById(window, workflow + "DiagnosticListView");
            await WaitForAsync(() => !diagnostics.IsOffscreen);
            await WaitForAsync(() => diagnostics.FindAllDescendants(cf => cf.ByAutomationId("DiagnosticThumbnail"))
                .Any(image => image.Name == (language == 1 ? "Preview" : "预览") + ": unmatched.jpg"));
            await WaitForAsync(() => diagnostics.FindAllDescendants(cf => cf.ByAutomationId("DiagnosticThumbnail"))
                .Any(image => image.Name == (language == 1 ? "Preview" : "预览") + ": video-only.mp4"));
            var rows = diagnostics.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
            Assert.True(rows.Length >= 2);
            var taskRow = tasks.FindFirstDescendant(cf => cf.ByControlType(ControlType.ListItem));
            Assert.NotNull(taskRow);
            Assert.InRange(Math.Abs(taskRow.BoundingRectangle.Left - rows[0].BoundingRectangle.Left), 0, 2);
            Assert.InRange(Math.Abs(taskRow.BoundingRectangle.Right - rows[0].BoundingRectangle.Right), 0, 2);
            Assert.InRange(Math.Abs(taskRow.BoundingRectangle.Height - rows[0].BoundingRectangle.Height), 0, 2);
            var status = FindById(rows[0], "ScanDiagnosticStatus");
            Assert.True(status.BoundingRectangle.Right <= diagnostics.BoundingRectangle.Right);
            await page.ScreenshotAsync(Path.Combine(evidence, "expanded.png"));
            status.Click();
            await page.GetByTestId("ErrorDetailText").WaitForAsync();
            Assert.False(string.IsNullOrWhiteSpace(await page.GetByTestId("ErrorDetailText").InnerTextAsync()));
            status.Click();
            await page.GetByTestId("DiagnosticToggleButton").ClickAsync();
            await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId(workflow + "DiagnosticListView")) is not { IsOffscreen: false });
            Assert.InRange(Math.Abs(tasks.BoundingRectangle.Height - collapsedHeight), 0, 2);
            window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Normal);
            window.Patterns.Transform.Pattern.Resize(1400, 950);
            await Task.Delay(600);
            await ExpandDiagnosticsAsync(window);
            await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId(workflow + "DiagnosticListView")) is { IsOffscreen: false });
            diagnostics = FindById(window, workflow + "DiagnosticListView");
            rows = diagnostics.FindAllDescendants(cf => cf.ByControlType(ControlType.ListItem));
            foreach (var row in rows)
            {
                var text = FindById(row, "ScanDiagnosticStatus");
                Assert.True(text.BoundingRectangle.Right <= diagnostics.BoundingRectangle.Right);
                Assert.True(text.BoundingRectangle.Right <= window.BoundingRectangle.Right);
                Assert.True(text.BoundingRectangle.Width >= (language == 1 ? 80 : 35));
            }
            await page.ScreenshotAsync(Path.Combine(evidence, "narrow.png"));
            File.WriteAllText(Path.Combine(evidence, "ready"), workflow);
            if (Environment.GetEnvironmentVariable("LPB_LAYOUT_OBSERVE") == "1")
                await WaitForAsync(() => File.Exists(Path.Combine(evidence, "observed")), 60);
            foreach (var (path, hash) in before) Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
            File.WriteAllText(Path.Combine(evidence, "result.json"), JsonSerializer.Serialize(new { workflow, language, theme, collapsedHeight, rows = rows.Length, before }));
        }
        catch
        {
            await page.ScreenshotAsync(Path.Combine(evidence, "failure.png"));
            throw;
        }
    }
}
