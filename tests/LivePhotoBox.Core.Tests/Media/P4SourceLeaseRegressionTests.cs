using System;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Threading.Tasks;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P4SourceLeaseRegressionTests
{
    [Theory]
    [InlineData("copy")]
    [InlineData("transcode")]
    [InlineData("auxiliary")]
    [Trait("Category", "RealSamples")]
    public async Task ConcurrentWriter_RejectsAtSourceReadBoundary(string operation)
    {
        using var workspace = new TestFixtureWorkspace();
        string source = PrepareSource(workspace.RootDirectory, operation);
        byte[] before = File.ReadAllBytes(source);
        using (var writer = new FileStream(source, FileMode.Open, FileAccess.Write, FileShare.ReadWrite))
            await AssertReadRejected(operation, source, workspace.RootDirectory);
        Assert.Equal(before, File.ReadAllBytes(source));
        Assert.Single(Directory.GetFiles(workspace.RootDirectory));
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("heic-copy")]
    [InlineData("image")]
    [InlineData("primary")]
    [InlineData("auxiliary")]
    [InlineData("probe")]
    [InlineData("remux")]
    [InlineData("transcode")]
    [Trait("Category", "RealSamples")]
    public async Task JunctionAncestor_RejectsAtSourceReadBoundary(string operation)
    {
        using var workspace = new TestFixtureWorkspace();
        string source = PrepareSource(workspace.RootDirectory, operation);
        string before = Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source)));
        string junction = Path.Combine(workspace.RootDirectory, "redirect");
        using var process = Process.Start(new ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{junction}\" \"{workspace.RootDirectory}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        })!;
        string creation = await process.StandardOutput.ReadToEndAsync() + await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, creation);
        try
        {
            Assert.True((File.GetAttributes(junction) & FileAttributes.ReparsePoint) != 0);
            await AssertReadRejected(operation, Path.Combine(junction, Path.GetFileName(source)), workspace.RootDirectory);
        }
        finally { Directory.Delete(junction); }

        Assert.Equal(before, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(source))));
        Assert.Single(Directory.GetFiles(workspace.RootDirectory));
    }

    private static string PrepareSource(string directory, string operation)
    {
        string name = operation switch
        {
            "primary" or "auxiliary" or "heic-copy" => "苹果双文件.HEIC",
            "probe" or "remux" or "transcode" => "苹果双文件.MOV",
            _ => "oppo.jpg"
        };
        string path = Path.Combine(directory, name);
        File.Copy(TestSampleResolver.ResolveSample(name), path);
        return path;
    }

    private static async Task AssertReadRejected(string operation, string source, string directory)
    {
        if (operation == "auxiliary")
        {
            using var context = NativeContext.Create();
            var info = new NativeHeicAuxiliaryInfo { StructSize = (uint)Marshal.SizeOf<NativeHeicAuxiliaryInfo>() };
            // A nonzero selector gets past ABI argument validation. The error
            // proves source-open rejection before graph/item interpretation.
            NativeResult result = NativeMethods.DecodeHeicAuxiliaryImage(context.Handle, source, 1, ref info);
            Assert.NotEqual(NativeResult.Ok, result);
            Assert.Contains("HEIC input is unavailable", context.GetLastError(), StringComparison.Ordinal);
            return;
        }

        Func<Task> action = operation switch
        {
            "copy" => () => NativeMediaService.ConvertImageAsync(source, Path.Combine(directory, "out.jpg"), ImageContainer.Jpeg, 85),
            "heic-copy" => () => NativeMediaService.ConvertImageAsync(source, Path.Combine(directory, "out.heic"), ImageContainer.Heic, 85),
            "image" => () => NativeMediaService.ConvertImageAsync(source, Path.Combine(directory, "out.heic"), ImageContainer.Heic, 85),
            "primary" => () => NativeMediaService.DecodeHeicPrimaryAsync(source),
            "probe" => () => NativeMediaService.ProbeVideoAsync(source),
            "remux" => () => NativeMediaService.RemuxVideoAsync(source, Path.Combine(directory, "out.mov"), VideoContainer.Mov),
            "transcode" => () => NativeMediaService.TranscodeVideoAsync(source, Path.Combine(directory, "out.mp4"), VideoContainer.Mp4, VideoCodec.H264, 23),
            _ => throw new ArgumentException(operation)
        };
        Exception error = await Assert.ThrowsAnyAsync<Exception>(action);
        string expected = operation switch
        {
            "copy" or "heic-copy" or "image" => "Cannot read the input image header",
            "primary" => "HEIC input is unavailable",
            "probe" => "Cannot open video file for probing",
            "remux" => "Cannot open input video file for remux",
            "transcode" => "video source could not be pinned read-only",
            _ => throw new ArgumentException(operation)
        };
        Assert.Contains(expected, error.Message, StringComparison.Ordinal);
    }
}
