using System.IO;
using System.Threading.Tasks;
using LivePhotoBox.Cli.Commands;
using Xunit;

namespace LivePhotoBox.Cli.Tests
{
    /// <summary>--key-timestamp：split（Apple 转换覆盖封面位置）与 merge 共用同一解析/校验逻辑。</summary>
    [Collection("cli-log")]
    public sealed class KeyTimestampTests
    {
        [Fact]
        public async Task Split_KeyTimestampApple_RejectedInRebuilt()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_kt_ok_");
            string photo = CliTestHost.CreateDummyFile(dir, "photo.jpg");
            try
            {
                var r = await CliTestHost.RunAsync(
                    SplitCommand.Create(), photo, "-p", "apple", "--key-timestamp", "2.500", "--dry-run", "--json");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("rebuilt split exports protocol-free neutral media only", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Split_KeyTimestampInvalid_Rejected()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_kt_bad_");
            string photo = CliTestHost.CreateDummyFile(dir, "photo.jpg");
            try
            {
                var r = await CliTestHost.RunAsync(SplitCommand.Create(), photo, "--key-timestamp", "abc", "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("Invalid --key-timestamp", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Split_KeyTimestampBatch_Rejected()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_kt_batch_");
            try
            {
                var r = await CliTestHost.RunAsync(
                    SplitCommand.Create(), "-d", dir, "--key-timestamp", "2.500", "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("only works with a single live photo file", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Split_KeyTimestampNonApple_Rejected()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_kt_proto_");
            string photo = CliTestHost.CreateDummyFile(dir, "photo.jpg");
            try
            {
                var r = await CliTestHost.RunAsync(
                    SplitCommand.Create(), photo, "-p", "none", "--key-timestamp", "2.500", "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("--key-timestamp is unavailable in rebuilt split", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Split_KeyTimestampAllVariants_Rejected()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_kt_variants_");
            string photo = CliTestHost.CreateDummyFile(dir, "photo.jpg");
            try
            {
                var r = await CliTestHost.RunAsync(
                    SplitCommand.Create(), photo, "--all-variants", "--key-timestamp", "2.500", "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("not supported with --all-variants", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Merge_KeyTimestamp_DryRunStillWorks()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_kt_merge_");
            string photo = CliTestHost.CreateDummyFile(dir, "pair.jpg");
            string video = CliTestHost.CreateDummyFile(dir, "pair.mp4");
            try
            {
                var r = await CliTestHost.RunAsync(
                    MergeCommand.Create(), photo, video, "-p", "huawei", "--key-timestamp", "2.500", "--dry-run", "--json");
                Assert.Equal(0, r.ExitCode);
                Assert.Contains("\"status\": \"would-merge\"", r.StdOut);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
