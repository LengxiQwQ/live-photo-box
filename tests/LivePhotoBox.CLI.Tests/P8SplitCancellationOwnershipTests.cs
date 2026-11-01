using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using LivePhotoBox.Cli.Commands;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Cli.Tests;

[Collection("cli-log")]
public sealed class P8SplitCancellationOwnershipTests
{
    [Theory]
    [InlineData("single")]
    [InlineData("batch")]
    [InlineData("variants")]
    [Trait("Category", "RealSamples")]
    public async Task ActualSplit_PreservesForeignTemp(string mode)
    {
        string root = CliTestHost.CreateTempDir("lpb-p8-split-ownership-");
        try
        {
            string input = Path.Combine(root, "live.jpg");
            File.Copy(Sample(mode == "variants" ? "oppo.jpg" : "红米老款-GV1.JPG"), input);
            string before = Hash(input);
            string output = Path.Combine(root, "output");
            string temp = mode == "variants" ? Path.Combine(output, "split_live_All_Variants", "Temp") : Path.Combine(output, "Temp");
            Directory.CreateDirectory(temp);
            string sentinel = Path.Combine(temp, "foreign.bin");
            byte[] bytes = [1, 7, 11, 29];
            await File.WriteAllBytesAsync(sentinel, bytes);
            var args = new List<string> { mode == "batch" ? root : input, "--output", output, "--yes" };
            if (mode == "variants") args.Add("--all-variants");
            else args.AddRange(["--format", "keep", "--json"]);
            CliResult result = await CliTestHost.RunAsync(SplitCommand.Create(), args.ToArray());
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(bytes, await File.ReadAllBytesAsync(sentinel));
            Assert.Equal(before, Hash(input));
            Assert.Equal(mode == "variants" ? 8 : 2, Directory.EnumerateFiles(output, "*", SearchOption.AllDirectories).Count(path => path != sentinel));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task BatchCancellationAtPublish_IsCancelledWithoutFailureOrAfterMove()
    {
        string root = CliTestHost.CreateTempDir("lpb-p8-split-cancel-");
        try
        {
            string input = Path.Combine(root, "live.jpg");
            File.Copy(Sample(), input);
            string before = Hash(input);
            string output = Path.Combine(root, "output");
            using var cancellation = new CancellationTokenSource();
            using var hook = LivePhotoSplitService.SetBeforeSplitPublicationTestHook(output, (_, _) =>
            {
                cancellation.Cancel();
                return Task.FromCanceled(cancellation.Token);
            });
            CliResult result = await InvokeSplitAsync(root, output, cancellation.Token, after: "move:" + Path.Combine(root, "moved"));
            Assert.Equal(130, result.ExitCode);
            Assert.Contains("\"status\": \"cancelled\"", result.StdOut);
            Assert.Contains("\"failed\": 0", result.StdOut);
            Assert.DoesNotContain("\"Status\": \"failed\"", result.StdOut);
            Assert.Equal(before, Hash(input));
            Assert.False(Directory.Exists(Path.Combine(root, "moved")));
            Assert.Empty(Directory.GetFiles(output, "*", SearchOption.AllDirectories));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PreserveSubdirs_ExplicitCliFlagWorksWithGuiSettingFalse()
    {
        string root = CliTestHost.CreateTempDir("lpb-p8-split-subdirs-");
        // Change only the in-memory unpackaged settings view, with exact rollback;
        // this test never persists a user's GUI preferences to disk.
        var store = (Dictionary<string, object?>)typeof(AppSettingsService)
            .GetField("_jsonStore", BindingFlags.NonPublic | BindingFlags.Static)!.GetValue(null)!;
        string[] keys = ["IsOutputPreserveSubfolderStructure", "IsRecursiveScanEnabled"];
        var previous = keys.Select(key => (Key: key, Present: store.ContainsKey(key), Value: store.GetValueOrDefault(key))).ToArray();
        try
        {
            store[keys[0]] = false;
            store[keys[1]] = true;
            string nested = Path.Combine(root, "input", "nested");
            Directory.CreateDirectory(nested);
            File.Copy(Sample(), Path.Combine(nested, "live.jpg"));
            string output = Path.Combine(root, "output");
            object?[] args = [Path.Combine(root, "input"), output, null, 0, 0, null,
                1, true, true, false, false, false, true, null, false, CancellationToken.None];
            CliResult result = await CaptureInvocation(typeof(SplitCommand), args, "SplitBatchAsync");
            Assert.Equal(0, result.ExitCode);
            Assert.Equal(2, Directory.GetFiles(Path.Combine(output, "nested")).Length);
            Assert.Empty(Directory.GetFiles(output));
            Assert.False(AppSettingsService.GetValue("IsOutputPreserveSubfolderStructure", true));
        }
        finally
        {
            foreach (var item in previous)
                if (item.Present) store[item.Key] = item.Value;
                else store.Remove(item.Key);
            Directory.Delete(root, recursive: true);
        }
    }

    [Fact]
    public async Task MergePrecancelledWrapper_Returns130BeforeCreatingOutput()
    {
        string root = CliTestHost.CreateTempDir("lpb-p8-merge-precancel-");
        try
        {
            string output = Path.Combine(root, "not-created");
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();
            object?[] args = [null, null, new DirectoryInfo(root), "motionphoto", new DirectoryInfo(output), null,
                "keep", false, 1, true, true, false, false, false, false, false,
                "name", "none", false, null, new StructuredMergeOptions(), false, cancellation.Token];
            CliResult result = await CaptureInvocation(typeof(MergeCommand), args);
            Assert.Equal(130, result.ExitCode);
            Assert.Contains("cancelled", result.StdOut);
            Assert.False(Directory.Exists(output));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("none")]
    [InlineData("image")]
    [InlineData("video")]
    [InlineData("archive-file")]
    [Trait("Category", "RealSamples")]
    public async Task MergeAfterMove_ReportsFailureWithoutSplittingKnownConflicts(string conflict)
    {
        string root = CliTestHost.CreateTempDir("lpb-p9-merge-after-");
        try
        {
            string input = Path.Combine(root, "input"), archive = Path.Combine(root, "archive"), output = Path.Combine(root, "output");
            Directory.CreateDirectory(input);
            string image = Path.Combine(input, "live.jpg"), video = Path.Combine(input, "live.mov");
            File.Copy(Sample("苹果-双文件.JPG"), image);
            File.Copy(Sample("苹果-双文件.MOV"), video);
            string imageHash = Hash(image), videoHash = Hash(video);
            byte[] foreign = [3, 8, 13, 21];
            if (conflict == "archive-file") File.WriteAllBytes(archive, foreign);
            else
            {
                Directory.CreateDirectory(archive);
                if (conflict is "image" or "video")
                    File.WriteAllBytes(Path.Combine(archive, conflict == "image" ? "live.jpg" : "live.mov"), foreign);
            }
            var result = await CliTestHost.RunAsync(MergeCommand.Create(),
                input, "-o", output, "--after", "move:" + archive, "--json");
            bool failed = conflict != "none";
            Assert.Equal(failed ? 1 : 0, result.ExitCode);
            using var document = JsonDocument.Parse(result.StdOut);
            var json = document.RootElement;
            Assert.Equal(1, json.GetProperty("merged").GetInt32());
            Assert.Equal(0, json.GetProperty("failed").GetInt32());
            Assert.Equal(failed ? 1 : 0, json.GetProperty("afterFailed").GetInt32());
            Assert.Equal(failed ? "failed" : "completed", json.GetProperty("status").GetString());
            var entry = json.GetProperty("files")[0];
            Assert.Equal("merged", entry.GetProperty("Status").GetString());
            Assert.True(File.Exists(entry.GetProperty("Output").GetString()));
            var action = entry.GetProperty("AfterAction");
            Assert.Equal(failed ? "failed" : "completed", action.GetProperty("Status").GetString());
            if (failed) Assert.False(string.IsNullOrWhiteSpace(action.GetProperty("Reason").GetString()));
            Assert.Equal(imageHash, Hash(failed ? image : Path.Combine(archive, "live.jpg")));
            Assert.Equal(videoHash, Hash(failed ? video : Path.Combine(archive, "live.mov")));
            foreach (var file in action.GetProperty("Files").EnumerateArray())
                Assert.Equal(failed ? "retained" : "moved", file.GetProperty("Status").GetString());
            if (conflict is "image" or "video")
                Assert.Equal(foreign, File.ReadAllBytes(Path.Combine(archive, conflict == "image" ? "live.jpg" : "live.mov")));
            if (conflict == "archive-file") Assert.Equal(foreign, File.ReadAllBytes(archive));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    public void MergeAfterMove_LateSharingViolationReportsActualPartialState()
    {
        string root = CliTestHost.CreateTempDir("lpb-p9-merge-after-lock-");
        try
        {
            string image = Path.Combine(root, "live.jpg"), video = Path.Combine(root, "live.mov"), archive = Path.Combine(root, "archive");
            File.WriteAllBytes(image, [1, 2, 3]);File.WriteAllBytes(video, [4, 5, 6]);
            using (File.Open(video, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                var method = typeof(MergeCommand).GetMethod("ApplySourceAction", BindingFlags.NonPublic | BindingFlags.Static)!;
                var result = method.Invoke(null, [image, video, archive, CancellationToken.None]);
                using var json = JsonDocument.Parse(JsonSerializer.Serialize(result));
                Assert.Equal("failed", json.RootElement.GetProperty("Status").GetString());
                Assert.Equal("moved", json.RootElement.GetProperty("Files")[0].GetProperty("Status").GetString());
                Assert.Equal("retained", json.RootElement.GetProperty("Files")[1].GetProperty("Status").GetString());
            }
            Assert.False(File.Exists(image));Assert.True(File.Exists(video));
            Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(Path.Combine(archive, "live.jpg")));
            Assert.Equal(new byte[] { 4, 5, 6 }, File.ReadAllBytes(video));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static Task<CliResult> InvokeSplitAsync(string inputRoot, string output, CancellationToken token,
        string after = "none", bool preserveSubdirs = false)
    {
        object?[] args = [null, new DirectoryInfo(inputRoot), "all", "none", "keep", new DirectoryInfo(output),
            "keep", false, 1, true, true, false, false, false, false, preserveSubdirs, after, false, null, token];
        return CaptureInvocation(typeof(SplitCommand), args);
    }

    private static async Task<CliResult> CaptureInvocation(Type command, object?[] args, string methodName = "RunAsync")
    {
        using var stdout = new StringWriter();
        using var stderr = new StringWriter();
        TextWriter oldOut = Console.Out, oldError = Console.Error;
        try
        {
            Console.SetOut(stdout);
            Console.SetError(stderr);
            var method = command.GetMethod(methodName, BindingFlags.NonPublic | BindingFlags.Static)!;
            int code = await ((Task<int>)method.Invoke(null, args)!).WaitAsync(TimeSpan.FromSeconds(30));
            return new CliResult(code, stdout.ToString(), stderr.ToString());
        }
        finally { Console.SetOut(oldOut); Console.SetError(oldError); }
    }

    private static string Sample(string name = "红米老款-GV1.JPG")
    {
        string? root = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_TEST_SAMPLES_DIR");
        if (string.IsNullOrEmpty(root))
        {
            var directory = new DirectoryInfo(AppContext.BaseDirectory);
            while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, ".ai-tmp", "cache", "samples"))) directory = directory.Parent;
            root = directory == null ? throw new DirectoryNotFoundException("Frozen sample cache is required.") : Path.Combine(directory.FullName, ".ai-tmp", "cache", "samples");
        }
        string file = Path.Combine(root, name);
        return File.Exists(file) ? file : throw new FileNotFoundException("Frozen cached sample is required.", file);
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
