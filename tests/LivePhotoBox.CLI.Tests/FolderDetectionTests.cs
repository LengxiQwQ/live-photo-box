using System.IO;
using System.Threading.Tasks;
using LivePhotoBox.Cli.Commands;
using Xunit;

namespace LivePhotoBox.Cli.Tests
{
    /// <summary>目录自动识别：位置参数不带扩展名 = 批量模式；带扩展名 = 单文件模式。</summary>
    [Collection("cli-log")]
    public sealed class FolderDetectionTests
    {
        [Fact]
        public async Task Split_ExistingFolderWithoutDash_EntersBatchMode()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_split_");
            try
            {
                var r = await CliTestHost.RunAsync(SplitCommand.Create(), dir, "--dry-run", "--json");
                Assert.Equal(0, r.ExitCode);
                Assert.Contains("\"mode\": \"batch\"", r.StdOut);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Merge_ExistingFolderWithoutDash_EntersBatchMode()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_merge_");
            try
            {
                var r = await CliTestHost.RunAsync(MergeCommand.Create(), dir, "-p", "motionphoto", "--dry-run", "--json");
                Assert.Equal(0, r.ExitCode);
                Assert.Contains("\"mode\": \"batch\"", r.StdOut);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task Repair_ExistingFolderWithoutDash_EntersBatchMode()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_repair_");
            try
            {
                var r = await CliTestHost.RunAsync(RepairCommand.Create(), dir, "--dry-run", "--json");
                Assert.Equal(0, r.ExitCode);
                Assert.Contains("\"mode\": \"batch\"", r.StdOut);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task FolderNameWithDot_StillDetectedAsFolder()
        {
            string root = CliTestHost.CreateTempDir("lpb_test_dot_");
            string dir = Path.Combine(root, "My.Photos");
            Directory.CreateDirectory(dir);
            try
            {
                var r = await CliTestHost.RunAsync(SplitCommand.Create(), dir, "--dry-run", "--json");
                Assert.Equal(0, r.ExitCode);
                Assert.Contains("\"mode\": \"batch\"", r.StdOut);
            }
            finally { Directory.Delete(root, recursive: true); }
        }

        [Fact]
        public async Task FileWithImageExtension_StaysSingleFile()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_file_");
            string photo = CliTestHost.CreateDummyFile(dir, "photo.jpg");
            try
            {
                var r = await CliTestHost.RunAsync(SplitCommand.Create(), photo, "--dry-run", "--json");
                Assert.Equal(0, r.ExitCode);
                Assert.Contains("\"mode\": \"single\"", r.StdOut);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task NonexistentFolder_ReportsDirectoryNotFound()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_missing_");
            try
            {
                var r = await CliTestHost.RunAsync(SplitCommand.Create(), Path.Combine(dir, "no_such"), "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("Directory not found", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }

        [Fact]
        public async Task FileWithoutExtension_TreatedAsFolderWithHint()
        {
            string dir = CliTestHost.CreateTempDir("lpb_test_noext_");
            string file = CliTestHost.CreateDummyFile(dir, "IMG_1234");
            try
            {
                var r = await CliTestHost.RunAsync(SplitCommand.Create(), file, "--dry-run");
                Assert.Equal(1, r.ExitCode);
                Assert.Contains("this is a file without a file extension", r.StdErr);
            }
            finally { Directory.Delete(dir, recursive: true); }
        }
    }
}
