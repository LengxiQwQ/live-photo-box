using System;
using System.Collections.Concurrent;
using System.CommandLine;
using System.IO;
using System.Linq;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LivePhotoBox.Cli.Commands;
using Xunit;

namespace LivePhotoBox.Cli.Tests;

[Collection("cli-log")]
public sealed class ConvertExpectedResultTests
{
    [Theory]
    [InlineData(".jpg")]
    [InlineData(".mp4")]
    public async Task ReadableCorruptSource_IsCopiedUnchangedToIsolation_WithoutExceptions(string extension)
    {
        string dir = CliTestHost.CreateTempDir("lpb-convert-corrupt-");
        try
        {
            string source = Path.Combine(dir, "broken" + extension);
            byte[] bytes = [1, 2, 3, 4, 5];
            File.WriteAllBytes(source, bytes);
            string outputDir = Path.Combine(dir, "output");
            string output = Path.Combine(outputDir, "converted" + extension);
            var (result, exceptions) = await RunObservedAsync(source, "-o", output);

            Assert.Equal(2, result.ExitCode);
            Assert.Empty(exceptions);
            Assert.False(File.Exists(output));
            string[] files = Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories);
            string isolated = Assert.Single(files);
            Assert.Contains("Quarantine", isolated, StringComparison.OrdinalIgnoreCase);
            Assert.Equal(SHA256.HashData(bytes), SHA256.HashData(File.ReadAllBytes(isolated)));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task MissingSource_ReturnsErrorWithoutOutputOrExceptions()
    {
        string dir = CliTestHost.CreateTempDir("lpb-convert-missing-");
        try
        {
            string outputDir = Path.Combine(dir, "output");
            var (result, exceptions) = await RunObservedAsync(Path.Combine(dir, "missing.jpg"), "-o", Path.Combine(outputDir, "out.jpg"));
            Assert.Equal(1, result.ExitCode);
            Assert.Empty(exceptions);
            Assert.False(Directory.Exists(outputDir));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task LockedSource_ReturnsErrorWithoutOutputOrExceptions()
    {
        string dir = CliTestHost.CreateTempDir("lpb-convert-locked-");
        try
        {
            string source = CliTestHost.CreateDummyFile(dir, "locked.jpg");
            string outputDir = Path.Combine(dir, "output");
            using var held = new FileStream(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            var (result, exceptions) = await RunObservedAsync(source, "-o", Path.Combine(outputDir, "out.jpg"));
            Assert.Equal(1, result.ExitCode);
            Assert.Empty(exceptions);
            Assert.Empty(Directory.Exists(outputDir) ? Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories) : Array.Empty<string>());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task OutputParentIsFile_ReturnsErrorWithoutOutputOrExceptions()
    {
        string dir = CliTestHost.CreateTempDir("lpb-convert-parent-");
        try
        {
            string source = CliTestHost.CreateDummyFile(dir, "source.jpg");
            string parent = CliTestHost.CreateDummyFile(dir, "parent");
            var (result, exceptions) = await RunObservedAsync(source, "-o", Path.Combine(parent, "out.jpg"));
            Assert.Equal(1, result.ExitCode);
            Assert.Empty(exceptions);
            Assert.True(File.Exists(parent));
            Assert.False(File.Exists(Path.Combine(parent, "out.jpg")));
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    [Fact]
    public async Task UnknownCodec_ReturnsErrorWithoutOutputOrExceptions()
    {
        string dir = CliTestHost.CreateTempDir("lpb-convert-codec-");
        try
        {
            string source = CliTestHost.CreateDummyFile(dir, "source.mp4");
            string outputDir = Path.Combine(dir, "output");
            var (result, exceptions) = await RunObservedAsync(source, "-o", Path.Combine(outputDir, "out.mp4"), "--codec", "nonsense");
            Assert.Equal(1, result.ExitCode);
            Assert.Empty(exceptions);
            Assert.Empty(Directory.Exists(outputDir) ? Directory.GetFiles(outputDir, "*", SearchOption.AllDirectories) : Array.Empty<string>());
        }
        finally { Directory.Delete(dir, recursive: true); }
    }

    private static async Task<(CliResult Result, Exception[] Exceptions)> RunObservedAsync(params string[] arguments)
    {
        var root = new RootCommand { ConvertCommand.Create() };
        var exceptions = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> handler = (_, args) => exceptions.Enqueue(args.Exception);
        CliResult result;
        AppDomain.CurrentDomain.FirstChanceException += handler;
        try { result = await CliTestHost.RunAsync(root, new[] { "convert" }.Concat(arguments).ToArray()); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= handler; }
        return (result, exceptions.ToArray());
    }
}
