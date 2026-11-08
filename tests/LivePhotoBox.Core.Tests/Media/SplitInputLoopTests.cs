using Xunit;
using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;
using LivePhotoBox.Services;

namespace LivePhotoBox.Core.Tests.Media;

[Trait("Category", "RealSamples")]
public sealed class SplitInputLoopTests
{
    private static string Sample(string name) => TestSampleResolver.ResolveSample(name);
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));

    [Theory]
    [InlineData("苹果-双文件.JPG", "苹果-双文件.MOV", 1433334L)]
    [InlineData("苹果双文件.HEIC", "苹果双文件.MOV", 1500000L)]
    [InlineData("vivo双文件.jpg", "vivo双文件.mp4", 1333867L)]
    public async Task PairExportsCleanMediaAndKeepsTimingAndBothOriginals(string imageName, string videoName, long expectedCoverUs)
    {
        string image = Sample(imageName), video = Sample(videoName);
        string imageHash = Hash(image), videoHash = Hash(video);
        using var ws = new Support.TestFixtureWorkspace();
        // Frozen source fixture timeline positions, independently checked in step2 CLI evidence.
        var result = await LivePhotoSplitService.SplitAsync(image, ws.RootDirectory, SplitMediaFormat.KeepSource,
            CancellationToken.None, secondaryPath: video);
        var outcome = Assert.IsType<ProductOperationOutcome>(result.Outcome);
        Assert.True(outcome.IsSuccess, outcome.Details);
        Assert.Equal(new[] { image, video }, outcome.Inputs);
        Assert.NotEmpty(outcome.PreparationManifest);
        Assert.NotNull(outcome.PreparationSemantics);
        Assert.Equal(expectedCoverUs, outcome.PreparationSemantics.Timing.CoverTimestampUs);
        Assert.True(outcome.PreparationSemantics.Timing.DurationUs > expectedCoverUs);
        var cleanImage = await new SourceInspector().InspectAsync(result.ImageOutputPath);
        var cleanVideo = await new SourceInspector().InspectAsync(result.VideoOutputPath);
        Assert.Equal(SourceProtocol.NonLive, cleanImage.Protocol);
        Assert.Equal(SourceProtocol.NonLive, cleanVideo.Protocol);
        Assert.Null(cleanImage.PairingIdentifier);
        Assert.Null(cleanVideo.PairingIdentifier);
        Assert.Equal(imageHash, Hash(image)); Assert.Equal(videoHash, Hash(video));
    }

    [Fact]
    public async Task MismatchedCompanionIsNotPairedAndIndependentMediaIsIsolated()
    {
        using var ws = new Support.TestFixtureWorkspace();
        string output = Path.Combine(ws.RootDirectory, "output");
        string image = Sample("苹果-双文件.JPG"), video = Sample("vivo双文件.mp4");
        string imageHash = Hash(image), videoHash = Hash(video);
        var result = await LivePhotoSplitService.SplitWithOutcomeAsync(image, output,
            SplitMediaFormat.KeepSource, CancellationToken.None, secondaryPath: video);
        Assert.False(result.IsSuccess);
        Assert.Equal(ProductOperationStatus.Warning, result.Status);
        Assert.Equal(ProductCompletionKind.RecoveredMedia, result.CompletionKind);
        Assert.Null(result.SourceActionGuard);
        Assert.Equal(2, result.CommittedOutputs.Count);
        var original = Assert.Single(result.CommittedOutputs, x => x.Role == "OriginalUnconverted");
        var recovered = Assert.Single(result.CommittedOutputs, x => x.Role == "RecoveredMotionVideo");
        Assert.Equal(imageHash, Hash(original.Path));
        Assert.Equal(videoHash, Hash(recovered.Path));
        Assert.All(result.CommittedOutputs, x => Assert.True(Path.GetFullPath(x.Path).StartsWith(
            Path.GetFullPath(result.IsolationDirectory!) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)));
        Assert.Equal(imageHash, Hash(image)); Assert.Equal(videoHash, Hash(video));
    }

    [Fact]
    public async Task CompanionDestinationCannotOverwriteOriginalAndRollsBack()
    {
        using var ws = new Support.TestFixtureWorkspace();
        string image = Path.Combine(ws.RootDirectory, "original.jpg"), video = Path.Combine(ws.RootDirectory, "export.MOV");
        File.Copy(Sample("苹果-双文件.JPG"), image); File.Copy(Sample("苹果-双文件.MOV"), video);
        string imageHash = Hash(image), videoHash = Hash(video);
        var result = await LivePhotoSplitService.SplitWithOutcomeAsync(image, ws.RootDirectory,
            SplitMediaFormat.KeepSource, CancellationToken.None, outputBaseName: "export", overwriteExisting: true, secondaryPath: video);
        Assert.False(result.IsSuccess); Assert.Empty(result.CommittedOutputs);
        Assert.False(File.Exists(Path.Combine(ws.RootDirectory, "export.JPG")));
        Assert.Equal(imageHash, Hash(image)); Assert.Equal(videoHash, Hash(video));
    }

    [Fact]
    public async Task BothOriginalsRemainLockedAtPublicationBoundary()
    {
        using var ws = new Support.TestFixtureWorkspace();
        string image = Path.Combine(ws.RootDirectory, "input.jpg"), video = Path.Combine(ws.RootDirectory, "input.MOV");
        File.Copy(Sample("苹果-双文件.JPG"), image); File.Copy(Sample("苹果-双文件.MOV"), video);
        string output = Path.Combine(ws.RootDirectory, "out");
        bool visited = false;
        using var hook = LivePhotoSplitService.SetBeforeSplitPublicationTestHook(output, (_, _) =>
        {
            visited = true;
            Assert.Throws<IOException>(() => File.Open(image, FileMode.Open, FileAccess.Write, FileShare.Read).Dispose());
            Assert.Throws<IOException>(() => File.Open(video, FileMode.Open, FileAccess.Write, FileShare.Read).Dispose());
            return Task.CompletedTask;
        });
        var result = await LivePhotoSplitService.SplitWithOutcomeAsync(image, output,
            SplitMediaFormat.KeepSource, CancellationToken.None, secondaryPath: video);
        Assert.True(result.IsSuccess, result.Details); Assert.True(visited);
    }

    [Fact]
    public async Task DualDiscoveryDoesNotGuessNamesOrAcceptDuplicateIdentity()
    {
        using var ws = new Support.TestFixtureWorkspace();
        string image = Path.Combine(ws.RootDirectory, "photo.jpg"), video = Path.Combine(ws.RootDirectory, "renamed.MOV");
        File.Copy(Sample("苹果-双文件.JPG"), image); File.Copy(Sample("苹果-双文件.MOV"), video);
        var first = await LivePhotoDiscoveryService.ScanAsync(ws.RootDirectory, DiscoveryScanMode.SplitOnly | DiscoveryScanMode.CidMatch);
        Assert.Single(first.DualFilePairs);
        File.Copy(image, Path.Combine(ws.RootDirectory, "duplicate.jpg"));
        var conflict = await LivePhotoDiscoveryService.ScanAsync(ws.RootDirectory, DiscoveryScanMode.SplitOnly | DiscoveryScanMode.CidMatch);
        Assert.Empty(conflict.DualFilePairs);
        Assert.Contains(conflict.Diagnostics, d => d.DiagnosticCode == "ConflictDuplicateIdentity");
    }
}
