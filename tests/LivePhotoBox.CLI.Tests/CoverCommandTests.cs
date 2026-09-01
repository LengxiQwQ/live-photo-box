using LivePhotoBox.Cli.Commands;
using System.CommandLine;
using Xunit;

namespace LivePhotoBox.Cli.Tests;

public sealed class CoverCommandTests
{
    [Fact]
    public async Task KeyphotoAlias_ResolvesToCoverCommand()
    {
        RootCommand root = new() { CoverCommand.Create() };

        CliResult result = await CliTestHost.RunAsync(root, "keyphoto", "--help");

        Assert.Equal(0, result.ExitCode);
        string help = result.StdOut + result.StdErr;
        Assert.Contains("Change the cover frame", help, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("cover <files>", help, StringComparison.OrdinalIgnoreCase);
    }
}
