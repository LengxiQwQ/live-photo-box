using LivePhotoBox.Interop;
using LivePhotoBox.Services.Protocols;
using System.Buffers.Binary;
using Xunit;
using System;

namespace LivePhotoBox.Core.Tests;

[Trait("Category", "NativeDifferential")]
public sealed class HeifBoxParserDifferentialTests
{
    private static byte[] CreateValidExifFile()
    {
        byte[] infePayload = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(infePayload.AsSpan(0), 42);
        "Exif"u8.CopyTo(infePayload.AsSpan(4));
        byte[] infe = BuildFullBox("infe", 2, 0, infePayload);
        byte[] iinfPayload = new byte[2 + infe.Length];
        BinaryPrimitives.WriteUInt16BigEndian(iinfPayload.AsSpan(0), 1);
        infe.CopyTo(iinfPayload, 2);
        byte[] iinf = BuildFullBox("iinf", 0, 0, iinfPayload);

        byte[] ilocPayload = new byte[2 + 2 + 18];
        ilocPayload[0] = 0x44;
        BinaryPrimitives.WriteUInt16BigEndian(ilocPayload.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(ilocPayload.AsSpan(4), 42);
        BinaryPrimitives.WriteUInt16BigEndian(ilocPayload.AsSpan(8), 1);
        BinaryPrimitives.WriteUInt32BigEndian(ilocPayload.AsSpan(10), 100);
        BinaryPrimitives.WriteUInt32BigEndian(ilocPayload.AsSpan(14), 200);
        byte[] iloc = BuildFullBox("iloc", 0, 0, ilocPayload);
        byte[] meta = BuildFullBox("meta", 0, 0, [.. iinf, .. iloc]);
        return [.. BuildBox("ftyp", new byte[16]), .. meta, .. new byte[500]];
    }

    private static byte[] BuildBox(string type, byte[] payload)
    {
        byte[] box = new byte[8 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(box, (uint)box.Length);
        box[4] = (byte)type[0];
        box[5] = (byte)type[1];
        box[6] = (byte)type[2];
        box[7] = (byte)type[3];
        payload.CopyTo(box, 8);
        return box;
    }

    private static byte[] BuildFullBox(string type, byte version, uint flags, byte[] payload)
    {
        byte[] fullPayload = new byte[4 + payload.Length];
        fullPayload[0] = version;
        fullPayload[1] = (byte)(flags >> 16);
        fullPayload[2] = (byte)(flags >> 8);
        fullPayload[3] = (byte)flags;
        payload.CopyTo(fullPayload, 4);
        return BuildBox(type, fullPayload);
    }

    [Fact]
    public void LocateExifItem_IsIdenticalToLegacyParser()
    {
        // Construct a mock ISOBMFF meta box containing iinf and iloc
        // iinf: 1 entry, item_id = 42, item_type = "Exif"
        // iloc: 1 entry, item_id = 42, construction_method = 0, offset = 100, length = 200

        byte[] infePayload = new byte[12];
        BinaryPrimitives.WriteUInt16BigEndian(infePayload.AsSpan(0), 42); // item_id
        BinaryPrimitives.WriteUInt16BigEndian(infePayload.AsSpan(2), 0); // item_protection_index
        infePayload[4] = (byte)'E';
        infePayload[5] = (byte)'x';
        infePayload[6] = (byte)'i';
        infePayload[7] = (byte)'f';
        // Null terminated item_name
        byte[] infe = BuildFullBox("infe", 2, 0, infePayload);

        byte[] iinfPayload = new byte[2 + infe.Length];
        BinaryPrimitives.WriteUInt16BigEndian(iinfPayload.AsSpan(0), 1); // entry_count
        infe.CopyTo(iinfPayload, 2);
        byte[] iinf = BuildFullBox("iinf", 0, 0, iinfPayload);

        byte[] ilocPayload = new byte[2 + 2 + 18];
        ilocPayload[0] = (4 << 4) | 4; // offset_size=4, length_size=4
        ilocPayload[1] = (0 << 4) | 0; // base_offset_size=0, index_size=0
        BinaryPrimitives.WriteUInt16BigEndian(ilocPayload.AsSpan(2), 1); // item_count
        
        // Item 0
        BinaryPrimitives.WriteUInt16BigEndian(ilocPayload.AsSpan(4), 42); // item_id
        BinaryPrimitives.WriteUInt16BigEndian(ilocPayload.AsSpan(6), 0); // data_reference_index
        BinaryPrimitives.WriteUInt16BigEndian(ilocPayload.AsSpan(8), 1); // extent_count
        // Extent 0
        BinaryPrimitives.WriteUInt32BigEndian(ilocPayload.AsSpan(10), 100); // extent_offset
        BinaryPrimitives.WriteUInt32BigEndian(ilocPayload.AsSpan(14), 200); // extent_length
        
        byte[] iloc = BuildFullBox("iloc", 0, 0, ilocPayload);

        byte[] metaPayload = [.. iinf, .. iloc];
        byte[] meta = BuildFullBox("meta", 0, 0, metaPayload);
        
        // Wrap in a larger file payload to verify safe parsing
        byte[] ftyp = BuildBox("ftyp", new byte[16]);
        byte[] file = [.. ftyp, .. meta, .. new byte[500]];

        // Test Legacy C# parser
        bool legacySuccess = HeifBoxParser.TryLocateExifItem(file, out long legacyOffset, out long legacyLength, out string? legacyError);
        
        // Test Native C++ parser
        bool nativeSuccess = NativeHeifBoxParser.TryLocateExifItem(file, out long nativeOffset, out long nativeLength, out string? nativeError);

        Assert.True(legacySuccess, $"Legacy parser failed: {legacyError}");
        Assert.True(nativeSuccess, $"Native parser failed: {nativeError}");

        Assert.Equal(100L, legacyOffset);
        Assert.Equal(200L, legacyLength);

        Assert.Equal(legacyOffset, nativeOffset);
        Assert.Equal(legacyLength, nativeLength);
    }

    [Fact]
    public void LocateXmpItem_LocatesRdfMimeItem()
    {
        const ushort itemId = 43;
        byte[] contentType = "application/rdf+xml\0"u8.ToArray();
        byte[] infePayload = new byte[8 + contentType.Length];
        BinaryPrimitives.WriteUInt16BigEndian(infePayload.AsSpan(0), itemId);
        // item_protection_index remains zero at bytes 2..3.
        "mime"u8.CopyTo(infePayload.AsSpan(4));
        contentType.CopyTo(infePayload, 8);
        byte[] infe = BuildFullBox("infe", 2, 0, infePayload);

        byte[] iinfPayload = new byte[2 + infe.Length];
        BinaryPrimitives.WriteUInt16BigEndian(iinfPayload.AsSpan(0), 1);
        infe.CopyTo(iinfPayload, 2);
        byte[] iinf = BuildFullBox("iinf", 0, 0, iinfPayload);

        byte[] ilocPayload = new byte[2 + 2 + 18];
        ilocPayload[0] = (4 << 4) | 4; // offset_size=4, length_size=4
        BinaryPrimitives.WriteUInt16BigEndian(ilocPayload.AsSpan(2), 1);
        BinaryPrimitives.WriteUInt16BigEndian(ilocPayload.AsSpan(4), itemId);
        BinaryPrimitives.WriteUInt16BigEndian(ilocPayload.AsSpan(8), 1); // extent_count
        BinaryPrimitives.WriteUInt32BigEndian(ilocPayload.AsSpan(10), 100);
        BinaryPrimitives.WriteUInt32BigEndian(ilocPayload.AsSpan(14), 200);
        byte[] iloc = BuildFullBox("iloc", 0, 0, ilocPayload);

        byte[] file = [.. BuildBox("ftyp", new byte[16]), .. BuildFullBox("meta", 0, 0, [.. iinf, .. iloc]), .. new byte[500]];

        Assert.True(NativeHeifBoxParser.TryLocateXmpItem(
            file, out long offset, out long length, out string? error), error);
        Assert.Equal(100L, offset);
        Assert.Equal(200L, length);
    }

    [Fact]
    public void LocateExifItem_RejectsUnsupportedOrOutOfRangeIloc()
    {
        byte[] file = CreateValidExifFile();
        int iloc = file.AsSpan().IndexOf("iloc"u8) - 4;
        Assert.True(iloc >= 8);

        // iloc offset_size=3 is not a layout this implementation can safely
        // interpret. It must not guess a field width.
        file[iloc + 12] = 0x34;
        Assert.False(NativeHeifBoxParser.TryLocateExifItem(file, out _, out _, out _));

        file = CreateValidExifFile();
        iloc = file.AsSpan().IndexOf("iloc"u8) - 4;
        // extent_count is at box + 20 for this version-0, 4/4-byte fixture.
        BinaryPrimitives.WriteUInt16BigEndian(file.AsSpan(iloc + 20), 2);
        Assert.False(NativeHeifBoxParser.TryLocateExifItem(file, out _, out _, out _));

        file = CreateValidExifFile();
        iloc = file.AsSpan().IndexOf("iloc"u8) - 4;
        BinaryPrimitives.WriteUInt32BigEndian(file.AsSpan(iloc + 22), uint.MaxValue);
        Assert.False(NativeHeifBoxParser.TryLocateExifItem(file, out _, out _, out _));
    }
}
