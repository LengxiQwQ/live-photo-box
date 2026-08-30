using LivePhotoBox.Cli.Commands;
using System.CommandLine;
using System.Linq;
using System.Threading.Tasks;
using Xunit;

namespace LivePhotoBox.Cli.Tests
{
    [Collection("cli-log")]
    public sealed class BackendCommandTests
    {
        [Fact]
        public void Create_RegistersAllConfigurationSubcommands()
        {
            var command = BackendCommand.Create();

            Assert.Equal("backend", command.Name);
            Assert.Equal(
                ["mode", "protocol", "reset"],
                command.Subcommands.Select(item => item.Name).OrderBy(name => name).ToArray());
        }

        [Fact]
        public async Task Help_DescribesBackendConfiguration()
        {
            var root = new RootCommand { BackendCommand.Create() };
            var result = await CliTestHost.RunAsync(root, "backend", "--help");

            Assert.Equal(0, result.ExitCode);
            Assert.Contains("processing backend", result.StdOut);
            Assert.Contains("mode", result.StdOut);
            Assert.Contains("protocol", result.StdOut);
            Assert.Contains("reset", result.StdOut);
        }
    }
}
