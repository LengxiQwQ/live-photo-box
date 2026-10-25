using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P8ProductSelectionTests
{
    // Frozen persisted Windows/CLI selections, including the distinct split/merge ordinals.
    [Theory]
    [InlineData(1, 0, TargetProtocol.GoogleMicroVideoV1, ImageContainer.Jpeg, VideoContainer.Mp4, VideoCodec.Copy)]
    [InlineData(2, 1, TargetProtocol.GoogleMotionPhotoV2, ImageContainer.Jpeg, VideoContainer.Mov, VideoCodec.Copy)]
    [InlineData(2, 3, TargetProtocol.GoogleMotionPhotoV2, ImageContainer.Heic, VideoContainer.Mov, VideoCodec.Copy)]
    [InlineData(3, 0, TargetProtocol.OppoLivePhoto, ImageContainer.Jpeg, VideoContainer.Mp4, VideoCodec.Copy)]
    [InlineData(4, 0, TargetProtocol.VivoLivePhoto, ImageContainer.Jpeg, VideoContainer.Mp4, VideoCodec.Copy)]
    [InlineData(5, 2, TargetProtocol.SamsungMotionPhoto, ImageContainer.Heic, VideoContainer.Mp4, VideoCodec.Copy)]
    [InlineData(6, 4, TargetProtocol.HuaweiMovingPhoto, ImageContainer.Heic, VideoContainer.Mp4, VideoCodec.Hevc)]
    public void PersistedMergeSelectionsRetainTheirMeaning(int target, int format, TargetProtocol expectedTarget,
        ImageContainer image, VideoContainer video, VideoCodec codec)
    {
        var profile = ProductSelectionAdapter.Merge(target, format);
        Assert.Equal(expectedTarget, profile.TargetProtocol);
        Assert.Equal(image, profile.MediaRequirement.ImageContainer);
        Assert.Equal(video, profile.MediaRequirement.VideoContainer);
        Assert.Equal(codec, profile.MediaRequirement.VideoCodec);
    }

    [Theory]
    [InlineData(-1, 0)]
    [InlineData(7, 0)]
    [InlineData(2, -1)]
    [InlineData(2, 5)]
    [InlineData(3, 1)]
    [InlineData(4, 3)]
    [InlineData(5, 4)]
    [InlineData(6, 1)]
    public void UnknownOrIncompatibleMergeSelectionsNeverFallBack(int target, int format) =>
        Assert.ThrowsAny<ArgumentException>(() => ProductSelectionAdapter.Merge(target, format));

    [Theory]
    [InlineData(0, ImageContainer.Unknown, VideoContainer.Unknown, VideoCodec.Copy)]
    [InlineData(1, ImageContainer.Jpeg, VideoContainer.Mov, VideoCodec.Hevc)]
    [InlineData(2, ImageContainer.Heic, VideoContainer.Mov, VideoCodec.Hevc)]
    [InlineData(3, ImageContainer.Jpeg, VideoContainer.Mp4, VideoCodec.H264)]
    public void SplitSelectionsRetainProtocolFreeMediaMeaning(int format,
        ImageContainer image, VideoContainer video, VideoCodec codec)
    {
        var requirement = SplitMediaRequirements.Get(ProductSelectionAdapter.Split(0, format));
        Assert.Equal(image, requirement.ImageContainer);
        Assert.Equal(video, requirement.VideoContainer);
        Assert.Equal(codec, requirement.VideoCodec);
    }

    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, 2)]
    [InlineData(2, 3)]
    [InlineData(-1, 0)]
    public void SplitAdapterRejectsTargetWriters(int target, int format) =>
        Assert.Throws<NotSupportedException>(() => ProductSelectionAdapter.Split(target, format));

    [Fact]
    public async Task InvalidTypedSplitRejectsBeforeReadingSourceOrCreatingOutput()
    {
        string path = Path.Combine(Path.GetTempPath(), "lpb-invalid-typed-selection");
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() => LivePhotoSplitService.SplitAsync(
            Path.Combine(path, "missing.jpg"), path, (SplitMediaFormat)int.MaxValue, CancellationToken.None));
        Assert.False(Directory.Exists(path));
    }

    [Fact]
    public void TypedProfileRejectsUndefinedEnumsAndCannotBeReconfiguredWithInvalidMedia()
    {
        Assert.Throws<ArgumentException>(() => OutputProfile.Create((TargetProtocol)int.MaxValue, OutputMediaFormat.JpegMp4));
        Assert.Throws<ArgumentException>(() => OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, (OutputMediaFormat)int.MaxValue));
        Assert.Throws<ArgumentException>(() => OutputProfile.Create(TargetProtocol.OppoLivePhoto, OutputMediaFormat.HeicMov));
        var profile = OutputProfile.Create(TargetProtocol.HuaweiMovingPhoto, OutputMediaFormat.HeicMp4Hevc);
        var changedRequirement = profile.MediaRequirement with { ImageContainer = ImageContainer.Jpeg };
        Assert.NotEqual(changedRequirement, profile.MediaRequirement);
        Assert.Equal(ImageContainer.Heic, profile.MediaRequirement.ImageContainer);
    }

    [Fact]
    public async Task MergeRejectsMissingProfileBeforeCreatingOutputDirectory()
    {
        string output = Path.Combine(Path.GetTempPath(), "lpb-p8-null-profile-" + Guid.NewGuid().ToString("N"));
        var options = new LivePhotoMergeRunOptions { OutputDirectory = output, OutputProfile = null! };
        using var pause = new ManualResetEventSlim(true);
        await Assert.ThrowsAsync<ArgumentNullException>(() => LivePhotoMergeRunnerService.RunAsync(
            [], options, pause, CancellationToken.None, null, null));
        Assert.False(Directory.Exists(output));
    }

    [Fact]
    public async Task PrecancelledMergeDoesNotCreateOutputDirectory()
    {
        string output = Path.Combine(Path.GetTempPath(), "lpb-p8-cancelled-" + Guid.NewGuid().ToString("N"));
        var options = new LivePhotoMergeRunOptions { OutputDirectory = output,
            OutputProfile = OutputProfile.Create(TargetProtocol.GoogleMotionPhotoV2, OutputMediaFormat.JpegMp4) };
        using var pause = new ManualResetEventSlim(true);
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAsync<OperationCanceledException>(() => LivePhotoMergeRunnerService.RunAsync(
            [], options, pause, cancellation.Token, null, null));
        Assert.False(Directory.Exists(output));
    }
}
