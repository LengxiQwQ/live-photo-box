using System;
using System.Buffers.Binary;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading.Tasks;
using System.Xml.Linq;
using LivePhotoBox.Interop;
using Xunit;

namespace LivePhotoBox.Core.Tests.Media;

public class NativeGainMapReassemblyTests : IDisposable
{
    private readonly string _tempDir;

    public NativeGainMapReassemblyTests()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), "lpb_gainmap_test_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(_tempDir);
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_tempDir))
            {
                Directory.Delete(_tempDir, recursive: true);
            }
        }
        catch
        {
            // Ignore cleanup failures in test teardown
        }
    }

    [Fact]
    public async Task ReassembleJpegGainMapAsync_ValidJpegs_CreatesCombinedFile()
    {
        string primaryPath = Path.Combine(_tempDir, "primary.jpg");
        string gainmapPath = Path.Combine(_tempDir, "gainmap.jpg");
        string outputPath = Path.Combine(_tempDir, "output.jpg");

        byte[] gainmapBytes = CreateGainMapJpeg();
        byte[] primaryBytes = CreateMpfPrimary(gainmapBytes.Length,
            stalePrimarySizeDelta: 456, staleGainMapOffsetDelta: 1_000);

        await File.WriteAllBytesAsync(primaryPath, primaryBytes);
        await File.WriteAllBytesAsync(gainmapPath, gainmapBytes);

        string expectedSha = Convert.ToHexString(SHA256.HashData(gainmapBytes));
        await NativeMediaService.ReassembleJpegGainMapAsync(primaryPath, gainmapPath, outputPath, expectedSha);

        Assert.True(File.Exists(outputPath));
        byte[] outputBytes = await File.ReadAllBytesAsync(outputPath);
        MpfLayout layout = ReadMpfLayout(outputBytes);
        Assert.Equal((uint)layout.EoiEnd, layout.PrimarySize);
        Assert.Equal(0u, layout.PrimaryOffset);
        Assert.Equal((uint)gainmapBytes.Length, layout.GainMapSize);
        Assert.Equal((uint)primaryBytes.Length, layout.GainMapOffset);
        Assert.Equal(gainmapBytes, outputBytes.AsSpan(primaryBytes.Length).ToArray());
        Assert.Equal(expectedSha, Convert.ToHexString(SHA256.HashData(outputBytes.AsSpan(primaryBytes.Length))));

        byte[] expectedPrimary = (byte[])primaryBytes.Clone();
        MpfLayout originalLayout = ReadMpfLayout(primaryBytes);
        WriteU32(expectedPrimary, originalLayout.PrimarySizeFieldOffset, (uint)originalLayout.EoiEnd);
        WriteU32(expectedPrimary, originalLayout.GainMapSizeFieldOffset, (uint)gainmapBytes.Length);
        WriteU32(expectedPrimary, originalLayout.GainMapOffsetFieldOffset,
            (uint)(primaryBytes.Length - originalLayout.TiffOffset));
        byte[] expectedBytes = [.. expectedPrimary, .. gainmapBytes];
        Assert.Equal(expectedBytes, outputBytes);
    }

    [Fact]
    public async Task ReassembleJpegGainMapAsync_ThreeEntryIndex_RemovesOnlyOriginalMembership()
    {
        string primaryPath = Path.Combine(_tempDir, "three_entry_primary.jpg");
        string gainmapPath = Path.Combine(_tempDir, "gainmap.jpg");
        string outputPath = Path.Combine(_tempDir, "three_entry_output.jpg");
        byte[] gainmapBytes = CreateGainMapJpeg();
        byte[] primaryBytes = CreateMpfPrimary(gainmapBytes.Length, includeOriginal: true);
        string expectedSha = Convert.ToHexString(SHA256.HashData(gainmapBytes));

        await File.WriteAllBytesAsync(primaryPath, primaryBytes);
        await File.WriteAllBytesAsync(gainmapPath, gainmapBytes);
        await NativeMediaService.ReassembleJpegGainMapAsync(primaryPath, gainmapPath, outputPath, expectedSha);

        byte[] outputBytes = await File.ReadAllBytesAsync(outputPath);
        MpfLayout layout = ReadMpfLayout(outputBytes);
        Assert.Equal(2u, layout.ImageCount);
        Assert.Equal(32u, layout.EntriesByteCount);
        Assert.Equal((uint)layout.EoiEnd, layout.PrimarySize);
        Assert.Equal(0u, layout.PrimaryOffset);
        Assert.Equal((uint)gainmapBytes.Length, layout.GainMapSize);
        Assert.Equal((uint)primaryBytes.Length, layout.GainMapOffset);
        Assert.Equal(expectedSha, Convert.ToHexString(SHA256.HashData(
            outputBytes.AsSpan(checked((int)layout.GainMapOffset), checked((int)layout.GainMapSize)))), ignoreCase: true);
        Assert.Equal(gainmapBytes, outputBytes.AsSpan(checked((int)layout.GainMapOffset), gainmapBytes.Length).ToArray());

        MpfLayout sourceLayout = ReadMpfLayout(primaryBytes);
        Assert.Equal(3u, sourceLayout.ImageCount);
        Assert.All(outputBytes.AsSpan(layout.EntriesArrayOffset + 32, 16).ToArray(), value => Assert.Equal(0, value));
        Assert.Equal(ReadJpegSegment(primaryBytes, 0xC0), ReadJpegSegment(outputBytes, 0xC0));
        Assert.Equal(ReadJpegScanAndEoi(primaryBytes), ReadJpegScanAndEoi(outputBytes));

        string xmp = ReadStandardXmp(outputBytes);
        Assert.Equal(ReadStandardXmp(primaryBytes).Length, xmp.Length);
        Assert.Equal(["Primary", "GainMap"], ReadNeutralDirectorySemantics(xmp));
        Assert.Contains("custom:Keep=\"retained\"", xmp, StringComparison.Ordinal);
        Assert.Contains("retained", xmp, StringComparison.Ordinal);
    }

    [Fact]
    public async Task ReassembleJpegGainMapAsync_MpfAndXmpCountsDisagree_FailsAndLeavesNoOutput()
    {
        string primaryPath = Path.Combine(_tempDir, "count_mismatch_primary.jpg");
        string gainmapPath = Path.Combine(_tempDir, "gainmap.jpg");
        string outputPath = Path.Combine(_tempDir, "count_mismatch_output.jpg");
        byte[] gainmap = CreateGainMapJpeg();

        await File.WriteAllBytesAsync(primaryPath,
            CreateMpfPrimary(gainmap.Length, directoryIncludesOriginal: true));
        await File.WriteAllBytesAsync(gainmapPath, gainmap);
        await Assert.ThrowsAnyAsync<Exception>(() => NativeMediaService.ReassembleJpegGainMapAsync(
            primaryPath, gainmapPath, outputPath, Convert.ToHexString(SHA256.HashData(gainmap))));

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ReassembleJpegGainMapAsync_GainMapDirectoryOrdinalHasDifferentMpfSize_FailsClosed()
    {
        string primaryPath = Path.Combine(_tempDir, "ordinal_size_mismatch_primary.jpg");
        string gainmapPath = Path.Combine(_tempDir, "gainmap.jpg");
        string outputPath = Path.Combine(_tempDir, "ordinal_size_mismatch_output.jpg");
        byte[] gainmap = CreateGainMapJpeg();

        await File.WriteAllBytesAsync(primaryPath,
            CreateMpfPrimary(gainmap.Length, mismatchedGainMapMpfSize: true));
        await File.WriteAllBytesAsync(gainmapPath, gainmap);
        await Assert.ThrowsAnyAsync<Exception>(() => NativeMediaService.ReassembleJpegGainMapAsync(
            primaryPath, gainmapPath, outputPath, Convert.ToHexString(SHA256.HashData(gainmap))));

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ReassembleJpegGainMapAsync_ExtraItemStillPointsIntoOwner_FailsAndLeavesNoOutput()
    {
        string primaryPath = Path.Combine(_tempDir, "overlapping_extra_primary.jpg");
        string gainmapPath = Path.Combine(_tempDir, "gainmap.jpg");
        string outputPath = Path.Combine(_tempDir, "overlapping_extra_output.jpg");
        byte[] gainmap = CreateGainMapJpeg();

        await File.WriteAllBytesAsync(primaryPath,
            CreateMpfPrimary(gainmap.Length, includeOriginal: true, originalOffsetInsideOwner: true));
        await File.WriteAllBytesAsync(gainmapPath, gainmap);
        await Assert.ThrowsAnyAsync<Exception>(() => NativeMediaService.ReassembleJpegGainMapAsync(
            primaryPath, gainmapPath, outputPath, Convert.ToHexString(SHA256.HashData(gainmap))));

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ReassembleJpegGainMapAsync_ExtraItemWithoutExactLength_FailsAndLeavesNoOutput()
    {
        string primaryPath = Path.Combine(_tempDir, "malformed_extra_primary.jpg");
        string gainmapPath = Path.Combine(_tempDir, "gainmap.jpg");
        string outputPath = Path.Combine(_tempDir, "malformed_extra_output.jpg");
        byte[] gainmap = CreateGainMapJpeg();

        await File.WriteAllBytesAsync(primaryPath,
            CreateMpfPrimary(gainmap.Length, includeOriginal: true, malformedOriginalItem: true));
        await File.WriteAllBytesAsync(gainmapPath, gainmap);
        await Assert.ThrowsAnyAsync<Exception>(() => NativeMediaService.ReassembleJpegGainMapAsync(
            primaryPath, gainmapPath, outputPath, Convert.ToHexString(SHA256.HashData(gainmap))));

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ReassembleJpegGainMapAsync_DuplicateMpf_FailsAndLeavesNoOutput()
    {
        string primaryPath = Path.Combine(_tempDir, "duplicate_mpf.jpg");
        string gainmapPath = Path.Combine(_tempDir, "gainmap.jpg");
        string outputPath = Path.Combine(_tempDir, "output.jpg");
        byte[] gainmap = CreateGainMapJpeg();

        await File.WriteAllBytesAsync(primaryPath, CreateMpfPrimary(gainmap.Length, duplicateMpf: true));
        await File.WriteAllBytesAsync(gainmapPath, gainmap);

        await Assert.ThrowsAnyAsync<Exception>(() => NativeMediaService.ReassembleJpegGainMapAsync(
            primaryPath, gainmapPath, outputPath, Convert.ToHexString(SHA256.HashData(gainmap))));

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ReassembleJpegGainMapAsync_MalformedMpf_FailsAndLeavesNoOutput()
    {
        string primaryPath = Path.Combine(_tempDir, "malformed_mpf.jpg");
        string gainmapPath = Path.Combine(_tempDir, "gainmap.jpg");
        string outputPath = Path.Combine(_tempDir, "output.jpg");
        byte[] gainmap = CreateGainMapJpeg();

        await File.WriteAllBytesAsync(primaryPath, CreateMpfPrimary(gainmap.Length, malformedMpf: true));
        await File.WriteAllBytesAsync(gainmapPath, gainmap);

        await Assert.ThrowsAnyAsync<Exception>(() => NativeMediaService.ReassembleJpegGainMapAsync(
            primaryPath, gainmapPath, outputPath, Convert.ToHexString(SHA256.HashData(gainmap))));

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ReassembleJpegGainMapAsync_InvalidPrimaryMagic_ThrowsAndLeavesNoOutput()
    {
        string primaryPath = Path.Combine(_tempDir, "invalid_primary.jpg");
        string gainmapPath = Path.Combine(_tempDir, "gainmap.jpg");
        string outputPath = Path.Combine(_tempDir, "output.jpg");

        byte[] invalidPrimary = [0x00, 0x00, 0x01, 0x02];
        byte[] validGainmap = CreateGainMapJpeg();

        await File.WriteAllBytesAsync(primaryPath, invalidPrimary);
        await File.WriteAllBytesAsync(gainmapPath, validGainmap);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NativeMediaService.ReassembleJpegGainMapAsync(primaryPath, gainmapPath, outputPath,
                Convert.ToHexString(SHA256.HashData(validGainmap))));

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ReassembleJpegGainMapAsync_InvalidGainmapMagic_ThrowsAndLeavesNoOutput()
    {
        string primaryPath = Path.Combine(_tempDir, "primary.jpg");
        string gainmapPath = Path.Combine(_tempDir, "invalid_gainmap.jpg");
        string outputPath = Path.Combine(_tempDir, "output.jpg");

        byte[] validPrimary = CreateMpfPrimary(gainmapBytes: 4);
        byte[] invalidGainmap = [0x89, 0x50, 0x4E, 0x47];

        await File.WriteAllBytesAsync(primaryPath, validPrimary);
        await File.WriteAllBytesAsync(gainmapPath, invalidGainmap);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NativeMediaService.ReassembleJpegGainMapAsync(primaryPath, gainmapPath, outputPath,
                Convert.ToHexString(SHA256.HashData(invalidGainmap))));

        Assert.False(File.Exists(outputPath));
    }

    [Fact]
    public async Task ReassembleJpegGainMapAsync_OutputAlreadyExists_FailsExclusiveCreate()
    {
        string primaryPath = Path.Combine(_tempDir, "primary.jpg");
        string gainmapPath = Path.Combine(_tempDir, "gainmap.jpg");
        string outputPath = Path.Combine(_tempDir, "output.jpg");

        byte[] validGainmap = CreateGainMapJpeg();
        byte[] validPrimary = CreateMpfPrimary(validGainmap.Length);

        await File.WriteAllBytesAsync(primaryPath, validPrimary);
        await File.WriteAllBytesAsync(gainmapPath, validGainmap);
        await File.WriteAllBytesAsync(outputPath, [0x01, 0x02]);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NativeMediaService.ReassembleJpegGainMapAsync(primaryPath, gainmapPath, outputPath,
                Convert.ToHexString(SHA256.HashData(validGainmap))));
    }

    [Fact]
    public async Task ReassembleJpegGainMapAsync_ExpectedIdentityRejectsSameLengthReplacement()
    {
        string primaryPath = Path.Combine(_tempDir, "primary.jpg");
        string gainmapPath = Path.Combine(_tempDir, "gainmap.jpg");
        string outputPath = Path.Combine(_tempDir, "output.jpg");
        byte[] gainmapA = [0xFF, 0xD8, 0xFF, 0xD9, 0x00, 0x00];
        byte[] gainmapB = [0xFF, 0xD8, 0xFF, 0xD9, 0x00, 0x01];
        byte[] primaryBytes = CreateMpfPrimary(gainmapA.Length);

        await File.WriteAllBytesAsync(primaryPath, primaryBytes);
        await File.WriteAllBytesAsync(gainmapPath, gainmapB);

        await Assert.ThrowsAnyAsync<Exception>(() =>
            NativeMediaService.ReassembleJpegGainMapAsync(primaryPath, gainmapPath, outputPath,
                Convert.ToHexString(SHA256.HashData(gainmapA))));

        Assert.False(File.Exists(outputPath));
    }

    private static byte[] CreateGainMapJpeg() =>
        [0xFF, 0xD8, 0xFF, 0xD9, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00,
         0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00, 0x00];

    private static byte[] CreateMpfPrimary(int gainmapBytes, int stalePrimarySizeDelta = 0,
        int staleGainMapOffsetDelta = 0, bool duplicateMpf = false, bool malformedMpf = false,
        bool includeOriginal = false, bool directoryIncludesOriginal = false,
        bool mismatchedGainMapMpfSize = false, bool originalOffsetInsideOwner = false,
        bool malformedOriginalItem = false)
    {
        const int tiffOffset = 10;
        const int originalLength = 8_178_321;
        int imageCount = includeOriginal ? 3 : 2;
        int entriesByteCount = imageCount * 16;
        int entriesArrayOffsetInTiff = 38;
        byte[] tiff = new byte[entriesArrayOffsetInTiff + entriesByteCount];
        tiff[0] = (byte)'I';
        tiff[1] = (byte)'I';
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(2, 2), 42);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(4, 4), 8);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(8, 2), 2);
        int firstIfdEntry = 10;
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(firstIfdEntry, 2), 0xB001);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(firstIfdEntry + 2, 2), 4);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(firstIfdEntry + 4, 4), 1);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(firstIfdEntry + 8, 4), (uint)imageCount);
        int secondIfdEntry = firstIfdEntry + 12;
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(secondIfdEntry, 2), 0xB002);
        BinaryPrimitives.WriteUInt16LittleEndian(tiff.AsSpan(secondIfdEntry + 2, 2), 7);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(secondIfdEntry + 4, 4), (uint)entriesByteCount);
        BinaryPrimitives.WriteUInt32LittleEndian(tiff.AsSpan(secondIfdEntry + 8, 4), (uint)entriesArrayOffsetInTiff);

        var primary = new System.Collections.Generic.List<byte> { 0xFF, 0xD8 };
        byte[] mpfPayload = [.. "MPF\0"u8, .. tiff];
        byte[] mpfSegment = CreateJpegSegment(0xE2, mpfPayload);
        primary.AddRange(mpfSegment);
        if (duplicateMpf) primary.AddRange(mpfSegment);

        string xmp = CreateXmp(gainmapBytes, includeOriginal || directoryIncludesOriginal,
            malformedOriginalItem);
        byte[] xmpPayload = [.. Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0"), .. Encoding.UTF8.GetBytes(xmp)];
        AppendJpegSegment(primary, 0xE1, xmpPayload);
        AppendJpegSegment(primary, 0xC0, [8, 0, 1, 0, 1, 1, 1, 0x11, 0]);
        AppendJpegSegment(primary, 0xDA, [1, 1, 0, 0, 63, 0]);
        primary.Add(0x00);
        primary.Add(0xFF);
        primary.Add(0xD9);

        byte[] bytes = primary.ToArray();
        int eoiEnd = bytes.Length;
        int entriesArrayOffset = tiffOffset + entriesArrayOffsetInTiff;
        WriteU32(bytes, entriesArrayOffset + 4, (uint)(eoiEnd + stalePrimarySizeDelta));
        WriteU32(bytes, entriesArrayOffset + 8, 0);
        WriteU32(bytes, entriesArrayOffset + 16 + 4,
            (uint)(gainmapBytes + (mismatchedGainMapMpfSize ? 1 : 0)));
        WriteU32(bytes, entriesArrayOffset + 16 + 8,
            (uint)(eoiEnd + staleGainMapOffsetDelta - tiffOffset));
        if (includeOriginal)
        {
            int originalEntryOffset = entriesArrayOffset + 32;
            WriteU32(bytes, originalEntryOffset + 4, originalLength);
            uint originalAbsoluteOffset = originalOffsetInsideOwner
                ? (uint)(tiffOffset + 64)
                : checked((uint)(eoiEnd + 4_096));
            WriteU32(bytes, originalEntryOffset + 8, originalAbsoluteOffset - tiffOffset);
        }
        if (malformedMpf)
            WriteU32(bytes, tiffOffset + secondIfdEntry + 8, uint.MaxValue - 8);
        return bytes;
    }

    private static string CreateXmp(int gainmapBytes, bool includeOriginal, bool malformedOriginalItem)
    {
        const int originalLength = 8_178_321;
        var xml = new StringBuilder();
        xml.Append("<x:xmpmeta xmlns:x=\"adobe:ns:meta/\"><rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">");
        xml.Append("<rdf:Description xmlns:Container=\"http://ns.google.com/photos/1.0/container/\" ");
        xml.Append("xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\" ");
        xml.Append("xmlns:custom=\"urn:live-photo-box:test\" custom:Keep=\"retained\">");
        xml.Append("<Container:Directory><rdf:Seq>");
        AppendDirectoryItem(xml, "Primary", 0, hasLength: true);
        AppendDirectoryItem(xml, "GainMap", gainmapBytes, hasLength: true);
        if (includeOriginal)
            AppendDirectoryItem(xml, "Original", originalLength, hasLength: !malformedOriginalItem);
        xml.Append("</rdf:Seq></Container:Directory>");
        xml.Append("</rdf:Description></rdf:RDF></x:xmpmeta>");
        return xml.ToString();
    }

    private static void AppendDirectoryItem(StringBuilder xml, string semantic, int length, bool hasLength)
    {
        xml.Append("<rdf:li><Container:Item Item:Semantic=\"").Append(semantic)
            .Append("\" Item:Mime=\"image/jpeg\"");
        if (hasLength) xml.Append(" Item:Length=\"").Append(length).Append('\"');
        xml.Append("/></rdf:li>");
    }

    private static byte[] CreateJpegSegment(byte marker, byte[] payload)
    {
        byte[] segment = new byte[payload.Length + 4];
        segment[0] = 0xFF;
        segment[1] = marker;
        BinaryPrimitives.WriteUInt16BigEndian(segment.AsSpan(2, 2), checked((ushort)(payload.Length + 2)));
        payload.CopyTo(segment.AsSpan(4));
        return segment;
    }

    private static void AppendJpegSegment(System.Collections.Generic.List<byte> bytes, byte marker, byte[] payload) =>
        bytes.AddRange(CreateJpegSegment(marker, payload));

    private static MpfLayout ReadMpfLayout(byte[] bytes)
    {
        int cursor = 2;
        int foundTiffOffset = -1;
        int eoiEnd = -1;
        int primarySizeFieldOffset = -1;
        int gainmapSizeFieldOffset = -1;
        int gainmapOffsetFieldOffset = -1;
        int imageCountFieldOffset = -1;
        int entriesByteCountFieldOffset = -1;
        int entriesArrayOffset = -1;
        uint imageCount = 0;
        uint entriesByteCount = 0;
        uint primarySize = 0;
        uint primaryOffset = 0;
        uint gainmapSize = 0;
        uint gainmapRelativeOffset = 0;
        while (cursor + 1 < bytes.Length)
        {
            Assert.Equal(0xFF, bytes[cursor]);
            while (cursor < bytes.Length && bytes[cursor] == 0xFF) cursor++;
            byte marker = bytes[cursor++];
            if (marker == 0xD9)
            {
                eoiEnd = cursor;
                break;
            }
            Assert.InRange(cursor + 2, 0, bytes.Length);
            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(cursor, 2));
            Assert.True(segmentLength >= 2 && segmentLength <= bytes.Length - cursor);
            int payloadStart = cursor + 2;
            int payloadEnd = cursor + segmentLength;
            if (marker == 0xE2 && payloadEnd - payloadStart >= 4 &&
                bytes.AsSpan(payloadStart, 4).SequenceEqual("MPF\0"u8))
            {
                Assert.Equal(-1, foundTiffOffset);
                foundTiffOffset = payloadStart + 4;
                bool littleEndian = bytes[foundTiffOffset] == 'I' && bytes[foundTiffOffset + 1] == 'I';
                Assert.True(littleEndian || (bytes[foundTiffOffset] == 'M' && bytes[foundTiffOffset + 1] == 'M'));
                int ifd = foundTiffOffset + checked((int)ReadU32(bytes, foundTiffOffset + 4, littleEndian));
                ushort ifdEntryCount = ReadU16(bytes, ifd, littleEndian);
                int mpEntryValueOffset = -1;
                for (int index = 0; index < ifdEntryCount; index++)
                {
                    int field = ifd + 2 + index * 12;
                    ushort tag = ReadU16(bytes, field, littleEndian);
                    uint valueCount = ReadU32(bytes, field + 4, littleEndian);
                    uint value = ReadU32(bytes, field + 8, littleEndian);
                    if (tag == 0xB001)
                    {
                        Assert.Equal(1u, valueCount);
                        imageCount = value;
                        imageCountFieldOffset = field + 8;
                    }
                    else if (tag == 0xB002)
                    {
                        entriesByteCount = valueCount;
                        entriesByteCountFieldOffset = field + 4;
                        mpEntryValueOffset = checked((int)value);
                    }
                }
                Assert.InRange(imageCount, 2u, 9u);
                Assert.Equal(imageCount * 16, entriesByteCount);
                Assert.True(mpEntryValueOffset >= 0);
                entriesArrayOffset = foundTiffOffset + mpEntryValueOffset;
                Assert.True(entriesArrayOffset >= 0 && entriesArrayOffset + entriesByteCount <= payloadEnd);
                primarySizeFieldOffset = entriesArrayOffset + 4;
                gainmapSizeFieldOffset = entriesArrayOffset + 16 + 4;
                gainmapOffsetFieldOffset = entriesArrayOffset + 16 + 8;
                primarySize = ReadU32(bytes, entriesArrayOffset + 4, littleEndian);
                primaryOffset = ReadU32(bytes, entriesArrayOffset + 8, littleEndian);
                gainmapSize = ReadU32(bytes, entriesArrayOffset + 16 + 4, littleEndian);
                gainmapRelativeOffset = ReadU32(bytes, entriesArrayOffset + 16 + 8, littleEndian);
            }
            cursor = payloadEnd;
            if (marker == 0xDA)
            {
                while (cursor + 1 < bytes.Length)
                {
                    if (bytes[cursor] != 0xFF)
                    {
                        cursor++;
                        continue;
                    }
                    int markerCode = cursor + 1;
                    while (markerCode < bytes.Length && bytes[markerCode] == 0xFF) markerCode++;
                    Assert.True(markerCode < bytes.Length);
                    byte scanMarker = bytes[markerCode];
                    if (scanMarker == 0x00 || scanMarker is >= 0xD0 and <= 0xD7)
                    {
                        cursor = markerCode + 1;
                        continue;
                    }
                    if (scanMarker == 0xD9)
                    {
                        eoiEnd = markerCode + 1;
                        break;
                    }
                    cursor = markerCode + 1;
                }
                break;
            }
        }
        Assert.True(foundTiffOffset >= 0 && eoiEnd > 0);
        return new MpfLayout(eoiEnd, foundTiffOffset, imageCount, entriesByteCount, primarySize,
            primaryOffset, gainmapSize, checked((uint)(foundTiffOffset + gainmapRelativeOffset)),
            imageCountFieldOffset, entriesByteCountFieldOffset, entriesArrayOffset,
            primarySizeFieldOffset, gainmapSizeFieldOffset, gainmapOffsetFieldOffset);
    }

    private static string ReadStandardXmp(byte[] bytes)
    {
        byte[] header = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
        int cursor = 2;
        string? found = null;
        while (cursor + 1 < bytes.Length)
        {
            Assert.Equal(0xFF, bytes[cursor]);
            while (cursor < bytes.Length && bytes[cursor] == 0xFF) cursor++;
            byte marker = bytes[cursor++];
            if (marker is 0xDA or 0xD9) break;
            if (marker == 0x01) continue;
            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(cursor, 2));
            Assert.True(segmentLength >= 2 && segmentLength <= bytes.Length - cursor);
            int payloadStart = cursor + 2;
            int payloadLength = segmentLength - 2;
            if (marker == 0xE1 && payloadLength >= header.Length &&
                bytes.AsSpan(payloadStart, header.Length).SequenceEqual(header))
            {
                Assert.Null(found);
                found = Encoding.UTF8.GetString(bytes, payloadStart + header.Length, payloadLength - header.Length);
            }
            cursor += segmentLength;
        }
        return Assert.IsType<string>(found);
    }

    private static byte[] ReadJpegSegment(byte[] bytes, byte requestedMarker)
    {
        int cursor = 2;
        while (cursor + 1 < bytes.Length)
        {
            Assert.Equal(0xFF, bytes[cursor]);
            int markerStart = cursor;
            while (cursor < bytes.Length && bytes[cursor] == 0xFF) cursor++;
            byte marker = bytes[cursor++];
            if (marker is 0xDA or 0xD9) break;
            if (marker == 0x01) continue;
            int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(cursor, 2));
            Assert.True(segmentLength >= 2 && segmentLength <= bytes.Length - cursor);
            int payloadEnd = cursor + segmentLength;
            if (marker == requestedMarker) return bytes.AsSpan(markerStart, payloadEnd - markerStart).ToArray();
            cursor = payloadEnd;
        }
        throw new Xunit.Sdk.XunitException($"JPEG marker 0xFF{requestedMarker:X2} was not found before SOS.");
    }

    private static byte[] ReadJpegScanAndEoi(byte[] bytes)
    {
        int cursor = 2;
        while (cursor + 1 < bytes.Length)
        {
            Assert.Equal(0xFF, bytes[cursor]);
            int markerStart = cursor;
            while (cursor < bytes.Length && bytes[cursor] == 0xFF) cursor++;
            byte marker = bytes[cursor++];
            if (marker == 0xDA)
            {
                int segmentLength = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(cursor, 2));
                cursor += segmentLength;
                while (cursor + 1 < bytes.Length)
                {
                    if (bytes[cursor] != 0xFF)
                    {
                        cursor++;
                        continue;
                    }
                    int markerCode = cursor + 1;
                    while (markerCode < bytes.Length && bytes[markerCode] == 0xFF) markerCode++;
                    Assert.True(markerCode < bytes.Length);
                    byte scanMarker = bytes[markerCode];
                    if (scanMarker == 0x00 || scanMarker is >= 0xD0 and <= 0xD7)
                    {
                        cursor = markerCode + 1;
                        continue;
                    }
                    if (scanMarker == 0xD9)
                        return bytes.AsSpan(markerStart, markerCode + 1 - markerStart).ToArray();
                    cursor = markerCode + 1;
                }
                break;
            }
            if (marker is 0xD9 or 0x01) continue;
            int length = BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(cursor, 2));
            cursor += length;
        }
        throw new Xunit.Sdk.XunitException("JPEG SOS scan and EOI were not found.");
    }

    private static string[] ReadNeutralDirectorySemantics(string xml)
    {
        XDocument document = XDocument.Parse(xml);
        XNamespace container = "http://ns.google.com/photos/1.0/container/";
        XNamespace item = "http://ns.google.com/photos/1.0/container/item/";
        XNamespace rdf = "http://www.w3.org/1999/02/22-rdf-syntax-ns#";
        XElement directory = Assert.Single(document.Descendants(container + "Directory"));
        XElement sequence = Assert.Single(directory.Elements(rdf + "Seq"));
        return sequence.Elements(rdf + "li")
            .Select(li => li.Elements(container + "Item").Single().Attribute(item + "Semantic")!.Value)
            .ToArray();
    }

    private static ushort ReadU16(byte[] bytes, int offset, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(offset, 2))
        : BinaryPrimitives.ReadUInt16BigEndian(bytes.AsSpan(offset, 2));

    private static uint ReadU32(byte[] bytes, int offset, bool littleEndian) => littleEndian
        ? BinaryPrimitives.ReadUInt32LittleEndian(bytes.AsSpan(offset, 4))
        : BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset, 4));

    private static void WriteU32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset, 4), value);

    private sealed record MpfLayout(int EoiEnd, int TiffOffset, uint ImageCount, uint EntriesByteCount,
        uint PrimarySize, uint PrimaryOffset, uint GainMapSize, uint GainMapOffset,
        int ImageCountFieldOffset, int EntriesByteCountFieldOffset, int EntriesArrayOffset,
        int PrimarySizeFieldOffset, int GainMapSizeFieldOffset, int GainMapOffsetFieldOffset);
}
