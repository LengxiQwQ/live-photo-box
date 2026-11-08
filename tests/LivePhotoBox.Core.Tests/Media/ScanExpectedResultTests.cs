using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class ScanExpectedResultTests
{
    [Theory]
    [InlineData("merge", "cancelled")]
    [InlineData("discovery", "cancelled")]
    [InlineData("merge", "mid-cancel")]
    [InlineData("discovery", "mid-cancel")]
    [InlineData("merge", "missing-directory")]
    [InlineData("discovery", "missing-directory")]
    [InlineData("index", "missing")]
    [InlineData("index", "cancelled")]
    public async Task ExpectedScanOutcomesDoNotRaiseFirstChanceEvents(string route, string condition)
    {
        using var workspace = new TestFixtureWorkspace();
        string source = Path.Combine(workspace.RootDirectory, "source.jpg");
        File.WriteAllBytes(source, new byte[32]);
        using var cancellation = new CancellationTokenSource();
        if (condition == "cancelled") cancellation.Cancel();
        var progress = new CallbackProgress<WorkProgressSnapshot>(_ =>
        {
            if (condition == "mid-cancel") cancellation.Cancel();
        });
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        ScanOperationStatus? status = null;
        AppDomain.CurrentDomain.FirstChanceException += observer;
        try
        {
            if (route == "index")
            {
                var result = await new FileIndexService().GetAsync(Path.Combine(workspace.RootDirectory, "missing.jpg"),
                    DiscoveryCapability.AppleId, cancellation.Token);
                if (condition == "cancelled") Assert.Null(result);
                else Assert.Equal(FactState.ReadFailed, result!.State);
            }
            else if (route == "merge")
            {
                var result = await LivePhotoMergeScanService.ScanAsync(new MergeScanOptions {
                    InputDirectory = condition == "missing-directory" ? Path.Combine(workspace.RootDirectory, "missing") : workspace.RootDirectory
                }, cancellation.Token, progress);
                status = result.Status;
                Assert.Empty(result.Pairs);
                if (condition != "missing-directory") { Assert.Empty(result.StandaloneImagePaths); Assert.Empty(result.StandaloneVideoPaths); }
            }
            else
            {
                var result = await LivePhotoDiscoveryService.ScanAsync(condition == "missing-directory"
                    ? Path.Combine(workspace.RootDirectory, "missing") : workspace.RootDirectory,
                    DiscoveryScanMode.All, cancellation.Token, progress);
                status = result.Status;
            }
        }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
        Assert.Empty(events);
        if (route != "index") Assert.Equal(condition == "missing-directory" ? ScanOperationStatus.Failed : ScanOperationStatus.Cancelled, status);
        Assert.Equal(new byte[32], File.ReadAllBytes(source));
    }
}
