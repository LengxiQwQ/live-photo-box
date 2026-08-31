using LivePhotoBox.Interop;
using LivePhotoBox.Services.Protocols;
using Xunit;

namespace LivePhotoBox.Core.Tests;

[Trait("Category", "NativeDifferential")]
public sealed class SamsungSefDifferentialTests
{
    [Theory]
    [InlineData("jpg", 0L)]
    [InlineData("heic", 4096L)]
    public void BuildTrailer_IsByteIdenticalToManagedFallback(string imageType, long imageSize)
    {
        byte[] video = [0x00, 0x00, 0x00, 0x18, (byte)'f', (byte)'t', (byte)'y', (byte)'p', 0x69, 0x73, 0x6F, 0x6D];

        byte[] expected = SamsungMotionPhotoProtocol.BuildTrailerLegacy(video, imageType, imageSize);
        byte[]? actual = NativeSamsungSef.BuildTrailer(video, imageType, imageSize);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual);
    }

    [Fact]
    public void TryParse_LocatesMotionPhotoDataPayload()
    {
        byte[] imagePrefix = new byte[37];
        byte[] video = [0x01, 0x02, 0x03, 0x04, 0x05];
        byte[] trailer = SamsungMotionPhotoProtocol.BuildTrailerLegacy(video, "jpg");
        byte[] input = [.. imagePrefix, .. trailer];

        Assert.True(NativeSamsungSef.TryParse(input, out long videoOffset, out long videoSize, out string? error), error);
        Assert.Equal(imagePrefix.Length + 24L, videoOffset);
        Assert.Equal(video.Length, videoSize);
        Assert.Equal(video, input.AsSpan((int)videoOffset, (int)videoSize).ToArray());
    }
}
