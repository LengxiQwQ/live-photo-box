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

    [Fact]
    public async Task BackendCommand_ShowAndSetModeAndReset()
    {
        var root = new RootCommand { BackendCommand.Create() };

        var showResult = await CliTestHost.RunAsync(root, "backend");
        Assert.Equal(0, showResult.ExitCode);
        Assert.Contains("legacy", showResult.StdOut, System.StringComparison.OrdinalIgnoreCase);

        var setRebuiltResult = await CliTestHost.RunAsync(root, "backend", "mode", "rebuilt");
        Assert.Equal(0, setRebuiltResult.ExitCode);
        Assert.Contains("rebuilt", setRebuiltResult.StdOut, System.StringComparison.OrdinalIgnoreCase);

        var resetResult = await CliTestHost.RunAsync(root, "backend", "reset");
        Assert.Equal(0, resetResult.ExitCode);
        Assert.Contains("reset", resetResult.StdOut, System.StringComparison.OrdinalIgnoreCase);

        var setInvalidResult = await CliTestHost.RunAsync(root, "backend", "mode", "invalid_mode");
        Assert.Equal(1, setInvalidResult.ExitCode);
    }
}
