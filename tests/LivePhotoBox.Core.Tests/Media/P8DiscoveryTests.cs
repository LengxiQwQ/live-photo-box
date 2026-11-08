using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Core.Tests.Protocols;
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
    public async Task SplitDiscovery_MalformedMemberIsReportedOnceAcrossPairingPasses()
    {
        using var workspace = new TestFixtureWorkspace();
        string broken = Path.Combine(workspace.RootDirectory, "broken.jpg");
        File.WriteAllText(broken, "not media");
        var (image, video) = CopyVivoPair(workspace);

        var result = await LivePhotoDiscoveryService.ScanAsync(workspace.RootDirectory, DiscoveryScanMode.SplitOnly);

        var error = Assert.Single(result.Diagnostics, d => d.PrimaryPath == broken);
        Assert.Equal("DiscoveryIncomplete",error.DiagnosticCode);
        Assert.True(result.IsPartial);
        Assert.Empty(result.DualFilePairs);
        Assert.Contains(result.Items,item=>item.FilePath==image);
        Assert.DoesNotContain(result.Items,item=>item.FilePath==video);

    }

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
        var (image, video) = CopyVivoPair(workspace);
        string duplicate = Path.Combine(workspace.RootDirectory, "duplicate.jpg");
        File.Copy(image, duplicate);

        // 1. 底层 MatchVivo 仍严格抛出 Ambiguous/Pairing 异常（保留严格安全门禁）
        var ex = Assert.Throws<SourceInspectionException>(() =>
            LivePhotoMetadataMatcher.MatchVivo([image, duplicate], [video]));
        Assert.Equal(SourceInspectionFailureCategory.Ambiguous, ex.Category);
        Assert.Equal(SourceInspectionStage.Pairing, ex.Stage);

        string independentImage = Path.Combine(workspace.RootDirectory, "independent.jpg");
        string independentVideo = Path.Combine(workspace.RootDirectory, "independent.mov");
        // Use a complete independent camera pair, so this conflict/isolation
        // test does not depend on the validity of marker-only synthetic media.
        File.Copy(TestSampleResolver.ResolveSample("苹果-双文件.JPG"), independentImage);
        File.Copy(TestSampleResolver.ResolveSample("苹果-双文件.MOV"), independentVideo);
        var result = await LivePhotoDiscoveryService.ScanAsync(workspace.RootDirectory, DiscoveryScanMode.MergeOnly);
        Assert.Equal(LivePhotoType.None, result.Items.Single(i => i.FilePath == image).LivePhotoType);
        Assert.Equal(LivePhotoType.None, result.Items.Single(i => i.FilePath == duplicate).LivePhotoType);
        Assert.Equal(LivePhotoType.None, result.Items.Single(i => i.FilePath == video).LivePhotoType);
        var independent = Assert.Single(result.DualFilePairs);
        Assert.Equal(independentImage, independent.Image.FilePath);
        Assert.Equal(independentVideo, independent.Video.FilePath);
        Assert.Contains(result.Diagnostics, d => d.DiagnosticCode == "ConflictDuplicateIdentity" &&
            d.Severity == ScanDiagnosticSeverity.Error && d.Stage == ScanDiagnosticStage.Pairing &&
            d.RelatedPaths.Contains(image) && d.RelatedPaths.Contains(duplicate) && d.RelatedPaths.Contains(video));
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
        string? plainInputs = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P8_PLAIN_PAIR_ROOT");
        if (plainInputs != null && File.Exists(Path.Combine(plainInputs, "image.jpg")))
            File.Copy(Path.Combine(plainInputs, "image.jpg"), image, overwrite: true);
        else
            File.WriteAllBytes(image, [0xFF, 0xD8, 0xFF, 0xE0, 0x00, 0x10, 0x4A, 0x46, 0x49, 0x46, 0x00, 0x01, 0x01, 0x00, 0x00, 0x01, 0x00, 0x01, 0x00, 0x00, 0xFF, 0xDB, 0x00, 0x43, 0x00, 0x08, 0x06, 0x06, 0x07, 0x06, 0x05, 0x08, 0x07, 0x07, 0x07, 0x09, 0x09, 0x08, 0x0A, 0x0C, 0x14, 0x0D, 0x0C, 0x0B, 0x0B, 0x0C, 0x19, 0x12, 0x13, 0x0F, 0x14, 0x1D, 0x1A, 0x1F, 0x1E, 0x1D, 0x1A, 0x1C, 0x1C, 0x20, 0x24, 0x2E, 0x27, 0x20, 0x22, 0x2C, 0x23, 0x1C, 0x1C, 0x28, 0x37, 0x29, 0x2C, 0x30, 0x31, 0x34, 0x34, 0x34, 0x1F, 0x27, 0x39, 0x3D, 0x38, 0x32, 0x3C, 0x2E, 0x33, 0x34, 0x32, 0xFF, 0xC0, 0x00, 0x0B, 0x08, 0x00, 0x01, 0x00, 0x01, 0x01, 0x01, 0x11, 0x00, 0xFF, 0xC4, 0x00, 0x1F, 0x00, 0x00, 0x01, 0x05, 0x01, 0x01, 0x01, 0x01, 0x01, 0x01, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x01, 0x02, 0x03, 0x04, 0x05, 0x06, 0x07, 0x08, 0x09, 0x0A, 0x0B, 0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00, 0x7F, 0xFF, 0xD9]);
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

    [Fact]
    [Trait("Category", "Synthetic")]
    public async Task MergeScan_NameMode_PairsOnlySameDirectoryAndBaseName()
    {
        using var workspace = new TestFixtureWorkspace();
        string tempDir = Path.Combine(Path.GetTempPath(), $"lpb-test-neutral-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string matchImg = Path.Combine(workspace.RootDirectory, "sample.jpg");
            string matchVid = Path.Combine(workspace.RootDirectory, "sample.mov");
            await CopyPlainPairAsync(matchImg, matchVid, tempDir);

            // Subdirectory with same base name: should NOT cross-pair across directories
            string subDir = Path.Combine(workspace.RootDirectory, "subdir");
            Directory.CreateDirectory(subDir);
            string crossImg = Path.Combine(subDir, "cross.jpg");
            string crossVid = Path.Combine(workspace.RootDirectory, "cross.mov");
            await CopyPlainPairAsync(crossImg, crossVid, tempDir);

            var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions
            {
                InputDirectory = workspace.RootDirectory,
                Mode = PairingMode.Name,
                Recursive = true
            });

            // Only sample.jpg + sample.mov in the same directory should be paired
            var pair = Assert.Single(result.Pairs);
            Assert.Equal(matchImg, pair.ImagePath);
            Assert.Equal(matchVid, pair.VideoPath);
            Assert.Equal(PairingKind.FilenameCompositionRequest, pair.PairingKind);

            // cross.jpg and cross.mov remain standalone
            Assert.Contains(crossImg, result.StandaloneImagePaths);
            Assert.Contains(crossVid, result.StandaloneVideoPaths);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Synthetic")]
    public async Task MergeScan_NameMode_MultipleMediaConflict_ProducesDiagnostic()
    {
        using var workspace = new TestFixtureWorkspace();
        string tempDir = Path.Combine(Path.GetTempPath(), $"lpb-test-neutral-{Guid.NewGuid():N}");
        Directory.CreateDirectory(tempDir);

        try
        {
            string img1 = Path.Combine(workspace.RootDirectory, "conflict.jpg");
            string img2 = Path.Combine(workspace.RootDirectory, "conflict.jpeg");
            string vid = Path.Combine(workspace.RootDirectory, "conflict.mov");
            await CopyPlainPairAsync(img1, vid, tempDir);
            File.Copy(img1, img2);

            var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions
            {
                InputDirectory = workspace.RootDirectory,
                Mode = PairingMode.Name
            });

            // The page matching change forbids choosing an arbitrary member.
            Assert.Empty(result.Pairs);
            Assert.Empty(result.StandaloneImagePaths);
            Assert.Empty(result.StandaloneVideoPaths);
            var conflict = Assert.Single(result.Diagnostics, d => d.DiagnosticCode == "ConflictMultipleMedia");
            Assert.Contains(img1, conflict.RelatedPaths);
            Assert.Contains(img2, conflict.RelatedPaths);
            Assert.Contains(vid, conflict.RelatedPaths);
            Assert.Equal(ScanOperationStatus.Completed, result.Status);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch { }
        }
    }

    [Fact]
    [Trait("Category", "Synthetic")]
    public async Task MergeScan_EmptyDirectory_ReturnsZeroPairsWithoutError()
    {
        using var workspace = new TestFixtureWorkspace();
        var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions
        {
            InputDirectory = workspace.RootDirectory,
            Mode = PairingMode.Name
        });

        Assert.Empty(result.Pairs);
        Assert.Empty(result.Diagnostics);
        Assert.Equal(ScanOperationStatus.Completed, result.Status);
        Assert.False(result.IsPartial);
    }

    private static async Task CopyPlainPairAsync(string imageDst, string videoDst, string tempDir)
    {
        File.WriteAllBytes(imageDst, SyntheticProtocolFixtures.CreateMinimalJpeg());
        string oppo = TestSampleResolver.ResolveSample("oppo.jpg");
        var split = await LivePhotoSplitService.SplitAsync(
            oppo,
            tempDir,
            protocolIndex: ProtocolFormatMatrix.SplitProtocolNone,
            outputFormatIndex: ProtocolFormatMatrix.SplitFormatJpgMov,
            CancellationToken.None);
        File.Copy(split.VideoOutputPath, videoDst, overwrite: true);
    }
}
