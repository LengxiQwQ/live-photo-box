using System;
using System.IO;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Core.Tests.Protocols;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ScanPairingIsolationTests
{
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task MixedCameraDirectory_ExpectedRefusalsNeverRaiseInspectionExceptions()
    {
        string root = Environment.GetEnvironmentVariable("LPB_SCAN_REFUSAL_INPUT_ROOT")
            ?? throw new InvalidOperationException("Set LPB_SCAN_REFUSAL_INPUT_ROOT to the cached camera directory.");
        Assert.True(Directory.Exists(root));
        Assert.True(File.Exists(Path.Combine(root, "三星.heic")));
        Assert.True(File.Exists(Path.Combine(root, "vivo双文件.jpg")));
        var raised = new System.Collections.Concurrent.ConcurrentQueue<string>();
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> observe = (_, args) =>
        {
            if (args.Exception is SourceInspectionException) raised.Enqueue(args.Exception.Message);
        };
        AppDomain.CurrentDomain.FirstChanceException += observe;
        try
        {
            var name = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { InputDirectory = root });
            Assert.Equal(ScanOperationStatus.Completed, name.Status);
            Assert.Contains(name.Diagnostics, d => d.DiagnosticCode == "ConflictMultipleMedia" && d.RelatedPaths.Any(p => p.EndsWith("三星.heic")));
            Assert.Contains(name.Pairs, p => p.Protocol == LivePhotoProtocolType.Vivo);
            foreach (var mode in new[] { PairingMode.Cid, PairingMode.Vivo })
            {
                var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { InputDirectory = root, Mode = mode });
                Assert.Equal(ScanOperationStatus.Completed, result.Status);
                Assert.NotEmpty(result.Pairs);
            }
            var split = await LivePhotoDiscoveryService.ScanAsync(root, DiscoveryScanMode.SplitOnly);
            Assert.Equal(ScanOperationStatus.Completed, split.Status);
            Assert.Contains(split.Items, i => i.FilePath.EndsWith("oppo.jpg") && i.IsLivePhoto);
            Assert.Contains(split.DualFilePairs, p => p.Image.FilePath.EndsWith("vivo双文件.jpg"));
            Assert.DoesNotContain(split.Diagnostics, d => d.PrimaryPath.EndsWith("vivo双文件.jpg"));
            var single = await LivePhotoDiscoveryService.DetectSingleFileWithDiagnosticAsync(Path.Combine(root, "vivo双文件.jpg"));
            Assert.NotNull(single.Diagnostic);
            using var ws = new TestFixtureWorkspace();
            PlainPair(ws, "good");
            File.WriteAllText(Path.Combine(ws.RootDirectory, "broken.jpg"), "truncated JPEG");
            File.WriteAllText(Path.Combine(ws.RootDirectory, "broken.mp4"), "truncated MP4");
            string locked = Path.Combine(ws.RootDirectory, "locked.jpg");
            File.Copy(Path.Combine(ws.RootDirectory, "good.jpg"), locked);
            using var lease = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
            var mixedFailure = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { InputDirectory = ws.RootDirectory });
            Assert.Single(mixedFailure.Pairs);
            Assert.Contains(mixedFailure.Diagnostics, d => d.PrimaryPath == locked && d.Severity == ScanDiagnosticSeverity.Error);
            Assert.Contains(mixedFailure.Diagnostics, d => d.PrimaryPath.EndsWith("broken.jpg") && d.Severity == ScanDiagnosticSeverity.Error);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observe; }
        Assert.Empty(raised);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task DiscoveryRefusalHasNoFactsAndStrictInspectorStillRejects()
    {
        using var ws = new TestFixtureWorkspace();
        string path = Copy(ws, "vivo双文件.jpg", "orphan.jpg");
        var inspector = new SourceInspector();
        var result = await inspector.InspectForDiscoveryAsync(path);
        Assert.Null(result.Facts);
        Assert.NotNull(result.Failure);
        Assert.Equal(SourceInspectionFailureCategory.Ambiguous, result.Failure.Category);
        Assert.Equal(SourceInspectionStage.Pairing, result.Failure.Stage);
        var strict = await Assert.ThrowsAsync<SourceInspectionException>(() => inspector.InspectAsync(path));
        Assert.Equal(strict.Category, result.Failure.Category);
        Assert.Equal(strict.Stage, result.Failure.Stage);
        Assert.Equal(strict.Capability, result.Failure.Capability);
        Assert.Equal(strict.Reason, result.Failure.Reason);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Name_ConfirmsLegacyVivoWithoutRelaxingSoloRefusal()
    {
        using var ws = new TestFixtureWorkspace();
        string image = Copy(ws, "vivo双文件.jpg", "pair.jpg");
        string video = Copy(ws, "vivo双文件.mp4", "pair.mp4");
        await Assert.ThrowsAsync<SourceInspectionException>(() => new SourceInspector().InspectAsync(image));
        var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { InputDirectory = ws.RootDirectory });
        var pair = Assert.Single(result.Pairs);
        Assert.Equal(PairingKind.NativeConfirmedSourcePair, pair.PairingKind);
        Assert.Equal(LivePhotoProtocolType.Vivo, pair.Protocol);
        Assert.Equal(video, pair.VideoPath);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ScanDiagnosticSeverity.Error);
    }

    [Fact]
    [Trait("Category", "DerivedRealSamples")]
    public async Task Name_MultipleGroupsBadOrphansAndConflicts_PreservesIndependentRequests()
    {
        using var ws = new TestFixtureWorkspace();
        PlainPair(ws, "a");
        PlainPair(ws, "b");
        PlainPair(ws, "conflict");
        File.Copy(Path.Combine(ws.RootDirectory, "conflict.jpg"), Path.Combine(ws.RootDirectory, "conflict.jpeg"));
        string broken = Path.Combine(ws.RootDirectory, "broken.heic");
        File.WriteAllText(broken, "truncated HEIF");
        File.WriteAllText(Path.Combine(ws.RootDirectory, "broken-video.mp4"), "truncated MP4");
        var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { InputDirectory = ws.RootDirectory });
        Assert.Equal(ScanOperationStatus.Completed, result.Status);
        Assert.Equal(new[] { "a", "b" }, result.Pairs.Select(p => p.BaseName));
        Assert.All(result.Pairs, p => { Assert.Equal(PairingKind.FilenameCompositionRequest, p.PairingKind); Assert.Null(p.PairingIdentifier); });
        Assert.Contains(result.Diagnostics, d => d.PrimaryPath == broken && d.Severity == ScanDiagnosticSeverity.Error);
        Assert.Contains(result.Diagnostics, d => d.DiagnosticCode == "ConflictMultipleMedia");
        Assert.DoesNotContain(broken, result.StandaloneImagePaths);
    }

    [Theory]
    [InlineData(PairingMode.Cid)]
    [InlineData(PairingMode.Vivo)]
    [Trait("Category", "DerivedRealSamples")]
    public async Task IdentityModes_DoNotFallbackToOrdinaryNames(PairingMode mode)
    {
        using var ws = new TestFixtureWorkspace();
        PlainPair(ws, "ordinary");
        var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { InputDirectory = ws.RootDirectory, Mode = mode });
        Assert.Empty(result.Pairs);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ScanDiagnosticSeverity.Error);
        Assert.Equal(1, result.StandaloneImagesCount);
        Assert.Equal(1, result.StandaloneVideosCount);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Cid_DuplicateIdentityBlocksWholeGroupButKeepsIndependentApplePair()
    {
        using var ws = new TestFixtureWorkspace();
        string image = Copy(ws, "苹果双文件.HEIC", "one.heic");
        string video = Copy(ws, "苹果双文件.MOV", "renamed.mov");
        string duplicate = Path.Combine(ws.RootDirectory, "duplicate.heic");
        File.Copy(image, duplicate);
        string good = Copy(ws, "苹果-双文件.JPG", "good.jpg");
        Copy(ws, "苹果-双文件.MOV", "good-renamed.mov");
        var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { InputDirectory = ws.RootDirectory, Mode = PairingMode.Cid });
        Assert.Equal(good, Assert.Single(result.Pairs).ImagePath);
        Assert.Contains(result.Diagnostics, d => d.DiagnosticCode == "ConflictDuplicateIdentity" &&
            d.RelatedPaths.Contains(image) && d.RelatedPaths.Contains(video) && d.RelatedPaths.Contains(duplicate));
        Assert.DoesNotContain(image, result.StandaloneImagePaths);
    }

    [Fact]
    [Trait("Category", "DerivedRealSamples")]
    public async Task CombinedRootsAndFiles_DeduplicateAndHaveOrderIndependentResults()
    {
        using var ws = new TestFixtureWorkspace();
        PlainPair(ws, "good");
        var files = Directory.GetFiles(ws.RootDirectory);
        var first = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { InputDirectories = [ws.RootDirectory], ExplicitFiles = files.Reverse().ToArray() });
        var second = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { ExplicitFiles = files.Concat(files).ToArray() });
        Assert.Single(first.Pairs);
        Assert.Equal(first.Pairs.Select(p => (p.ImagePath, p.VideoPath)), second.Pairs.Select(p => (p.ImagePath, p.VideoPath)));
        Assert.Empty(first.Diagnostics);
    }

    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task Split_BadAndOrdinaryFilesDoNotHideGoodLiveOrReasons()
    {
        using var ws = new TestFixtureWorkspace();
        string good = Copy(ws, "oppo.jpg", "live.jpg");
        string broken = Path.Combine(ws.RootDirectory, "broken.jpg");
        File.WriteAllText(broken, "truncated JPEG");
        File.WriteAllBytes(Path.Combine(ws.RootDirectory, "ordinary.jpg"), SyntheticProtocolFixtures.CreateMinimalJpeg());
        var result = await LivePhotoDiscoveryService.ScanAsync(ws.RootDirectory, DiscoveryScanMode.SplitOnly);
        Assert.Equal(LivePhotoType.SingleFileJpeg, result.Items.Single(i => i.FilePath == good).LivePhotoType);
        Assert.Contains(result.Diagnostics, d => d.PrimaryPath == broken && d.Severity == ScanDiagnosticSeverity.Error);
        Assert.Contains(result.Diagnostics, d => d.DiagnosticCode == "NotSelected");
        var compatibility = LivePhotoSplitScanService.Scan(ws.RootDirectory);
        Assert.Equal(good, Assert.Single(compatibility.Files).SourcePath);
        Assert.Contains(compatibility.Diagnostics, d => d.PrimaryPath == broken);
    }

    [Fact]
    [Trait("Category", "DerivedRealSamples")]
    public async Task ActualShareLockIsIsolatedAndDoesNotBecomeOrdinaryUnmatched()
    {
        using var ws = new TestFixtureWorkspace();
        PlainPair(ws, "good");
        string locked = Path.Combine(ws.RootDirectory, "locked.jpg");
        File.Copy(Path.Combine(ws.RootDirectory, "good.jpg"), locked);
        using var lease = new FileStream(locked, FileMode.Open, FileAccess.Read, FileShare.None);
        var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { InputDirectory = ws.RootDirectory });
        Assert.Single(result.Pairs);
        Assert.Contains(result.Diagnostics, d => d.PrimaryPath == locked && d.Severity == ScanDiagnosticSeverity.Error);
        Assert.DoesNotContain(locked, result.StandaloneImagePaths);
    }

    [Fact]
    [Trait("Category", "DerivedRealSamples")]
    public async Task Vivo_UnresolvedImageDoesNotRejectAnUnrelatedVideo()
    {
        using var ws = new TestFixtureWorkspace();
        string image = Copy(ws, "vivo双文件.jpg", "orphan.jpg");
        PlainPair(ws, "ordinary");
        string video = Path.Combine(ws.RootDirectory, "ordinary.mp4");
        var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions
        {
            InputDirectory = ws.RootDirectory, Mode = PairingMode.Vivo
        });
        Assert.Empty(result.Pairs);
        Assert.Contains(result.Diagnostics, d => d.PrimaryPath == image && d.DiagnosticCode == "UnresolvedBinding");
        Assert.Contains(video, result.StandaloneVideoPaths);
        Assert.DoesNotContain(result.Diagnostics, d => d.Severity == ScanDiagnosticSeverity.Error && d.RelatedPaths.Contains(video));
    }

    [Fact]
    public async Task CancellationDoesNotPublishIncompleteRelationships()
    {
        using var ws = new TestFixtureWorkspace();
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        var merge = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { InputDirectory = ws.RootDirectory }, cts.Token);
        Assert.Equal(ScanOperationStatus.Cancelled, merge.Status);
        Assert.Empty(merge.Pairs);
        var split = await LivePhotoDiscoveryService.ScanAsync(ws.RootDirectory, DiscoveryScanMode.SplitOnly, cts.Token);
        Assert.Equal(ScanOperationStatus.Cancelled, split.Status);
        Assert.Empty(split.Items);
        Assert.DoesNotContain(merge.Diagnostics, d => d.Severity == ScanDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task MissingRootIsFailedNotSuccessfulEmptyScan()
    {
        using var ws = new TestFixtureWorkspace();
        var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { InputDirectory = Path.Combine(ws.RootDirectory, "missing") });
        Assert.Equal(ScanOperationStatus.Failed, result.Status);
        Assert.True(result.IsPartial);
        Assert.Contains(result.Diagnostics, d => d.Stage == ScanDiagnosticStage.Enumeration && d.Severity == ScanDiagnosticSeverity.Error);
    }

    [Fact]
    [Trait("Category", "DerivedRealSamples")]
    public async Task RecursiveScanDoesNotFollowDirectoryLinkBackToRoot()
    {
        using var ws = new TestFixtureWorkspace();
        PlainPair(ws, "good");
        string link = Path.Combine(ws.RootDirectory, "loop");
        using (var process = Process.Start(new ProcessStartInfo("cmd.exe")
        {
            ArgumentList = { "/c", "mklink", "/J", link, ws.RootDirectory },
            UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true
        })!)
        {
            Assert.True(process.WaitForExit(5000));
            Assert.Equal(0, process.ExitCode);
        }
        try
        {
            var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions { InputDirectory = ws.RootDirectory, Recursive = true });
            Assert.Single(result.Pairs);
            Assert.Contains(result.Diagnostics, d => d.DiagnosticCode == "ReparsePointSkipped" && d.PrimaryPath == link);
        }
        finally { Directory.Delete(link); }
    }

    private static string Copy(TestFixtureWorkspace ws, string sample, string name)
    {
        string path = Path.Combine(ws.RootDirectory, name);
        File.Copy(TestSampleResolver.ResolveSample(sample), path);
        return path;
    }

    private static void PlainPair(TestFixtureWorkspace ws, string name)
    {
        string inputs = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P8_PLAIN_PAIR_ROOT")
            ?? throw new InvalidOperationException("Required frozen derived plain image/video fixtures are missing.");
        File.Copy(Path.Combine(inputs, "image.jpg"), Path.Combine(ws.RootDirectory, name + ".jpg"));
        File.Copy(Path.Combine(inputs, "video.mp4"), Path.Combine(ws.RootDirectory, name + ".mp4"));
    }
}
