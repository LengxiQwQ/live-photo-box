using System.CommandLine;
using System.Threading.Tasks;
using LivePhotoBox.Cli.Commands;
using LivePhotoBox.Models;
using Xunit;

namespace LivePhotoBox.Cli.Tests;

[Collection("cli-log")]
public sealed class ProtocolsCommandTests
{
    [Fact]
    public async Task RebuiltTable_ExplainsCurrentNativeCoverage()
    {
        var root = new RootCommand { ProtocolsCommand.Create() };

        CliResult result = await CliTestHost.RunAsync(
            root, "protocols");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Engine: Rebuilt (Native)", result.StdOut);
        Assert.Contains("merge/split paths are active", result.StdOut);
    }

    [Fact]
    public async Task RebuiltJson_ReportsPartialProtocolCoverage()
    {
        var root = new RootCommand { ProtocolsCommand.Create() };

        CliResult result = await CliTestHost.RunAsync(
            root, "protocols", "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"backendMode\": \"rebuilt\"", result.StdOut);
        Assert.Contains("\"protocolCommands\": \"rebuilt\"", result.StdOut);
    }
}
