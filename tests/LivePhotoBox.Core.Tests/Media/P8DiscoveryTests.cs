using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P8DiscoveryTests
{
    [Fact]
    [Trait("Category", "RealSamples")]
    public void MergeScan_RealVivoPair_ReachesNativeConfirmedPair()
    {
        using var workspace = new TestFixtureWorkspace();
        var (image, video) = CopyVivoPair(workspace);
        var result = LivePhotoMergeScanService.Scan(workspace.RootDirectory);
        var pair = Assert.Single(result.Pairs);
        Assert.Equal(image, pair.ImagePath);
        Assert.Equal(video, pair.VideoPath);
        Assert.Equal(LivePhotoProtocolType.Vivo, pair.Protocol);
        Assert.Equal(LivePhotoDetectionMethod.VivoLivePhoto, pair.DetectionMethod);
        Assert.Equal(0, result.StandaloneImagesCount);
        Assert.Equal(0, result.StandaloneVideosCount);
    }

    [Theory]
    [InlineData(DiscoveryScanMode.MergeOnly, true)]
    [InlineData(DiscoveryScanMode.All, true)]
    [InlineData(DiscoveryScanMode.VivoMatch, true)]
    [InlineData(DiscoveryScanMode.CidMatch, false)]
    [Trait("Category", "RealSamples")]
    public async Task Discovery_RealVivoPair_RespectsSelectedMode(DiscoveryScanMode mode, bool expectedPair)
    {
        using var workspace = new TestFixtureWorkspace();
        var (image, video) = CopyVivoPair(workspace);
        var result = await LivePhotoDiscoveryService.ScanAsync(workspace.RootDirectory, mode);
        Assert.Equal(2, result.Items.Count);
        var imageItem = Assert.Single(result.Items.Where(item => item.FilePath == image));
        var videoItem = Assert.Single(result.Items.Where(item => item.FilePath == video));
        Assert.Equal(expectedPair ? LivePhotoType.DualFile : LivePhotoType.None, imageItem.LivePhotoType);
        Assert.Equal(expectedPair ? LivePhotoType.DualFile : LivePhotoType.None, videoItem.LivePhotoType);
        if (expectedPair)
        {
            Assert.Equal(video, imageItem.PairedVideoPath);
            Assert.Equal(SourceProtocol.VivoLegacyDualFile, imageItem.Protocol);
            Assert.Equal(LivePhotoDetectionMethod.VivoLivePhoto, imageItem.DetectionMethod);
        }
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task GuiSharedMatcher_RealVivoPair_ConfirmsBeforeAppleSoloInspection()
    {
        using var workspace = new TestFixtureWorkspace();
        var (image, video) = CopyVivoPair(workspace);
        var pairs = await LivePhotoMergeScanService.MatchMetadataPairsAsync([image], [video], CancellationToken.None);
        var pair = Assert.Single(pairs);
        Assert.Equal(MatchSource.VivoLivePhoto, pair.Source);
        Assert.Equal(image, pair.ImagePath);
        Assert.Equal(video, pair.VideoPath);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Discovery_DuplicateVivoIdentity_FailsClosed()
    {
        using var workspace = new TestFixtureWorkspace();
        var (image, _) = CopyVivoPair(workspace);
        File.Copy(image, Path.Combine(workspace.RootDirectory, "duplicate.jpg"));
        var error = await Assert.ThrowsAsync<SourceInspectionException>(() =>
            LivePhotoDiscoveryService.ScanAsync(workspace.RootDirectory, DiscoveryScanMode.MergeOnly));
        Assert.Equal(SourceInspectionFailureCategory.Ambiguous, error.Category);
        Assert.Equal(SourceInspectionStage.Pairing, error.Stage);
    }

    [Fact]
    [Trait("Category", "Synthetic")]
    public async Task GuiSharedMatcher_MalformedVideo_RemainsObservable()
    {
        using var workspace = new TestFixtureWorkspace();
        var (image, video) = CopyVivoPair(workspace);
        // An orphan vivo image intentionally produces Pairing/Ambiguous before
        // unrelated video-container checks. Use an unbound primary to test the
        // malformed-container propagation path without bypassing that contract.
        string plainInputs = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P8_PLAIN_PAIR_ROOT")
            ?? throw new InvalidOperationException("Required derived non-live primary missing.");
        File.Copy(Path.Combine(plainInputs, "image.jpg"), image, overwrite: true);
        // The established malformed-container fixture has a valid ftyp brand
        // but no movie/media hierarchy. An empty ftyp is merely unsupported.
        File.WriteAllBytes(video, [0, 0, 0, 20, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0, 0, 0, 0,
            (byte)'i', (byte)'s', (byte)'o', (byte)'m']);
        var standalone = await Assert.ThrowsAsync<SourceInspectionException>(() => new SourceInspector().InspectAsync(video));
        Assert.Equal(SourceInspectionFailureCategory.Malformed, standalone.Category);
        var error = await Assert.ThrowsAsync<SourceInspectionException>(() =>
            LivePhotoMergeScanService.MatchMetadataPairsAsync([image], [video], CancellationToken.None));
        Assert.Equal(SourceInspectionFailureCategory.Malformed, error.Category);
    }

    private static (string Image, string Video) CopyVivoPair(TestFixtureWorkspace workspace)
    {
        string image = Path.Combine(workspace.RootDirectory, "unrelated-image-name.jpg");
        string video = Path.Combine(workspace.RootDirectory, "different-video-name.mp4");
        File.Copy(TestSampleResolver.ResolveSample("vivo双文件.jpg"), image);
        File.Copy(TestSampleResolver.ResolveSample("vivo双文件.mp4"), video);
        return (image, video);
    }
}
