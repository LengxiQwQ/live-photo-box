using LivePhotoBox.Core.Tests.Support;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public class RepairPublishedIndexTests
{
    [Fact]
    [Trait("Category", "RealSamples")]
    public async Task PublishedAppleCopyRecordsBothActualMemberOutcomes()
    {
        using var workspace = new TestFixtureWorkspace();
        string image = Path.Combine(workspace.RootDirectory, "source.jpg");
        File.Copy(TestSampleResolver.ResolveSample("苹果-双文件.JPG"), image);
        File.Copy(TestSampleResolver.ResolveSample("苹果-双文件.MOV"), Path.Combine(workspace.RootDirectory, "renamed.mov"));
        var options = new LivePhotoBox.Models.RepairOptions
        { FixImageRotation = false, StripImageThumbnail = false, FixHeicOrientation = false, FixVideoRotation = false };
        var result = await LivePhotoRepairService.RepairImageFileAsync(image, Path.Combine(workspace.RootDirectory, "out"),
            false, false, options, default, copyPerfect: true);
        Assert.Equal("copied", result.Status);
        Assert.Equal("copied", result.ImageStatus);
        Assert.Equal("copied", result.VideoStatus);
        Assert.Equal(File.ReadAllBytes(image), File.ReadAllBytes(result.Image!));
        Assert.Equal(File.ReadAllBytes(Path.Combine(workspace.RootDirectory, "renamed.mov")), File.ReadAllBytes(result.Video!));
    }

    [Fact]
    public async Task OwnOutputsBeforeAndAfterFirstDiscoveryAreExcludedAndProtected()
    {
        using var workspace = new TestFixtureWorkspace();
        string source = Path.Combine(workspace.RootDirectory, "source.jpg");
        string before = Path.Combine(workspace.RootDirectory, "before.jpg");
        string after = Path.Combine(workspace.RootDirectory, "after.jpg");
        File.WriteAllBytes(source, P9TargetTestInputs.Image);
        File.Copy(source, before);
        using (var batch = new RepairBatchContext())
        {
            batch.RecordPublished(before);
            Assert.Equal(source, Assert.Single(await batch.CandidatesAsync(source, default)).Path);
            File.Copy(source, after);
            batch.RecordPublished(after);
            Directory.SetLastWriteTimeUtc(workspace.RootDirectory, DateTime.UtcNow.AddSeconds(2));
            Assert.Equal(source, Assert.Single(await batch.CandidatesAsync(source, default)).Path);
            Assert.Equal(1, batch.DiscoveryInspections);
            Assert.Throws<IOException>(() => File.WriteAllBytes(before, new byte[] { 1 }));
            Assert.Throws<IOException>(() => File.Move(after, after + ".moved"));
        }
        File.Move(after, after + ".moved");
        Assert.Equal(P9TargetTestInputs.Image, File.ReadAllBytes(source));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExternalAdditionOrRenameStillInvalidatesDirectoryWithOwnOutputs(bool rename)
    {
        using var workspace = new TestFixtureWorkspace();
        string source = Path.Combine(workspace.RootDirectory, "source.jpg");
        string own = Path.Combine(workspace.RootDirectory, "own.jpg");
        string notes = Path.Combine(workspace.RootDirectory, "notes.txt");
        File.WriteAllBytes(source, P9TargetTestInputs.Image);
        File.Copy(source, own);
        File.WriteAllText(notes, "external non-media file");
        using var batch = new RepairBatchContext();
        batch.RecordPublished(own);
        await batch.CandidatesAsync(source, default);
        if (rename) File.Move(notes, notes + ".renamed");
        else File.Copy(source, Path.Combine(workspace.RootDirectory, "external.jpg"));
        Directory.SetLastWriteTimeUtc(workspace.RootDirectory, DateTime.UtcNow.AddSeconds(2));
        await Assert.ThrowsAsync<IOException>(() => batch.CandidatesAsync(source, default));
    }
}
