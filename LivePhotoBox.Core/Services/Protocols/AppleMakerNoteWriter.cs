using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Services.Protocols;

/// <summary>Read-only Apple metadata observation and historical signature compatibility.</summary>
public static class AppleMakerNoteWriter
{
    public static byte[] BuildMakerNote(string contentId)
    {
        byte[] cidBytes = Encoding.ASCII.GetBytes(contentId + "\0"); // 37 bytes（UUID 36 + \0）

        // 头 10 + 版本 2 + "MM" 2 + 条目数 2 + 1 条 12 + next-IFD 4 = 32
        const int dataOffset = 10 + 2 + 2 + 2 + 12 + 4;
        int total = dataOffset + cidBytes.Length;      // 32 + 37 = 69
        int pad = (total % 2 == 0) ? 0 : 1;            // 对齐到偶数 = 70

        var ms = new MemoryStream(total + pad);
        ms.Write(Encoding.ASCII.GetBytes("Apple iOS\0"));
        ms.WriteByte(0x00);
        ms.WriteByte(0x01);
        ms.Write(Encoding.ASCII.GetBytes("MM"));

        WriteU16Be(ms, 1);                                                       // entry count = 1
        WriteEntry(ms, 0x0011, 2, (uint)cidBytes.Length, (uint)dataOffset);      // ContentIdentifier
        WriteU32Be(ms, 0);                                                      // next IFD = 0

        ms.Write(cidBytes);
        if (pad > 0) ms.WriteByte(0);

        return ms.ToArray();
    }

    internal static byte[] BuildHdrMakerNote(
        HdrSignedRational hdrHeadroom, HdrSignedRational hdrGain, string? contentId = null)
    {
        if (!string.IsNullOrEmpty(contentId))
        {
            byte[] cidBytes = Encoding.ASCII.GetBytes(contentId + "\0"); // 37 bytes（UUID 36 + \0）
            const int entryCount = 3;
            const int ifdBytes = 2 + entryCount * 12 + 4; // count + 3×12 + next-IFD = 42
            const int dataStart = 10 + 2 + 2 + ifdBytes;  // 56
            int cidOffset = dataStart;
            int headroomOffset = (cidOffset + cidBytes.Length + 1) & ~1; // 偶数对齐
            int gainOffset = headroomOffset + 8;
            int totalWithCid = gainOffset + 8; // 110

            var msWithCid = new MemoryStream(totalWithCid);
            msWithCid.Write(Encoding.ASCII.GetBytes("Apple iOS\0"));
            msWithCid.WriteByte(0x00);
            msWithCid.WriteByte(0x01);
            msWithCid.Write(Encoding.ASCII.GetBytes("MM"));

            WriteU16Be(msWithCid, entryCount);
            WriteEntry(msWithCid, 0x0011, 2, (uint)cidBytes.Length, (uint)cidOffset);  // ContentIdentifier
            WriteEntry(msWithCid, 0x0021, 10, 1, (uint)headroomOffset);                // HDRHeadroom
            WriteEntry(msWithCid, 0x0030, 10, 1, (uint)gainOffset);                    // HDRGain
            WriteU32Be(msWithCid, 0);                                                  // next IFD = 0

            while (msWithCid.Position < cidOffset)
            {
                msWithCid.WriteByte(0);
            }
            msWithCid.Write(cidBytes);
            while (msWithCid.Position < headroomOffset)
            {
                msWithCid.WriteByte(0);
            }
            WriteU32Be(msWithCid, (uint)hdrHeadroom.Numerator);
            WriteU32Be(msWithCid, (uint)hdrHeadroom.Denominator);
            WriteU32Be(msWithCid, (uint)hdrGain.Numerator);
            WriteU32Be(msWithCid, (uint)hdrGain.Denominator);
            return msWithCid.ToArray();
        }

        const int headroomDataOffset = 10 + 2 + 2 + 2 + 2 * 12 + 4; // 44
        const int gainDataOffset = headroomDataOffset + 8;           // 52
        const int total = gainDataOffset + 8;                        // 60

        var ms = new MemoryStream(total);
        ms.Write(Encoding.ASCII.GetBytes("Apple iOS\0"));
        ms.WriteByte(0x00);
        ms.WriteByte(0x01);
        ms.Write(Encoding.ASCII.GetBytes("MM"));

        WriteU16Be(ms, 2);                                                         // entry count = 2
        WriteEntry(ms, 0x0021, 10, 1, (uint)headroomDataOffset);                  // HDRHeadroom
        WriteEntry(ms, 0x0030, 10, 1, (uint)gainDataOffset);                      // HDRGain
        WriteU32Be(ms, 0);                                                        // next IFD = 0

        WriteU32Be(ms, (uint)hdrHeadroom.Numerator);
        WriteU32Be(ms, (uint)hdrHeadroom.Denominator);
        WriteU32Be(ms, (uint)hdrGain.Numerator);
        WriteU32Be(ms, (uint)hdrGain.Denominator);

        return ms.ToArray();
    }

    public static bool TryReadContentIdentifierFromImage(string imagePath, out string? contentId, out string? error)
    {
        contentId = null;
        error = null;
        try
        {
            var facts = NativeMediaService.InspectMediaAsync(imagePath)
                .GetAwaiter().GetResult();
            if (!facts.PrimaryImage.IsPresent ||
                facts.PrimaryImage.Container is not ImageContainer.Jpeg and not ImageContainer.Heic)
            {
                error = "Native SourceInspector did not confirm a JPEG or HEIF image owner for ContentIdentifier.";
                return false;
            }

            string? candidate = facts.PairingIdentifier;
            if (string.IsNullOrWhiteSpace(candidate) || !Guid.TryParseExact(candidate, "D", out _))
            {
                error = "Native SourceInspector did not confirm a formally owned Apple ContentIdentifier.";
                return false;
            }

            contentId = candidate;
            return true;
        }
        catch (SourceInspectionException ex)
        {
            error = $"Native SourceInspector rejected the image: {ex.Message}";
            return false;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void WriteEntry(Stream ms, ushort tag, ushort type, uint count, uint value)
    {
        WriteU16Be(ms, tag);
        WriteU16Be(ms, type);
        WriteU32Be(ms, count);
        WriteU32Be(ms, value);
    }

    private static void WriteU16Be(Stream ms, int v)
    {
        Span<byte> b = stackalloc byte[2];
        BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
        ms.Write(b);
    }

    private static void WriteU32Be(Stream ms, uint v)
    {
        Span<byte> b = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(b, v);
        ms.Write(b);
    }

    public static bool TryInjectIntoJpeg(string jpegPath, byte[] makerNote, out string? error)
    {
        error = "[AuthorityViolation] Historical path mutation is unsupported; use the authorized media pipeline.";
        return false;
    }

    public static bool TryStripAppleLivePhotoEntries(string imagePath, out string? error)
    {
        error = "[AuthorityViolation] Historical path mutation is unsupported; use the authorized media pipeline.";
        return false;
    }

    public static bool TryWriteContentIdentifier(string imagePath, string contentId, out string? error)
    {
        error = "[AuthorityViolation] Historical path mutation is unsupported; use the authorized media pipeline.";
        return false;
    }

    public static bool TryInjectAppleMakerNoteIntoHeic(string heicPath, string contentId, out string? error)
    {
        error = "[AuthorityViolation] Historical path mutation is unsupported; use the authorized media pipeline.";
        return false;
    }

    public static bool TryInjectMakerNoteIntoHeic(string heicPath, byte[] makerNote, out string? error)
    {
        error = "[AuthorityViolation] Historical path mutation is unsupported; use the authorized media pipeline.";
        return false;
    }

    public static bool TryNormalizeExifItem(string heicPath, out string? error)
    {
        error = "[AuthorityViolation] Historical path mutation is unsupported; use the authorized media pipeline.";
        return false;
    }
}
