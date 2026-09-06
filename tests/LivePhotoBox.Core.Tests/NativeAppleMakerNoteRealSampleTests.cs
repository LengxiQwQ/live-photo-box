using System.Diagnostics;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.Services.Protocols;
using Xunit;

namespace LivePhotoBox.Core.Tests;

/// <summary>
/// Exercises the Native in-place Apple MakerNote path against an untouched real HEIC copy.
/// The source sample is never modified; each invocation creates its own disposable copy.
/// </summary>
[Trait("Category", "RealSamples")]
public sealed class NativeAppleMakerNoteRealSampleTests
{
    [Fact]
    public void StripThenRewrite_RealAppleHeic_PreservesContainerLengthAndReadableContentIdentifier()
    {
        const string replacementContentId = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEFFFF0000";
        string workDirectory = Path.Combine(Path.GetTempPath(), "lpb_apple_makernote", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        string copyPath = Path.Combine(workDirectory, "苹果双文件.HEIC");

        try
        {
            File.Copy(ResolveSample("苹果双文件.HEIC"), copyPath);
            long originalLength = new FileInfo(copyPath).Length;

            Assert.True(
                AppleMakerNoteWriter.TryReadContentIdentifierFromImage(copyPath, out string? originalContentId, out string? readError),
                $"Could not read the source ContentIdentifier: {readError}");
            Assert.False(string.IsNullOrWhiteSpace(originalContentId));

            Assert.True(
                AppleMakerNoteWriter.TryStripAppleLivePhotoEntries(copyPath, out string? stripError),
                $"Could not strip the source Apple MakerNote: {stripError}");
            Assert.False(
                AppleMakerNoteWriter.TryReadContentIdentifierFromImage(copyPath, out _, out _),
                "ContentIdentifier must no longer be readable after stripping the real HEIC copy.");

            Assert.True(
                AppleMakerNoteWriter.TryWriteContentIdentifier(copyPath, replacementContentId, out string? writeError),
                $"Could not rewrite the stripped Apple MakerNote: {writeError}");
            Assert.Equal(originalLength, new FileInfo(copyPath).Length);

            Assert.True(
                AppleMakerNoteWriter.TryReadContentIdentifierFromImage(copyPath, out string? rewrittenContentId, out string? rewrittenReadError),
                $"Could not read the rewritten ContentIdentifier: {rewrittenReadError}");
            Assert.Equal(replacementContentId, rewrittenContentId);
        }
        finally
        {
            try { Directory.Delete(workDirectory, recursive: true); } catch { /* Do not hide test failures. */ }
        }
    }

    [Fact]
    public async Task InjectMakerNote_RealAppleHeic_RemainsDetectableAsApplePair()
    {
        string workDirectory = Path.Combine(Path.GetTempPath(), "lpb_apple_makernote", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(workDirectory);
        string copyPath = Path.Combine(workDirectory, "苹果双文件.HEIC");

        try
        {
            File.Copy(ResolveSample("苹果双文件.HEIC"), copyPath);
            Assert.True(
                AppleMakerNoteWriter.TryReadContentIdentifierFromImage(copyPath, out string? contentId, out string? readError),
                $"Could not read the source ContentIdentifier: {readError}");

            Assert.True(
                AppleMakerNoteWriter.TryInjectAppleMakerNoteIntoHeic(copyPath, contentId!, out string? injectError),
                $"Could not inject the Apple MakerNote into the real HEIC copy: {injectError}");
            Assert.True(
                AppleMakerNoteWriter.TryReadContentIdentifierFromImage(copyPath, out string? rewrittenContentId, out string? rewrittenReadError),
                $"Could not read the injected ContentIdentifier: {rewrittenReadError}");
            Assert.Equal(contentId, rewrittenContentId);

            string externalContentIdentifier = await ReadContentIdentifierWithExifToolAsync(copyPath);
            Assert.Contains(contentId!, externalContentIdentifier, StringComparison.OrdinalIgnoreCase);

            LivePhotoProtocolType protocol = await LivePhotoMetadataMatcher.DetectDualFileProtocolAsync(
                copyPath,
                ResolveSample("苹果双文件.MOV"),
                token: CancellationToken.None);
            Assert.Equal(LivePhotoProtocolType.Apple, protocol);
        }
        finally
        {
            try { Directory.Delete(workDirectory, recursive: true); } catch { /* Do not hide test failures. */ }
        }
    }

    private static string ResolveSample(string fileName) => TestSampleResolver.ResolveSample(fileName);

    private static string? FindExifToolOnPath()
    {
        string? pathEnv = Environment.GetEnvironmentVariable("PATH");
        if (string.IsNullOrEmpty(pathEnv)) return null;

        foreach (string dir in pathEnv.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            try
            {
                string candidate = Path.Combine(dir, "exiftool.exe");
                if (File.Exists(candidate)) return candidate;
                candidate = Path.Combine(dir, "exiftool");
                if (File.Exists(candidate)) return candidate;
            }
            catch { }
        }
        return null;
    }

    private static async Task<string> ReadContentIdentifierWithExifToolAsync(string imagePath)
    {
        string? exifToolPath = FindExifToolOnPath();
        Assert.False(string.IsNullOrWhiteSpace(exifToolPath));

        var startInfo = new ProcessStartInfo(exifToolPath!)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };
        startInfo.ArgumentList.Add("-j");
        startInfo.ArgumentList.Add("-ContentIdentifier");
        startInfo.ArgumentList.Add(imagePath);

        using Process process = Process.Start(startInfo)!;
        string output = await process.StandardOutput.ReadToEndAsync();
        string error = await process.StandardError.ReadToEndAsync();
        await process.WaitForExitAsync();
        Assert.True(process.ExitCode == 0, $"ExifTool failed: {error}");
        return output;
    }
}
