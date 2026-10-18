using System.Security.Cryptography;
using System.Text.Json;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P7R2EditorPublicationTests
{
    [Fact, Trait("Category", "RealSamples")]
    public async Task ImageExport_RealLockedDestination_PreservesTargetAndSource()
    {
        string source = TestSampleResolver.ResolveSample("oppo.jpg");
        string sha = Sha(source);
        using var output = new TestFixtureWorkspace();
        string target = Path.Combine(output.RootDirectory, "locked.jpg");
        byte[] sentinel = [0x52,0x32,0x55,0x49]; File.WriteAllBytes(target, sentinel);
        var identity = WindowsFileIdentity.Capture(target);
        Exception? failure;
        using (var targetLock = new FileStream(target, FileMode.Open, FileAccess.Read, FileShare.None))
            failure = await Record.ExceptionAsync(() => ImageFormatService.ConvertImageAsync(source, target));
        Assert.NotNull(failure);
        Assert.Equal(identity, WindowsFileIdentity.Capture(target));
        Assert.Equal(sentinel, File.ReadAllBytes(target)); Assert.Equal(sha, Sha(source));
        Assert.Single(Directory.GetFiles(output.RootDirectory));
        Save("editor-export-locked", new { source, before = sha, after = Sha(source), target, identity, finalIdentity = WindowsFileIdentity.Capture(target), sentinel = Convert.ToHexString(File.ReadAllBytes(target)), error = failure.ToString(), files = Directory.GetFiles(output.RootDirectory) });
    }

    [Fact, Trait("Category", "RealSamples")]
    public async Task ImageExport_CancelAtFinalBarrier_RestoresExactOriginal()
    {
        string source = TestSampleResolver.ResolveSample("oppo.jpg"), sha = Sha(source);
        using var output = new TestFixtureWorkspace();
        string target = Path.Combine(output.RootDirectory, "cancelled.jpg");
        byte[] sentinel = [0x52,0x32,0x55,0x49,0x43];File.WriteAllBytes(target, sentinel);
        var identity = WindowsFileIdentity.Capture(target); using var cts = new CancellationTokenSource(); bool reached = false;
        var prior = WindowsSplitOutputPublisher.TestHook.Value;
        WindowsSplitOutputPublisher.TestHook.Value = (trace, _) =>
        {
            if (trace.FinalPath == target && trace.Boundary == SplitPublicationBoundary.BeforeCommitBarrier) { reached = true; cts.Cancel(); }
            return Task.CompletedTask;
        };
        Exception? failure;
        try { failure = await Record.ExceptionAsync(() => ImageFormatService.ConvertImageAsync(source, target, token: cts.Token)); }
        finally { WindowsSplitOutputPublisher.TestHook.Value = prior; }
        Assert.True(reached); Assert.IsAssignableFrom<OperationCanceledException>(failure);
        Assert.Equal(identity, WindowsFileIdentity.Capture(target)); Assert.Equal(sentinel, File.ReadAllBytes(target)); Assert.Equal(sha, Sha(source));
        Assert.Single(Directory.GetFiles(output.RootDirectory));
        Save("editor-export-cancel", new { source, before = sha, after = Sha(source), target, reached, identity, finalIdentity = WindowsFileIdentity.Capture(target), sentinel = Convert.ToHexString(File.ReadAllBytes(target)), error = failure!.ToString(), files = Directory.GetFiles(output.RootDirectory) });
    }
    static string Sha(string p) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(p)));
    static void Save(string name, object value)
    {
        string root = Environment.GetEnvironmentVariable("LPB_R2_EVIDENCE") ?? throw new InvalidOperationException("R2 evidence root required");
        Directory.CreateDirectory(root); using var stream = new FileStream(Path.Combine(root, name + ".json"), FileMode.CreateNew);
        JsonSerializer.Serialize(stream, value, new JsonSerializerOptions { WriteIndented = true });
    }
}
