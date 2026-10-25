using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public sealed class P8ControlledFallbackDiagnosticsTests
{
    [Theory]
    [InlineData("s1s", "?", false)]
    [InlineData("s1c", "libx264", true)]
    [InlineData("s1v", "libx264", true)]
    public void SyntheticFallbackDiagnostics_RetainSelectionCandidateAndActualIdentity(
        string stage, string encoder, bool candidate)
    {
        string output = stage == "s1v" ? "A8.8.1.1.1.L" : "?";
        var actual = NativeMediaService.ProjectVideoDiagnostics(VideoBackend.MinimalLibav,
            VideoHardwareMode.SoftwareForced, true,
            $"{stage}|9.0.1|{encoder}|iA8.8.1.1.1.L|o{output}",
            "MFComponentMissing: controlled x264 route; Synthetic diagnostic projection.");
        Assert.True(actual.IdentityValid);
        Assert.True(actual.HardwareFallbackOccurred);
        Assert.True(actual.MinimalLibavSelected);
        Assert.Equal(candidate, actual.OutputCandidateObserved);
        Assert.Equal("9.0.1", actual.BackendVersion);
        Assert.Contains("MFComponentMissing", actual.HardwareFallbackReason, StringComparison.Ordinal);
        if (encoder == "libx264") Assert.Equal("minimal-libav/libx264", actual.SelectedEncoder);
        else Assert.Equal("minimal-libav/selected-encoder-unknown", actual.SelectedEncoder);
        Assert.Contains("H.264;lumaDepth=8", actual.InputProfile, StringComparison.Ordinal);
        Assert.Equal(stage != "s1v", actual.OutputProfile == "Unknown");
    }

    [Fact]
    public void SyntheticSemanticSidecarDiagnostics_DoNotInventFallback()
    {
        var actual = NativeMediaService.ProjectVideoDiagnostics(VideoBackend.MinimalLibav,
            VideoHardwareMode.SoftwareForced, false,
            "s1v|9.0.1|libx264|iH8.8.1.1.1.L|oA8.8.1.1.1.L", "Native source-profile route; no fallback.");
        Assert.True(actual.IdentityValid);
        Assert.False(actual.HardwareFallbackOccurred);
        Assert.Equal("minimal-libav/P7-R4-HEVC-v2/libx264", actual.SelectedEncoder);
    }

    [Fact]
    public void SyntheticInvalidIdentity_IsNotPromotedByFallbackFlag()
    {
        var actual = NativeMediaService.ProjectVideoDiagnostics(VideoBackend.MinimalLibav,
            VideoHardwareMode.SoftwareForced, true, "invalid", "MFComponentMissing: Synthetic.");
        Assert.False(actual.IdentityValid);
        Assert.False(actual.OutputCandidateObserved);
        Assert.Empty(actual.BackendVersion);
    }
}
