using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;

namespace LivePhotoBox.Interop;

public static class NativeAppleMakerNoteWriter
{
    /// <summary>
    /// Reads an image ContentIdentifier through the Native SourceInspector.
    /// The Native reader resolves the formal JPEG APP1 ExifIFD 0x927C owner or
    /// the unique HEIF Exif item; arbitrary MakerNote signatures are never an
    /// authority here.
    /// </summary>
    public static bool TryReadContentIdentifierFromImage(
        string imagePath, out string? contentId, out string? error)
    {
        contentId = null;
        error = null;
        try
        {
            var facts = NativeMediaService.InspectMediaAsync(imagePath)
                .GetAwaiter().GetResult();
            if (!facts.PrimaryImage.IsPresent
                || facts.PrimaryImage.Container is not ImageContainer.Jpeg and not ImageContainer.Heic)
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

    /// <summary>Builds the minimal Apple MakerNote payload used by the rebuilt split writer.</summary>
    public static byte[] BuildContentIdentifierMakerNote(string contentId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(contentId);
        byte[] cid = Encoding.ASCII.GetBytes(contentId + "\0");
        const int dataOffset = 32;
        byte[] makerNote = new byte[dataOffset + cid.Length + 1];
        Encoding.ASCII.GetBytes("Apple iOS\0").CopyTo(makerNote, 0);
        makerNote[10] = 0;
        makerNote[11] = 1;
        makerNote[12] = (byte)'M';
        makerNote[13] = (byte)'M';
        BinaryPrimitives.WriteUInt16BigEndian(makerNote.AsSpan(14, 2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(makerNote.AsSpan(16, 2), 0x0011);
        BinaryPrimitives.WriteUInt16BigEndian(makerNote.AsSpan(18, 2), 2);
        BinaryPrimitives.WriteUInt32BigEndian(makerNote.AsSpan(20, 4), (uint)cid.Length);
        BinaryPrimitives.WriteUInt32BigEndian(makerNote.AsSpan(24, 4), dataOffset);
        BinaryPrimitives.WriteUInt32BigEndian(makerNote.AsSpan(28, 4), 0);
        cid.CopyTo(makerNote, dataOffset);
        return makerNote;
    }

    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_apple_strip_live_photo_entries")]
    private static extern NativeResult LpbAppleStripLivePhotoEntries(
        nint context,
        ref byte data,
        nuint dataSize);

    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_apple_write_content_identifier", CharSet = CharSet.Ansi)]
    private static extern NativeResult LpbAppleWriteContentIdentifier(
        nint context,
        ref byte data,
        nuint dataSize,
        string contentId);

    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_apple_strip_live_photo_entries_selective")]
    private static extern NativeResult LpbAppleStripLivePhotoEntriesSelective(
        nint context,
        ref byte data,
        nuint dataSize,
        ushort[] authorizedTags,
        nuint authorizedCount,
        ushort[]? outStrippedTags,
        nuint maxStrippedTags,
        out nuint outStrippedCount);

    public static bool TryStripLivePhotoEntries(byte[] imageBytes, out string? error)
    {
        error = null;
        nint context = nint.Zero;
        try
        {
            if (NativeMethods.CreateContext(nint.Zero, out context) != NativeResult.Ok) return false;

            NativeResult res = LpbAppleStripLivePhotoEntries(
                context,
                ref MemoryMarshal.GetArrayDataReference(imageBytes),
                (nuint)imageBytes.Length);

            if (res != NativeResult.Ok)
            {
                error = ReadLastError(context) ?? $"Native error {res}";
                return false;
            }
            return true;
        }
        finally
        {
            if (context != nint.Zero) { NativeMethods.DestroyContext(context); }
        }
    }

    public static bool TryStripLivePhotoEntriesSelective(byte[] imageBytes, ushort[] authorizedTags, out string? error)
    {
        error = null;
        nint context = nint.Zero;
        try
        {
            if (NativeMethods.CreateContext(nint.Zero, out context) != NativeResult.Ok) return false;

            NativeResult res = LpbAppleStripLivePhotoEntriesSelective(
                context,
                ref MemoryMarshal.GetArrayDataReference(imageBytes),
                (nuint)imageBytes.Length,
                authorizedTags,
                (nuint)authorizedTags.Length,
                null,
                0,
                out _);

            if (res != NativeResult.Ok)
            {
                error = ReadLastError(context) ?? $"Native error {res}";
                return false;
            }
            return true;
        }
        finally
        {
            if (context != nint.Zero) { NativeMethods.DestroyContext(context); }
        }
    }

    public static bool TryWriteContentIdentifier(byte[] imageBytes, string contentId, out string? error)
    {
        error = null;
        nint context = nint.Zero;
        try
        {
            if (NativeMethods.CreateContext(nint.Zero, out context) != NativeResult.Ok) return false;

            NativeResult res = LpbAppleWriteContentIdentifier(
                context,
                ref MemoryMarshal.GetArrayDataReference(imageBytes),
                (nuint)imageBytes.Length,
                contentId);

            if (res != NativeResult.Ok)
            {
                error = ReadLastError(context) ?? $"Native error {res}";
                return false;
            }
            return true;
        }
        finally
        {
            if (context != nint.Zero) { NativeMethods.DestroyContext(context); }
        }
    }

    /// <summary>
    /// Reserves one formally-owned MakerNote directory slot for the native
    /// preserving writer when the source has already been stripped.  The
    /// native writer replaces live entries in place; when no live entry is
    /// present, its old-directory cleanup range would otherwise overlap the
    /// newly appended entry.  This helper only prepares a structurally
    /// validated clone; the native writer remains the mutation authority.
    /// </summary>
    internal static bool TryPrepareContentIdentifierWrite(byte[] imageBytes, out string? error)
    {
        error = null;
        if (imageBytes is null || imageBytes.Length == 0)
        {
            error = "Image bytes are empty.";
            return false;
        }

        try
        {
            if (imageBytes.Length >= 2 && imageBytes[0] == 0xFF && imageBytes[1] == 0xD8)
            {
                return TryPrepareJpegOwner(imageBytes, out error);
            }

            if (!TryPrepareHeifOwner(imageBytes, out error))
            {
                return false;
            }
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryPrepareJpegOwner(byte[] data, out string? error)
    {
        error = null;
        int pos = 2;
        int tiffStart = -1;
        int tiffEnd = -1;
        while (pos + 2 <= data.Length)
        {
            if (data[pos++] != 0xFF)
            {
                error = "JPEG marker structure is malformed.";
                return false;
            }
            while (pos < data.Length && data[pos] == 0xFF) pos++;
            if (pos >= data.Length)
            {
                error = "JPEG marker structure is truncated.";
                return false;
            }

            byte marker = data[pos++];
            if (marker is 0xDA or 0xD9) break;
            if (marker == 0x00 || marker is >= 0xD0 and <= 0xD7) continue;
            if (pos + 2 > data.Length)
            {
                error = "JPEG segment length is truncated.";
                return false;
            }

            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(pos, 2));
            if (segmentLength < 2 || segmentLength - 2 > data.Length - (pos + 2))
            {
                error = "JPEG segment exceeds the file bounds.";
                return false;
            }

            int payload = pos + 2;
            int payloadLength = segmentLength - 2;
            if (marker == 0xE1 && payloadLength >= 6 &&
                data.AsSpan(payload, 6).SequenceEqual("Exif\0\0"u8))
            {
                if (tiffStart >= 0)
                {
                    error = "JPEG contains duplicate Exif APP1 owners.";
                    return false;
                }
                tiffStart = payload + 6;
                tiffEnd = payload + payloadLength;
            }
            pos = payload + payloadLength;
        }

        if (tiffStart < 0)
        {
            error = "JPEG has no formal Exif APP1 owner.";
            return false;
        }
        return TryPrepareTiffOwner(data, tiffStart, tiffEnd, out error);
    }

    private static bool TryPrepareHeifOwner(byte[] data, out string? error)
    {
        error = null;
        if (!NativeHeifBoxParser.TryLocateExifItem(data, out long itemOffset, out long itemLength, out error))
        {
            return false;
        }
        if (itemOffset < 0 || itemLength < 0 || itemOffset > data.Length ||
            itemLength > data.Length - itemOffset)
        {
            error = "HEIF Exif item is outside the file bounds.";
            return false;
        }

        int itemStart = checked((int)itemOffset);
        int itemEnd = checked(itemStart + (int)itemLength);
        if (itemEnd - itemStart < 4)
        {
            error = "HEIF Exif item is truncated.";
            return false;
        }

        int tiffHeaderOffset = checked((int)BinaryPrimitives.ReadUInt32BigEndian(
            data.AsSpan(itemStart, 4)));
        int tiffStart = checked(itemStart + 4 + tiffHeaderOffset);
        if (tiffStart < itemStart + 4 || tiffStart > itemEnd)
        {
            error = "HEIF Exif TIFF offset is outside the item.";
            return false;
        }
        return TryPrepareTiffOwner(data, tiffStart, itemEnd, out error);
    }

    private static bool TryPrepareTiffOwner(byte[] data, int tiffStart, int tiffEnd, out string? error)
    {
        error = null;
        if (tiffStart < 0 || tiffEnd < tiffStart || tiffEnd - tiffStart < 8)
        {
            error = "Exif TIFF bounds are malformed.";
            return false;
        }

        bool bigEndian = data[tiffStart] == (byte)'M' && data[tiffStart + 1] == (byte)'M';
        bool littleEndian = data[tiffStart] == (byte)'I' && data[tiffStart + 1] == (byte)'I';
        if (!bigEndian && !littleEndian)
        {
            error = "Exif TIFF byte order is unknown.";
            return false;
        }
        if (ReadU16(data, tiffStart + 2, bigEndian) != 42)
        {
            error = "Exif TIFF magic is invalid.";
            return false;
        }

        uint ifd0Offset = ReadU32(data, tiffStart + 4, bigEndian);
        if (!TryReadIfd(data, tiffStart, tiffEnd, ifd0Offset, bigEndian, out List<TiffEntry>? ifd0))
        {
            error = "Exif IFD0 is malformed.";
            return false;
        }
        if (ifd0 is null)
        {
            error = "Exif IFD0 is malformed.";
            return false;
        }
        TiffEntry[] exifPointers = ifd0.FindAll(static e => e.Tag == 0x8769).ToArray();
        if (exifPointers.Length != 1 || exifPointers[0].Type != 4 || exifPointers[0].Count != 1)
        {
            error = "Exif IFD0 does not contain one ExifIFD owner pointer.";
            return false;
        }
        if (ifd0.Any(static e => e.Tag == 0x927C))
        {
            error = "MakerNote is not owned by the ExifIFD.";
            return false;
        }

        uint exifOffset = exifPointers[0].Value;
        if (!TryReadIfd(data, tiffStart, tiffEnd, exifOffset, bigEndian, out List<TiffEntry>? exifIfd))
        {
            error = "ExifIFD is malformed.";
            return false;
        }
        if (exifIfd is null)
        {
            error = "ExifIFD is malformed.";
            return false;
        }
        TiffEntry[] makerNotes = exifIfd.FindAll(static e => e.Tag == 0x927C).ToArray();
        if (makerNotes.Length != 1 || makerNotes[0].Type != 7 || makerNotes[0].Count < 14)
        {
            error = "ExifIFD does not contain one owned MakerNote.";
            return false;
        }

        int makerStart = checked(tiffStart + (int)makerNotes[0].Value);
        int makerLength = checked((int)makerNotes[0].Count);
        if (makerStart < tiffStart || makerLength > tiffEnd - makerStart || makerLength < 16 ||
            !data.AsSpan(makerStart, 10).SequenceEqual("Apple iOS\0"u8) ||
            data[makerStart + 10] != 0 || data[makerStart + 11] != 1 ||
            data[makerStart + 12] != (byte)'M' || data[makerStart + 13] != (byte)'M')
        {
            error = "Owned MakerNote header is malformed.";
            return false;
        }

        int count = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(makerStart + 14, 2));
        if (count > 64 || count > (makerLength - 16) / 12 ||
            16 + (count * 12) + 4 > makerLength)
        {
            error = "Owned MakerNote directory is malformed.";
            return false;
        }

        bool hasLiveEntry = false;
        for (int i = 0; i < count; i++)
        {
            int entry = makerStart + 16 + i * 12;
            ushort tag = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(entry, 2));
            if (tag is 0x0011 or 0x0017 or 0x0025 or 0x002b)
            {
                hasLiveEntry = true;
                break;
            }
        }
        if (hasLiveEntry)
        {
            return true;
        }
        if (count >= 64)
        {
            error = "Owned MakerNote has no slot for a replacement ContentIdentifier.";
            return false;
        }

        int slot = makerStart + 16 + count * 12;
        int nextIfd = slot + 12;
        if (nextIfd + 4 > makerStart + makerLength)
        {
            error = "Owned MakerNote has no preserving replacement slot.";
            return false;
        }
        for (int i = slot; i < nextIfd + 4; i++)
        {
            if (data[i] != 0)
            {
                error = "Owned MakerNote replacement slot is occupied.";
                return false;
            }
        }

        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(makerStart + 14, 2), (ushort)(count + 1));
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(slot, 2), 0x0011);
        BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(slot + 2, 2), 2);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(slot + 4, 4), 1);
        BinaryPrimitives.WriteUInt32BigEndian(data.AsSpan(slot + 8, 4), 0);
        return true;
    }

    private readonly record struct TiffEntry(ushort Tag, ushort Type, uint Count, uint Value);

    private static bool TryReadIfd(
        byte[] data, int tiffStart, int tiffEnd, uint relativeOffset, bool bigEndian,
        out List<TiffEntry>? entries)
    {
        entries = null;
        if (relativeOffset > int.MaxValue || relativeOffset > (uint)(tiffEnd - tiffStart - 2))
        {
            return false;
        }
        int start = checked(tiffStart + (int)relativeOffset);
        int count = ReadU16(data, start, bigEndian);
        int entriesEnd;
        try
        {
            entriesEnd = checked(start + 2 + count * 12 + 4);
        }
        catch (OverflowException)
        {
            return false;
        }
        if (entriesEnd > tiffEnd)
        {
            return false;
        }

        var result = new List<TiffEntry>(count);
        for (int i = 0; i < count; i++)
        {
            int entry = start + 2 + i * 12;
            result.Add(new TiffEntry(
                ReadU16(data, entry, bigEndian),
                ReadU16(data, entry + 2, bigEndian),
                ReadU32(data, entry + 4, bigEndian),
                ReadU32(data, entry + 8, bigEndian)));
        }
        entries = result;
        return true;
    }

    private static ushort ReadU16(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(offset, 2))
            : BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, 2));
    }

    private static uint ReadU32(byte[] data, int offset, bool bigEndian)
    {
        return bigEndian
            ? BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(offset, 4))
            : BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, 4));
    }

    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_apple_inject_makernote_jpeg")]
    private static extern unsafe NativeResult lpb_apple_inject_makernote_jpeg(
        nint context, byte* input, nuint inputSize, byte* makernote, nuint makernoteSize, byte* output, nuint outputSize, out nuint outWritten);

    [DllImport(NativeMethods.LibraryName, CallingConvention = CallingConvention.Cdecl, EntryPoint = "lpb_apple_inject_makernote_heic")]
    private static extern unsafe NativeResult lpb_apple_inject_makernote_heic(
        nint context, byte* input, nuint inputSize, byte* makernote, nuint makernoteSize, byte* output, nuint outputSize, out nuint outWritten);

    public static unsafe bool TryInjectMakerNoteIntoJpeg(byte[] imageBytes, byte[] makerNote, out byte[]? output, out string? error)
    {
        output = null;
        error = null;
        nint context = nint.Zero;
        try
        {
            byte[] prepared = (byte[])imageBytes.Clone();
            if (!TryPrepareJpegOwner(prepared, out error))
            {
                return false;
            }
            if (NativeMethods.CreateContext(nint.Zero, out context) != NativeResult.Ok) return false;

            int expectedSize = imageBytes.Length + makerNote.Length + 1024;
            byte[] outBuf = new byte[expectedSize];
            NativeResult res;
            nuint written;

            fixed (byte* pIn = prepared)
            fixed (byte* pMn = makerNote)
            fixed (byte* pOut = outBuf)
            {
                res = lpb_apple_inject_makernote_jpeg(
                    context, pIn, (nuint)prepared.Length,
                    pMn, (nuint)makerNote.Length, 
                    pOut, (nuint)outBuf.Length, out written);
            }

            if (res == NativeResult.BufferTooSmall)
            {
                outBuf = new byte[(int)written];
                fixed (byte* pIn = prepared)
                fixed (byte* pMn = makerNote)
                fixed (byte* pOut = outBuf)
                {
                    res = lpb_apple_inject_makernote_jpeg(
                        context, pIn, (nuint)prepared.Length,
                        pMn, (nuint)makerNote.Length, 
                        pOut, (nuint)outBuf.Length, out written);
                }
            }

            if (res == NativeResult.Ok)
            {
                output = new byte[(int)written];
                Array.Copy(outBuf, output, (int)written);
                return true;
            }
            error = ReadLastError(context) ?? $"Native error {res}";
            return false;
        }
        finally
        {
            if (context != nint.Zero) { NativeMethods.DestroyContext(context); }
        }
    }

    public static unsafe bool TryInjectMakerNoteIntoHeic(byte[] imageBytes, byte[] makerNote, out byte[]? output, out string? error)
    {
        output = null;
        error = null;
        nint context = nint.Zero;
        try
        {
            byte[] prepared = (byte[])imageBytes.Clone();
            bool hasExif = NativeHeifBoxParser.TryLocateExifItem(
                prepared, out _, out _, out string? exifLocateError);
            if (hasExif)
            {
                if (!TryPrepareHeifOwner(prepared, out error))
                {
                    return false;
                }
            }
            else if (!string.Equals(
                exifLocateError,
                "Confirmed absent: no Exif item in validated iinf.",
                StringComparison.Ordinal))
            {
                error = exifLocateError ?? "HEIF Exif item ownership could not be validated.";
                return false;
            }
            if (NativeMethods.CreateContext(nint.Zero, out context) != NativeResult.Ok) return false;

            int expectedSize = imageBytes.Length + makerNote.Length + 1024;
            byte[] outBuf = new byte[expectedSize];
            NativeResult res;
            nuint written;

            fixed (byte* pIn = prepared)
            fixed (byte* pMn = makerNote)
            fixed (byte* pOut = outBuf)
            {
                res = lpb_apple_inject_makernote_heic(
                    context, pIn, (nuint)prepared.Length,
                    pMn, (nuint)makerNote.Length, 
                    pOut, (nuint)outBuf.Length, out written);
            }

            if (res == NativeResult.BufferTooSmall)
            {
                outBuf = new byte[(int)written];
                fixed (byte* pIn = prepared)
                fixed (byte* pMn = makerNote)
                fixed (byte* pOut = outBuf)
                {
                    res = lpb_apple_inject_makernote_heic(
                        context, pIn, (nuint)prepared.Length,
                        pMn, (nuint)makerNote.Length, 
                        pOut, (nuint)outBuf.Length, out written);
                }
            }

            if (res == NativeResult.Ok)
            {
                output = new byte[(int)written];
                Array.Copy(outBuf, output, (int)written);
                return true;
            }
            error = ReadLastError(context) ?? $"Native error {res}";
            return false;
        }
        finally
        {
            if (context != nint.Zero) { NativeMethods.DestroyContext(context); }
        }
    }

    private static string? ReadLastError(nint context)
    {
        NativeResult sizeResult = NativeMethods.GetLastError(
            context, nint.Zero, 0, out nuint requiredSize);
        if (sizeResult != NativeResult.BufferTooSmall || requiredSize <= 1)
            return null;

        nint buffer = Marshal.AllocHGlobal(checked((nint)requiredSize));
        try
        {
            return NativeMethods.GetLastError(context, buffer, requiredSize, out _) == NativeResult.Ok
                ? Marshal.PtrToStringUTF8(buffer)
                : null;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }
}
