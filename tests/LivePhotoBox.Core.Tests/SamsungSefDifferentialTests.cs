using LivePhotoBox.Interop;
using Xunit;

namespace LivePhotoBox.Core.Tests;

[Trait("Category", "NativeContract")]
public sealed class SamsungSefDifferentialTests
{
    private static byte[] CreateMinimalMp4()
    {
        return [
            0x00, 0x00, 0x00, 0x10, (byte)'f', (byte)'t', (byte)'y', (byte)'p',
            (byte)'i', (byte)'s', (byte)'o', (byte)'m', 0x00, 0x00, 0x00, 0x00,
            0x00, 0x00, 0x00, 0x08, (byte)'m', (byte)'d', (byte)'a', (byte)'t',
            0x00, 0x00, 0x00, 0x08, (byte)'m', (byte)'o', (byte)'o', (byte)'v'];
    }

    [Fact]
    public void BuildTrailer_ParseRoundTrip_UsesExactMotionPayload()
    {
        byte[] video = CreateMinimalMp4();
        byte[]? trailer = NativeSamsungSef.BuildTrailer(video, "jpg");

        Assert.NotNull(trailer);
        byte[] input = [0xFF, 0xD8, 0xFF, 0xD9, .. trailer!];
        Assert.True(NativeSamsungSef.TryParse(input, out long videoOffset, out long videoSize, out string? error), error);
        Assert.Equal(video.Length, videoSize);
        Assert.Equal(video, input.AsSpan(checked((int)videoOffset), checked((int)videoSize)).ToArray());
    }

    [Fact]
    public void BuildHeicTrailer_WritesAbsoluteMpv2Pointer()
    {
        byte[] video = CreateMinimalMp4();
        const long imageSize = 4096;
        byte[]? trailer = NativeSamsungSef.BuildTrailer(video, "heic", imageSize);

        Assert.NotNull(trailer);
        int mpv2 = trailer!.AsSpan().IndexOf("mpv2"u8);
        Assert.True(mpv2 >= 0);
        Assert.Equal((uint)(imageSize + 8), System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(trailer.AsSpan(mpv2 + 4, 4)));
        Assert.Equal((uint)video.Length, System.Buffers.Binary.BinaryPrimitives.ReadUInt32BigEndian(trailer.AsSpan(mpv2 + 8, 4)));
    }

    [Fact]
    public void TryParse_RejectsWrongTotalSizeAndOutOfRangeDirectory()
    {
        byte[] video = CreateMinimalMp4();
        byte[] trailer = NativeSamsungSef.BuildTrailer(video, "jpg")!;
        byte[] input = [0xFF, 0xD8, 0xFF, 0xD9, .. trailer];

        int footer = input.Length - 8;
        uint total = System.Buffers.Binary.BinaryPrimitives.ReadUInt32LittleEndian(input.AsSpan(footer, 4));
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(footer, 4), total + 24);
        Assert.False(NativeSamsungSef.TryParse(input, out _, out _, out _));

        input = [0xFF, 0xD8, 0xFF, 0xD9, .. trailer];
        int sefh = input.AsSpan().IndexOf("SEFH"u8);
        Assert.True(sefh >= 0);
        System.Buffers.Binary.BinaryPrimitives.WriteUInt32LittleEndian(input.AsSpan(sefh + 16, 4), uint.MaxValue);
        Assert.False(NativeSamsungSef.TryParse(input, out _, out _, out _));
    }

    [Fact]
    public void TryParse_LocatesMotionPhotoDataPayload()
    {
        byte[] imagePrefix = new byte[37];
        byte[] video = CreateMinimalMp4();
        byte[] trailer = NativeSamsungSef.BuildTrailer(video, "jpg")!;
        byte[] input = [.. imagePrefix, .. trailer];

        Assert.True(NativeSamsungSef.TryParse(input, out long videoOffset, out long videoSize, out string? error), error);
        int relativeVideoOffset = trailer.AsSpan().IndexOf(video);
        Assert.True(relativeVideoOffset >= 0);
        Assert.Equal(imagePrefix.Length + relativeVideoOffset, videoOffset);
        Assert.Equal(video.Length, videoSize);
        Assert.Equal(video, input.AsSpan((int)videoOffset, (int)videoSize).ToArray());
    }
}
