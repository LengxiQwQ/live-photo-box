using System.Collections.Concurrent;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.Cryptography;
using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class NeutralHandoffResultTests
{
    [Theory]
    [InlineData("missing")]
    [InlineData("locked")]
    [InlineData("changed")]
    [InlineData("cancelled")]
    [InlineData("valid")]
    public async Task ProductionAuxiliaryVerificationReturnsExternalProblemsWithoutThrowing(string condition)
    {
        using var fixture = new TestFixtureWorkspace();
        using var workspace = new MediaWorkspace();
        string path = Path.Combine(fixture.RootDirectory, "auxiliary.jpg");
        byte[] bytes = [1, 3, 5, 7];
        File.WriteAllBytes(path, bytes);
        string sha = Convert.ToHexString(SHA256.HashData(bytes));
        var descriptor = new AuxiliaryMediaDescriptor
        {
            StableIdentity = "aux:verified", Semantic = "TestAuxiliary", OwnerIdentity = "primary:0",
            Relationship = "test:auxiliary", SourceLength = bytes.Length, SourceSha256 = sha,
            Representation = AuxiliaryRepresentation.Materialized,
            MaterializedArtifact = new MediaArtifact { Path = path, Kind = MediaArtifactKind.AuxiliaryItem,
                ByteLength = bytes.Length, Sha256 = sha }
        };
        if (condition == "missing") File.Delete(path);
        if (condition == "changed") File.WriteAllBytes(path, [8, 6, 4, 2]);
        using var held = condition == "locked" ? File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None) : null;
        using var cancellation = new CancellationTokenSource();
        if (condition == "cancelled") cancellation.Cancel();
        var method = typeof(NeutralMediaService).GetMethod("VerifyAuxiliaryHandoffAsync", BindingFlags.NonPublic | BindingFlags.Static)!;
        object[] arguments = { new[] { descriptor }, workspace, cancellation.Token };
        var events = new ConcurrentQueue<Exception>();
        EventHandler<FirstChanceExceptionEventArgs> observer = (_, e) => events.Enqueue(e.Exception);
        Exception? failure;
        AppDomain.CurrentDomain.FirstChanceException += observer;
        try { failure = await (Task<Exception?>)method.Invoke(null, arguments)!; }
        finally { AppDomain.CurrentDomain.FirstChanceException -= observer; }
        Assert.Empty(events);
        if (condition == "valid") Assert.Null(failure); else Assert.NotNull(failure);
        if (condition == "cancelled") Assert.IsAssignableFrom<OperationCanceledException>(failure);
        if (condition == "changed") Assert.Equal(new byte[] { 8, 6, 4, 2 }, File.ReadAllBytes(path));
    }
}
