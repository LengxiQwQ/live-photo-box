using System.Buffers.Binary;
using System.Text;

namespace LivePhotoBox.Core.Tests;

/// <summary>
/// Fixed-shape input builder for Native Apple MakerNote contract tests.
/// It creates one APP1 Exif segment with one big-endian TIFF whose IFD0 points
/// to one ExifIFD MakerNote tag. It deliberately does not parse or mutate
/// MakerNotes; those behaviours remain the Native system under test.
/// </summary>
internal static class AppleMakerNoteFixture
{
    // SOI (2) + APP1 marker/length (4) + Exif signature (6) + TIFF owner (44).
    internal const int FormalMakerNoteOffset = 56;

    internal static byte[] BuildJpegWithFormalExifMakerNote(byte[] makerNote)
    {
        ArgumentNullException.ThrowIfNull(makerNote);

        byte[] tiff = new byte[44 + makerNote.Length];
        tiff[0] = (byte)'M';
        tiff[1] = (byte)'M';
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4, 4), 8);

        // IFD0: ExifIFD pointer (0x8769) to the one-entry ExifIFD at 26.
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10, 2), 0x8769);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12, 2), 4);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(18, 4), 26);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(22, 4), 0);

        // ExifIFD: one UNDEFINED MakerNote (0x927C) whose value starts at 44.
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(26, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(28, 2), 0x927C);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(30, 2), 7);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(32, 4), (uint)makerNote.Length);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(36, 4), 44);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(40, 4), 0);
        makerNote.CopyTo(tiff, 44);

        byte[] jpeg = new byte[FormalMakerNoteOffset + makerNote.Length + 2];
        jpeg[0] = 0xFF;
        jpeg[1] = 0xD8;
        jpeg[2] = 0xFF;
        jpeg[3] = 0xE1;
        BinaryPrimitives.WriteUInt16BigEndian(jpeg.AsSpan(4, 2), checked((ushort)(tiff.Length + 8)));
        "Exif\0\0"u8.CopyTo(jpeg.AsSpan(6, 6));
        tiff.CopyTo(jpeg, 12);
        jpeg[^2] = 0xFF;
        jpeg[^1] = 0xD9;
        return jpeg;
    }
}
