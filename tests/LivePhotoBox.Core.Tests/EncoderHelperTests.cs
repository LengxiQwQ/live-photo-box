using LivePhotoBox.Services;
using System.Reflection;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class EncoderHelperTests
{
    [Theory]
    [InlineData("h264_nvenc", 19)]
    [InlineData("hevc_nvenc", 21)]
    public void NvencParameters_MatchV221QualityMode(string encoder, int expectedQuality)
    {
        string parameters = EncoderHelper.GetHardwareEncoderParams(encoder, (19, 21));

        Assert.Contains("-rc:v vbr_hq", parameters);
        Assert.Contains($"-cq:v {expectedQuality}", parameters);
    }

    [Fact]
    public void RepairNvencParameters_MatchV221RcMode()
    {
        string parameters = EncoderHelper.GetHardwareEncoderParams("h264_nvenc", (13, 14));

        Assert.Contains("-rc:v vbr_hq", parameters);
    }
}
