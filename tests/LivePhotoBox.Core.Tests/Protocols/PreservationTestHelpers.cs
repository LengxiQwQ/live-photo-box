using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml.Linq;
using LivePhotoBox.Interop;

namespace LivePhotoBox.Core.Tests.Protocols;

public sealed record HeicAuxRelationSnapshot(
    uint PrimaryItemId,
    uint AuxiliaryItemId,
    uint FromItemId,
    uint ToItemId,
    string AuxiliaryPayloadSha256);

/// <summary>
/// Test-only binary inspection and parsing helpers.
/// Moved out of MetadataPreservationVerifier to guarantee that production code
/// contains no C# binary media parsers.
/// </summary>
public static class PreservationTestHelpers
{
    public sealed class TiffMetadata
    {
        public bool IsValid { get; set; }
        public ushort? Orientation { get; set; }
        public string? Make { get; set; }
        public string? Model { get; set; }
        public string? DateTimeOriginal { get; set; }
        public Dictionary<ushort, byte[]> Ifd0Tags { get; } = new();
        public Dictionary<ushort, byte[]> ExifTags { get; } = new();
        public Dictionary<ushort, byte[]> GpsTags { get; } = new();
        public byte[]? MakerNote { get; set; }
    }

    public static TiffMetadata ParseTiff(byte[] tiffBytes)
    {
        var meta = new TiffMetadata();
        if (tiffBytes == null || tiffBytes.Length < 8) return meta;

        bool isLittleEndian;
        if (tiffBytes[0] == 0x49 && tiffBytes[1] == 0x49 && tiffBytes[2] == 0x2A && tiffBytes[3] == 0x00)
        {
            isLittleEndian = true;
        }
        else if (tiffBytes[0] == 0x4D && tiffBytes[1] == 0x4D && tiffBytes[2] == 0x00 && tiffBytes[3] == 0x2A)
        {
            isLittleEndian = false;
        }
        else
        {
            return meta;
        }

        meta.IsValid = true;

        uint ifd0Offset = ReadUInt32(tiffBytes, 4, isLittleEndian);
        uint exifIfdOffset = 0;
        uint gpsIfdOffset = 0;

        ParseIfd(tiffBytes, ifd0Offset, isLittleEndian, (tag, type, count, valOrOffset, rawBytes) =>
        {
            meta.Ifd0Tags[tag] = rawBytes;
            switch (tag)
            {
                case 0x0112: // Orientation
                    meta.Orientation = (ushort)valOrOffset;
                    break;
                case 0x010F: // Make
                    meta.Make = ParseAsciiString(rawBytes);
                    break;
                case 0x0110: // Model
                    meta.Model = ParseAsciiString(rawBytes);
                    break;
                case 0x8769: // Exif IFD Pointer
                    exifIfdOffset = valOrOffset;
                    break;
                case 0x8825: // GPS IFD Pointer
                    gpsIfdOffset = valOrOffset;
                    break;
            }
        });

        if (exifIfdOffset > 0 && exifIfdOffset < tiffBytes.Length)
        {
            ParseIfd(tiffBytes, exifIfdOffset, isLittleEndian, (tag, type, count, valOrOffset, rawBytes) =>
            {
                meta.ExifTags[tag] = rawBytes;
                if (tag == 0x9003) // DateTimeOriginal
                {
                    meta.DateTimeOriginal = ParseAsciiString(rawBytes);
                }
                else if (tag == 0x927C) // MakerNote
                {
                    meta.MakerNote = rawBytes;
                }
            });
        }

        if (gpsIfdOffset > 0 && gpsIfdOffset < tiffBytes.Length)
        {
            ParseIfd(tiffBytes, gpsIfdOffset, isLittleEndian, (tag, type, count, valOrOffset, rawBytes) =>
            {
                meta.GpsTags[tag] = rawBytes;
            });
        }

        return meta;
    }

    private static void ParseIfd(
        byte[] buffer,
        uint offset,
        bool isLittleEndian,
        Action<ushort, ushort, uint, uint, byte[]> onEntry)
    {
        if (offset + 2 > buffer.Length) return;
        ushort count = ReadUInt16(buffer, (int)offset, isLittleEndian);
        int pos = (int)offset + 2;

        for (int i = 0; i < count; i++)
        {
            if (pos + 12 > buffer.Length) break;
            ushort tag = ReadUInt16(buffer, pos, isLittleEndian);
            ushort type = ReadUInt16(buffer, pos + 2, isLittleEndian);
            uint entryCount = ReadUInt32(buffer, pos + 4, isLittleEndian);
            uint valOrOffset = ReadUInt32(buffer, pos + 8, isLittleEndian);

            int typeSize = GetTiffTypeSize(type);
            long totalBytes = (long)entryCount * typeSize;

            byte[] rawBytes;
            if (totalBytes <= 4 && totalBytes > 0)
            {
                rawBytes = new byte[totalBytes];
                Buffer.BlockCopy(buffer, pos + 8, rawBytes, 0, (int)totalBytes);
            }
            else if (totalBytes > 4 && valOrOffset + totalBytes <= buffer.Length)
            {
                rawBytes = new byte[totalBytes];
                Buffer.BlockCopy(buffer, (int)valOrOffset, rawBytes, 0, (int)totalBytes);
            }
            else
            {
                rawBytes = BitConverter.GetBytes(valOrOffset);
            }

            onEntry(tag, type, entryCount, valOrOffset, rawBytes);
            pos += 12;
        }
    }

    private static ushort ReadUInt16(byte[] buffer, int offset, bool isLittleEndian)
    {
        if (offset + 2 > buffer.Length) return 0;
        return isLittleEndian
            ? (ushort)(buffer[offset] | (buffer[offset + 1] << 8))
            : (ushort)((buffer[offset] << 8) | buffer[offset + 1]);
    }

    private static uint ReadUInt32(byte[] buffer, int offset, bool isLittleEndian)
    {
        if (offset + 4 > buffer.Length) return 0;
        return isLittleEndian
            ? (uint)(buffer[offset] | (buffer[offset + 1] << 8) | (buffer[offset + 2] << 16) | (buffer[offset + 3] << 24))
            : (uint)((buffer[offset] << 24) | (buffer[offset + 1] << 16) | (buffer[offset + 2] << 8) | buffer[offset + 3]);
    }

    private static int GetTiffTypeSize(ushort type) => type switch
    {
        1 => 1, // BYTE
        2 => 1, // ASCII
        3 => 2, // SHORT
        4 => 4, // LONG
        5 => 8, // RATIONAL
        7 => 1, // UNDEFINED
        9 => 4, // SLONG
        10 => 8, // SRATIONAL
        _ => 1
    };

    private static string ParseAsciiString(byte[] bytes)
    {
        if (bytes == null || bytes.Length == 0) return string.Empty;
        int len = bytes.Length;
        while (len > 0 && bytes[len - 1] == 0) len--;
        return Encoding.ASCII.GetString(bytes, 0, len);
    }

    private static bool IsJpeg(byte[] data) =>
        data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8;

    private static bool IsHeifContainer(byte[] data)
    {
        if (data == null || data.Length < 12) return false;
        if (data[4] == (byte)'f' && data[5] == (byte)'t' && data[6] == (byte)'y' && data[7] == (byte)'p')
            return true;
        if (data[4] == (byte)'m' && data[5] == (byte)'e' && data[6] == (byte)'t' && data[7] == (byte)'a')
            return true;
        return false;
    }

    public static byte[]? ExtractTiff(byte[] data, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".heic" or ".heif" || IsHeifContainer(data))
        {
            return ExtractHeicTiff(data);
        }
        if (ext is ".jpg" or ".jpeg" || IsJpeg(data))
        {
            return ExtractJpegTiff(data);
        }
        return ExtractJpegTiff(data) ?? ExtractHeicTiff(data);
    }

    private static byte[]? ExtractJpegTiff(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        int pos = 2;
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF) break;
            while (pos < data.Length && data[pos] == 0xFF) pos++;
            if (pos >= data.Length) break;
            byte marker = data[pos++];
            if (marker == 0xDA || marker == 0xD9) break; // SOS or EOI
            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            if (pos + 2 > data.Length) break;
            int len = (data[pos] << 8) | data[pos + 1];
            if (len < 2 || pos + len > data.Length) break;

            if (marker == 0xE1) // APP1
            {
                if (len >= 8 && pos + 8 <= data.Length &&
                    data[pos + 2] == 'E' && data[pos + 3] == 'x' && data[pos + 4] == 'i' && data[pos + 5] == 'f' &&
                    data[pos + 6] == 0 && data[pos + 7] == 0)
                {
                    int tiffLen = len - 8;
                    byte[] tiff = new byte[tiffLen];
                    Buffer.BlockCopy(data, pos + 8, tiff, 0, tiffLen);
                    return tiff;
                }
            }
            pos += len;
        }
        return null;
    }

    private static byte[]? ExtractHeicTiff(byte[] data)
    {
        if (data.Length < 16) return null;
        if (!NativeHeifBoxParser.TryLocateExifItem(data, out long offset, out long length, out _))
        {
            return null;
        }

        if (offset < 0 || length < 4 || (ulong)offset + (ulong)length > (ulong)data.Length)
        {
            return null;
        }

        int itemStart = (int)offset;
        int itemLen = (int)length;

        uint tiffHeaderOffset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(itemStart, 4));

        int[] candidateStarts = [
            itemStart + 4 + (int)tiffHeaderOffset,
            itemStart + 10,
            itemStart + 4,
            itemStart
        ];

        foreach (int tiffStart in candidateStarts)
        {
            if (tiffStart >= itemStart && tiffStart + 4 <= itemStart + itemLen)
            {
                bool isTiff = (data[tiffStart] == 0x49 && data[tiffStart + 1] == 0x49 && data[tiffStart + 2] == 0x2A && data[tiffStart + 3] == 0x00) ||
                              (data[tiffStart] == 0x4D && data[tiffStart + 1] == 0x4D && data[tiffStart + 2] == 0x00 && data[tiffStart + 3] == 0x2A);
                if (isTiff)
                {
                    int tiffLen = (itemStart + itemLen) - tiffStart;
                    byte[] tiff = new byte[tiffLen];
                    Buffer.BlockCopy(data, tiffStart, tiff, 0, tiffLen);
                    return tiff;
                }
            }
        }

        return null;
    }

    public static byte[]? ExtractIcc(byte[] data, string path)
    {
        string ext = Path.GetExtension(path).ToLowerInvariant();
        if (ext is ".heic" or ".heif" || IsHeifContainer(data))
        {
            return ExtractHeicIcc(data);
        }

        if (ext is ".jpg" or ".jpeg" || IsJpeg(data))
        {
            return ExtractJpegIcc(data);
        }

        return ExtractJpegIcc(data) ?? ExtractHeicIcc(data);
    }

    private static byte[]? ExtractJpegIcc(byte[] data)
    {
        if (data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        int pos = 2;
        List<byte[]> chunks = new();
        while (pos + 4 <= data.Length)
        {
            if (data[pos] != 0xFF) break;
            while (pos < data.Length && data[pos] == 0xFF) pos++;
            if (pos >= data.Length) break;
            byte marker = data[pos++];
            if (marker == 0xDA || marker == 0xD9) break;
            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            if (pos + 2 > data.Length) break;
            int len = (data[pos] << 8) | data[pos + 1];
            if (len < 2 || pos + len > data.Length) break;

            if (marker == 0xE2) // APP2
            {
                if (len >= 14 && pos + 14 <= data.Length &&
                    data[pos + 2] == 'I' && data[pos + 3] == 'C' && data[pos + 4] == 'C' &&
                    data[pos + 5] == '_' && data[pos + 6] == 'P' && data[pos + 7] == 'R' &&
                    data[pos + 8] == 'O' && data[pos + 9] == 'F' && data[pos + 10] == 'I' &&
                    data[pos + 11] == 'L' && data[pos + 12] == 'E' && data[pos + 13] == 0)
                {
                    int payloadLen = len - 14;
                    byte[] chunk = new byte[payloadLen];
                    Buffer.BlockCopy(data, pos + 14, chunk, 0, payloadLen);
                    chunks.Add(chunk);
                }
            }
            pos += len;
        }

        if (chunks.Count == 1) return chunks[0];
        if (chunks.Count > 1)
        {
            int total = chunks.Sum(c => c.Length);
            byte[] combined = new byte[total];
            int offset = 0;
            foreach (var c in chunks)
            {
                Buffer.BlockCopy(c, 0, combined, offset, c.Length);
                offset += c.Length;
            }
            return combined;
        }

        return null;
    }

    private static byte[]? ExtractHeicIcc(byte[] data)
    {
        var metaChildren = GetMetaChildren(data, out _);
        if (metaChildren == null) return null;

        HeifBoxHeader? iprpBox = null;
        foreach (var b in metaChildren)
        {
            if (b.Type == "iprp")
            {
                if (iprpBox != null) return null;
                iprpBox = b;
            }
        }
        if (iprpBox == null || iprpBox.Value.BodyLength < 8) return null;

        var iprpChildren = ParseSequentialBoxes(data, iprpBox.Value.BodyStart, iprpBox.Value.BodyLength);
        if (iprpChildren == null) return null;

        HeifBoxHeader? ipcoBox = null;
        HeifBoxHeader? ipmaBox = null;
        foreach (var b in iprpChildren)
        {
            if (b.Type == "ipco")
            {
                if (ipcoBox != null) return null;
                ipcoBox = b;
            }
            else if (b.Type == "ipma")
            {
                if (ipmaBox != null) return null;
                ipmaBox = b;
            }
        }
        if (ipcoBox == null || ipcoBox.Value.BodyLength < 8) return null;

        var propBoxes = ParseSequentialBoxes(data, ipcoBox.Value.BodyStart, ipcoBox.Value.BodyLength);
        if (propBoxes == null) return null;

        var colrProps = new List<(int Index, HeifBoxHeader Box)>();
        for (int i = 0; i < propBoxes.Count; i++)
        {
            if (propBoxes[i].Type == "colr")
            {
                colrProps.Add((i + 1, propBoxes[i]));
            }
        }

        if (colrProps.Count == 0) return null;

        if (colrProps.Count == 1)
        {
            var colr = colrProps[0].Box;
            byte[] bytes = new byte[colr.BoxLength];
            Buffer.BlockCopy(data, colr.BoxStart, bytes, 0, colr.BoxLength);
            return bytes;
        }

        uint? primaryId = ExtractHeicPrimaryItemId(data);
        if (primaryId != null && ipmaBox != null)
        {
            var associatedIndices = GetItemPropertyAssociations(data, ipmaBox.Value, primaryId.Value);
            var matching = colrProps.Where(cp => associatedIndices.Contains(cp.Index)).ToList();
            if (matching.Count == 1)
            {
                var colr = matching[0].Box;
                byte[] bytes = new byte[colr.BoxLength];
                Buffer.BlockCopy(data, colr.BoxStart, bytes, 0, colr.BoxLength);
                return bytes;
            }
        }

        return null;
    }

    private static HashSet<int> GetItemPropertyAssociations(byte[] data, HeifBoxHeader ipmaBox, uint targetItemId)
    {
        var result = new HashSet<int>();
        if (ipmaBox.BodyLength < 8) return result;

        int p = ipmaBox.BodyStart;
        int end = ipmaBox.BodyStart + ipmaBox.BodyLength;

        byte ver = data[p++];
        int flags = (data[p++] << 16) | (data[p++] << 8) | data[p++];
        bool isLargePropertyIndex = (flags & 1) != 0;

        if (p + 4 > end) return result;
        uint entryCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
        p += 4;

        for (uint i = 0; i < entryCount && p < end; i++)
        {
            uint itemId;
            if (ver < 1)
            {
                if (p + 2 > end) break;
                itemId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
                p += 2;
            }
            else
            {
                if (p + 4 > end) break;
                itemId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
                p += 4;
            }

            if (p >= end) break;
            byte assocCount = data[p++];

            for (byte a = 0; a < assocCount && p < end; a++)
            {
                int propIndex;
                if (isLargePropertyIndex)
                {
                    if (p + 2 > end) break;
                    ushort raw = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
                    p += 2;
                    propIndex = raw & 0x7FFF;
                }
                else
                {
                    byte raw = data[p++];
                    propIndex = raw & 0x7F;
                }

                if (itemId == targetItemId)
                {
                    result.Add(propIndex);
                }
            }
        }

        return result;
    }

    public static string ExtractXmp(byte[] data, string? path = null)
    {
        if (data.Length < 32) return string.Empty;

        string ext = !string.IsNullOrEmpty(path) ? Path.GetExtension(path).ToLowerInvariant() : string.Empty;

        if ((ext is ".jpg" or ".jpeg" || IsJpeg(data)) && data.Length >= 4 && data[0] == 0xFF && data[1] == 0xD8)
        {
            int p = 2;
            byte[] xmpHeader = Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");
            while (p + 4 <= data.Length)
            {
                if (data[p] != 0xFF) break;
                while (p < data.Length && data[p] == 0xFF) p++;
                if (p >= data.Length) break;
                byte marker = data[p++];
                if (marker == 0xDA || marker == 0xD9) break;
                if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
                if (p + 2 > data.Length) break;
                int len = (data[p] << 8) | data[p + 1];
                if (len < 2 || p + len > data.Length) break;

                if (marker == 0xE1 && len >= xmpHeader.Length + 2)
                {
                    if (data.AsSpan(p + 2, xmpHeader.Length).SequenceEqual(xmpHeader))
                    {
                        int xmlStart = p + 2 + xmpHeader.Length;
                        int xmlLen = len - 2 - xmpHeader.Length;
                        return ExtractXmlFragment(Encoding.UTF8.GetString(data, xmlStart, xmlLen));
                    }
                }
                p += len;
            }
            return string.Empty;
        }

        if (ext is ".heic" or ".heif" || IsHeifContainer(data))
        {
            if (NativeHeifBoxParser.TryLocateXmpItem(data, out long offset, out long length, out _) &&
                offset >= 0 && length > 0 && (ulong)offset + (ulong)length <= (ulong)data.Length)
            {
                string xmpStr = Encoding.UTF8.GetString(data, (int)offset, (int)length);
                return ExtractXmlFragment(xmpStr);
            }
            return string.Empty;
        }

        var trimmed = data.AsSpan().TrimStart(" \t\r\n"u8);
        if (trimmed.StartsWith("<?xml"u8) || trimmed.StartsWith("<x:xmpmeta"u8) || trimmed.StartsWith("<rdf:RDF"u8))
        {
            return ExtractXmlFragment(Encoding.UTF8.GetString(data));
        }

        return string.Empty;
    }

    private static string ExtractXmlFragment(string text)
    {
        int start = text.IndexOf("<x:xmpmeta", StringComparison.OrdinalIgnoreCase);
        if (start < 0) start = text.IndexOf("<rdf:RDF", StringComparison.OrdinalIgnoreCase);
        if (start < 0) return string.Empty;

        int end = text.IndexOf("</x:xmpmeta>", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) end = text.IndexOf("</rdf:RDF>", start, StringComparison.OrdinalIgnoreCase);
        if (end < 0) return string.Empty;

        int endTagLen = text.Substring(end).StartsWith("</x:xmpmeta>", StringComparison.OrdinalIgnoreCase) ? 12 : 10;
        return text.Substring(start, end + endTagLen - start);
    }

    public static Dictionary<string, string> ExtractNonTargetXmpProperties(string xmpText)
    {
        var dict = new Dictionary<string, string>(StringComparer.Ordinal);
        if (string.IsNullOrWhiteSpace(xmpText)) return dict;

        try
        {
            var doc = XDocument.Parse(xmpText);
            foreach (var elem in doc.Descendants())
            {
                string localName = elem.Name.LocalName;
                string ns = elem.Name.NamespaceName;

                if (IsMotionPhotoItem(elem))
                {
                    continue;
                }

                if (IsProtocolNamespaceOrName(localName, ns))
                {
                    continue;
                }

                string elemPath = GetElementPath(elem);

                if (!elem.HasElements && !string.IsNullOrWhiteSpace(elem.Value))
                {
                    dict[$"elem:{elemPath}"] = elem.Value.Trim();
                }

                foreach (var attr in elem.Attributes())
                {
                    if (attr.IsNamespaceDeclaration) continue;
                    string aLocal = attr.Name.LocalName;
                    string aNs = attr.Name.NamespaceName;
                    if (IsProtocolNamespaceOrName(aLocal, aNs)) continue;

                    dict[$"attr:{elemPath}@{aNs}:{aLocal}"] = attr.Value.Trim();
                }
            }
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Failed to parse XMP XML into dictionary: {ex.Message}");
        }

        return dict;
    }

    private static bool IsMotionPhotoItem(XElement elem)
    {
        foreach (var desc in elem.DescendantsAndSelf())
        {
            foreach (var attr in desc.Attributes())
            {
                if (attr.Name.LocalName.Equals("Semantic", StringComparison.OrdinalIgnoreCase) &&
                    attr.Value.Trim().Equals("MotionPhoto", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
            if (desc.Name.LocalName.Equals("Semantic", StringComparison.OrdinalIgnoreCase) &&
                desc.Value.Trim().Equals("MotionPhoto", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        for (var p = elem.Parent; p != null; p = p.Parent)
        {
            foreach (var attr in p.Attributes())
            {
                if (attr.Name.LocalName.Equals("Semantic", StringComparison.OrdinalIgnoreCase) &&
                    attr.Value.Trim().Equals("MotionPhoto", StringComparison.OrdinalIgnoreCase))
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static string GetElementPath(XElement elem)
    {
        var parts = new List<string>();
        for (var cur = elem; cur != null; cur = cur.Parent)
        {
            string name = cur.Name.LocalName;
            var semAttr = cur.Attributes().FirstOrDefault(a => a.Name.LocalName.Equals("Semantic", StringComparison.OrdinalIgnoreCase));
            if (semAttr != null)
            {
                name += $"[Semantic={semAttr.Value}]";
            }
            else
            {
                var semElem = cur.Elements().FirstOrDefault(e => e.Name.LocalName.Equals("Semantic", StringComparison.OrdinalIgnoreCase));
                if (semElem != null)
                {
                    name += $"[Semantic={semElem.Value.Trim()}]";
                }
                else if (cur.Parent != null)
                {
                    int index = cur.ElementsBeforeSelf(cur.Name).Count();
                    if (index > 0)
                    {
                        name += $"[{index}]";
                    }
                }
            }
            parts.Add(name);
        }
        parts.Reverse();
        return string.Join("/", parts);
    }

    private static bool IsProtocolNamespaceOrName(string name, string ns)
    {
        if (ns.Contains("camera", StringComparison.OrdinalIgnoreCase) ||
            ns.Contains("livephotobox", StringComparison.OrdinalIgnoreCase))
        {
            if (name.Contains("MotionPhoto", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("MicroVideo", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("LivePhoto", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("SpecialTypeID", StringComparison.OrdinalIgnoreCase) ||
                name.Contains("MovingPhoto", StringComparison.OrdinalIgnoreCase))
            {
                return true;
            }
        }

        if (name.StartsWith("GCamera:", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("OpCamera:", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("VCamera:", StringComparison.OrdinalIgnoreCase) ||
            name.StartsWith("LivePhotoBox:", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        if (name.Equals("MotionPhoto", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("MotionPhotoVersion", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("MotionPhotoPresentationTimestampUs", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("MicroVideo", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("MicroVideoVersion", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("MicroVideoOffset", StringComparison.OrdinalIgnoreCase) ||
            name.Equals("MicroVideoPresentationTimestampUs", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }

        return false;
    }

    public static string? ExtractJpegEntropyScanSha256(byte[] data)
    {
        if (data == null || data.Length < 4 || data[0] != 0xFF || data[1] != 0xD8) return null;
        int p = 2;
        while (p + 4 <= data.Length)
        {
            if (data[p] != 0xFF) break;
            while (p < data.Length && data[p] == 0xFF) p++;
            if (p >= data.Length) break;
            byte marker = data[p++];
            if (marker == 0xDA)
            {
                if (p + 2 > data.Length) return null;
                int sosHeaderLen = (data[p] << 8) | data[p + 1];
                int scanStart = p + sosHeaderLen;
                if (scanStart > data.Length) return null;

                int scanEnd = -1;
                for (int i = scanStart; i + 1 < data.Length; i++)
                {
                    if (data[i] == 0xFF)
                    {
                        byte next = data[i + 1];
                        if (next == 0x00 || (next >= 0xD0 && next <= 0xD7))
                        {
                            i++;
                            continue;
                        }
                        if (next == 0xD9)
                        {
                            scanEnd = i;
                            break;
                        }
                    }
                }

                if (scanEnd <= scanStart) return null;
                using var sha = SHA256.Create();
                byte[] hash = sha.ComputeHash(data, scanStart, scanEnd - scanStart);
                return Convert.ToHexString(hash);
            }
            if (marker == 0xD9) break;
            if (marker == 0x00 || (marker >= 0xD0 && marker <= 0xD7)) continue;
            if (p + 2 > data.Length) break;
            int len = (data[p] << 8) | data[p + 1];
            if (len < 2 || p + len > data.Length) break;
            p += len;
        }
        return null;
    }

    public readonly struct HeifBoxHeader
    {
        public readonly string Type;
        public readonly int HeaderSize;
        public readonly int BoxStart;
        public readonly int BoxLength;
        public readonly int BodyStart;
        public readonly int BodyLength;

        public HeifBoxHeader(string type, int headerSize, int boxStart, int boxLength, int bodyStart, int bodyLength)
        {
            Type = type;
            HeaderSize = headerSize;
            BoxStart = boxStart;
            BoxLength = boxLength;
            BodyStart = bodyStart;
            BodyLength = bodyLength;
        }
    }

    public static List<HeifBoxHeader>? ParseSequentialBoxes(byte[] data, int start, int length)
    {
        if (data == null || start < 0 || length < 8 || start + length > data.Length)
            return null;

        var boxes = new List<HeifBoxHeader>();
        int p = start;
        int end = start + length;

        while (p + 8 <= end)
        {
            uint size32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
            string type = Encoding.ASCII.GetString(data, p + 4, 4);
            int headerSize = 8;
            long actualSize = size32;

            if (size32 == 1)
            {
                if (p + 16 > end) return null;
                actualSize = (long)BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(p + 8, 8));
                headerSize = 16;
            }
            else if (size32 == 0)
            {
                actualSize = end - p;
            }

            if (actualSize < headerSize || p + actualSize > end)
            {
                return null;
            }

            boxes.Add(new HeifBoxHeader(type, headerSize, p, (int)actualSize, p + headerSize, (int)(actualSize - headerSize)));
            p += (int)actualSize;
        }

        if (p != end) return null;
        return boxes;
    }

    private static HeifBoxHeader? FindUniqueMetaBox(byte[] data)
    {
        var topBoxes = ParseSequentialBoxes(data, 0, data.Length);
        if (topBoxes == null) return null;

        HeifBoxHeader? metaBox = null;
        foreach (var b in topBoxes)
        {
            if (b.Type == "meta")
            {
                if (metaBox != null) return null;
                metaBox = b;
            }
        }
        return metaBox;
    }

    private static List<HeifBoxHeader>? GetMetaChildren(byte[] data, out HeifBoxHeader metaBox)
    {
        metaBox = default;
        var metaOpt = FindUniqueMetaBox(data);
        if (metaOpt == null) return null;
        metaBox = metaOpt.Value;

        if (metaBox.BodyLength < 4) return null;

        return ParseSequentialBoxes(data, metaBox.BodyStart + 4, metaBox.BodyLength - 4);
    }

    public static byte[]? ExtractHeicItemPayload(byte[] data, uint targetItemId)
    {
        var metaChildren = GetMetaChildren(data, out _);
        if (metaChildren == null) return null;

        HeifBoxHeader? ilocBox = null;
        foreach (var b in metaChildren)
        {
            if (b.Type == "iloc")
            {
                if (ilocBox != null) return null;
                ilocBox = b;
            }
        }
        if (ilocBox == null || ilocBox.Value.BodyLength < 8) return null;

        int p = ilocBox.Value.BodyStart;
        int ilocEnd = ilocBox.Value.BodyStart + ilocBox.Value.BodyLength;

        byte ver = data[p++];
        p += 3;
        if (p + 2 > ilocEnd) return null;
        int offsetSize = (data[p] >> 4) & 0x0F;
        int lengthSize = data[p] & 0x0F;
        int baseOffsetSize = (data[p + 1] >> 4) & 0x0F;
        int indexSize = (ver == 1 || ver == 2) ? (data[p + 1] & 0x0F) : 0;
        p += 2;

        uint itemCount = 0;
        if (ver < 2)
        {
            if (p + 2 > ilocEnd) return null;
            itemCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
            p += 2;
        }
        else
        {
            if (p + 4 > ilocEnd) return null;
            itemCount = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
            p += 4;
        }

        for (uint it = 0; it < itemCount; it++)
        {
            uint itemId = 0;
            if (ver < 2)
            {
                if (p + 2 > ilocEnd) return null;
                itemId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
                p += 2;
            }
            else
            {
                if (p + 4 > ilocEnd) return null;
                itemId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
                p += 4;
            }
            if (ver == 1 || ver == 2)
            {
                if (p + 2 > ilocEnd) return null;
                p += 2;
            }
            if (p + 2 > ilocEnd) return null;
            p += 2;
            ulong baseOffset = 0;
            for (int b = 0; b < baseOffsetSize; b++) { if (p >= ilocEnd) return null; baseOffset = (baseOffset << 8) | data[p++]; }
            if (p + 2 > ilocEnd) return null;
            ushort extentCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
            p += 2;

            if (itemId == targetItemId)
            {
                if (extentCount != 1) return null;

                if ((ver == 1 || ver == 2) && indexSize > 0) p += indexSize;
                ulong extentOffset = 0;
                for (int b = 0; b < offsetSize; b++) { if (p >= ilocEnd) return null; extentOffset = (extentOffset << 8) | data[p++]; }
                ulong extentLength = 0;
                for (int b = 0; b < lengthSize; b++) { if (p >= ilocEnd) return null; extentLength = (extentLength << 8) | data[p++]; }

                ulong absOffset = baseOffset + extentOffset;
                if (absOffset + extentLength <= (ulong)data.Length && extentLength > 0)
                {
                    byte[] payload = new byte[extentLength];
                    Buffer.BlockCopy(data, (int)absOffset, payload, 0, (int)extentLength);
                    return payload;
                }
                return null;
            }

            for (ushort e = 0; e < extentCount; e++)
            {
                if ((ver == 1 || ver == 2) && indexSize > 0) p += indexSize;
                p += offsetSize + lengthSize;
                if (p > ilocEnd) return null;
            }
        }
        return null;
    }

    public static uint? ExtractHeicPrimaryItemId(byte[] data)
    {
        var metaChildren = GetMetaChildren(data, out _);
        if (metaChildren == null) return null;

        HeifBoxHeader? pitmBox = null;
        foreach (var b in metaChildren)
        {
            if (b.Type == "pitm")
            {
                if (pitmBox != null) return null;
                pitmBox = b;
            }
        }
        if (pitmBox == null || pitmBox.Value.BodyLength < 4) return null;

        int p = pitmBox.Value.BodyStart;
        byte version = data[p];
        if (version == 0 && pitmBox.Value.BodyLength >= 6)
        {
            return BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 4, 2));
        }
        else if (version == 1 && pitmBox.Value.BodyLength >= 8)
        {
            return BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p + 4, 4));
        }
        return null;
    }

    public static string? ExtractHeicPrimaryItemSha256(byte[] data)
    {
        if (data == null || data.Length < 16) return null;
        uint? primaryIdOpt = ExtractHeicPrimaryItemId(data);
        if (primaryIdOpt != null)
        {
            byte[]? payload = ExtractHeicItemPayload(data, primaryIdOpt.Value);
            if (payload != null)
            {
                using var sha = SHA256.Create();
                return Convert.ToHexString(sha.ComputeHash(payload));
            }
            return null;
        }

        return null;
    }

    public static HeicAuxRelationSnapshot? ExtractHeicAuxRelationSnapshot(byte[] data)
    {
        if (data == null || data.Length < 16) return null;
        uint? primaryIdOpt = ExtractHeicPrimaryItemId(data);
        if (primaryIdOpt == null) return null;
        uint primaryId = primaryIdOpt.Value;

        var metaChildren = GetMetaChildren(data, out _);
        if (metaChildren == null) return null;

        HeifBoxHeader? irefBox = null;
        foreach (var b in metaChildren)
        {
            if (b.Type == "iref")
            {
                if (irefBox != null) return null;
                irefBox = b;
            }
        }
        if (irefBox == null || irefBox.Value.BodyLength < 4) return null;

        byte irefVer = data[irefBox.Value.BodyStart];
        if (irefVer > 1) return null;

        var irefChildren = ParseSequentialBoxes(data, irefBox.Value.BodyStart + 4, irefBox.Value.BodyLength - 4);
        if (irefChildren == null) return null;

        var candidateRelations = new List<(uint fromId, uint toId, uint auxId)>();
        bool duplicateFound = false;
        var seenPairs = new HashSet<(uint, uint)>();

        foreach (var refBox in irefChildren)
        {
            if (refBox.Type == "auxl")
            {
                int p = refBox.BodyStart;
                int end = refBox.BodyStart + refBox.BodyLength;

                if (irefVer == 0)
                {
                    if (p + 4 > end) return null;
                    uint fromId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
                    ushort refCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 2, 2));
                    p += 4;
                    if (p + refCount * 2 > end) return null;

                    for (int r = 0; r < refCount; r++)
                    {
                        uint toId = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p, 2));
                        p += 2;

                        if (fromId == toId) return null;

                        if (fromId == primaryId || toId == primaryId)
                        {
                            var pair = (fromId, toId);
                            if (!seenPairs.Add(pair))
                            {
                                duplicateFound = true;
                            }
                            uint auxId = (fromId == primaryId) ? toId : fromId;
                            candidateRelations.Add((fromId, toId, auxId));
                        }
                    }
                }
                else if (irefVer == 1)
                {
                    if (p + 6 > end) return null;
                    uint fromId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
                    ushort refCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(p + 4, 2));
                    p += 6;
                    if (p + refCount * 4 > end) return null;

                    for (int r = 0; r < refCount; r++)
                    {
                        uint toId = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(p, 4));
                        p += 4;

                        if (fromId == toId) return null;

                        if (fromId == primaryId || toId == primaryId)
                        {
                            var pair = (fromId, toId);
                            if (!seenPairs.Add(pair))
                            {
                                duplicateFound = true;
                            }
                            uint auxId = (fromId == primaryId) ? toId : fromId;
                            candidateRelations.Add((fromId, toId, auxId));
                        }
                    }
                }
            }
        }

        if (duplicateFound) return null;
        if (candidateRelations.Count != 1) return null;

        var singleRel = candidateRelations[0];
        byte[]? payload = ExtractHeicItemPayload(data, singleRel.auxId);
        if (payload == null) return null;

        using var sha = SHA256.Create();
        string payloadSha = Convert.ToHexString(sha.ComputeHash(payload));
        return new HeicAuxRelationSnapshot(primaryId, singleRel.auxId, singleRel.fromId, singleRel.toId, payloadSha);
    }
}
