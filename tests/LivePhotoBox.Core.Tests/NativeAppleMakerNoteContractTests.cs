using LivePhotoBox.Interop;
using LivePhotoBox.Services.Protocols;
using System.Buffers.Binary;
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
        byte[] data = [0x10, 0x20, .. makerNote, 0x30, 0x40];
        int makerNoteOffset = 2;

        Assert.True(NativeAppleMakerNoteWriter.TryStripLivePhotoEntries(data, out string? stripError), stripError);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(makerNoteOffset + 14, 2)));
        Assert.Equal(-1, data.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(oldContentId)));

        Assert.True(NativeAppleMakerNoteWriter.TryWriteContentIdentifier(data, newContentId, out string? writeError), writeError);
        Assert.Equal(1, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(makerNoteOffset + 14, 2)));
        Assert.Equal(0x0011, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(makerNoteOffset + 16, 2)));
        Assert.Equal(newContentId, System.Text.Encoding.ASCII.GetString(data, makerNoteOffset + 32, newContentId.Length));
        Assert.Equal(0x10, data[0]);
        Assert.Equal(0x40, data[^1]);
    }

    [Fact]
    public void StripLivePhotoEntries_MultipleMakerNotes_StripsEveryContentIdentifier()
    {
        const string firstContentId = "11111111-2222-3333-4444-555566667777";
        const string secondContentId = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEFFFF0000";
        byte[] first = AppleMakerNoteWriter.BuildMakerNote(firstContentId);
        byte[] second = AppleMakerNoteWriter.BuildMakerNote(secondContentId);
        byte[] data = [0x10, .. first, 0x20, .. second, 0x30];
        int firstOffset = 1;
        int secondOffset = firstOffset + first.Length + 1;

        Assert.True(NativeAppleMakerNoteWriter.TryStripLivePhotoEntries(data, out string? error), error);
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(firstOffset + 14, 2)));
        Assert.Equal(0, BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(secondOffset + 14, 2)));
        Assert.Equal(-1, data.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(firstContentId)));
        Assert.Equal(-1, data.AsSpan().IndexOf(System.Text.Encoding.ASCII.GetBytes(secondContentId)));
        Assert.Equal(0x10, data[0]);
        Assert.Equal(0x30, data[^1]);
    }
}
