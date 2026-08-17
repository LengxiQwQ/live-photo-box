using System.CommandLine;
using System.IO;
using System.Threading.Tasks;
using LivePhotoBox.Cli.Commands;
using Xunit;

namespace LivePhotoBox.Cli.Tests
{
    /// <summary>参数校验：并发数、输出路径、输入文件、批量专属选项、空模板/空路径。</summary>
    [Collection("cli-log")]
    public sealed class ValidationTests
    {
        [Theory]
        [InlineData(0)]
        [InlineData(-2)]
        [InlineData(65)]
        public async Task Parallel_OutOfRange_Rejected(int parallel)
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_par_");
            try
            {
                var r = await CliTestHost.RunAsync(MergeCommand.Create(), "-d", dir, "-j", parallel.ToString(), "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("--parallel must be between 1 and 64", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task SplitParallelZero_Rejected()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_par0_");
            string photo = CliTestHost.CreateDummyFile(dir, "photo.jpg");
            try
            {
                var r = await CliTestHost.RunAsync(SplitCommand.Create(), photo, "-j", "0", "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("--parallel must be between 1 and 64", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task RepairParallelNegative_Rejected()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_parneg_");
            string photo = CliTestHost.CreateDummyFile(dir, "photo.jpg");
            try
            {
                var r = await CliTestHost.RunAsync(RepairCommand.Create(), photo, "-j", "-3", "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("--parallel must be between 1 and 64", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Theory]
        [InlineData("split")]
        [InlineData("merge")]
        [InlineData("repair")]
        public async Task OutputPathIsFile_Rejected(string command)
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_outfile_");
            string outFile = CliTestHost.CreateDummyFile(dir, "out.txt");
            Command cmd = command switch
            {
                "split" => SplitCommand.Create(),
                "merge" => MergeCommand.Create(),
                _ => RepairCommand.Create(),
            };
            var args = new[] { "-d", dir, "-o", outFile, "--dry-run" };
            try
            {
                var r = await CliTestHost.RunAsync(cmd, args);
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("Output path is a file, not a folder", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Merge_NonexistentImage_Rejected()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_missimg_");
            string video = CliTestHost.CreateDummyFile(dir, "video.mp4");
            try
            {
                var r = await CliTestHost.RunAsync(MergeCommand.Create(), Path.Combine(dir, "missing.jpg"), video, "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("File not found", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Repair_CopyPerfectSingle_Rejected()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_copy_");
            string photo = CliTestHost.CreateDummyFile(dir, "photo.jpg");
            try
            {
                var r = await CliTestHost.RunAsync(RepairCommand.Create(), photo, "--copy-perfect", "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("--copy-perfect only works in batch mode", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Split_AllVariantsWithoutInput_ReportsSpecificError()
        {
            var r = await CliTestHost.RunAsync(SplitCommand.Create(), "--all-variants");
            Assert.Equal(1, r.ExitCode);
            Assert.Contains("--all-variants requires a single live photo file", r.StdErr);
        }

        [Fact]
        public async Task Split_EmptyCustomNaming_Rejected()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_custom_");
            string photo = CliTestHost.CreateDummyFile(dir, "photo.jpg");
            try
            {
                var r = await CliTestHost.RunAsync(SplitCommand.Create(), photo, "-n", "custom:", "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("naming template cannot be empty", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Split_EmptyAfterMove_Rejected()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_after_");
            string photo = CliTestHost.CreateDummyFile(dir, "photo.jpg");
            try
            {
                var r = await CliTestHost.RunAsync(SplitCommand.Create(), photo, "--after", "move:", "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("requires a non-empty folder path", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Merge_TwoImages_ReportsAmbiguous()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_ambig_");
            string a = CliTestHost.CreateDummyFile(dir, "a.jpg");
            string b = CliTestHost.CreateDummyFile(dir, "b.jpg");
            try
            {
                var r = await CliTestHost.RunAsync(MergeCommand.Create(), a, b, "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("Cannot determine which file is the image and which is the video", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Repair_AllFixesDisabled_ReportsHint()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_nofix_");
            string photo = CliTestHost.CreateDummyFile(dir, "photo.jpg");
            try
            {
                var r = await CliTestHost.RunAsync(
                    RepairCommand.Create(), photo,
                    "--no-rotate", "--no-thumbnail", "--no-heic", "--no-video", "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("All repair options are disabled", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Merge_TwoExistingFiles_DryRunSucceeds()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_ok_");
            string photo = CliTestHost.CreateDummyFile(dir, "pair.jpg");
            string video = CliTestHost.CreateDummyFile(dir, "pair.mp4");
            try
            {
                var r = await CliTestHost.RunAsync(MergeCommand.Create(), photo, video, "--dry-run", "--json");
                Assert.Equal(0, r.ExitCode);
                Assert.Contains("\"mode\": \"single\"", r.StdOut);
                Assert.Contains("\"status\": \"would-merge\"", r.StdOut);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Theory]
        [InlineData("split")]
        [InlineData("merge")]
        [InlineData("repair")]
        public async Task WildcardPositional_ReportsFriendlyError(string command)
        {
            Command cmd = command switch
            {
                "split" => SplitCommand.Create(),
                "merge" => MergeCommand.Create(),
                _ => RepairCommand.Create(),
            };
            var args = command == "merge"
                ? new[] { "*.jpg", "*.mp4", "--dry-run" }
                : new[] { "*.jpg", "--dry-run" };
            var r = await CliTestHost.RunAsync(cmd, args);
            Assert.Equal(1, r.ExitCode);
            Assert.Contains("Wildcards are not supported", r.StdErr);
        }

        [Fact]
        public async Task Split_OutputPathWithWildcard_ReportsFriendlyError()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_outwc_");
            try
            {
                var r = await CliTestHost.RunAsync(SplitCommand.Create(), "-d", dir, "-o", Path.Combine(dir, "out*"), "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("wildcards", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
