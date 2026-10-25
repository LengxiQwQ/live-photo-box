using System.Security.Cryptography;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P8ExplicitMediaPreparationTests
{
    [Theory]
    [InlineData(OutputMediaFormat.JpegMp4)]
    [InlineData(OutputMediaFormat.JpegMov)]
    [Trait("Category", "DerivedRealSamples")]
    public async Task ExplicitOrdinaryPair_PreparesNeutralAndPublishesWithoutInventingSourceLivePair(OutputMediaFormat format)
    {
        string inputs = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P8_PLAIN_PAIR_ROOT")
            ?? throw new InvalidOperationException("LIVEPHOTOBOX_P8_PLAIN_PAIR_ROOT must identify the frozen, derived non-live image/video inputs.");
        string image = Path.Combine(inputs, "image.jpg");
        string video = Path.Combine(inputs, "video.mp4");
        Assert.True(File.Exists(image));
        Assert.True(File.Exists(video));
        string imageBefore = Hash(image), videoBefore = Hash(video);
        var inspector = new SourceInspector();
        var source = await inspector.InspectAsync(image, video);
        Assert.Equal(SourceProtocol.NonLive, source.Protocol);
        Assert.Null(source.MotionVideo);
        Assert.Null(source.PairingIdentifier);
        var selection = await LivePhotoMergeScanService.PrepareExplicitMediaSelectionAsync(image, video, CancellationToken.None);
        Assert.NotNull(selection);
        Assert.Equal(LivePhotoBox.Models.LivePhotoProtocolType.Unknown, selection.Protocol);
        Assert.Equal(LivePhotoBox.Models.LivePhotoDetectionMethod.ExplicitMediaSelection, selection.DetectionMethod);
        var profile = OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, format);
        using var workspace = new MediaWorkspace();
        var bundle = await new NeutralMediaService().CreateNeutralBundleAsync(image, video, workspace, profile.MediaRequirement);
        Assert.Equal(SourceProtocol.NonLive, bundle.SourceProvenance.Protocol);
        Assert.NotNull(bundle.MotionVideo);
        Assert.Equal(profile.MediaRequirement.VideoContainer, bundle.MotionVideo.VideoContainer);
        Assert.Equal(NeutralEvidenceState.NotApplicable, bundle.Semantics.Timing.EvidenceState);
        Assert.True(bundle.Semantics.Timing.DurationUs > 0);
        Assert.Contains(bundle.Manifest, row => row.Role == "MotionVideo" && row.ByteLength > 0 && row.Sha256.Length == 64);

        string output = Path.Combine(workspace.RootDirectory, "composed.jpg");
        var result = await LivePhotoMergeRunnerService.ProcessSinglePairAsync(image, video, "composed", 1,
            new LivePhotoMergeRunOptions { OutputDirectory = workspace.RootDirectory, OutputProfile = profile,
                OutputFilePath = output, OverwriteExisting = false }, workspace.RootDirectory, CancellationToken.None);
        Assert.True(result.IsSuccess, result.Details);
        Assert.True(File.Exists(output));
        Assert.Equal(imageBefore, Hash(image));
        Assert.Equal(videoBefore, Hash(video));
    }

    [Fact]
    public async Task ExplicitOrdinaryPair_MalformedCompanionCannotBecomeSourceAuthorityOrPublish()
    {
        string inputs = Environment.GetEnvironmentVariable("LIVEPHOTOBOX_P8_PLAIN_PAIR_ROOT")
            ?? throw new InvalidOperationException("Required derived inputs missing.");
        using var workspace = new MediaWorkspace();
        using var inputsWorkspace = new LivePhotoBox.Core.Tests.Support.TestFixtureWorkspace();
        string malformed = Path.Combine(inputsWorkspace.RootDirectory, "malformed.mp4");
        await File.WriteAllTextAsync(malformed, "not an MP4");
        string output = Path.Combine(workspace.RootDirectory, "must-not-exist.jpg");
        var result = await LivePhotoMergeRunnerService.ProcessSinglePairAsync(Path.Combine(inputs, "image.jpg"),
            malformed, "invalid", 1, new LivePhotoMergeRunOptions { OutputDirectory = workspace.RootDirectory,
                OutputProfile = OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4),
                OutputFilePath = output }, workspace.RootDirectory, CancellationToken.None);
        Assert.False(result.IsSuccess);
        Assert.False(File.Exists(output));
        Assert.Equal("not an MP4", await File.ReadAllTextAsync(malformed));
    }

    private static string Hash(string path) => Convert.ToHexString(SHA256.HashData(File.ReadAllBytes(path)));
}
