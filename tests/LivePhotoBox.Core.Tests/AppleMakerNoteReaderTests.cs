using System.Buffers.Binary;
using System.Text;
using LivePhotoBox.Services.Protocols;
using Xunit;

namespace LivePhotoBox.Core.Tests;

public sealed class AppleMakerNoteReaderTests
{
    [Fact]
    public void ReadContentIdentifier_FromFormallyOwnedJpegMakerNote_ReturnsCid()
    {
        const string expected = "11111111-2222-3333-4444-555566667777";
        string temp = CreateTempFile();
        try
        {
            File.WriteAllBytes(temp, BuildOwnedJpeg(AppleMakerNoteWriter.BuildMakerNote(expected)));

            bool ok = AppleMakerNoteWriter.TryReadContentIdentifierFromImage(
                temp, out string? cid, out string? error);

            Assert.True(ok, $"expected read success, error={error}");
            Assert.Equal(expected, cid);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    [Fact]
    public void ReadContentIdentifier_RejectsUnownedMakerNoteBytes()
    {
        string temp = CreateTempFile();
        try
        {
            File.WriteAllBytes(temp, AppleMakerNoteWriter.BuildMakerNote(
                "11111111-2222-3333-4444-555566667777"));

            Assert.False(AppleMakerNoteWriter.TryReadContentIdentifierFromImage(
                temp, out _, out _));
        }
        finally
        {
            TryDelete(temp);
        }
    }

    [Fact]
    public void ReadContentIdentifier_RejectsMakerNoteOwnedOnlyByIfd0()
    {
        string temp = CreateTempFile();
        try
        {
            File.WriteAllBytes(temp, BuildIfd0OnlyJpeg(AppleMakerNoteWriter.BuildMakerNote(
                "11111111-2222-3333-4444-555566667777")));

            Assert.False(AppleMakerNoteWriter.TryReadContentIdentifierFromImage(
                temp, out _, out _));
        }
        finally
        {
            TryDelete(temp);
        }
    }

    [Fact]
    public void InjectMakerNote_RejectsMakerNoteOwnedOnlyByIfd0()
    {
        string temp = CreateTempFile();
        try
        {
            File.WriteAllBytes(temp, BuildIfd0OnlyJpeg(AppleMakerNoteWriter.BuildMakerNote(
                "11111111-2222-3333-4444-555566667777")));

            Assert.False(AppleMakerNoteWriter.TryInjectIntoJpeg(
                temp, AppleMakerNoteWriter.BuildMakerNote(
                    "AAAAAAAA-BBBB-CCCC-DDDD-EEEEFFFF0000"), out _));
        }
        finally
        {
            TryDelete(temp);
        }
    }

    [Fact]
    public void ReadContentIdentifier_RejectsDuplicateExifOwners()
    {
        string temp = CreateTempFile();
        try
        {
            byte[] first = AppleMakerNoteWriter.BuildMakerNote(
                "11111111-2222-3333-4444-555566667777");
            byte[] second = AppleMakerNoteWriter.BuildMakerNote(
                "AAAAAAAA-BBBB-CCCC-DDDD-EEEEFFFF0000");
            File.WriteAllBytes(temp, BuildOwnedJpeg(first, second));

            Assert.False(AppleMakerNoteWriter.TryReadContentIdentifierFromImage(
                temp, out _, out _));
        }
        finally
        {
            TryDelete(temp);
        }
    }

    [Fact]
    public void StripThenRewrite_UsesTheSingleFormalExifOwner()
    {
        const string replacement = "AAAAAAAA-BBBB-CCCC-DDDD-EEEEFFFF0000";
        string temp = CreateTempFile();
        try
        {
            File.WriteAllBytes(temp, BuildOwnedJpeg(AppleMakerNoteWriter.BuildMakerNote(
                "99999999-8888-7777-6666-555544443333")));
            Assert.True(AppleMakerNoteWriter.TryStripAppleLivePhotoEntries(
                temp, out string? stripError), stripError);
            Assert.False(AppleMakerNoteWriter.TryReadContentIdentifierFromImage(
                temp, out _, out _));

            Assert.True(AppleMakerNoteWriter.TryWriteContentIdentifier(
                temp, replacement, out string? writeError), writeError);
            Assert.True(AppleMakerNoteWriter.TryReadContentIdentifierFromImage(
                temp, out string? cid, out string? readError), readError);
            Assert.Equal(replacement, cid);
        }
        finally
        {
            TryDelete(temp);
        }
    }

    private static string CreateTempFile()
    {
        string dir = Path.Combine(Path.GetTempPath(), "lpb_mn_tests");
        Directory.CreateDirectory(dir);
        return Path.Combine(dir, $"{Guid.NewGuid():N}.jpg");
    }

    private static byte[] BuildOwnedJpeg(params byte[][] makerNotes)
    {
        var jpeg = new List<byte> { 0xFF, 0xD8 };
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
            jpeg.AddRange(tiff);
        }
        jpeg.Add(0xFF);
        jpeg.Add(0xD9);
        return jpeg.ToArray();
    }

    private static byte[] BuildIfd0OnlyJpeg(byte[] makerNote)
    {
        byte[] tiff = new byte[26 + makerNote.Length];
        tiff[0] = (byte)'M';
        tiff[1] = (byte)'M';
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(4, 4), 8);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(8, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(10, 2), 0x927C);
        BinaryPrimitives.WriteUInt16BigEndian(tiff.AsSpan(12, 2), 7);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(14, 4), (uint)makerNote.Length);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(18, 4), 26);
        BinaryPrimitives.WriteUInt32BigEndian(tiff.AsSpan(22, 4), 0);
        makerNote.CopyTo(tiff, 26);

        var jpeg = new List<byte> { 0xFF, 0xD8, 0xFF, 0xE1 };
        int segmentLength = tiff.Length + 8;
        jpeg.Add((byte)(segmentLength >> 8));
        jpeg.Add((byte)segmentLength);
        jpeg.AddRange(Encoding.ASCII.GetBytes("Exif\0\0"));
        jpeg.AddRange(tiff);
        jpeg.Add(0xFF);
        jpeg.Add(0xD9);
        return jpeg.ToArray();
    }

    private static void TryDelete(string path)
    {
        try
        {
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // Test cleanup failure must not hide assertion failures.
        }
    }
}
