using System;
using System.IO;
using System.Threading.Tasks;
using LivePhotoBox.Cli.Commands;
using LivePhotoBox.Models;
using Xunit;

namespace LivePhotoBox.Cli.Tests;

[Collection("cli-log")]
public sealed class RebuiltSplitIsolationTests
{
    [Fact]
    public async Task RebuiltSplit_RejectsTargetProtocolWriter()
    {
        string directory = CliTestHost.CreateTempDir("lpb-rebuilt-split-");
        string input = CliTestHost.CreateDummyFile(directory, "photo.jpg");
        try
        {
            CliResult result = await CliTestHost.RunAsync(
                SplitCommand.Create(),
                ProcessingPipelineMode.Rebuilt,
                input, "--protocol", "apple", "--dry-run", "--json");

            Assert.Equal(1, result.ExitCode);
            Assert.Contains("protocol-free neutral media only", result.StdErr, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("apple_jpg+mov", result.StdOut, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task RebuiltSplit_AllVariantsListsOnlyNeutralOutputs()
    {
        string directory = CliTestHost.CreateTempDir("lpb-rebuilt-split-variants-");
        string input = CliTestHost.CreateDummyFile(directory, "photo.jpg");
        try
        {
            CliResult result = await CliTestHost.RunAsync(
                SplitCommand.Create(),
                ProcessingPipelineMode.Rebuilt,
                input, "--all-variants", "--dry-run");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("Variants", result.StdOut, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("4", result.StdOut, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Apple Live Photo", result.StdOut, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("vivo Live Photo", result.StdOut, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }
}
