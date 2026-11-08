using System.Collections.Concurrent;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class CleanupOutputIsolationTests
{
    [Theory]
    [InlineData("success")]
    [InlineData("collision")]
    [InlineData("cancel-second")]
    [InlineData("foreign-same-bytes")]
    public async Task RelocationMovesOnlyPublishedObjectsAndRollsBackAnInterruptedPair(string scenario)
    {
        using var fixture = new TestFixtureWorkspace();
        using var scope = WorkspaceCleanupScope.Begin();
        string source = Path.Combine(fixture.RootDirectory, "source.jpg");
        File.WriteAllBytes(source, P9TargetTestInputs.Image);
        string sourceHash = Hash(source);
        var outputs = new List<ProductCommittedArtifact>();
        var identities = new List<WindowsFileIdentity>();
        foreach (string name in new[] { "image.jpg", "movie.mp4" })
        {
            string path = Path.Combine(fixture.RootDirectory, "output", name);
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var result = await WindowsSplitOutputPublisher.TryPublishSingleAsync(path, false,
                (stream, token) => Task.FromResult(TransactionStreamWriteAttempt.Write(stream, new byte[] { 1, 3, 5, 7 }, token)), null, CancellationToken.None);
            Assert.Null(result.Failure);
            outputs.Add(new() { Role = name, Path = path, Sha256 = result.Receipt!.Sha256, ByteLength = result.Receipt.ByteLength });
            identities.Add(result.Receipt.FileIdentity);
        }
        string? collision = null;
        string? relocatedOriginal = null;
        using var cancellation = new CancellationTokenSource();
        if (scenario == "foreign-same-bytes")
        {
            relocatedOriginal = outputs[0].Path + ".original";
            File.Move(outputs[0].Path, relocatedOriginal);
            File.WriteAllBytes(outputs[0].Path, [1, 3, 5, 7]);
        }
        CleanupOutputIsolation.BeforeMoveForTests.Value = (index, destination) =>
        {
            if (index != 1) return;
            if (scenario == "collision") { collision = destination; File.WriteAllText(destination, "foreign destination"); }
            if (scenario == "cancel-second") cancellation.Cancel();
        };
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        CleanupOutputIsolation.IsolationAttempt attempt;
        AppDomain.CurrentDomain.FirstChanceException += observer;
        try { attempt = await CleanupOutputIsolation.TryIsolateAsync(outputs, [source], Path.Combine(fixture.RootDirectory, "output"), cancellation.Token); }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observer; CleanupOutputIsolation.BeforeMoveForTests.Value = null; }
        Assert.Empty(events);
        Assert.Equal(sourceHash, Hash(source));
        if (scenario == "success")
        {
            Assert.Null(attempt.Failure); Assert.NotNull(attempt.Directory);
            for (int i = 0; i < outputs.Count; i++)
            {
                Assert.False(File.Exists(outputs[i].Path));
                Assert.Equal(identities[i], WindowsFileIdentity.Capture(attempt.Outputs[i].Path));
                Assert.Equal(outputs[i].Sha256, Hash(attempt.Outputs[i].Path));
                Assert.StartsWith(attempt.Directory! + Path.DirectorySeparatorChar, attempt.Outputs[i].Path, StringComparison.OrdinalIgnoreCase);
            }
        }
        else
        {
            Assert.NotNull(attempt.Failure); Assert.Null(attempt.Directory);
            Assert.Equal(outputs.Skip(scenario == "foreign-same-bytes" ? 1 : 0).Select(x => x.Path), attempt.Outputs.Select(x => x.Path));
            Assert.All(outputs, output => Assert.True(File.Exists(output.Path)));
            if (scenario != "foreign-same-bytes")
                for (int i = 0; i < outputs.Count; i++) Assert.Equal(identities[i], WindowsFileIdentity.Capture(outputs[i].Path));
            else
            {
                Assert.Equal(identities[0], WindowsFileIdentity.Capture(relocatedOriginal!));
                Assert.NotEqual(identities[0].FileIndex, WindowsFileIdentity.Capture(outputs[0].Path).FileIndex);
            }
            if (collision is not null) Assert.Equal("foreign destination", File.ReadAllText(collision));
            if (scenario == "cancel-second") Assert.IsAssignableFrom<OperationCanceledException>(attempt.Failure);
        }
    }
    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
