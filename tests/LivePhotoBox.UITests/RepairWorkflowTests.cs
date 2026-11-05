using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using Flawright;
using FlaUI.Core.Definitions;
using FlaUI.Core.AutomationElements;

namespace LivePhotoBox.UITests;

public sealed partial class ScanPairingInteractionTests
{
    [Theory]
    [InlineData(true,3,6)]
    [InlineData(false,2,2)]
    public async Task RepairPage_UsesSharedBackendAndPublishesNewPairs(bool appleFixtures,int expectedTasks,int expectedFiles)
    {
        string root = Path.GetFullPath(RequiredEnvironment("LPB_UI_TEST_INPUT_ROOT"));
        string input = Path.Combine(root, appleFixtures ? "gui-input" : "gui-samsung-input");
        string output = NewOutputDirectory(root, appleFixtures ? "gui-output" : "gui-samsung-output");
        var before = Directory.GetFiles(input).ToDictionary(path => Path.GetFileName(path),
            path => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path))));
        var settings = JsonNode.Parse(File.ReadAllText(_settingsPath))!.AsObject();
        settings["IsOutputToDirectory"] = true;
        settings["IsHeicRepairEnabled"] = true;
        settings["IsAppleOnlyScanEnabled"] = true;
        settings["IsCopyPerfectToOutput"] = false;
        settings["IsStrictLivePhotoScanEnabled"] = true;
        File.WriteAllText(_settingsPath, settings.ToJsonString());

        await using var app = await Flawright.Flawright.LaunchAsync(new LaunchOptions
        {
            ApplicationPath = RequiredEnvironment("LPB_UI_TEST_APP_PATH")
        }, new FlawrightOptions { DefaultTimeout = TimeSpan.FromSeconds(30) });
        var page = await app.Browser.NewPageAsync();
        using var automation = new FlaUI.UIA3.UIA3Automation();
        var window = FindLaunchedWindow(automation);
        window.Patterns.Window.Pattern.SetWindowVisualState(WindowVisualState.Maximized);
        try
        {
            await page.GetByTestId("NavRepair").ClickAsync();
            await page.GetByTestId("RepairInputDirectory").FillAsync(input);
            await page.GetByTestId("RepairOutputDirectory").FillAsync(output);
            // Changing a valid input directory starts the page's automatic scan.
            // Clicking the same command here would cancel that active scan.
            var rows = page.GetByTestId("RepairTaskListView").Locator("controltype:ListItem");
            await rows.Expect().ToHaveCountAsync(expectedTasks);
            await WaitForAsync(() => FindById(window, "RepairScanButton").IsEnabled);
            Assert.Equal(expectedTasks.ToString(),await page.GetByTestId("RepairLivePhotoCount").InnerTextAsync());
            await page.GetByTestId("RepairTypeFilter").ClickAsync();
            await page.GetByTestId("RepairLivePhotoFilter").ClickAsync();
            await rows.Expect().ToHaveCountAsync(expectedTasks);
            var queueNames = FindById(window, "RepairTaskListView").FindAllDescendants()
                .Select(element => element.Properties.Name.ValueOrDefault ?? "").ToArray();
            Assert.Contains(queueNames, name => name.StartsWith("[") && name.Contains("90°"));
            Assert.DoesNotContain(queueNames, name => name.Contains("Samsung image direction=") || name.Contains("Orientation="));
            if (!appleFixtures)
                Assert.Contains(queueNames, name => name.Count(c => c == '[') >= 3);
            await page.GetByTestId("StartRepairButton").ClickAsync();
            await WaitForAsync(() => window.FindFirstDescendant(cf => cf.ByAutomationId("PrimaryButton")) is { IsEnabled: true });
            FindById(window, "PrimaryButton").AsButton().Invoke();
            await WaitForAsync(() => Directory.GetFiles(output, "*_repaired.*").Length == expectedFiles, 120);
            await WaitForAsync(() => FindById(window, "RepairScanButton").IsEnabled);
            Assert.DoesNotContain(window.FindAllDescendants(), element =>
                element.Properties.Name.ValueOrDefault == "Repair Failed");
            await page.ScreenshotAsync(Path.Combine(output, "repair-completed.png"));
            foreach (var (name, hash) in before)
            {
                byte[] bytes = File.ReadAllBytes(Path.Combine(input, name!));
                Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(bytes)));
            }
            File.WriteAllText(Path.Combine(output, "gui-result.json"), JsonSerializer.Serialize(new
            {
                input, output, sources = before, diagnosisLabels = queueNames.Where(name => name.StartsWith("[")),
                files = Directory.GetFiles(output, "*_repaired.*")
            }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch
        {
            await page.ScreenshotAsync(Path.Combine(output, "repair-failed.png"));
            File.WriteAllText(Path.Combine(output, "tree.json"), JsonSerializer.Serialize(
                window.FindAllDescendants().Select(element => new
                {
                    id = element.Properties.AutomationId.ValueOrDefault,
                    name = element.Properties.Name.ValueOrDefault
                }), new JsonSerializerOptions { WriteIndented = true }));
            throw;
        }
    }
}
