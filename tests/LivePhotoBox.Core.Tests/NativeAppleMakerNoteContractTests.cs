using LivePhotoBox.Interop;
using LivePhotoBox.Services.Protocols;
using System.Buffers.Binary;
using System.Text;
using Xunit;

namespace LivePhotoBox.Core.Tests;

[Trait("Category", "NativeContract")]
public sealed class NativeAppleMakerNoteContractTests
{
    [Fact]
    public void StripThenWriteContentIdentifier_RebuildsSingleLivePhotoEntryInPlace()
    {
        const string oldContentId = "11111111-2222-3333-4444-555566667777";
        const string newContentId = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEFFFF0000";
        byte[] makerNote = AppleMakerNoteWriter.BuildMakerNote(oldContentId);
        (byte[] data, int makerNoteOffset) = BuildOwnedJpeg(makerNote);

        Assert.True(NativeAppleMakerNoteWriter.TryStripLivePhotoEntries(data, out string? stripError), stripError);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(makerNoteOffset + 14, 2)));
        Assert.Equal(-1, data.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(oldContentId)));

        Assert.True(NativeAppleMakerNoteWriter.TryWriteContentIdentifier(data, newContentId, out string? writeError), writeError);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(makerNoteOffset + 14, 2)));
        Assert.Equal(0x0011, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(makerNoteOffset + 16, 2)));
        Assert.Equal(newContentId, System.Text.Encoding.ASCII.GetString(data, makerNoteOffset + 32, newContentId.Length));
        Assert.Equal(0xFF, data[0]);
        Assert.Equal(0xD9, data[^1]);
    }

    [Fact]
    public void StripLivePhotoEntries_MultipleMakerNotes_RejectsAmbiguousOwnershipWithoutMutation()
    {
        const string firstContentId = "11111111-2222-3333-4444-555566667777";
        const string secondContentId = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEFFFF0000";
        byte[] first = AppleMakerNoteWriter.BuildMakerNote(firstContentId);
        byte[] second = AppleMakerNoteWriter.BuildMakerNote(secondContentId);
        (byte[] data, _) = BuildOwnedJpeg(first, second);
        byte[] before = (byte[])data.Clone();

        Assert.False(NativeAppleMakerNoteWriter.TryStripLivePhotoEntries(data, out _));
        Assert.Equal(before, data);
    }

    private static (byte[] Bytes, int FirstMakerNoteOffset) BuildOwnedJpeg(params byte[][] makerNotes)
    {
        var jpeg = new List<byte> { 0xFF, 0xD8 };
        int firstMakerNoteOffset = -1;
        foreach (byte[] makerNote in makerNotes)
        {
            byte[] tiff = new byte[44 + makerNote.Length];
            tiff[0] = (byte)'M';
            tiff[1] = (byte)'M';
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2, 2), 42);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4, 4), 8);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10, 2), 0x8769);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12, 2), 4);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14, 4), 1);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(18, 4), 26);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(22, 4), 0);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(26, 2), 1);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(28, 2), 0x927C);
            BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(30, 2), 7);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(32, 4), (uint)makerNote.Length);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(36, 4), 44);
            BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(40, 4), 0);
            makerNote.CopyTo(tiff, 44);

            jpeg.Add(0xFF);
            jpeg.Add(0xE1);
            int segmentLength = tiff.Length + 8;
            jpeg.Add((byte)(segmentLength >> 8));
            jpeg.Add((byte)segmentLength);
            jpeg.AddRange(Encoding.ASCII.GetBytes("Exif\0\0"));
            int tiffStart = jpeg.Count;
            jpeg.AddRange(tiff);
            if (firstMakerNoteOffset < 0) firstMakerNoteOffset = tiffStart + 44;
        }
        jpeg.Add(0xFF);
        jpeg.Add(0xD9);
        return (jpeg.ToArray(), firstMakerNoteOffset);
    }
}
