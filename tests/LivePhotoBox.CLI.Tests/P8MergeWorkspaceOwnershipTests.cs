using LivePhotoBox.Cli.Commands;
using Xunit;

namespace LivePhotoBox.Cli.Tests;

[Collection("cli-log")]
public sealed class P8MergeWorkspaceOwnershipTests
{
    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task MergeNeverDeletesPreexistingOutputTemp(bool allVariants, bool dryRun)
    {
        string root = CliTestHost.CreateTempDir("lpb-p8-merge-ownership-");
        try
        {
            string image = CliTestHost.CreateDummyFile(root, "image.jpg");
            string video = CliTestHost.CreateDummyFile(root, "video.mp4");
            string output = Path.Combine(root, "output");
            string foreignDirectory = Path.Combine(output, "Temp", "nested");
            Directory.CreateDirectory(foreignDirectory);
            string foreignFile = Path.Combine(foreignDirectory, "user-data.txt");
            await File.WriteAllTextAsync(foreignFile, "user-owned content");
            var args = new List<string> { image, video, "--output", output };
            if (allVariants) args.Add("--all-variants");
            else args.AddRange(["--yes", "--json"]);
            if (dryRun) args.Add("--dry-run");

            var result = await CliTestHost.RunAsync(MergeCommand.Create(), args.ToArray());

            Assert.Equal(dryRun ? 0 : 1, result.ExitCode);
            Assert.True(File.Exists(foreignFile), result.StdErr);
            Assert.Equal("user-owned content", await File.ReadAllTextAsync(foreignFile));
            Assert.Equal("dummy", await File.ReadAllTextAsync(image));
            Assert.Equal("dummy", await File.ReadAllTextAsync(video));
            Assert.Single(Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories));
        }
        finally
        {
            Directory.Delete(root, recursive: true);
        }
    }
}
