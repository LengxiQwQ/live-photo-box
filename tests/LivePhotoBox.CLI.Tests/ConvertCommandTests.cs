using System;
using System.CommandLine;
using System.Threading.Tasks;
using LivePhotoBox.Cli.Commands;
using Xunit;

namespace LivePhotoBox.Cli.Tests;

[Collection("cli-log")]
public sealed class ConvertCommandTests
{
    [Fact]
    public async Task Help_ExposesRebuiltNativeConversionAndCodecOptions()
    {
        var root = new RootCommand { ConvertCommand.Create() };

        CliResult result = await CliTestHost.RunAsync(root, "convert", "--help");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Rebuilt Native", result.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--codec", result.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("--overwrite", result.StdOut, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(".png", result.StdOut, StringComparison.OrdinalIgnoreCase);
    }
}
