using Flawright;

namespace LivePhotoBox.UITests;

[Trait("Category", "ManualUI")]
public sealed class MainWindowSmokeTests
{
    [Fact]
    public async Task MainWindow_LaunchesWithExpectedTitle()
    {
        string applicationPath = Environment.GetEnvironmentVariable("LPB_UI_TEST_APP_PATH")
            ?? throw new InvalidOperationException(
                "Set LPB_UI_TEST_APP_PATH to the built Live Photo Box executable before running ManualUI tests.");

        Assert.True(File.Exists(applicationPath), $"Application executable was not found: {applicationPath}");

        await using var flawright = await Flawright.Flawright.LaunchAsync(
            new LaunchOptions
            {
                ApplicationPath = applicationPath,
                WorkingDirectory = Path.GetDirectoryName(applicationPath)
            },
            new FlawrightOptions
            {
                DefaultTimeout = TimeSpan.FromSeconds(20)
            });

        IFlawrightPage page = await flawright.Browser.NewPageAsync();
        string title = await page.TitleAsync();
        Assert.True(
            title is "Live Photo Box" or "实况照片工具箱",
            $"Unexpected localized window title: {title}");
    }

    [Fact]
    public async Task MainWindow_MainNavigation_ChangesSelectedWorkflow()
    {
        string applicationPath = Environment.GetEnvironmentVariable("LPB_UI_TEST_APP_PATH")
            ?? throw new InvalidOperationException(
                "Set LPB_UI_TEST_APP_PATH to the built Live Photo Box executable before running ManualUI tests.");

        await using var flawright = await Flawright.Flawright.LaunchAsync(
            new LaunchOptions { ApplicationPath = applicationPath, WorkingDirectory = Path.GetDirectoryName(applicationPath) },
            new FlawrightOptions { DefaultTimeout = TimeSpan.FromSeconds(20) });

        IFlawrightPage page = await flawright.Browser.NewPageAsync();
        var merge = page.GetByTestId("NavMerge");
        var split = page.GetByTestId("NavSplit");

        await merge.ClickAsync();
        Assert.Equal("true", await merge.GetAttributeAsync("Selected"));
        await split.ClickAsync();
        Assert.Equal("true", await split.GetAttributeAsync("Selected"));
    }
}
