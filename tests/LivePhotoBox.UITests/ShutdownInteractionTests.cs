using System.Diagnostics;
using Flawright;
using FlaUI.Core.Definitions;

namespace LivePhotoBox.UITests;

public sealed partial class ScanPairingInteractionTests
{
    [Theory]
    [InlineData("Home")]
    [InlineData("Split")]
    public async Task Shutdown_ClosesNativeWindowWithFirstChanceObservation(string workflow)
    {
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        string evidence = NewOutputDirectory(root, "shutdown-" + workflow);
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        if (workflow == "Split")
        {
            await page.GetByTestId("NavSplit").ClickAsync();
            await page.GetByTestId("ScanInputDirectory").FillAsync(Path.Combine(root, "split-mixed"));
            await page.GetByTestId("SplitTaskListView").Locator("controltype:ListItem").Expect().ToHaveCountAsync(2);
            await ExpandDiagnosticsAsync(window);
            await page.GetByTestId("SplitDiagnosticListView").GetByTestId("ScanDiagnosticStatus").First.ClickAsync();
            await page.GetByTestId("ErrorDetailText").WaitForAsync();
            await Task.Delay(500);
        }
        using var process = Process.GetProcessById(window.Properties.ProcessId.Value);
        string observation = Path.Combine(evidence, "native");
        using var observer = Process.Start(new ProcessStartInfo(RequiredEnvironment("LPB_NATIVE_OBSERVER_PATH"))
        {
            ArgumentList = { process.Id.ToString(), observation }, UseShellExecute = false, CreateNoWindow = true
        })!;
        await WaitForAsync(() => File.Exists(observation + ".ready"));
        File.WriteAllText(observation + ".phase", "close-window");
        window.Patterns.Window.Pattern.Close();
        await process.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(20));
        await observer.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(15));
        File.WriteAllText(Path.Combine(evidence, "exit.txt"), process.ExitCode.ToString());
        Assert.Equal(0, process.ExitCode);
        Assert.Equal(0, observer.ExitCode);
        // Baseline logs first; the strict expected-exception check is enabled
        // only for the repair candidate, without altering observation itself.
        if (Environment.GetEnvironmentVariable("LPB_ASSERT_CLEAN_SHUTDOWN") == "1")
            Assert.DoesNotContain("code=e06d7363", File.ReadAllText(observation + ".log"));
    }
}
