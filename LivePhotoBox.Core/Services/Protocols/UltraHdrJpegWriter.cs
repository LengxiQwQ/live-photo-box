using System;
using System.Globalization;
using System.IO;
using System.Text;

namespace LivePhotoBox.Services.Protocols;

/*
 * UltraHdrJpegWriter.cs
 *
 * 把已解码的 SDR 主 JPEG + gain map JPEG 拼成 Google Ultra HDR / ISO 21496-1 JPEG。
 * 结构参考荣耀 Ultra HDR 真机样本。
 *
 *   - APP1 XMP（hdrgm + GContainer Primary/GainMap）
 *   - APP2 ISO 21496-1 marker
 *   - APP2 MPF（两幅图像：primary + gain map）
 *   - 主 JPEG 字节 + gain map JPEG 字节
 */
internal static class UltraHdrJpegWriter
{
    private static readonly byte[] XmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");

    public static void Write(
        string primaryJpegPath,
        string gainMapJpegPath,
        string outputPath,
        IsoGainMapMetadata metadata)
    {
        byte[] primary = File.ReadAllBytes(primaryJpegPath);
        byte[] gainMap = File.ReadAllBytes(gainMapJpegPath);

        if (primary.Length < 2 || primary[0] != 0xFF || primary[1] != 0xD8)
        {
            throw new InvalidDataException("Primary image is not a valid JPEG.");
        }

        if (gainMap.Length < 2 || gainMap[0] != 0xFF || gainMap[1] != 0xD8)
        {
            throw new InvalidDataException("Gain map image is not a valid JPEG.");
        }

        primary = StripXmpSegments(primary);
        gainMap = InjectXmpIntoJpeg(gainMap, BuildGainMapXmpPayload(metadata));

        byte[] primaryXmpPayload = BuildPrimaryXmpPayload(gainMap.Length);
        byte[] isoMarkerPayload = Encoding.ASCII.GetBytes("urn:iso:std:iso:ts:21496:-1\0\0\0\0\0");
        byte[] mpfPayload = BuildMpfPayload(primary.Length, gainMap.Length);

        using var output = new FileStream(outputPath, FileMode.Create, FileAccess.Write, FileShare.None, 81920);
        output.WriteByte(0xFF);
        output.WriteByte(0xD8);

        WriteAppSegment(output, 0xE1, primaryXmpPayload);
        WriteAppSegment(output, 0xE2, isoMarkerPayload);
        WriteAppSegment(output, 0xE2, mpfPayload);

        // 主 JPEG 保留其自身的 EXIF / ICC / 图像数据，只跳过它原来的 SOI。
        output.Write(primary, 2, primary.Length - 2);
        output.Write(gainMap, 0, gainMap.Length);
    }

    private static byte[] InjectXmpIntoJpeg(byte[] jpeg, byte[] xmpPayload)
    {
        if (jpeg.Length < 2 || jpeg[0] != 0xFF || jpeg[1] != 0xD8)
        {
            throw new InvalidDataException("Gain map is not a valid JPEG.");
        }

        int segmentLength = xmpPayload.Length + 2;
        if (segmentLength > ushort.MaxValue)
        {
            throw new InvalidDataException($"Gain map XMP segment too large: {segmentLength}");
        }

        byte[] result = new byte[2 + 2 + 2 + xmpPayload.Length + (jpeg.Length - 2)];
        result[0] = 0xFF;
        result[1] = 0xD8;
        result[2] = 0xFF;
        result[3] = 0xE1;
        result[4] = (byte)(segmentLength >> 8);
        result[5] = (byte)segmentLength;
        Buffer.BlockCopy(xmpPayload, 0, result, 6, xmpPayload.Length);
        Buffer.BlockCopy(jpeg, 2, result, 6 + xmpPayload.Length, jpeg.Length - 2);
        return result;
    }

    private static byte[] StripXmpSegments(byte[] jpeg)
    {
        using var output = new MemoryStream(jpeg.Length);
        output.WriteByte(0xFF);
        output.WriteByte(0xD8);

        int p = 2;
        while (p + 4 <= jpeg.Length)
        {
            if (jpeg[p] != 0xFF)
            {
                p++;
                continue;
            }

            byte marker = jpeg[p + 1];
            p += 2;
            if (marker == 0xD8 || marker == 0xD9 || (marker >= 0xD0 && marker <= 0xD7) || marker == 0x01)
            {
                output.WriteByte(0xFF);
                output.WriteByte(marker);
                if (marker == 0xD9)
                {
                    break;
                }

                continue;
            }

            if (p + 2 > jpeg.Length)
            {
                break;
            }

            int segmentLength = (jpeg[p] << 8) | jpeg[p + 1];
            if (segmentLength < 2 || p + segmentLength > jpeg.Length)
            {
                break;
            }

            int payloadLength = segmentLength - 2;
            int payloadStart = p + 2;
            bool isXmp = marker == 0xE1
                && payloadLength >= XmpHeader.Length
                && jpeg.AsSpan(payloadStart, XmpHeader.Length).SequenceEqual(XmpHeader);

            if (!isXmp)
            {
                output.WriteByte(0xFF);
                output.WriteByte(marker);
                output.Write(jpeg, p, segmentLength);
            }

            p += segmentLength;
            if (marker == 0xDA)
            {
                output.Write(jpeg, p, jpeg.Length - p);
                break;
            }
        }

        return output.ToArray();
    }

    private static byte[] BuildPrimaryXmpPayload(int gainMapLength)
    {
        string lengthText = gainMapLength.ToString("D8");

        string xmp =
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"Adobe XMP Core 5.1.2\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description " +
            "xmlns:Container=\"http://ns.google.com/photos/1.0/container/\" " +
            "xmlns:Item=\"http://ns.google.com/photos/1.0/container/item/\" " +
            "xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\" " +
            "hdrgm:Version=\"1.0\">" +
            "<Container:Directory><rdf:Seq>" +
            "<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Semantic=\"Primary\" Item:Mime=\"image/jpeg\"/></rdf:li>" +
            $"<rdf:li rdf:parseType=\"Resource\"><Container:Item Item:Semantic=\"GainMap\" Item:Mime=\"image/jpeg\" Item:Length=\"{lengthText}\"/></rdf:li>" +
            "</rdf:Seq></Container:Directory>" +
            "</rdf:Description></rdf:RDF></x:xmpmeta>";

        return BuildXmpPayload(xmp);
    }

    private static byte[] BuildGainMapXmpPayload(IsoGainMapMetadata metadata)
    {
        string gainMapMin = FormatDouble(metadata.GainMapMin);
        string gainMapMax = FormatDouble(metadata.GainMapMax);
        string gamma = FormatDouble(metadata.Gamma);
        string offsetSdr = FormatDouble(metadata.OffsetSDR);
        string offsetHdr = FormatDouble(metadata.OffsetHDR);
        string hdrCapacityMin = FormatDouble(metadata.HDRCapacityMin);
        string hdrCapacityMax = FormatDouble(metadata.HDRCapacityMax);

        string xmp =
            "<x:xmpmeta xmlns:x=\"adobe:ns:meta/\" x:xmptk=\"Adobe XMP Core 5.1.2\">" +
            "<rdf:RDF xmlns:rdf=\"http://www.w3.org/1999/02/22-rdf-syntax-ns#\">" +
            "<rdf:Description " +
            "xmlns:hdrgm=\"http://ns.adobe.com/hdr-gain-map/1.0/\" " +
            "hdrgm:Version=\"1.0\" " +
            $"hdrgm:GainMapMin=\"{gainMapMin}\" " +
            $"hdrgm:GainMapMax=\"{gainMapMax}\" " +
            $"hdrgm:Gamma=\"{gamma}\" " +
            $"hdrgm:OffsetSDR=\"{offsetSdr}\" " +
            $"hdrgm:OffsetHDR=\"{offsetHdr}\" " +
            $"hdrgm:HDRCapacityMin=\"{hdrCapacityMin}\" " +
            $"hdrgm:HDRCapacityMax=\"{hdrCapacityMax}\" " +
            "hdrgm:BaseRenditionIsHDR=\"False\"/>" +
            "</rdf:RDF></x:xmpmeta>";

        return BuildXmpPayload(xmp);
    }

    private static byte[] BuildXmpPayload(string xmp)
    {

        byte[] xmlBytes = Encoding.UTF8.GetBytes(xmp);
        byte[] payload = new byte[XmpHeader.Length + xmlBytes.Length];
        Buffer.BlockCopy(XmpHeader, 0, payload, 0, XmpHeader.Length);
        Buffer.BlockCopy(xmlBytes, 0, payload, XmpHeader.Length, xmlBytes.Length);
        return payload;
    }

    private static string FormatDouble(double value)
    {
        return value.ToString("0.########", CultureInfo.InvariantCulture);
    }

    private static byte[] BuildMpfPayload(int primaryLength, int gainMapLength)
    {
        // 固定布局与荣耀样本一致：
        //   MPF\0 (4)
        //   TIFF header (8): MM + 0x002A + IFD0 offset(8)
        //   IFD0: count(2) + 3 entries(12 each) + next IFD(4)  => 2 + 36 + 4 = 42
        //   MPEntry[2] (16 each) => 32
        // 总长 = 4 + 8 + 42 + 32 = 86。
        const int mpEntryOffset = 0x32; // MP entry 数据相对 TIFF header 的偏移（绝对 4 + 0x32 = 54）。
        byte[] payload = new byte[86];
        Encoding.ASCII.GetBytes("MPF\0").CopyTo(payload, 0);
        payload[4] = 0x4D; // MM
        payload[5] = 0x4D;
        WriteU16(payload, 6, 0x002A);
        WriteU32(payload, 8, 8); // IFD0 offset

        WriteU16(payload, 12, 3); // IFD entry count
        WriteIfdEntry(payload, 14, 0xB000, 7, 4, 0x30313030); // "0100"
        WriteIfdEntry(payload, 26, 0xB001, 4, 1, 2);
        WriteIfdEntry(payload, 38, 0xB002, 7, 32, mpEntryOffset);
        WriteU32(payload, 50, 0); // next IFD

        int entry0 = mpEntryOffset + 4; // 绝对偏移 54
        WriteU32(payload, entry0, 0x00030000); // primary image attribute
        WriteU32(payload, entry0 + 4, (uint)primaryLength);
        WriteU32(payload, entry0 + 8, 0);
        WriteU32(payload, entry0 + 12, 0);

        int entry1 = entry0 + 16; // 绝对偏移 70
        WriteU32(payload, entry1, 0x00050000); // gain map image attribute
        WriteU32(payload, entry1 + 4, (uint)gainMapLength);
        WriteU32(payload, entry1 + 8, (uint)primaryLength);
        WriteU32(payload, entry1 + 12, 0);
        return payload;
    }

    private static void WriteAppSegment(Stream output, byte marker, byte[] payload)
    {
        int segmentLength = payload.Length + 2;
        if (segmentLength > ushort.MaxValue)
        {
            throw new InvalidDataException($"JPEG APP segment too large: {segmentLength}");
        }

        output.WriteByte(0xFF);
        output.WriteByte(marker);
        output.WriteByte((byte)(segmentLength >> 8));
        output.WriteByte((byte)segmentLength);
        output.Write(payload, 0, payload.Length);
    }

    private static void WriteIfdEntry(byte[] data, int offset, ushort tag, ushort type, uint count, uint value)
    {
        WriteU16(data, offset, tag);
        WriteU16(data, offset + 2, type);
        WriteU32(data, offset + 4, count);
        WriteU32(data, offset + 8, value);
    }

    private static void WriteU16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    private static void WriteU32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }
}
