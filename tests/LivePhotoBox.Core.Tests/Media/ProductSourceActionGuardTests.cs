using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ProductSourceActionGuardTests
{
    [Theory]
    [InlineData("move")]
    [InlineData("occupied")]
    [InlineData("changed")]
    public async Task ArchivePostProcessingReturnsExpectedResultsWithoutThrowing(string scenario)
    {
        using var workspace = new TestFixtureWorkspace();
        string source = Path.Combine(workspace.RootDirectory, "source.jpg");
        string output = Path.Combine(workspace.RootDirectory, "output.jpg");
        string archive = Path.Combine(workspace.RootDirectory, "archive");
        File.WriteAllText(source, "original source"); File.WriteAllText(output, "committed output");
        Directory.CreateDirectory(archive);
        string destination = Path.Combine(archive, "source.jpg");
        ProductCommittedArtifact[] artifacts = [new() { Role = "TargetLivePhoto", Path = output }];
        var guard = ProductSourceActionGuard.Capture([source], artifacts);
        var outcome = new ProductOperationOutcome { Status = ProductOperationStatus.Success,
            CompletionKind = ProductCompletionKind.TargetCompleted, Inputs = [source], CommittedOutputs = artifacts, SourceActionGuard = guard };
        if (scenario == "occupied") File.WriteAllText(destination, "foreign archive member");
        if (scenario == "changed") File.WriteAllText(source, "changed source");
        var events = new System.Collections.Concurrent.ConcurrentQueue<Exception>();
        EventHandler<System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        ProductSourcePostProcessing processing;
        AppDomain.CurrentDomain.FirstChanceException += observer;
        try { processing = await ProductSourcePostProcessor.ProcessAsync(outcome, [source], true, archive, default); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
        Assert.Empty(events);
        Assert.Equal("move", processing.Action);
        Assert.Equal(scenario == "move" ? "completed" : "failed", processing.Status);
        if (scenario == "move")
        {
            Assert.False(File.Exists(source));
            Assert.Equal("original source", File.ReadAllText(destination));
        }
        else
        {
            Assert.Equal(scenario == "changed" ? "changed source" : "original source", File.ReadAllText(source));
            if (scenario == "occupied") Assert.Equal("foreign archive member", File.ReadAllText(destination));
            else Assert.False(File.Exists(destination));
        }
        Assert.Equal("committed output", File.ReadAllText(output));
    }

    [Fact]
    public void QueuedReadFailureStillPinsOriginalAgainstReplacement()
    {
        using var workspace = new TestFixtureWorkspace();
        string input = Path.Combine(workspace.RootDirectory, "source");
        string replacement = Path.Combine(workspace.RootDirectory, "replacement");
        File.WriteAllText(input, "original"); File.WriteAllText(replacement, "foreign");
        using var exclusive = File.Open(input, FileMode.Open, FileAccess.Read, FileShare.None);
        using var protection = new SourceInputProtection([input]);
        exclusive.Dispose();
        // The metadata pin must still prevent replacement after the original
        // reason for the read failure has disappeared.
        Assert.Throws<UnauthorizedAccessException>(() => File.Move(replacement, input, overwrite: true));
        File.WriteAllText(Path.Combine(workspace.RootDirectory, "independent"), "safe independent output");
        Assert.Equal("original", File.ReadAllText(input));
        Assert.Equal("foreign", File.ReadAllText(replacement));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReplacedSourceOrOutputCannotAuthorizePostProcessing(bool replaceOutput)
    {
        using var workspace = new TestFixtureWorkspace();
        string input = Path.Combine(workspace.RootDirectory, "source.jpg");
        string output = Path.Combine(workspace.RootDirectory, "output.jpg");
        File.WriteAllText(input, "original source"); File.WriteAllText(output, "committed output");
        ProductCommittedArtifact[] artifacts = [new() { Role = "TargetLivePhoto", Path = output }];
        var guard = ProductSourceActionGuard.Capture([input], artifacts);
        var outcome = new ProductOperationOutcome { Status = ProductOperationStatus.Success,
            Inputs = [input], CommittedOutputs = artifacts, SourceActionGuard = guard };
        guard.Validate(outcome, [input]);
        string target = replaceOutput ? output : input;
        string replacement = Path.Combine(workspace.RootDirectory, "replacement");
        File.Copy(target, replacement); // Same bytes, different Windows file object.
        File.Move(replacement, target, overwrite: true);
        Assert.Throws<IOException>(() => guard.Validate(outcome, [input]));
        Assert.True(File.Exists(input)); Assert.True(File.Exists(output));
    }

    [Fact]
    public void YellowAndHistoricalReceiptsNeverAuthorizeSourceActions()
    {
        using var workspace = new TestFixtureWorkspace();
        string input = Path.Combine(workspace.RootDirectory, "source");
        string output = Path.Combine(workspace.RootDirectory, "output");
        File.WriteAllText(input, "source"); File.WriteAllText(output, "output");
        ProductCommittedArtifact[] artifacts = [new() { Role = "TargetLivePhoto", Path = output }];
        var guard = ProductSourceActionGuard.Capture([input], artifacts);
        var outcome = new ProductOperationOutcome { Status = ProductOperationStatus.Success,
            Inputs = [input], CommittedOutputs = artifacts, SourceActionGuard = guard };
        Assert.Throws<IOException>(() => guard.Validate(outcome with { Status = ProductOperationStatus.Warning }, [input]));
        Assert.Throws<IOException>(() => guard.Validate(outcome with { SourceActionGuard = null }, [input]));
        File.WriteAllText(output, "changed output");
        Assert.Throws<IOException>(() => guard.Validate(outcome, [input]));
        Assert.True(File.Exists(input));
    }
}
