using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Protocols.Cleaning;
using Xunit;

namespace LivePhotoBox.Core.Tests.Protocols;

/// <summary>
/// The Native staged-output registry is an ownership boundary.  These tests
/// exercise its managed admission gate independently of the Native writer so
/// duplicate, shadow, and role-confused records cannot become cleanup or
/// publication authority.
/// </summary>
public sealed class CleanerNativeRegistryValidationTests
{
    [Fact]
    public void DuplicateRegistryPath_IsRejectedBeforeJournalAdmission()
    {
        using var workspace = new MediaWorkspace();
        string staging = System.IO.Path.Combine(workspace.RootDirectory, "staging-registry");
        string image = System.IO.Path.Combine(staging, "stage-img.jpg");
        var record = Record(MediaArtifactKind.PrimaryImage, image);

        Assert.Throws<CleanerException>(() => SourceProtocolCleaner.ValidateNativeStagedRegistry(
            new[] { record, record },
            staging,
            image,
            null,
            SourceProtocol.OppoLivePhoto,
            requireExpectedOutputs: true));
    }

    [Fact]
    public void ShadowPathOutsideStagingNamespace_IsRejected()
    {
        using var workspace = new MediaWorkspace();
        string staging = System.IO.Path.Combine(workspace.RootDirectory, "staging-registry");
        string image = System.IO.Path.Combine(staging, "stage-img.jpg");
        string shadow = System.IO.Path.Combine(workspace.RootDirectory, "shadow.jpg");

        Assert.Throws<CleanerException>(() => SourceProtocolCleaner.ValidateNativeStagedRegistry(
            new[] { Record(MediaArtifactKind.PrimaryImage, shadow) },
            staging,
            image,
            null,
            SourceProtocol.OppoLivePhoto,
            requireExpectedOutputs: false));
    }

    [Fact]
    public void ExpectedPathWithWrongRole_IsRejected()
    {
        using var workspace = new MediaWorkspace();
        string staging = System.IO.Path.Combine(workspace.RootDirectory, "staging-registry");
        string image = System.IO.Path.Combine(staging, "stage-img.jpg");

        Assert.Throws<CleanerException>(() => SourceProtocolCleaner.ValidateNativeStagedRegistry(
            new[] { Record(MediaArtifactKind.MotionVideo, image) },
            staging,
            image,
            null,
            SourceProtocol.OppoLivePhoto,
            requireExpectedOutputs: true));
    }

    [Fact]
    public void UnexpectedAuxiliaryRole_IsRejected()
    {
        using var workspace = new MediaWorkspace();
        string staging = System.IO.Path.Combine(workspace.RootDirectory, "staging-registry");
        string image = System.IO.Path.Combine(staging, "stage-img.jpg");
        string auxiliary = System.IO.Path.Combine(staging, "stage-aux.bin");

        Assert.Throws<CleanerException>(() => SourceProtocolCleaner.ValidateNativeStagedRegistry(
            new[] { Record(MediaArtifactKind.PrimaryImage, image), Record(MediaArtifactKind.GainMap, auxiliary) },
            staging,
            image,
            null,
            SourceProtocol.OppoLivePhoto,
            requireExpectedOutputs: true));
    }

    [Fact]
    public void ExactPrimaryAndVideoBindings_AreAccepted()
    {
        using var workspace = new MediaWorkspace();
        string staging = System.IO.Path.Combine(workspace.RootDirectory, "staging-registry");
        string image = System.IO.Path.Combine(staging, "stage-img.jpg");
        string video = System.IO.Path.Combine(staging, "stage-vid.mov");

        SourceProtocolCleaner.ValidateNativeStagedRegistry(
            new[]
            {
                Record(MediaArtifactKind.PrimaryImage, image),
                Record(MediaArtifactKind.MotionVideo, video)
            },
            staging,
            image,
            video,
            SourceProtocol.AppleLivePhoto,
            requireExpectedOutputs: true);
    }

    private static NativeCleanService.CleanStagedOutputRecord Record(
        MediaArtifactKind role,
        string path)
        => new(
            role,
            uint.MaxValue,
            VolumeSerial: 1,
            FileIndex: (ulong)role + 1,
            FileSize: 1,
            LinkCount: 1,
            FinalPath: path);
}
