using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class NativeOutputResultTests
{
    [Theory]
    [InlineData("changed")]
    [InlineData("missing")]
    [InlineData("locked")]
    [InlineData("cancelled")]
    public async Task InspectedSourceBecomesUnavailable_ExtractionReturnsDataWithoutExceptions(string condition)
    {
        using var fixture = new TestFixtureWorkspace();
        using var workspace = new MediaWorkspace();
        string source = Path.Combine(fixture.RootDirectory, "source.jpg");
        File.WriteAllBytes(source, P9TargetTestInputs.Image);
        var inspection = await NativeMediaService.TryInspectMediaWithPlanAsync(source);
        Assert.Null(inspection.Failure);
        using var inspected = inspection.Source!;
        if (condition == "changed") File.AppendAllText(source, "changed after inspection");
        if (condition == "missing") File.Delete(source);
        using var held = condition == "locked" ? File.Open(source, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        using var cancellation = new CancellationTokenSource();
        if (condition == "cancelled") cancellation.Cancel();
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        ExtractionProductAttempt attempt;
        AppDomain.CurrentDomain.FirstChanceException += observer;
        try { attempt = await new SourceExtractor().TryExtractAsync(inspected.ExtractionPlan, source, null, workspace, cancellation.Token); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
        Assert.Empty(events);
        Assert.NotNull(attempt.Failure);
        Assert.Null(attempt.Bundle);
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ReassemblyMissingOrCancelledReturnsDataWithoutFirstChanceEvents(bool cancelled)
    {
        using var workspace = new TestFixtureWorkspace();
        string source = Path.Combine(workspace.RootDirectory, "missing.jpg");
        string output = Path.Combine(workspace.RootDirectory, "output.jpg");
        using var cancel = new CancellationTokenSource();
        if (cancelled) cancel.Cancel();
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        AppDomain.CurrentDomain.FirstChanceException += observer;
        NativeMediaService.RegisteredOutputAttempt attempt;
        try { attempt = await NativeMediaService.TryReassembleJpegGainMapAsync(source, source, output, new string('0', 64), cancel.Token); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
        Assert.Empty(events);
        Assert.NotNull(attempt.Failure);
        Assert.Null(attempt.Evidence);
        Assert.False(File.Exists(output));
        if (cancelled) Assert.IsAssignableFrom<OperationCanceledException>(attempt.Failure);
    }

    [Fact]
    public void MissingNativeReceiptCannotAdoptOrAlterExistingFile()
    {
        using var workspace = new TestFixtureWorkspace();
        string output = Path.Combine(workspace.RootDirectory, "foreign.jpg");
        byte[] bytes = [1, 3, 5, 7];
        File.WriteAllBytes(output, bytes);
        using var context = NativeContext.Create(CancellationToken.None);
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        AppDomain.CurrentDomain.FirstChanceException += observer;
        NativeMediaService.RegisteredOutputAttempt attempt;
        try { attempt = NativeMediaService.TryCaptureRegisteredOutputEvidence(context, output, MediaArtifactKind.PrimaryImage); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
        Assert.Empty(events);
        Assert.NotNull(attempt.Failure);
        Assert.Null(attempt.Evidence);
        Assert.Equal(bytes, File.ReadAllBytes(output));
    }
}
