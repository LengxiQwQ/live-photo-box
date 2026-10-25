using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P8CapabilityQueryTests
{
    [Fact]
    public void CopyRequest_DoesNotDependOnMfOrTranscodeCapability()
    {
        var result = ProductCapabilityQuery.Assess(Requirement(VideoCodec.Copy), Runtime(
            Identity(NativeRuntimeOperationClass.VideoRemux, true),
            Identity(NativeRuntimeOperationClass.VideoTranscodeSdrH264, false)));
        Assert.Equal(ProductCapabilityAvailability.Available, result.Availability);
        Assert.Equal(NativeRuntimeOperationClass.VideoRemux, Assert.Single(result.Components).Operation);
        Assert.True(result.RequiresRequestValidation);
    }

    [Fact]
    public void CoarseH264Unavailable_DoesNotRejectSupportedSubsetOrPassthrough()
    {
        var result = ProductCapabilityQuery.Assess(Requirement(VideoCodec.H264), Runtime(
            Identity(NativeRuntimeOperationClass.VideoTranscodeSdrH264, false)));
        Assert.Equal(ProductCapabilityAvailability.Unknown, result.Availability);
        Assert.Equal(ProductCapabilityAvailability.Unknown, Assert.Single(result.Components).Availability);
        Assert.True(result.RequiresRequestValidation);
    }

    [Fact]
    public void MissingIdentity_IsUnknownRatherThanAssumedSupported()
    {
        var result = ProductCapabilityQuery.Assess(Requirement(VideoCodec.H264), Runtime());
        Assert.Equal(ProductCapabilityAvailability.Unknown, result.Availability);
        Assert.Null(Assert.Single(result.Components).RuntimeIdentity);
    }

    [Fact]
    public void RuntimeUnavailable_IsExplicitAndRetainsDiagnostic()
    {
        var runtime = new NativeRuntimeInfo(false, 0, null, null, null, 0, [], "ABI or deployment unavailable");
        var result = ProductCapabilityQuery.Assess(Requirement(VideoCodec.Copy), runtime);
        Assert.Equal(ProductCapabilityAvailability.Unavailable, result.Availability);
        Assert.Equal("ABI or deployment unavailable", result.Diagnostic);
    }

    [Fact]
    public void AvailableMediaBackends_DoNotPromiseWriterOrPreservationSupport()
    {
        var profile = OutputProfile.Create(TargetProtocol.SamsungMotionPhoto, OutputMediaFormat.HeicMp4);
        var result = ProductCapabilityQuery.Assess(profile.MediaRequirement, Runtime(
            Identity(NativeRuntimeOperationClass.HeicCodec, true),
            Identity(NativeRuntimeOperationClass.VideoRemux, true)), profile);
        Assert.Same(profile, result.OutputProfile);
        Assert.Equal(ProductCapabilityAvailability.Available, result.Availability);
        Assert.True(result.RequiresRequestValidation);
        Assert.Contains("writer validation still required", result.Diagnostic);
    }

    [Fact]
    public void MissingEncoder_DoesNotProhibitAlreadyCompatibleMedia()
    {
        var result = ProductCapabilityQuery.Assess(Requirement(VideoCodec.Hevc), Runtime(
            Identity(NativeRuntimeOperationClass.VideoTranscodeSdrHevc, false)));
        Assert.Equal(ProductCapabilityAvailability.Unavailable, Assert.Single(result.Components).Availability);
        Assert.Equal(ProductCapabilityAvailability.Unknown, result.Availability);
    }

    [Fact]
    public void Query_RejectsUnknownSplitSelectionBeforeNativeProbe() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ProductCapabilityQuery.Query((SplitMediaFormat)int.MaxValue));

    private static MediaFormatRequirement Requirement(VideoCodec codec) => new()
    {
        ImageContainer = ImageContainer.Unknown,
        VideoContainer = VideoContainer.Mp4,
        VideoCodec = codec
    };

    private static NativeRuntimeInfo Runtime(params NativeRuntimeCapabilityIdentity[] identities) =>
        new(true, NativeRuntime.SupportedAbiVersion, "synthetic-query", null, null, 0, identities, null);

    private static NativeRuntimeCapabilityIdentity Identity(NativeRuntimeOperationClass operation, bool available) =>
        new(operation, NativeRuntimeBackend.Unknown, NativeRuntimeCodec.Unknown, available, 0, false,
            "synthetic-query", "Observational capability; no conversion executed.");
}

