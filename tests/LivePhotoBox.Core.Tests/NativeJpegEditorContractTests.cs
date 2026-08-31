using LivePhotoBox.Interop;
using System.Text;
using Xunit;

namespace LivePhotoBox.Core.Tests;

[Trait("Category", "NativeContract")]
public sealed class NativeJpegEditorContractTests
{
    private static readonly byte[] XmpHeader = "http://ns.adobe.com/xap/1.0/\0"u8.ToArray();

    [Fact]
    public void InjectXmp_ReplacesExistingXmpAndPreservesScanData()
    {
        byte[] scanData = [0x11, 0x22, 0xFF, 0x00, 0x33, 0xFF, 0xD9];
        byte[] input = BuildJpeg("old-xmp", scanData);
        byte[] newXmp = Encoding.UTF8.GetBytes("new-xmp");

        Assert.True(NativeJpegEditor.TryInjectXmp(input, newXmp, out byte[]? output, out string? error), error);
        Assert.NotNull(output);
        Assert.Equal(1, CountOccurrences(output, XmpHeader));
        Assert.Equal(-1, output.AsSpan().IndexOf("old-xmp"u8));
        Assert.NotEqual(-1, output.AsSpan().IndexOf(newXmp));
        Assert.True(output.AsSpan().EndsWith(scanData));
    }

    [Fact]
    public void InjectXmp_WithNullPayloadRemovesExistingXmp()
    {
        byte[] scanData = [0xAA, 0xBB, 0xFF, 0xD9];
        byte[] input = BuildJpeg("old-xmp", scanData);

        Assert.True(NativeJpegEditor.TryInjectXmp(input, null, out byte[]? output, out string? error), error);
        Assert.NotNull(output);
        Assert.Equal(0, CountOccurrences(output, XmpHeader));
        Assert.True(output.AsSpan().EndsWith(scanData));
    }

    private static byte[] BuildJpeg(string xmp, byte[] scanData)
    {
        byte[] xmpPayload = [.. XmpHeader, .. Encoding.UTF8.GetBytes(xmp)];
        using var stream = new MemoryStream();
        stream.Write([0xFF, 0xD8]);
        WriteSegment(stream, 0xE0, [0x4A, 0x46]);
        WriteSegment(stream, 0xE1, xmpPayload);
        stream.Write([0xFF, 0xDA, 0x00, 0x08, 0x01, 0x01, 0x00, 0x00, 0x3F, 0x00]);
        stream.Write(scanData);
        return stream.ToArray();
    }

    private static void WriteSegment(Stream stream, byte marker, byte[] payload)
    {
        stream.WriteByte(0xFF);
        stream.WriteByte(marker);
        stream.WriteByte(0);
        stream.WriteByte(checked((byte)(payload.Length + 2)));
        stream.Write(payload);
    }

    private static int CountOccurrences(byte[] value, byte[] pattern)
    {
        int count = 0;
        int start = 0;
        while (start <= value.Length - pattern.Length)
        {
            int index = value.AsSpan(start).IndexOf(pattern);
            if (index < 0) break;
            count++;
            start += index + pattern.Length;
        }
        return count;
    }
}
