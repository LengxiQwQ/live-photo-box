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
        byte[] data = AppleMakerNoteFixture.BuildJpegWithFormalExifMakerNote(makerNote);
        int makerNoteOffset = AppleMakerNoteFixture.FormalMakerNoteOffset;
        byte[] ownerStructureBefore = data.AsSpan(0, makerNoteOffset).ToArray();

        AssertFormalExifOwner(data, makerNote.Length);
        Assert.NotEqual(-1, data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(oldContentId)));

        Assert.True(NativeAppleMakerNoteWriter.TryStripLivePhotoEntries(data, out string? stripError), stripError);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(makerNoteOffset + 14, 2)));
        Assert.Equal(-1, data.AsSpan().IndexOf(Encoding.ASCII.GetBytes(oldContentId)));
        Assert.Equal(ownerStructureBefore, data.AsSpan(0, makerNoteOffset).ToArray());
        AssertFormalExifOwner(data, makerNote.Length);

        Assert.True(NativeAppleMakerNoteWriter.TryWriteContentIdentifier(data, newContentId, out string? writeError), writeError);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(makerNoteOffset + 14, 2)));
        Assert.Equal(0x0011, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(makerNoteOffset + 16, 2)));
        Assert.Equal(newContentId, System.Text.Encoding.ASCII.GetString(data, makerNoteOffset + 32, newContentId.Length));
        Assert.Equal(ownerStructureBefore, data.AsSpan(0, makerNoteOffset).ToArray());
        AssertFormalExifOwner(data, makerNote.Length);
    }

    [Fact]
    public void StripLivePhotoEntries_OnlyTouchesTheSingleFormalOwner()
    {
        const string ownedContentId = "11111111-2222-3333-4444-555566667777";
        const string unownedContentId = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEFFFF0000";
        byte[] owned = AppleMakerNoteWriter.BuildMakerNote(ownedContentId);
        byte[] nakedLookalike = AppleMakerNoteWriter.BuildMakerNote(unownedContentId);
        byte[] ownedJpeg = AppleMakerNoteFixture.BuildJpegWithFormalExifMakerNote(owned);
        byte[] data = [.. ownedJpeg, 0xA5, 0x5A, .. nakedLookalike];
        byte[] nakedSnapshot = data.AsSpan(ownedJpeg.Length + 2).ToArray();

        AssertFormalExifOwner(data, owned.Length);

        Assert.True(NativeAppleMakerNoteWriter.TryStripLivePhotoEntries(data, out string? error), error);
        Assert.Equal(-1, data.AsSpan(0, ownedJpeg.Length).IndexOf(Encoding.ASCII.GetBytes(ownedContentId)));
        Assert.Equal(nakedSnapshot, data.AsSpan(ownedJpeg.Length + 2).ToArray());
        Assert.NotEqual(-1, data.AsSpan(ownedJpeg.Length + 2).IndexOf(Encoding.ASCII.GetBytes(unownedContentId)));
        AssertFormalExifOwner(data, owned.Length);
    }

    [Fact]
    public void StripLivePhotoEntries_RejectsNakedMakerNoteWithoutFormalExifOwner()
    {
        byte[] nakedMakerNote = AppleMakerNoteWriter.BuildMakerNote(
            "11111111-2222-3333-4444-555566667777");

        Assert.False(NativeAppleMakerNoteWriter.TryStripLivePhotoEntries(nakedMakerNote, out string? error));
        Assert.Contains("not uniquely owned", error, StringComparison.OrdinalIgnoreCase);
    }

    private static void AssertFormalExifOwner(byte[] jpeg, int expectedMakerNoteLength)
    {
        const int tiffStart = 12;
        Assert.Equal(new byte[] { 0xFF, 0xD8, 0xFF, 0xE1 }, jpeg.AsSpan(0, 4).ToArray());
        Assert.Equal("Exif\0\0"u8.ToArray(), jpeg.AsSpan(6, 6).ToArray());
        Assert.Equal(0x8769, BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(tiffStart + 10, 2)));
        Assert.Equal(26u, BinaryPrimitives.ReadUInt32BigEndian(jpeg.AsSpan(tiffStart + 18, 4)));
        Assert.Equal(0x927C, BinaryPrimitives.ReadUInt16BigEndian(jpeg.AsSpan(tiffStart + 28, 2)));
        Assert.Equal((uint)expectedMakerNoteLength, BinaryPrimitives.ReadUInt32BigEndian(jpeg.AsSpan(tiffStart + 32, 4)));
        Assert.Equal(44u, BinaryPrimitives.ReadUInt32BigEndian(jpeg.AsSpan(tiffStart + 36, 4)));
    }
}
