using System.Security.Cryptography;
using LivePhotoBox.Interop;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P9TargetPublicationTests
{
    private static TargetValidationExpectations Expected => new(2, 0, 2, 0);
    private static async Task WriteValid(Stream stream, CancellationToken token) => await stream.WriteAsync(P9TargetTestInputs.V2(), token);

    [Fact]
    public async Task ValidationReopensFlushedReadOnlyStageBeforePublishingAndExpiresItsViews()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(ValidationReopensFlushedReadOnlyStageBeforePublishingAndExpiresItsViews));
        string final = Path.Combine(folder.Path, "target.jpg");
        byte[] payload = P9TargetTestInputs.V2(); Stream? writer = null; FileStream? view = null;
        WindowsSplitOutputPublisher.TargetPublicationStage? retained = null;
        using var hook = new ReaderHook((stage, reader) =>
        {
            Assert.False(File.Exists(final)); Assert.Throws<ObjectDisposedException>(() => writer!.WriteByte(1));
            retained = stage; view = reader; Assert.False(reader.CanWrite);
            using var bytes = new MemoryStream(); reader.CopyTo(bytes); Assert.Equal(payload, bytes.ToArray());
            Assert.Equal(payload.Length, stage.ByteLength); Assert.Equal(Convert.ToHexString(SHA256.HashData(payload)), stage.Sha256);
        });
        var receipt = await WindowsSplitOutputPublisher.PublishTargetAsync(final, false,
            async (stream, token) => { writer = stream; await stream.WriteAsync(payload, token); }, Expected, CancellationToken.None);
        Assert.NotNull(retained); Assert.Equal(payload, await File.ReadAllBytesAsync(final)); Assert.Equal(payload.Length, receipt.ByteLength);
        Assert.Throws<ObjectDisposedException>(() => retained.OpenRead()); Assert.False(view!.CanRead); Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task RejectedValidationDoesNotPublishOrAlterExistingTarget(bool existing)
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(RejectedValidationDoesNotPublishOrAlterExistingTarget) + existing);
        string final = Path.Combine(folder.Path, "target.jpg"); byte[] original = [11, 22, 33, 44];
        if (existing) await File.WriteAllBytesAsync(final, original);
        await Assert.ThrowsAsync<TargetValidationException>(() => WindowsSplitOutputPublisher.PublishTargetAsync(final, existing,
            async (stream, token) => await stream.WriteAsync(new byte[] { 99, 98, 97 }, token), Expected, CancellationToken.None));
        if (existing) Assert.Equal(original, await File.ReadAllBytesAsync(final)); else Assert.False(File.Exists(final));
        Assert.Equal(existing ? 1 : 0, Directory.GetFiles(folder.Path).Length);
    }

    [Fact]
    public async Task GuardBlocksActualForeignWriteDeleteAndRenameWhileFreshReaderIsOpen()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(GuardBlocksActualForeignWriteDeleteAndRenameWhileFreshReaderIsOpen));
        using var hook = new ReaderHook((stage, reader) =>
        {
            Assert.Throws<IOException>(() => File.WriteAllBytes(stage.Path, [99]));
            Assert.Throws<IOException>(() => File.Delete(stage.Path));
            Assert.Throws<IOException>(() => File.Move(stage.Path, stage.Path + ".foreign"));
            Assert.Equal(P9TargetTestInputs.V2().Length, reader.Length);
        });
        string final = Path.Combine(folder.Path, "target.jpg");
        await WindowsSplitOutputPublisher.PublishTargetAsync(final, false, WriteValid, Expected, CancellationToken.None);
        Assert.Equal(P9TargetTestInputs.V2(), await File.ReadAllBytesAsync(final)); Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task CancellationDuringValidationClosesAllReadersAndRemovesOnlyOwnedStage()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(CancellationDuringValidationClosesAllReadersAndRemovesOnlyOwnedStage));
        string final = Path.Combine(folder.Path, "target.jpg"), sentinel = Path.Combine(folder.Path, "foreign.bin");
        await File.WriteAllBytesAsync(sentinel, [1, 2, 3]); using var cancellation = new CancellationTokenSource(); FileStream? view = null;
        using var hook = new ReaderHook((stage, reader) => { view = reader; cancellation.Cancel(); });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => WindowsSplitOutputPublisher.PublishTargetAsync(final, false, WriteValid, Expected, cancellation.Token));
        Assert.False(view!.CanRead); Assert.False(File.Exists(final)); Assert.Equal(new byte[] { 1, 2, 3 }, File.ReadAllBytes(sentinel)); Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task LateForeignArrivalCannotBeReplacedAfterValidation()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(LateForeignArrivalCannotBeReplacedAfterValidation));
        string final = Path.Combine(folder.Path, "target.jpg"); byte[] foreign = [81, 82, 83];
        using var hook = new ReaderHook((stage, reader) => File.WriteAllBytes(final, foreign));
        await Assert.ThrowsAsync<IOException>(() => WindowsSplitOutputPublisher.PublishTargetAsync(final, false, WriteValid, Expected, CancellationToken.None));
        Assert.Equal(foreign, File.ReadAllBytes(final)); Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task ValidatedOverwriteKeepsRegisteredObjectAndCommitsExactNewBytes()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(ValidatedOverwriteKeepsRegisteredObjectAndCommitsExactNewBytes));
        string final = Path.Combine(folder.Path, "target.jpg"); byte[] original = [41, 42, 43];
        var first = await WindowsSplitOutputPublisher.PublishSingleAsync(final, false,
            async (stream, token) => await stream.WriteAsync(original, token), null, CancellationToken.None);
        bool observed = false; using var hook = new ReaderHook((stage, reader) => { Assert.Equal(original, File.ReadAllBytes(final)); observed = true; });
        var second = await WindowsSplitOutputPublisher.PublishTargetAsync(final, true, WriteValid, Expected, CancellationToken.None);
        Assert.True(observed); Assert.Equal(first.FileIdentity.VolumeSerialNumber, second.FileIdentity.VolumeSerialNumber);
        Assert.Equal(first.FileIdentity.FileIndex, second.FileIdentity.FileIndex); Assert.Equal(P9TargetTestInputs.V2(), File.ReadAllBytes(final)); Assert.Single(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public void MissingValidationExpectationsRejectBeforeOutputCreation()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(MissingValidationExpectationsRejectBeforeOutputCreation));
        Assert.Throws<ArgumentNullException>(() => { _ = WindowsSplitOutputPublisher.PublishTargetAsync(Path.Combine(folder.Path, "target.jpg"), false, WriteValid, null!, CancellationToken.None); });
        Assert.Empty(Directory.GetFiles(folder.Path));
    }

    [Fact]
    public async Task ClosedFreshReaderCannotBypassValidationOrPublish()
    {
        using var folder = new P9TargetTestInputs.Folder(nameof(ClosedFreshReaderCannotBypassValidationOrPublish));
        using var hook = new ReaderHook((stage, reader) => reader.Dispose()); string final = Path.Combine(folder.Path, "target.jpg");
        await Assert.ThrowsAsync<ObjectDisposedException>(() => WindowsSplitOutputPublisher.PublishTargetAsync(final, false, WriteValid, Expected, CancellationToken.None));
        Assert.False(File.Exists(final)); Assert.Empty(Directory.GetFiles(folder.Path));
    }

    internal sealed class ReaderHook : IDisposable
    {
        private readonly Action<WindowsSplitOutputPublisher.TargetPublicationStage, FileStream>? _previous;
        internal ReaderHook(Action<WindowsSplitOutputPublisher.TargetPublicationStage, FileStream> hook)
        { _previous = NativeTargetValidator.ReaderOpenedForTests.Value; NativeTargetValidator.ReaderOpenedForTests.Value = hook; }
        public void Dispose() => NativeTargetValidator.ReaderOpenedForTests.Value = _previous;
    }
}
