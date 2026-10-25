using System.Security.Cryptography;
using LivePhotoBox.Interop;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

/// <summary>Real Windows transaction/transport checks, not target-protocol conformance.</summary>
public sealed class P9TargetPublicationTests
{
    private static readonly byte[] Payload = Enumerable.Range(0, 16391).Select(i => (byte)(i * 31)).ToArray();

    [Fact]
    public async Task ValidationReopensFlushedReadOnlyStageBeforePublishingAndExpiresItsViews()
    {
        using var folder = new Folder();
        string final = Path.Combine(folder.Path, "target.bin");
        Stream? oldWriter = null;
        FileStream? retainedReader = null;
        WindowsSplitOutputPublisher.TargetPublicationStage? retainedStage = null;
        bool observed = false;
        var receipt = await WindowsSplitOutputPublisher.PublishTargetAsync(final, false,
            async (output, token) => { oldWriter = output; await output.WriteAsync(Payload, token); },
            async (stage, token) =>
            {
                Assert.False(File.Exists(final));
                Assert.Throws<ObjectDisposedException>(() => oldWriter!.WriteByte(1));
                retainedStage = stage;
                retainedReader = stage.OpenRead();
                Assert.False(retainedReader.CanWrite);
                using var bytes = new MemoryStream();
                await retainedReader.CopyToAsync(bytes, token);
                Assert.Equal(Payload, bytes.ToArray());
                Assert.Equal(Payload.Length, stage.ByteLength);
                Assert.Equal(Convert.ToHexString(SHA256.HashData(Payload)), stage.Sha256);
                observed = true;
            }, CancellationToken.None);
        Assert.True(observed);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(final));
        Assert.Equal(Payload.Length, receipt.ByteLength);
        Assert.Throws<ObjectDisposedException>(() => retainedStage!.OpenRead());
        Assert.False(retainedReader!.CanRead);
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RejectedValidationDoesNotPublishOrAlterExistingTarget(bool existing)
    {
        using var folder = new Folder();
        string final = Path.Combine(folder.Path, "target.bin");
        byte[] foreign = [11, 22, 33, 44];
        if (existing) await File.WriteAllBytesAsync(final, foreign);
        await Assert.ThrowsAsync<InvalidDataException>(() => WindowsSplitOutputPublisher.PublishTargetAsync(
            final, existing, WritePayload,
            (stage, token) => throw new InvalidDataException("Deliberate transaction validation rejection."),
            CancellationToken.None));
        if (existing) Assert.Equal(foreign, await File.ReadAllBytesAsync(final));
        else Assert.False(File.Exists(final));
        Assert.Equal(existing ? 1 : 0, Directory.GetFiles(folder.Path).Length);
    }

    [Fact]
    public async Task GuardBlocksActualForeignWriteDeleteAndRenameWhileFreshReaderIsOpen()
    {
        using var folder = new Folder();
        string final = Path.Combine(folder.Path, "target.bin");
        await WindowsSplitOutputPublisher.PublishTargetAsync(final, false, WritePayload,
            (stage, token) =>
            {
                using var reader = stage.OpenRead();
                Assert.Throws<IOException>(() => File.WriteAllBytes(stage.Path, [99]));
                Assert.Throws<IOException>(() => File.Delete(stage.Path));
                Assert.Throws<IOException>(() => File.Move(stage.Path, stage.Path + ".foreign"));
                Assert.Equal(Payload.Length, reader.Length);
                return Task.CompletedTask;
            }, CancellationToken.None);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(final));
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task CancellationDuringValidationClosesAllReadersAndRemovesOnlyOwnedStage()
    {
        using var folder = new Folder();
        string final = Path.Combine(folder.Path, "target.bin");
        string sentinel = Path.Combine(folder.Path, "foreign.bin");
        await File.WriteAllBytesAsync(sentinel, [1, 2, 3]);
        using var cancellation = new CancellationTokenSource();
        FileStream? leaked = null;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsSplitOutputPublisher.PublishTargetAsync(
            final, false, WritePayload, (stage, token) =>
            {
                leaked = stage.OpenRead();
                cancellation.Cancel();
                return Task.CompletedTask;
            }, cancellation.Token));
        Assert.False(leaked!.CanRead);
        Assert.False(File.Exists(final));
        Assert.Equal(new byte[] { 1, 2, 3 }, await File.ReadAllBytesAsync(sentinel));
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task LateForeignArrivalCannotBeReplacedAfterValidation()
    {
        using var folder = new Folder();
        string final = Path.Combine(folder.Path, "target.bin");
        byte[] foreign = [81, 82, 83];
        await Assert.ThrowsAsync<IOException>(() => WindowsSplitOutputPublisher.PublishTargetAsync(final, false,
            WritePayload, async (stage, token) =>
            {
                using var reader = stage.OpenRead();
                await File.WriteAllBytesAsync(final, foreign, token);
            }, CancellationToken.None));
        Assert.Equal(foreign, await File.ReadAllBytesAsync(final));
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task ValidatedOverwriteKeepsRegisteredObjectAndCommitsExactNewBytes()
    {
        using var folder = new Folder();
        string final = Path.Combine(folder.Path, "target.bin");
        byte[] original = [41, 42, 43];
        var first = await WindowsSplitOutputPublisher.PublishSingleAsync(final, false,
            async (stream, token) => await stream.WriteAsync(original, token), null, CancellationToken.None);
        bool validated = false;
        var second = await WindowsSplitOutputPublisher.PublishTargetAsync(final, true, WritePayload,
            async (stage, token) =>
            {
                Assert.Equal(original, await File.ReadAllBytesAsync(final, token));
                using var reader = stage.OpenRead();
                using var bytes = new MemoryStream();
                await reader.CopyToAsync(bytes, token);
                Assert.Equal(Payload, bytes.ToArray());
                validated = true;
            }, CancellationToken.None);
        Assert.True(validated);
        Assert.Equal(first.FileIdentity.VolumeSerialNumber, second.FileIdentity.VolumeSerialNumber);
        Assert.Equal(first.FileIdentity.FileIndex, second.FileIdentity.FileIndex);
        Assert.Equal(Payload, await File.ReadAllBytesAsync(final));
        Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void MissingValidationDelegateRejectsBeforeOutputCreation()
    {
        using var folder = new Folder();
        string final = Path.Combine(folder.Path, "target.bin");
        Assert.Throws<ArgumentNullException>(() =>
        {
            _ = WindowsSplitOutputPublisher.PublishTargetAsync(
                final, false, WritePayload, null!, CancellationToken.None);
        });
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    private static async Task WritePayload(Stream output, CancellationToken token) =>
        await output.WriteAsync(Payload, token);

    private sealed class Folder : IDisposable
    {
        internal string Path { get; }
        internal Folder([System.Runtime.CompilerServices.CallerMemberName] string testName = "")
        {
            Path = System.IO.Path.GetFullPath(System.IO.Path.Combine(
                System.IO.Path.GetTempPath(), "LivePhotoBox", "P9-target-publication", testName));
            if (Directory.Exists(Path))
                throw new IOException("Previous test residue must be inspected before reusing " + Path);
            Directory.CreateDirectory(Path);
        }
        public void Dispose()
        {
            // Only this fixture's explicitly created directory is cleaned; never
            // traverse a redirecting child or an unexpected directory tree.
            if ((File.GetAttributes(Path) & FileAttributes.ReparsePoint) != 0 ||
                Directory.GetDirectories(Path).Length != 0)
                throw new IOException("Unexpected fixture directory structure: " + Path);
            foreach (string file in Directory.GetFiles(Path)) File.Delete(file);
            Directory.Delete(Path);
        }
    }
}
