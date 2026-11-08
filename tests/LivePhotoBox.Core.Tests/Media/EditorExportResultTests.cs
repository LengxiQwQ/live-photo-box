using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Services;
using Xunit;
namespace LivePhotoBox.Core.Tests.Media;
public sealed class EditorExportResultTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("locked")]
    [InlineData("cancelled")]
    [InlineData("no-original")]
    public async Task OriginalPhotoLookupReturnsExternalProblemsAsData(string condition)
    {
        using var fixture = new TestFixtureWorkspace();
        string source = Path.Combine(fixture.RootDirectory, "source.jpg");
        if (condition != "missing") File.WriteAllBytes(source, P9TargetTestInputs.Image);
        using var held = condition == "locked" ? File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        using var cancellation = new CancellationTokenSource(); if (condition == "cancelled") cancellation.Cancel();
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        EditTimingService.OriginalPhotoAttempt attempt;
        AppDomain.CurrentDomain.FirstChanceException += observer;
        try { attempt = await EditTimingService.TryReadOriginalPhotoBytesAsync(source, cancellation.Token); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
        Assert.Empty(events); Assert.Null(attempt.Bytes);
        if (condition == "no-original") Assert.Null(attempt.Failure); else Assert.NotNull(attempt.Failure);
        if (condition == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(attempt.Failure);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task KnownExportProblemReturnsDataAndKeepsSourceAndExistingTarget(bool cancelled)
    {
        using var workspace = new TestFixtureWorkspace();
        string source = Path.Combine(workspace.RootDirectory, "source.jpg"), target = Path.Combine(workspace.RootDirectory, "target.jpg");
        File.WriteAllBytes(source, P9TargetTestInputs.Image); byte[] sentinel=[1,2,3,4]; File.WriteAllBytes(target,sentinel);
        using var held = cancelled ? null : File.Open(target, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        using var cancel = new CancellationTokenSource();if(cancelled) cancel.Cancel();
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer=(_,e)=>events.Enqueue(e.Exception);
        Exception? failure;
        AppDomain.CurrentDomain.FirstChanceException+=observer;
        try { failure=await ImageFormatService.TryConvertImageAsync(source,target,80,cancel.Token); }
        finally { AppDomain.CurrentDomain.FirstChanceException-=observer; }
        Assert.NotNull(failure);Assert.Empty(events);
        held?.Dispose();
        Assert.Equal(sentinel,File.ReadAllBytes(target));Assert.Equal(P9TargetTestInputs.Image,File.ReadAllBytes(source));
        Assert.Equal(2,Directory.GetFiles(workspace.RootDirectory).Length);
    }
    [Fact]
    public void PartialBatchAndUnsupportedMetadataAreResultData()
    {
        var events=new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer=(_,e)=>events.Enqueue(e.Exception);
        AppDomain.CurrentDomain.FirstChanceException+=observer;
        Exception? unsupported,partial;
        try { unsupported=EditExportMetadataPolicy.RequestFailure(true);partial=EditExportMetadataPolicy.IncompleteFailure(3,2,1); }
        finally {AppDomain.CurrentDomain.FirstChanceException-=observer;}
        Assert.IsType<NotSupportedException>(unsupported);Assert.IsType<InvalidOperationException>(partial);Assert.Empty(events);
        Assert.Null(EditExportMetadataPolicy.RequestFailure(false));Assert.Null(EditExportMetadataPolicy.IncompleteFailure(3,3,0));
    }
}
