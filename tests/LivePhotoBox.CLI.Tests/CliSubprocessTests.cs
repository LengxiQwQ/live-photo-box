using System.Diagnostics;
using System.Linq;
using System.Text.Json;
using Xunit;

namespace LivePhotoBox.Cli.Tests;

[Trait("Category", "Subprocess")]
public sealed class CliSubprocessTests
{
    [Theory]
    [InlineData("cover")]
    [InlineData("repair")]
    public async Task RebuiltUnimplementedCommandsFailBeforeCreatingOutput(string operation)
    {
        string directory = CreateTempDirectory("lpb_cli_rebuilt_process_");
        try
        {
            string imagePath = Path.Combine(directory, "pair.jpg");
            string outputDirectory = Path.Combine(directory, "output");
            await File.WriteAllBytesAsync(imagePath, [0xFF, 0xD8, 0xFF, 0xD9]);

            string[] arguments = operation switch
            {
                "cover" => ["cover", imagePath, "--at", "0.1", "--output", outputDirectory, "--json"],
                "repair" => ["repair", imagePath, "--output", outputDirectory, "--json"],
                _ => throw new ArgumentOutOfRangeException(nameof(operation), operation, null)
            };

            CliResult result = await RunCliAsync(
                directory,
                settingsPath: Path.Combine(directory, "missing-settings.json"),
                arguments);

            Assert.NotEqual(0, result.ExitCode);
            using JsonDocument json = JsonDocument.Parse(result.StdOut);
            Assert.Equal("failed", json.RootElement.GetProperty("status").GetString());
            Assert.Equal("rebuilt_not_ready", json.RootElement.GetProperty("errorCode").GetString());
            Assert.Equal(operation, json.RootElement.GetProperty("operation").GetString());
            Assert.False(Directory.Exists(outputDirectory));
            Assert.DoesNotContain("unknown bug", result.StdErr, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task RebuiltMergeAndSplitConvertTargetMediaShapes()
    {
        string repositoryRoot = FindRepositoryRoot();
        string directory = CreateTempDirectory("lpb_cli_rebuilt_conversion_");
        string imagePath = Path.Combine(repositoryRoot, "designs", "各个机型测试", "苹果-双文件.JPG");
        string videoPath = Path.Combine(repositoryRoot, "designs", "各个机型测试", "苹果-双文件.MOV");
        string mergeDirectory = Path.Combine(directory, "merged");
        string settingsPath = Path.Combine(directory, "rebuilt-settings.json");
        string imageHash = await ComputeSha256Async(imagePath);
        string videoHash = await ComputeSha256Async(videoPath);

        try
        {
            CliResult merge = await RunCliAsync(
                directory, settingsPath,
                "merge", imagePath, videoPath,
                "--all-variants", "--output", mergeDirectory, "--overwrite", "--yes", "--json");

            Assert.True(merge.ExitCode == 0, $"merge stdout: {merge.StdOut}\nmerge stderr: {merge.StdErr}");
            DirectoryInfo variantsDirectory = Assert.Single(new DirectoryInfo(mergeDirectory).GetDirectories("*_variants"));
            FileInfo[] mergedFiles = variantsDirectory.GetFiles();
            Assert.Equal(12, mergedFiles.Length);
            Assert.All(mergedFiles, file => Assert.True(
                file.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                || file.Extension.Equals(".heic", StringComparison.OrdinalIgnoreCase)));
            Assert.DoesNotContain(mergedFiles, file => file.Extension.Equals(".png", StringComparison.OrdinalIgnoreCase));

            FileInfo mergedImage = mergedFiles.Single(
                file => file.Name.Equals("苹果-双文件_MotionPhoto_JPEG+MP4.jpg", StringComparison.Ordinal));

            foreach (string format in new[] { "keep", "jpg+mov", "heic+mov", "jpg+mp4" })
            {
                string splitDirectory = Path.Combine(directory, $"split-{format.Replace('+', '-')}");
                CliResult split = await RunCliAsync(
                    directory, settingsPath,
                    "split", mergedImage.FullName,
                    "--protocol", "none", "--format", format,
                    "--output", splitDirectory, "--overwrite", "--yes", "--json");

                Assert.True(split.ExitCode == 0, $"split {format} stdout: {split.StdOut}\nsplit stderr: {split.StdErr}");
                FileInfo[] splitFiles = new DirectoryInfo(splitDirectory).GetFiles();
                Assert.Equal(2, splitFiles.Length);
                Assert.Contains(splitFiles, file => file.Extension.Equals(".jpg", StringComparison.OrdinalIgnoreCase)
                    || file.Extension.Equals(".heic", StringComparison.OrdinalIgnoreCase));
                Assert.Contains(splitFiles, file => file.Extension.Equals(".mp4", StringComparison.OrdinalIgnoreCase)
                    || file.Extension.Equals(".mov", StringComparison.OrdinalIgnoreCase));
            }

            Assert.Equal(imageHash, await ComputeSha256Async(imagePath));
            Assert.Equal(videoHash, await ComputeSha256Async(videoPath));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public async Task LegacyMergeProcessReachesThePreservedProcessingPath()
    {
        string directory = CreateTempDirectory("lpb_cli_legacy_process_");
        try
        {
            string imagePath = Path.Combine(directory, "pair.jpg");
            string videoPath = Path.Combine(directory, "pair.mp4");
            string outputDirectory = Path.Combine(directory, "output");
            string settingsPath = Path.Combine(directory, "legacy-settings.json");
            await File.WriteAllBytesAsync(imagePath, [0xFF, 0xD8, 0xFF, 0xD9]);
            await File.WriteAllBytesAsync(videoPath,
                [0, 0, 0, 8, (byte)'f', (byte)'t', (byte)'y', (byte)'p']);
            await File.WriteAllTextAsync(settingsPath,
                "{\"schemaVersion\":3,\"revision\":1,\"mode\":\"legacy\"}");

            CliResult result = await RunCliAsync(
                directory,
                settingsPath,
                "merge", imagePath, videoPath, "--output", outputDirectory,
                "--overwrite", "--json");

            Assert.NotEqual(0, result.ExitCode);
            Assert.DoesNotContain("rebuilt_not_ready", result.StdOut + result.StdErr,
                StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static async Task<CliResult> RunCliAsync(
        string workingDirectory,
        string settingsPath,
        params string[] arguments)
    {
        string repositoryRoot = FindRepositoryRoot();
        string cliTarget = FindCliExecutable(repositoryRoot);
        bool isDll = cliTarget.EndsWith(".dll", StringComparison.OrdinalIgnoreCase);

        var startInfo = new ProcessStartInfo
        {
            FileName = isDll ? "dotnet" : cliTarget,
            WorkingDirectory = repositoryRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        };

        if (isDll)
            startInfo.ArgumentList.Add(cliTarget);

        foreach (string argument in arguments)
            startInfo.ArgumentList.Add(argument);

        startInfo.Environment["LIVEPHOTOBOX_BACKEND_SETTINGS_PATH"] = settingsPath;
        startInfo.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
        startInfo.Environment["DOTNET_NOLOGO"] = "1";

        using var process = Process.Start(startInfo)
            ?? throw new InvalidOperationException("Unable to start the CLI subprocess.");
        Task<string> stdoutTask = process.StandardOutput.ReadToEndAsync();
        Task<string> stderrTask = process.StandardError.ReadToEndAsync();
        Task exitTask = process.WaitForExitAsync();
        Task completed = await Task.WhenAny(exitTask, Task.Delay(TimeSpan.FromSeconds(30)));
        if (completed != exitTask)
        {
            try { process.Kill(entireProcessTree: true); } catch { }
            throw new TimeoutException("CLI subprocess did not exit within 30 seconds.");
        }

        await exitTask;
        return new CliResult(process.ExitCode, await stdoutTask, await stderrTask);
    }

    private static string FindCliExecutable(string repositoryRoot)
    {
#if DEBUG
        const string configuration = "Debug";
#else
        const string configuration = "Release";
#endif
        string[] candidateDirs =
        [
            Path.Combine(repositoryRoot, "LivePhotoBox.CLI", "bin", "x64", configuration, "net9.0-windows10.0.19041.0"),
            Path.Combine(repositoryRoot, "LivePhotoBox.CLI", "bin", configuration, "net9.0-windows10.0.19041.0"),
        ];

        foreach (string dir in candidateDirs)
        {
            string exe = Path.Combine(dir, "livephotobox-boot.exe");
            if (File.Exists(exe)) return exe;
            string dll = Path.Combine(dir, "livephotobox-boot.dll");
            if (File.Exists(dll)) return dll;
        }

        throw new FileNotFoundException("Could not locate compiled CLI executable or DLL.");
    }

    private static string FindRepositoryRoot()
    {
        DirectoryInfo? directory = new(AppContext.BaseDirectory);
        while (directory != null)
        {
            if (File.Exists(Path.Combine(directory.FullName, "LivePhotoBox.CLI", "LivePhotoBox.CLI.csproj")))
                return directory.FullName;
            directory = directory.Parent;
        }
        throw new DirectoryNotFoundException("Could not locate the Live Photo Box repository root.");
    }

    private static string CreateTempDirectory(string prefix)
    {
        string directory = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static async Task<string> ComputeSha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        return Convert.ToHexString(await System.Security.Cryptography.SHA256.HashDataAsync(stream));
    }

    private sealed record CliResult(int ExitCode, string StdOut, string StdErr);
}
