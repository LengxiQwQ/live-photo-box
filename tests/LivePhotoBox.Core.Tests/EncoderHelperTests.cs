using LivePhotoBox.Services;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class EncoderHelperTests
{
    [Theory]
    [InlineData("h264_nvenc", 19)]
    [InlineData("hevc_nvenc", 21)]
    public void NvencParameters_UseFfmpeg9VbrMode(string encoder, int expectedQuality)
    {
        string parameters = EncoderHelper.GetHardwareEncoderParams(encoder, (19, 21));

        Assert.Contains("-rc:v vbr", parameters);
        Assert.DoesNotContain("vbr_hq", parameters, StringComparison.OrdinalIgnoreCase);
        Assert.Contains($"-cq:v {expectedQuality}", parameters);
    }

    [Fact]
    public void RepairNvencParameters_UseSameCurrentRcMode()
    {
        string parameters = EncoderHelper.GetHardwareEncoderParams("h264_nvenc", (13, 14));

        Assert.Contains("-rc:v vbr", parameters);
        Assert.DoesNotContain("vbr_hq", parameters, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void NvencApiMismatch_FallsBackToSoftware()
    {
        const string error = "Driver does not support the required nvenc API version. "
            + "The minimum required Nvidia driver for nvenc is 570.0 or newer.";

        Assert.True(VideoTranscodeService.ShouldFallbackToSoftware(error));
    }
}
