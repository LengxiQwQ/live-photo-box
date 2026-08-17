using System.IO;
using System.Threading.Tasks;
using LivePhotoBox.Cli.Commands;
using Xunit;

namespace LivePhotoBox.Cli.Tests
{
    /// <summary>拼写建议：选项/协议/格式/操作名拼错时给出 Did you mean。</summary>
    [Collection("cli-log")]
    public sealed class UnknownOptionTests
    {
        [Fact]
        public async Task Split_OptionTypo_SuggestsFormat()
        {
            var r = await CliTestHost.RunAsync(SplitCommand.Create(), "--fotmat");
            Assert.Equal(1, r.ExitCode);
            Assert.Contains("Did you mean: --format", r.StdErr);
        }

        [Fact]
        public async Task Merge_ProtocolTypo_SuggestsMotionPhoto()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_proto_");
            string photo = CliTestHost.CreateDummyFile(dir, "pair.jpg");
            string video = CliTestHost.CreateDummyFile(dir, "pair.mp4");
            try
            {
                var r = await CliTestHost.RunAsync(MergeCommand.Create(), photo, video, "-p", "motin", "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("Did you mean: motion photo", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Split_ProtocolTypo_SuggestsApple()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_app_");
            string photo = CliTestHost.CreateDummyFile(dir, "photo.jpg");
            try
            {
                var r = await CliTestHost.RunAsync(SplitCommand.Create(), photo, "-p", "apel", "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("Did you mean: apple", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Merge_AfterTypo_SuggestsRecycle()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_aftertypo_");
            try
            {
                var r = await CliTestHost.RunAsync(MergeCommand.Create(), "-d", dir, "--after", "recucle", "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("Did you mean: recycle", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Repair_UnknownOption_ReportsHelpHint()
        {
            var r = await CliTestHost.RunAsync(RepairCommand.Create(), "--paralel");
            Assert.Equal(1, r.ExitCode);
            Assert.Contains("Did you mean: --parallel", r.StdErr);
            Assert.Contains("Run 'lpb repair --help'", r.StdErr);
        }
    }
}
