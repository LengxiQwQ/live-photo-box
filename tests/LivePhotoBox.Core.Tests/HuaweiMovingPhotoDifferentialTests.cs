using LivePhotoBox.Interop;
using LivePhotoBox.Services.Protocols;
using Xunit;

namespace LivePhotoBox.Core.Tests;

[Trait("Category", "NativeDifferential")]
public sealed class HuaweiMovingPhotoDifferentialTests
{
    [Fact]
    public void BuildTail_IsByteIdenticalToManagedFallback()
    {
        const int coverFrame = 12;
        const int totalFrames = 87;
        const long mp4Size = 12_345_678;

        byte[] expected = HuaweiMovingPhotoProtocol.BuildTail(
            coverFrame, totalFrames, mp4Size, tailPrefix: "v6_f", preferNative: false);

        Assert.True(NativeHuaweiMovingPhoto.TryBuildTail(
            coverFrame, totalFrames, mp4Size, 0, 0, "v6_f", out byte[] actual, out string? error), error);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void BuildTail_WithPreservedHistory_IsByteIdenticalToManagedFallback()
    {
        const int coverFrame = 3;
        const int totalFrames = 45;
        const long mp4Size = 987_654;
        const int originalCoverMs = 1_234;
        const int originalDurationMs = 3_456;

        byte[] expected = HuaweiMovingPhotoProtocol.BuildTail(
            coverFrame, totalFrames, mp4Size, originalCoverMs, originalDurationMs,
            tailPrefix: "v6_f", preferNative: false);

        Assert.True(NativeHuaweiMovingPhoto.TryBuildTail(
            coverFrame, totalFrames, mp4Size, originalCoverMs, originalDurationMs,
            "v6_f", out byte[] actual, out string? error), error);

        Assert.Equal(expected, actual);
    }
}
