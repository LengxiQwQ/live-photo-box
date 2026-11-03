using System.Text.Json;
using LivePhotoBox.Cli.Commands;
using Xunit;

namespace LivePhotoBox.Cli.Tests;

[Collection("cli-log")]
public sealed class SplitInputSelectionTests
{
    [Fact]
    public async Task CompanionIsRejectedForFolderBeforeCreatingOutput()
    {
        string root = CliTestHost.CreateTempDir("lpb-split-companion-");
        try
        {
            string output = Path.Combine(root, "output");
            var result = await CliTestHost.RunAsync(SplitCommand.Create(), root, "--companion", Path.Combine(root, "video.mov"), "--output", output, "--json");
            Assert.Equal(1, result.ExitCode); Assert.Contains("--companion requires", result.StdErr);
            Assert.False(Directory.Exists(output));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact]
    public async Task PairedDryRunPreservesBothInputsAndCreatesNoOutput()
    {
        string root = CliTestHost.CreateTempDir("lpb-split-pair-dry-");
        try
        {
            string image = CliTestHost.CreateDummyFile(root, "input.jpg"), video = CliTestHost.CreateDummyFile(root, "input.mov");
            string output = Path.Combine(root, "output");
            var result = await CliTestHost.RunAsync(SplitCommand.Create(), image, "--companion", video, "--output", output, "--json", "--dry-run");
            Assert.Equal(0, result.ExitCode);
            using var json = JsonDocument.Parse(result.StdOut);
            Assert.Equal("would-split", json.RootElement.GetProperty("status").GetString());
            Assert.False(Directory.Exists(output)); Assert.True(File.Exists(image)); Assert.True(File.Exists(video));
        }
        finally { Directory.Delete(root, true); }
    }
    [Fact]
    public async Task PairedAllVariantsDryRunRetainsCompanionInEveryPlanEntry()
    {
        string root = CliTestHost.CreateTempDir("lpb-split-variants-pair-");
        try
        {
            string image = CliTestHost.CreateDummyFile(root, "input.jpg"), video = CliTestHost.CreateDummyFile(root, "input.mov");
            var result = await CliTestHost.RunAsync(SplitCommand.Create(), image, "--companion", video, "--all-variants", "--output", Path.Combine(root, "output"), "--json", "--dry-run");
            Assert.Equal(0, result.ExitCode);
            using var json = JsonDocument.Parse(result.StdOut);
            var entries = json.RootElement.GetProperty("files").EnumerateArray().ToArray();
            Assert.Equal(4, entries.Length);
            Assert.All(entries, entry => Assert.Equal(video, entry.GetProperty("CompanionPath").GetString()));
        }
        finally { Directory.Delete(root, true); }
    }

}
