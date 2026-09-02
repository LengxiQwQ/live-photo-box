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
    public async Task RebuiltTable_ExplainsThatProtocolMatrixIsLegacyOnly()
    {
        var root = new RootCommand { ProtocolsCommand.Create() };

        CliResult result = await CliTestHost.RunAsync(
            root, ProcessingPipelineMode.Rebuilt, "protocols");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("Current backend: Rebuilt", result.StdOut);
        Assert.Contains("Legacy compatibility data only", result.StdOut);
    }

    [Fact]
    public async Task RebuiltJson_ReportsProtocolCommandsNotReady()
    {
        var root = new RootCommand { ProtocolsCommand.Create() };

        CliResult result = await CliTestHost.RunAsync(
            root, ProcessingPipelineMode.Rebuilt, "protocols", "--json");

        Assert.Equal(0, result.ExitCode);
        Assert.Contains("\"backendMode\": \"rebuilt\"", result.StdOut);
        Assert.Contains("\"protocolCommands\": \"not_ready\"", result.StdOut);
    }
}
