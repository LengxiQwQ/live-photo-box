using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class RepairExpectedResultTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task DiagnosisMissingOrCancelledReturnsNormally(bool cancelled)
    {
        using var workspace = new TestFixtureWorkspace();
        string source = Path.Combine(workspace.RootDirectory, "missing.jpg");
        using var cancel = new CancellationTokenSource(); if (cancelled) cancel.Cancel();
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        LivePhotoBox.Models.RepairAnalysisResult result;
        AppDomain.CurrentDomain.FirstChanceException += observer;
        try { result = await LivePhotoRepairService.AnalyzeFileAsync(source, cancel.Token); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
        Assert.Empty(events);
        Assert.Equal(cancelled ? LivePhotoBox.Models.RepairIssueType.Limited : LivePhotoBox.Models.RepairIssueType.Error, result.IssueType);
        Assert.Null(result.DiagnosedIdentity);
    }

    [Fact]
    public async Task LostOutputTrackingStopsOnlyDependentPairingAndReportsLimitationWithoutThrows()
    {
        using var workspace = new TestFixtureWorkspace();
        using var batch = new RepairBatchContext();
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        Exception? failure;
        RepairBatchContext.CandidateAttempt candidates;
        AppDomain.CurrentDomain.FirstChanceException += observer;
        try
        {
            failure = batch.RecordPublished(Path.Combine(workspace.RootDirectory, "missing-output.jpg"));
            candidates = await batch.TryCandidatesAsync(Path.Combine(workspace.RootDirectory, "source.jpg"), default);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
        Assert.NotNull(failure); Assert.Same(failure, candidates.Failure); Assert.Empty(candidates.Candidates); Assert.Empty(events);
        var explained = batch.ExplainTrackingFailure(new("success", "已完成导出。", "actual-output.jpg"));
        Assert.Equal("success", explained.Status); Assert.Equal("actual-output.jpg", explained.Image);
        Assert.Contains(explained.Limitations, l => l.ReasonCode == "batch_pairing_protection");
    }

    [Theory]
    [InlineData("cancelled")]
    [InlineData("missing-directory")]
    [InlineData("locked-member")]
    public async Task IncompletePairingScopeReturnsDataWithoutFirstChanceException(string condition)
    {
        using var workspace = new TestFixtureWorkspace();
        string source = Path.Combine(workspace.RootDirectory, "source.jpg");
        File.WriteAllBytes(source, P9TargetTestInputs.Image);
        if (condition == "locked-member") File.WriteAllBytes(Path.Combine(workspace.RootDirectory, "broken.mov"), new byte[32]);
        if (condition == "missing-directory") source = Path.Combine(workspace.RootDirectory, "missing", "source.jpg");
        using var cancellation = new CancellationTokenSource();
        if (condition == "cancelled") cancellation.Cancel();
        using var locked = condition == "locked-member" ? File.Open(Path.Combine(workspace.RootDirectory, "broken.mov"), FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        using var batch = new RepairBatchContext();
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        RepairBatchContext.CandidateAttempt result;
        AppDomain.CurrentDomain.FirstChanceException += observer;
        try
        {
            result = await batch.TryCandidatesAsync(source, cancellation.Token,
                LivePhotoBox.Media.Models.SourceProtocol.AppleLivePhoto, "1393ed31-84bb-432c-a384-077aafed694d");
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
        Assert.NotNull(result.Failure);
        Assert.Empty(result.Candidates);
        Assert.Empty(events);
        if (condition == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(result.Failure);
        Assert.Empty(Directory.GetDirectories(workspace.RootDirectory));
    }

    [Theory]
    [InlineData("image", "missing")]
    [InlineData("image", "locked")]
    [InlineData("image", "cancelled")]
    [InlineData("video", "missing")]
    [InlineData("video", "malformed")]
    [InlineData("video", "cancelled")]
    [InlineData("samsung", "malformed")]
    [InlineData("huawei", "malformed")]
    [InlineData("samsung", "cancelled")]
    [InlineData("huawei", "cancelled")]
    public async Task KnownExternalProblemsReturnWithoutFirstChanceException(string route, string condition)
    {
        using var workspace = new TestFixtureWorkspace();
        string source = Path.Combine(workspace.RootDirectory, route == "video" ? "source.mov" : "source.jpg");
        if (condition != "missing") File.WriteAllBytes(source, new byte[32]);
        using var locked = condition == "locked" ? File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        using var cancellation = new CancellationTokenSource();
        if (condition == "cancelled") cancellation.Cancel();
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        string status;
        AppDomain.CurrentDomain.FirstChanceException += observer;
        try
        {
            if (route == "samsung")
            {
                var attempt = await NativeSamsungRepair.TryRunAsync(source, workspace.RootDirectory, null, 15, cancellation.Token);
                Assert.NotNull(attempt.Failure);
                status = attempt.Failure is OperationCanceledException ? "cancelled" : "failed";
            }
            else if (route == "huawei")
            {
                var attempt = await NativeHuaweiRepair.TryRunAsync(source, null, 15, cancellation.Token);
                Assert.True(attempt.Failure is not null || attempt.Observation?.Supported == false);
                status = attempt.Failure is OperationCanceledException ? "cancelled" : "failed";
            }
            else
            {
                var result = route == "image"
                    ? await LivePhotoRepairService.RepairImageFileAsync(source, Path.Combine(workspace.RootDirectory, "output"), true, false, new RepairOptions(), cancellation.Token)
                    : await LivePhotoRepairService.RepairVideoFileAsync(source, Path.Combine(workspace.RootDirectory, "output"), true, true, false, false, new RepairOptions(), cancellation.Token);
                status = result.Status;
                Assert.Empty(result.PublishedOutputs);
            }
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
        Assert.Equal(condition == "cancelled" ? "cancelled" : "failed", status);
        Assert.Empty(events);
        Assert.False(Directory.Exists(Path.Combine(workspace.RootDirectory, "output")));
    }
}
