using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;

namespace LivePhotoBox.Services.Protocols;

/*
 * HeifBoxParser.cs
 *
 * ISO/IEC 14496-12 (ISOBMFF) / ISO/IEC 23008-12 (HEIF) 的最小盒子解析器。
 * 只用于定位 Exif item（meta -> iinf/infe -> iloc），不解析像素数据。
 */
internal static class HeifBoxParser
{
    private readonly struct ItemInfo
    {
        public readonly uint Id;
        public readonly string Type;
        public ItemInfo(uint id, string type) { Id = id; Type = type; }
    }

    private readonly struct ItemLocation
    {
        public readonly byte ConstructionMethod;
        public readonly int ExtentCount;
        public readonly long Offset;
        public readonly long Length;

        public ItemLocation(byte constructionMethod, int extentCount, long offset, long length)
        {
            ConstructionMethod = constructionMethod;
            ExtentCount = extentCount;
            Offset = offset;
            Length = length;
        }
    }

    /// <summary>
    /// 定位 HEIC/HEIF 文件内 item_type='Exif' 的 item 的绝对字节偏移与长度。
    /// 仅接受 construction_method=0（文件偏移）且单 extent 的常规情况，
    /// 遇到 idat / 多 extent / 未知版本等一律返回 false，交由上层回退重编码。
    /// </summary>
    public static bool TryLocateExifItem(string heicPath, out long offset, out long length, out string? error)
    {
        offset = 0;
        length = 0;
        error = null;
        try
        {
            byte[] data = File.ReadAllBytes(heicPath);
            return TryLocateExifItem(data, out offset, out length, out error);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    public static bool TryLocateExifItem(byte[] data, out long offset, out long length, out string? error)
    {
        offset = 0;
        length = 0;
        error = null;

        if (!TryFindBox(data, 0, data.Length, "meta", out int metaStart, out int metaLen, out int metaBodyStart))
        {
            error = "No meta box found.";
            return false;
        }

        // meta 是 FullBox：body 前 4 字节为 version(1)+flags(3)，之后是子盒子。
        int childStart = metaBodyStart + 4;
        int childEnd = metaStart + metaLen;

        int iinfBody = -1, iinfLen = 0, ilocBody = -1, ilocLen = 0;
        if (!TryWalkBoxes(data, childStart, childEnd, (type, body, len) =>
        {
            if (type == "iinf") { iinfBody = body; iinfLen = len; }
            else if (type == "iloc") { ilocBody = body; ilocLen = len; }
        }))
        {
            error = "Meta box is malformed.";
            return false;
        }

        if (iinfBody < 0 || ilocBody < 0)
        {
            error = "Missing iinf or iloc box.";
            return false;
        }

        if (!TryParseIinf(data, iinfBody, iinfLen, out var items, out error)) return false;
        if (!TryParseIloc(data, ilocBody, ilocLen, out var locations, out error)) return false;

        foreach (var item in items)
        {
            if (!item.Type.Equals("Exif", StringComparison.Ordinal)) continue;
            if (!locations.TryGetValue(item.Id, out var loc))
            {
                error = "Exif item has no iloc entry.";
                return false;
            }
            if (loc.ConstructionMethod != 0)
            {
                error = $"Exif item uses construction_method={loc.ConstructionMethod} (unsupported).";
                return false;
            }
            if (loc.ExtentCount != 1)
            {
                error = $"Exif item has {loc.ExtentCount} extents (only single extent supported).";
                return false;
            }
            if (loc.Offset < 0 || loc.Length <= 0 || loc.Offset + loc.Length > data.LongLength)
            {
                error = "Exif item extent out of range.";
                return false;
            }

            offset = loc.Offset;
            length = loc.Length;
            return true;
        }

        error = "No Exif item found.";
        return false;
    }

    private static bool TryParseIinf(byte[] data, int body, int len, out List<ItemInfo> items, out string? error)
    {
        items = new List<ItemInfo>();
        error = null;
        int end = body + len;
        if (body + 4 > end) { error = "iinf too short."; return false; }

        int version = data[body] & 0xFF;
        int p = body + 4;
        uint count;
        if (version == 0)
        {
            if (p + 2 > end) { error = "iinf truncated."; return false; }
            count = Read16(data, p);
            p += 2;
        }
        else
        {
            if (p + 4 > end) { error = "iinf truncated."; return false; }
            count = Read32(data, p);
            p += 4;
        }

        if (count > 1_000_000) { error = "iinf entry count too large."; return false; }

        for (uint i = 0; i < count; i++)
        {
            if (p + 8 > end) { error = "iinf entry truncated."; return false; }
            int boxSize = checked((int)Read32(data, p));
            string boxType = ReadFourCc(data, p + 4);
            if (!boxType.Equals("infe", StringComparison.Ordinal))
            {
                error = $"Unexpected box '{boxType}' inside iinf.";
                return false;
            }
            if (boxSize < 8 || p + boxSize > end) { error = "infe box size out of range."; return false; }

            // infe：size(4) + 'infe'(4) + version(1) + flags(3) + item_ID + protection(2) + item_type(4) + name
            int q = p + 8; // version 字节
            int infeVersion = data[q] & 0xFF;
            uint itemId = infeVersion >= 3 ? Read32(data, q + 4) : Read16(data, q + 4);
            int itemTypeOff = infeVersion >= 3 ? 10 : 8;
            if (q + itemTypeOff + 4 > p + boxSize) { error = "infe item_type truncated."; return false; }
            string itemType = ReadFourCc(data, q + itemTypeOff);
            items.Add(new ItemInfo(itemId, itemType));
            p += boxSize;
        }

        return true;
    }

    private static bool TryParseIloc(byte[] data, int body, int len, out Dictionary<uint, ItemLocation> locations, out string? error)
    {
        locations = new Dictionary<uint, ItemLocation>();
        error = null;
        int end = body + len;
        if (body + 6 > end) { error = "iloc too short."; return false; }

        int version = data[body] & 0xFF;
        int p = body + 4;
        int b0 = data[p] & 0xFF;
        int b1 = data[p + 1] & 0xFF;
        p += 2;
        int offsetSize = (b0 >> 4) & 0x0F;
        int lengthSize = b0 & 0x0F;
        int baseOffsetSize = (b1 >> 4) & 0x0F;
        int indexSize = b1 & 0x0F;

        uint count;
        if (version < 2)
        {
            if (p + 2 > end) { error = "iloc truncated."; return false; }
            count = Read16(data, p);
            p += 2;
        }
        else
        {
            if (p + 4 > end) { error = "iloc truncated."; return false; }
            count = Read32(data, p);
            p += 4;
        }

        if (count > 1_000_000) { error = "iloc item count too large."; return false; }

        for (uint i = 0; i < count; i++)
        {
            uint itemId;
            if (version < 2)
            {
                if (p + 2 > end) { error = "iloc item truncated."; return false; }
                itemId = Read16(data, p);
                p += 2;
            }
            else
            {
                if (p + 4 > end) { error = "iloc item truncated."; return false; }
                itemId = Read32(data, p);
                p += 4;
            }

            byte constructionMethod = 0;
            if (version == 1 || version == 2)
            {
                if (p + 2 > end) { error = "iloc item truncated."; return false; }
                constructionMethod = (byte)(Read16(data, p) & 0x000F);
                p += 2;
            }

            // data_reference_index（16bit，恒存在）
            if (p + 2 > end) { error = "iloc item truncated."; return false; }
            p += 2;

            long baseOffset = 0;
            if (baseOffsetSize > 0)
            {
                if (p + baseOffsetSize > end) { error = "iloc base_offset truncated."; return false; }
                baseOffset = ReadUInt(data, p, baseOffsetSize);
                p += baseOffsetSize;
            }

            if (p + 2 > end) { error = "iloc item truncated."; return false; }
            int extentCount = Read16(data, p);
            p += 2;

            long firstOffset = -1, firstLength = -1;
            for (int e = 0; e < extentCount; e++)
            {
                if ((version == 1 || version == 2) && indexSize > 0)
                {
                    if (p + indexSize > end) { error = "iloc extent truncated."; return false; }
                    p += indexSize;
                }
                if (p + offsetSize > end || p + lengthSize > end) { error = "iloc extent truncated."; return false; }
                long extentOffset = ReadUInt(data, p, offsetSize);
                p += offsetSize;
                long extentLength = ReadUInt(data, p, lengthSize);
                p += lengthSize;
                if (e == 0)
                {
                    firstOffset = baseOffset + extentOffset;
                    firstLength = extentLength;
                }
            }

            locations[itemId] = new ItemLocation(constructionMethod, extentCount, firstOffset, firstLength);
        }

        return true;
    }

    internal static bool TryFindBox(byte[] data, int start, int end, string want, out int boxStart, out int boxLen, out int bodyStart)
    {
        int p = start;
        while (p + 8 <= end)
        {
            long size = Read32(data, p);
            string type = ReadFourCc(data, p + 4);
            int header = 8;
            if (size == 1)
            {
                if (p + 16 > end) break;
                size = (long)Read64(data, p + 8);
                header = 16;
            }
            else if (size == 0)
            {
                size = end - p;
            }

            if (size < header || p + size > end) break;
            if (type == want)
            {
                boxStart = p;
                boxLen = (int)size;
                bodyStart = p + header;
                return true;
            }
            p += (int)size;
        }

        boxStart = -1;
        boxLen = 0;
        bodyStart = -1;
        return false;
    }

    internal static bool TryWalkBoxes(byte[] data, int start, int end, Action<string, int, int> visit)
    {
        int p = start;
        while (p + 8 <= end)
        {
            long size = Read32(data, p);
            string type = ReadFourCc(data, p + 4);
            int header = 8;
            if (size == 1)
            {
                if (p + 16 > end) return false;
                size = (long)Read64(data, p + 8);
                header = 16;
            }
            else if (size == 0)
            {
                size = end - p;
            }

            if (size < header || p + size > end) return false;
            visit(type, p + header, (int)(size - header));
            p += (int)size;
        }
        return true;
    }

    internal static string ReadFourCc(byte[] data, int off)
    {
        if (off + 4 > data.Length) return string.Empty;
        return ((char)data[off]).ToString()
             + (char)data[off + 1]
             + (char)data[off + 2]
             + (char)data[off + 3];
    }

    internal static ushort Read16(byte[] data, int off) => BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(off));
    internal static uint Read32(byte[] data, int off) => BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(off));
    internal static ulong Read64(byte[] data, int off) => BinaryPrimitives.ReadUInt64BigEndian(data.AsSpan(off));

    internal static long ReadUInt(byte[] data, int off, int size)
    {
        long value = 0;
        for (int i = 0; i < size; i++)
        {
            value = (value << 8) | (uint)(data[off + i] & 0xFF);
        }
        return value;
    }
}
