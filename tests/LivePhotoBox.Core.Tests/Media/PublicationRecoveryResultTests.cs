using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class PublicationRecoveryResultTests
{
    [Theory]
    [InlineData("empty")]
    [InlineData("unreadable-seal")]
    [InlineData("active-journal")]
    [InlineData("root-is-file")]
    public async Task UnusableRecoveryStateIsRetainedWithoutExpectedThrows(string condition)
    {
        using var workspace = new TestFixtureWorkspace();
        string root = Path.Combine(workspace.RootDirectory, "recovery");
        string output = Path.Combine(workspace.RootDirectory, "output");
        Directory.CreateDirectory(output);
        if (condition == "root-is-file") File.WriteAllText(root, "foreign bytes");
        else Directory.CreateDirectory(root);
        string journal = Path.Combine(root, "foreign.lpbr");
        if (condition != "root-is-file") File.WriteAllBytes(journal, condition == "empty" ? [] : new byte[32]);
        using var locked = condition == "active-journal" ? File.Open(journal, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        WindowsSplitOutputPublisher.RecoveryRootForTests.Value = root;
        AppDomain.CurrentDomain.FirstChanceException += observer;
        try
        {
            var issues = WindowsSplitOutputPublisher.RecoverPublications(output, throwFailures: false);
            if (condition == "active-journal") Assert.Empty(issues);
            else
            {
                Assert.NotEmpty(issues);
                var attempt = await WindowsSplitOutputPublisher.TryPublishSingleAsync(Path.Combine(output, "result.jpg"), false,
                    (stream, token) => Task.FromResult(TransactionStreamWriteAttempt.Write(stream, new byte[32], token)), null, CancellationToken.None);
                Assert.NotNull(attempt.Failure);
                Assert.Null(attempt.Receipt);
            }
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= observer;
            WindowsSplitOutputPublisher.RecoveryRootForTests.Value = null;
        }
        Assert.Empty(events);
        Assert.Empty(Directory.GetFiles(output));
        if (condition == "root-is-file") Assert.Equal("foreign bytes", File.ReadAllText(root));
        else if (condition != "active-journal") Assert.Equal(condition == "empty" ? [] : new byte[32], File.ReadAllBytes(journal));
    }

    [Fact]
    public async Task ProductCancellationAfterMutationRestoresOriginalObjectsWithoutThrows()
    {
        using var workspace = new TestFixtureWorkspace();
        string root = workspace.RootDirectory;
        string image = Path.Combine(root, "source.jpg"), video = Path.Combine(root, "source.mp4");
        string targetImage = Path.Combine(root, "target.jpg"), targetVideo = Path.Combine(root, "target.mp4");
        File.WriteAllBytes(image, new byte[128]); File.WriteAllBytes(video, new byte[256]);
        byte[] oldImage = [1,2,3,4], oldVideo = [5,6,7,8];
        File.WriteAllBytes(targetImage, oldImage); File.WriteAllBytes(targetVideo, oldVideo);
        var imageIdentity = LivePhotoBox.Media.Models.WindowsFileIdentity.Capture(targetImage);
        var videoIdentity = LivePhotoBox.Media.Models.WindowsFileIdentity.Capture(targetVideo);
        using var cancel = new CancellationTokenSource();
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        WindowsSplitOutputPublisher.RecoveryRootForTests.Value = Path.Combine(root, "recovery");
        AppDomain.CurrentDomain.FirstChanceException += observer;
        WindowsSplitOutputPublisher.PublicationAttempt attempt;
        try
        {
            attempt = await WindowsSplitOutputPublisher.TryPublishAsync(image, video, targetImage, targetVideo, true,
                (trace, token) => { if (trace.Boundary == SplitPublicationBoundary.AfterTargetMutation) cancel.Cancel(); return Task.CompletedTask; }, cancel.Token);
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observer; WindowsSplitOutputPublisher.RecoveryRootForTests.Value = null; }
        Assert.IsAssignableFrom<OperationCanceledException>(attempt.Failure);
        Assert.Empty(events);
        Assert.Equal(oldImage, File.ReadAllBytes(targetImage)); Assert.Equal(oldVideo, File.ReadAllBytes(targetVideo));
        Assert.Equal(imageIdentity, LivePhotoBox.Media.Models.WindowsFileIdentity.Capture(targetImage));
        Assert.Equal(videoIdentity, LivePhotoBox.Media.Models.WindowsFileIdentity.Capture(targetVideo));
        Assert.Empty(Directory.GetFiles(root, ".lpb-split-*.tmp"));
        Assert.Empty(Directory.GetFiles(Path.Combine(root, "recovery")));
    }
}
