using System;
using System.IO;
using System.Security.Cryptography;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P4PublicationRegressionTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Publication_ChangedContentOnSameObject_RejectsAndCleansOwnedStage(bool changeLength)
    {
        using var workspace = new TestFixtureWorkspace();
        string staged = Path.Combine(workspace.RootDirectory, "vid-conv-rewrite.mp4");
        string final = Path.Combine(workspace.RootDirectory, "final.mp4");
        MediaArtifact artifact = CreateArtifact(staged);
        File.WriteAllBytes(staged, changeLength ? [9, 8, 7] : [9, 8, 7, 6]);
        Assert.Equal(artifact.FileIdentity, WindowsFileIdentity.Capture(staged));

        Assert.Throws<IOException>(() => VideoTranscodeService.PublishConverterArtifactNoReplace(artifact, final));

        Assert.False(File.Exists(final));
        Assert.Empty(Directory.GetFiles(workspace.RootDirectory));
    }

    [Fact]
    public void Publication_ReplacedStage_LeavesForeignObjectUntouched()
    {
        using var workspace = new TestFixtureWorkspace();
        string staged = Path.Combine(workspace.RootDirectory, "vid-conv-replaced.mp4");
        string retained = Path.Combine(workspace.RootDirectory, "retained.mp4");
        string final = Path.Combine(workspace.RootDirectory, "final.mp4");
        MediaArtifact artifact = CreateArtifact(staged);
        File.Move(staged, retained);
        File.WriteAllBytes(staged, [9, 8, 7, 6]);

        Assert.ThrowsAny<Exception>(() => VideoTranscodeService.PublishConverterArtifactNoReplace(artifact, final));

        Assert.Equal(new byte[] { 9, 8, 7, 6 }, File.ReadAllBytes(staged));
        Assert.Equal(new byte[] { 1, 2, 3, 4 }, File.ReadAllBytes(retained));
        Assert.False(File.Exists(final));
    }

    [Fact]
    public void Publication_PinsCallerDirectoryAgainstRename_AndReleasesLease()
    {
        using var workspace = new TestFixtureWorkspace();
        string parent = Path.Combine(workspace.RootDirectory, "parent");
        string moved = Path.Combine(workspace.RootDirectory, "moved");
        Directory.CreateDirectory(parent);
        string staged = Path.Combine(parent, "vid-conv-directory.mp4");
        string final = Path.Combine(parent, "final.mp4");
        MediaArtifact artifact = CreateArtifact(staged);
        bool reached = false;
        Action? previous = VideoTranscodeService.PublicationLeaseTestHook.Value;
        try
        {
            VideoTranscodeService.PublicationLeaseTestHook.Value = () =>
            {
                reached = true;
                Assert.Throws<IOException>(() => Directory.Move(parent, moved));
            };
            VideoTranscodeService.PublishConverterArtifactNoReplace(artifact, final);
        }
        finally
        {
            VideoTranscodeService.PublicationLeaseTestHook.Value = previous;
        }

        Assert.True(reached);
        Assert.Equal(artifact.Sha256, Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(final))));
        Directory.Move(parent, moved);
        File.Delete(Path.Combine(moved, "final.mp4"));
        Directory.Delete(moved);
    }

    [Fact]
    public void Registration_TargetConflict_DoesNotLoseEitherOwnershipReceipt()
    {
        using var workspace = new MediaWorkspace();
        string staged = workspace.AllocateFilePath("vid-conv", ".mp4");
        string final = workspace.AllocateFilePath("existing", ".mp4");
        MediaArtifact first = CreateArtifact(staged);
        MediaArtifact second = CreateArtifact(final);
        Assert.True(workspace.RegisterCreatedFile(staged, first.FileIdentity!, first.ByteLength, first.Sha256, "test"));
        Assert.True(workspace.RegisterCreatedFile(final, second.FileIdentity!, second.ByteLength, second.Sha256, "test"));

        Assert.Throws<IOException>(() => workspace.RegisterPublishedFile(
            new PublishedOwnedFile(final, first.FileIdentity!, first.ByteLength, first.Sha256!), true));

        Assert.True(workspace.HasRegisteredFile(staged, first.FileIdentity!));
        Assert.True(workspace.HasRegisteredFile(final, second.FileIdentity!));
    }

    private static MediaArtifact CreateArtifact(string path)
    {
        byte[] bytes = [1, 2, 3, 4];
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        output.Write(bytes);
        output.Flush(true);
        return new MediaArtifact
        {
            Path = path,
            Kind = MediaArtifactKind.MotionVideo,
            ByteLength = bytes.Length,
            Sha256 = Convert.ToHexString(SHA256.HashData(bytes)),
            FileIdentity = WindowsFileIdentity.Capture(output.SafeFileHandle)
        };
    }
}
