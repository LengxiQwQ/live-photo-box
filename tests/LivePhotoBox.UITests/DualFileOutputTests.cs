using System.Security.Cryptography;
using System.Text.Json;
using Flawright;
using FlaUI.Core.AutomationElements;
using FlaUI.Core.Definitions;

namespace LivePhotoBox.UITests;

public sealed partial class ScanPairingInteractionTests
{
    [Theory]
    [InlineData(1, ".JPG", ".MOV")]
    [InlineData(2, ".JPG", ".MP4")]
    public async Task DualFileTargetSelectionPublishesBothMembers(int protocol, string imageExtension, string extension)
    {
        string root = RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT");
        string samples = RequiredEnvironment("LIVEPHOTOBOX_TEST_SAMPLES_DIR");
        string input = Path.Combine(root, "single-source"); Directory.CreateDirectory(input);
        string source = Path.Combine(input, "source.jpg");
        if (!File.Exists(source)) File.Copy(Path.Combine(samples, "oppo.jpg"), source);
        string hash = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        string output = NewOutputDirectory(root, "dual-target-" + protocol);
        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions
            { ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH") });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        await page.GetByTestId("NavSplit").ClickAsync();
        var target = FindById(window, "ProtocolComboBox").AsComboBox();
        target.Select(protocol); target.Collapse();
        // The target change selects its first available media format.
        await page.GetByTestId("ScanInputDirectory").FillAsync(input);
        await page.GetByTestId("SplitTaskListView").Locator("controltype:ListItem").Expect().ToHaveCountAsync(1);
        await page.GetByTestId("ScanOutputDirectory").FillAsync(output);
        await page.GetByTestId("StartSplitButton").ClickAsync();
        await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByName("Total: 1 tasks. Succeeded: 1, Failed: 0.")) != null ||
            window.FindFirstDescendant(cf => cf.ByName("共 1 个任务，成功 1 个，失败 0 个")) != null, 180);
        await page.ScreenshotAsync(Path.Combine(output, "summary.png"));
        var files = Directory.GetFiles(output).Where(p => Path.GetExtension(p) != ".png").ToArray();
        Assert.Equal(2, files.Length);
        Assert.Single(files, p => Path.GetExtension(p).Equals(imageExtension, StringComparison.OrdinalIgnoreCase));
        Assert.Single(files, p => Path.GetExtension(p).Equals(extension, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
        File.WriteAllText(Path.Combine(output, "evidence.json"), JsonSerializer.Serialize(new { protocol, files, hash },
            new JsonSerializerOptions { WriteIndented = true }));
    }
}
