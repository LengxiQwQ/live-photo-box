using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Services;
using LivePhotoBox.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class CleanupCompletionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealHuaweiRepairReleasesOwnLeasesAndReportsOnlyRealCleanupLimitations(bool insertForeignFile)
    {
        using var fixture = new TestFixtureWorkspace();
        string source = TestSampleResolver.ResolveSample("华为Mate80.heic");
        string before = Hash(source);
        string? workspaceRoot = null;
        var previous = MediaWorkspace.TestCleanupHook.Value;
        MediaWorkspace.TestCleanupHook.Value = (_, root) =>
        {
            workspaceRoot = root;
            if (insertForeignFile) File.WriteAllText(Path.Combine(root, "foreign.txt"), "preserve");
        };
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        LivePhotoRepairService.RepairResult result;
        try
        {
            AppDomain.CurrentDomain.FirstChanceException += observer;
            try { result = await LivePhotoRepairService.RepairImageFileAsync(source, fixture.RootDirectory, true, false, new RepairOptions(), CancellationToken.None); }
            finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
            Assert.Empty(events);
            Assert.Equal(before, Hash(source));
            Assert.NotNull(workspaceRoot);
            Assert.NotNull(result.Image);
            Assert.True(File.Exists(result.Image));
            if (insertForeignFile)
            {
                Assert.Equal("warning", result.Status);
                Assert.NotNull(result.IsolationDirectory);
                Assert.StartsWith(result.IsolationDirectory! + Path.DirectorySeparatorChar, result.Image!, StringComparison.OrdinalIgnoreCase);
                Assert.Equal("isolated_repaired", Assert.Single(result.PublishedOutputs).Kind);
                Assert.Contains(result.Image!, LivePhotoRepairService.DescribeResult(result));
                Assert.Contains(workspaceRoot!, result.Message);
                Assert.Equal("preserve", File.ReadAllText(Path.Combine(workspaceRoot!, "foreign.txt")));
            }
            else
            {
                Assert.Equal("success", result.Status);
                Assert.Null(result.IsolationDirectory);
                Assert.False(Directory.Exists(workspaceRoot));
            }
        }
        finally { MediaWorkspace.TestCleanupHook.Value = previous; if (insertForeignFile && workspaceRoot is not null) Directory.Delete(workspaceRoot, true); }
    }

    [Theory]
    [InlineData(SplitMediaFormat.KeepSource)]
    [InlineData(SplitMediaFormat.AppleJpegMov)]
    [InlineData(SplitMediaFormat.VivoJpegMp4)]
    public async Task EverySplitReturnReportsCleanupAndActualIsolatedPair(SplitMediaFormat format)
    {
        using var fixture = new TestFixtureWorkspace();
        string source = await CreateSourceAsync(fixture.RootDirectory);
        string sourceHash = Hash(source);
        string output = Path.Combine(fixture.RootDirectory, "split");
        string? retainedRoot = null;
        var previous = MediaWorkspace.TestCleanupHook.Value;
        MediaWorkspace.TestCleanupHook.Value = (_, root) => { retainedRoot = root; File.WriteAllText(Path.Combine(root, "foreign.txt"), "foreign content"); };
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        ProductOperationOutcome outcome;
        try
        {
            AppDomain.CurrentDomain.FirstChanceException += observer;
            try { outcome = await LivePhotoSplitService.SplitWithOutcomeAsync(source, output, format, CancellationToken.None); }
            finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
            Assert.Empty(events);
            Assert.Equal(ProductOperationStatus.Warning, outcome.Status);
            Assert.Equal(ProductCompletionKind.CompatibleCompleted, outcome.CompletionKind);
            Assert.Null(outcome.SourceActionGuard);
            Assert.NotNull(outcome.IsolationDirectory);
            Assert.Equal(2, outcome.CommittedOutputs.Count);
            Assert.All(outcome.CommittedOutputs, artifact =>
            {
                Assert.True(File.Exists(artifact.Path));
                Assert.StartsWith(outcome.IsolationDirectory! + Path.DirectorySeparatorChar, artifact.Path, StringComparison.OrdinalIgnoreCase);
                Assert.Contains(artifact.Path, outcome.Details);
            });
            Assert.NotNull(retainedRoot);
            Assert.Contains(retainedRoot!, outcome.Details);
            Assert.Equal("foreign content", File.ReadAllText(Path.Combine(retainedRoot!, "foreign.txt")));
            Assert.Equal(sourceHash, Hash(source));
        }
        finally
        {
            MediaWorkspace.TestCleanupHook.Value = previous;
            if (retainedRoot is not null) Directory.Delete(retainedRoot, true);
        }
    }

    [Fact]
    public async Task OriginalImageResultKeepsBytesAndIncludesCleanupWarning()
    {
        string source = TestSampleResolver.ResolveSample("一加-改了封面照片.jpg");
        string before = Hash(source);
        string? retainedRoot = null;
        var previous = MediaWorkspace.TestCleanupHook.Value;
        MediaWorkspace.TestCleanupHook.Value = (_, root) => { retainedRoot = root; File.WriteAllText(Path.Combine(root, "foreign.txt"), "preserve"); };
        try
        {
            var result = await EditTimingService.TryReadOriginalPhotoBytesAsync(source, CancellationToken.None);
            Assert.Null(result.Failure); Assert.NotEmpty(result.Bytes!);
            Assert.Contains(result.Warnings, warning => warning.Contains(retainedRoot!, StringComparison.Ordinal));
            Assert.Equal(before, Hash(source));
            Assert.Equal("preserve", File.ReadAllText(Path.Combine(retainedRoot!, "foreign.txt")));
        }
        finally { MediaWorkspace.TestCleanupHook.Value = previous; if (retainedRoot is not null) Directory.Delete(retainedRoot, true); }
    }

    [Fact]
    public async Task EarlyRecoveryRetainsItsCompletionTypeAndReportsCleanup()
    {
        using var fixture = new TestFixtureWorkspace();
        string source = TestSampleResolver.ResolveSample("oppo.jpg");
        string before = Hash(source);
        string? retainedRoot = null;
        var previous = MediaWorkspace.TestCleanupHook.Value;
        MediaWorkspace.TestCleanupHook.Value = (_, root) => { retainedRoot = root; File.WriteAllText(Path.Combine(root, "foreign.txt"), "preserve"); };
        try
        {
            var outcome = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(source, "", "strict", 0,
                new LivePhotoMergeRunOptions { OutputDirectory = fixture.RootDirectory,
                    OutputProfile = OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMov),
                    StructuredOptions = new() { PreservationPolicy = PreservationPolicy.Strict } }, CancellationToken.None);
            Assert.NotNull(retainedRoot);
            Assert.Equal(ProductOperationStatus.Warning, outcome.Status);
            Assert.Equal(ProductCompletionKind.RecoveredMedia, outcome.CompletionKind);
            Assert.Contains(retainedRoot!, outcome.Details);
            Assert.Null(outcome.SourceActionGuard);
            Assert.NotEmpty(outcome.CommittedOutputs);
            Assert.Equal(before, Hash(source));
        }
        finally { MediaWorkspace.TestCleanupHook.Value = previous; if (retainedRoot is not null) Directory.Delete(retainedRoot, true); }
    }

    private static async Task<string> CreateSourceAsync(string root)
    {
        string image = Path.Combine(root, "image.jpg"), video = Path.Combine(root, "video.mp4");
        File.WriteAllBytes(image, P9TargetTestInputs.Image); File.WriteAllBytes(video, P9TargetTestInputs.Video);
        var outcome = await LivePhotoMergeRunnerService.ProcessSinglePairWithOutcomeAsync(image, video, "input", 0,
            new LivePhotoMergeRunOptions { OutputDirectory = Path.Combine(root, "made"), KeyPhotoTimestampUs = 0,
                OutputProfile = OutputProfile.Create(TargetProtocol.OppoLivePhoto, OutputMediaFormat.JpegMp4) }, CancellationToken.None);
        Assert.True(outcome.IsSuccess, outcome.Details);
        return Assert.Single(outcome.CommittedOutputs).Path;
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
