using LivePhotoBox.Services;
using System.Reflection;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class EncoderHelperTests
{
    [Theory]
    [InlineData("h264_nvenc", true)]
    [InlineData("hevc_qsv", true)]
    [InlineData("h264_amf", true)]
    [InlineData("hevc_vaapi", true)]
    [InlineData("libx264", false)]
    [InlineData("libx265", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsHardwareEncoder_IdentifiesKnownHardwareEncoders(string? encoder, bool expected)
    {
        Assert.Equal(expected, EncoderHelper.IsHardwareEncoder(encoder!));
    }

    [Theory]
    [InlineData("h264_nvenc", "hevc_nvenc")]
    [InlineData("hevc_qsv", "h264_qsv")]
    [InlineData("h264_amf", "hevc_amf")]
    [InlineData("unknown_codec", null)]
    public void DeriveCrossCodecEncoder_MapsAccurately(string input, string? expected)
    {
        Assert.Equal(expected, EncoderHelper.DeriveCrossCodecEncoder(input));
    }

    [Fact]
    public void IsEncoderAvailable_RebuiltMode_ReturnsFalseForExternalFfmpeg()
    {
        Assert.False(EncoderHelper.IsEncoderAvailable("h264_nvenc"));
        Assert.False(EncoderHelper.IsEncoderAvailable("libx264"));
    }
}
