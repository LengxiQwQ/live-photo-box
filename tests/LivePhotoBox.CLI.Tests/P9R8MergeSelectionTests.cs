using System.Text.Json;
using LivePhotoBox.Cli.Commands;
using Xunit;

namespace LivePhotoBox.Cli.Tests;

[Collection("cli-log")]
public sealed class P9R8MergeSelectionTests
{
    [Theory]
    [InlineData("guess",false)]
    [InlineData("keep-stored",true)]
    public async Task UnknownDirectionChoiceCannotBeGuessedOrAppliedToAWholeFolder(string handling,bool folder)
    {
        string root=CliTestHost.CreateTempDir("lpb-r8-direction-");
        try {
            string source=CliTestHost.CreateDummyFile(root,"live.jpg");string output=Path.Combine(root,"new-output");
            var result=await CliTestHost.RunAsync(MergeCommand.Create(),folder?root:source,"--unspecified-image-orientation",handling,"-o",output,"--json");
            Assert.Equal(1,result.ExitCode);Assert.Contains("--unspecified-image-orientation",result.StdErr);Assert.False(Directory.Exists(output));
        }finally{Directory.Delete(root,recursive:true);}
    }

    [Fact]
    public async Task SingleLiveInputAndAllVariantsDryRunDoNotCreateOutputDirectories()
    {
        string root = CliTestHost.CreateTempDir("lpb-r8-dry-");
        try
        {
            string source = CliTestHost.CreateDummyFile(root, "live.jpg");
            string output = Path.Combine(root, "new-output");
            foreach (bool all in new[] { false, true })
            {
                var args = new List<string> { source, "-o", output, "--json", "--dry-run", "--preservation", "strict" };
                if (all) args.Add("--all-variants");
                var result = await CliTestHost.RunAsync(MergeCommand.Create(), args.ToArray());
                Assert.Equal(0, result.ExitCode);
                using var json = JsonDocument.Parse(result.StdOut);
                Assert.False(Directory.Exists(output));
            }
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData("motionphoto", "mpv2")]
    [InlineData("samsung", "v6")]
    [InlineData("huawei", "mpv3")]
    public async Task WrongTargetVersionFailsBeforeCreatingOutput(string target, string version)
    {
        string root = CliTestHost.CreateTempDir("lpb-r8-version-");
        try
        {
            string source = CliTestHost.CreateDummyFile(root, "live.jpg");
            string output = Path.Combine(root, "new-output");
            var result = await CliTestHost.RunAsync(MergeCommand.Create(), source, "-p", target,
                "--target-version", version, "-o", output, "--json");
            Assert.Equal(1, result.ExitCode);
            Assert.Contains("--target-version", result.StdErr);
            Assert.False(Directory.Exists(output));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task BatchIncludeLiveRetainsGoodResultAndReportsBrokenFile()
    {
        string root = CliTestHost.CreateTempDir("lpb-scan-partial-");
        try
        {
            File.Copy(ResolveSample("oppo.jpg"), Path.Combine(root, "live.jpg"));
            string broken = CliTestHost.CreateDummyFile(root, "broken.jpg");
            string output = Path.Combine(root, "output");
            var result = await CliTestHost.RunAsync(MergeCommand.Create(), root, "--include-live", "--dry-run", "--json", "-o", output);
            Assert.Equal(1, result.ExitCode);
            using var json = JsonDocument.Parse(result.StdOut);
            Assert.Equal(1, json.RootElement.GetProperty("scanned").GetInt32());
            Assert.Contains(json.RootElement.GetProperty("scanDiagnostics").EnumerateArray(),
                d => d.GetProperty("primaryPath").GetString() == broken && d.GetProperty("severity").GetString() == "error");
            Assert.False(Directory.Exists(output));
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task BatchIncludeLive_DifferentExtensionsWithSameBaseName_DoesNotConflictAndScansBoth()
    {
        string root = CliTestHost.CreateTempDir("lpb-scan-conflict-");
        try
        {
            File.Copy(ResolveSample("oppo.jpg"), Path.Combine(root, "same.jpg"));
            File.Copy(ResolveSample("oppo.jpg"), Path.Combine(root, "same.jpeg"));
            var result = await CliTestHost.RunAsync(MergeCommand.Create(), root, "--include-live", "--dry-run", "--json");
            Assert.Equal(0, result.ExitCode);
            using var json = JsonDocument.Parse(result.StdOut);
            Assert.Equal(2, json.RootElement.GetProperty("scanned").GetInt32());
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task SplitBatchBrokenFileReportsDiagnosticWithoutLosingLiveSelection()
    {
        string root = CliTestHost.CreateTempDir("lpb-split-partial-");
        try
        {
            File.Copy(ResolveSample("oppo.jpg"), Path.Combine(root, "live.jpg"));
            string broken = CliTestHost.CreateDummyFile(root, "broken.heic");
            var result = await CliTestHost.RunAsync(SplitCommand.Create(), root, "--dry-run", "--json");
            Assert.Equal(1, result.ExitCode);
            using var json = JsonDocument.Parse(result.StdOut);
            Assert.Equal(1, json.RootElement.GetProperty("scanned").GetInt32());
            Assert.Contains(json.RootElement.GetProperty("scanDiagnostics").EnumerateArray(), d => d.GetProperty("primaryPath").GetString() == broken);
        }
        finally { Directory.Delete(root, recursive: true); }
    }

    private static string ResolveSample(string name)
    {
        for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory != null; directory = directory.Parent)
        {
            string path = Path.Combine(directory.FullName, "tests", "fixtures", "realsamples", name);
            if (File.Exists(path)) return path;
        }
        throw new FileNotFoundException("Required real sample fixture is missing.", name);
    }

    [Fact]
    public async Task ExplicitBatchLiveDiscoveryIncludesUnmatchedImagesWithoutChangingDefaultPairDiscovery()
    {
        string root = CliTestHost.CreateTempDir("lpb-r8-batch-");
        try
        {
            string samplePath = Path.Combine(AppContext.BaseDirectory, "tests", "fixtures", "realsamples", "oppo.jpg");
            if (!File.Exists(samplePath))
            {
                string dir = AppContext.BaseDirectory;
                while (!string.IsNullOrEmpty(dir))
                {
                    string candidate = Path.Combine(dir, "tests", "fixtures", "realsamples", "oppo.jpg");
                    if (File.Exists(candidate)) { samplePath = candidate; break; }
                    var parent = Directory.GetParent(dir);
                    if (parent == null) break;
                    dir = parent.FullName;
                }
            }
            File.Copy(samplePath, Path.Combine(root, "embedded.jpg"));
            string output = Path.Combine(root, "new-output");
            var defaultResult = await CliTestHost.RunAsync(MergeCommand.Create(), root, "-o", output, "--json", "--dry-run");
            using var defaultJson = JsonDocument.Parse(defaultResult.StdOut);
            Assert.Equal(0, defaultResult.ExitCode);
            Assert.Equal(0, defaultJson.RootElement.GetProperty("scanned").GetInt32());
            var liveResult = await CliTestHost.RunAsync(MergeCommand.Create(), root, "--include-live", "-o", output, "--json", "--dry-run");
            using var liveJson = JsonDocument.Parse(liveResult.StdOut);
            Assert.Equal(0, liveResult.ExitCode);
            Assert.Equal(1, liveJson.RootElement.GetProperty("scanned").GetInt32());
            Assert.False(Directory.Exists(output));
        }
        finally { Directory.Delete(root, recursive: true); }
    }
}
