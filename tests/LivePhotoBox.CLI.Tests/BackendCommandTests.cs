using System.Linq;
using System.CommandLine;
using System.Threading.Tasks;
using LivePhotoBox.Cli.Commands;
using Xunit;

namespace LivePhotoBox.Cli.Tests;

[Collection("cli-log")]
public sealed class BackendCommandTests
{
    [Fact]
    public void Create_RegistersOnlyGlobalConfigurationSubcommands()
    {
        Command command = BackendCommand.Create();
        Assert.Equal("backend", command.Name);
        Assert.Equal(["mode", "reset"], command.Subcommands.Select(item => item.Name).OrderBy(name => name).ToArray());
    }

    [Fact]
    public async Task Help_DescribesOneGlobalSwitch()
    {
        var root = new RootCommand { BackendCommand.Create() };
        var result = await CliTestHost.RunAsync(root, "backend", "--help");
        Assert.Equal(0, result.ExitCode);
        Assert.Contains("global", result.StdOut, System.StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("protocol", result.StdOut, System.StringComparison.OrdinalIgnoreCase);
    }
}
