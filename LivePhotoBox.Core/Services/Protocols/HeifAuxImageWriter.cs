using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LivePhotoBox.Services.Protocols;

/*
 * HeifAuxImageWriter.cs
 *
 * 把 heif-enc 生成的“多张顶层图像 HEIC”原地改造成含 Apple hdrgainmap 辅助图的 HEIC。
 *
 *   - heif-enc 已生成多个 hvc1 图像 item 及 iloc/iinf/iprp/ipco/ipma/iref
 *   - 本类只追加 auxC 属性、ipma 关联和 auxl 引用，并同步修正 iloc 的绝对偏移
 */
internal static class HeifAuxImageWriter
{
    private const string HdrGainMapUrn = "urn:com:apple:photo:2020:aux:hdrgainmap";

    private sealed record ItemInfo(uint Id, string Type);

    private sealed record IpmaEntry(uint ItemId, List<bool> Essential, List<int> PropertyIndexes);

    public static bool TryAddHdrGainMapAux(string heicPath, out string? error)
    {
        error = null;
        try
        {
            byte[] data = File.ReadAllBytes(heicPath);
            if (!TryPatch(data, out byte[] patched, out error))
            {
                return false;
            }

            File.WriteAllBytes(heicPath, patched);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static bool TryPatch(byte[] data, out byte[] patched, out string? error)
    {
        patched = Array.Empty<byte>();
        error = null;

        if (!HeifBoxParser.TryFindBox(data, 0, data.Length, "meta", out int metaStart, out int metaSize, out int metaBodyStart))
        {
            error = "No meta box found.";
            return false;
        }

        int metaChildStart = metaBodyStart + 4; // meta FullBox body
        int metaChildEnd = metaStart + metaSize;

        int ilocStart = -1, ilocSize = 0;
        int iinfBodyStart = -1, iinfBodyLen = 0;
        int pitmBodyStart = -1, pitmBodyLen = 0;
        int iprpStart = -1, iprpSize = 0, iprpBodyStart = -1;
        int irefStart = -1, irefSize = 0, irefBodyStart = -1;

        if (!HeifBoxParser.TryWalkBoxes(data, metaChildStart, metaChildEnd, (type, bodyStart, bodyLen) =>
        {
            switch (type)
            {
                case "iloc":
                    ilocStart = bodyStart - 8;
                    ilocSize = bodyLen + 8;
                    break;
                case "iinf":
                    iinfBodyStart = bodyStart;
                    iinfBodyLen = bodyLen;
                    break;
                case "pitm":
                    pitmBodyStart = bodyStart;
                    pitmBodyLen = bodyLen;
                    break;
                case "iprp":
                    iprpStart = bodyStart - 8;
                    iprpSize = bodyLen + 8;
                    iprpBodyStart = bodyStart;
                    break;
                case "iref":
                    irefStart = bodyStart - 8;
                    irefSize = bodyLen + 8;
                    irefBodyStart = bodyStart;
                    break;
            }
        }))
        {
            error = "Meta box is malformed.";
            return false;
        }

        if (ilocStart < 0 || iinfBodyStart < 0 || pitmBodyStart < 0 || iprpStart < 0 || irefStart < 0)
        {
            error = "Required HEIF boxes are missing.";
            return false;
        }

        List<ItemInfo> items = ParseIinf(data, iinfBodyStart, iinfBodyLen, out error);
        if (error != null) return false;

        uint primaryItemId = ReadPrimaryItemId(data, pitmBodyStart, pitmBodyLen);
        uint auxItemId = FindAuxImageItemId(items, primaryItemId);
        if (auxItemId == 0)
        {
            error = "Could not find a second hvc1 image item to use as the HDR gain map.";
            return false;
        }

        if (!HeifBoxParser.TryWalkBoxes(data, iprpBodyStart, iprpBodyStart + (iprpSize - 8), (type, bodyStart, bodyLen) =>
        {
            if (type == "ipco")
            {
                // 稍后统一处理；这里只通过闭包记录会太啰嗦，所以下面用第二次扫描。
            }
        }))
        {
            error = "iprp box is malformed.";
            return false;
        }

        int ipcoStart = -1, ipcoSize = 0;
        int ipmaBodyStart = -1, ipmaBodyLen = 0, ipmaStart = -1;
        if (!HeifBoxParser.TryWalkBoxes(data, iprpBodyStart, iprpBodyStart + (iprpSize - 8), (type, bodyStart, bodyLen) =>
        {
            if (type == "ipco")
            {
                ipcoStart = bodyStart - 8;
                ipcoSize = bodyLen + 8;
            }
            else if (type == "ipma")
            {
                ipmaStart = bodyStart - 8;
                ipmaBodyStart = bodyStart;
                ipmaBodyLen = bodyLen;
            }
        }))
        {
            error = "iprp box is malformed.";
            return false;
        }

        if (ipcoStart < 0 || ipmaBodyStart < 0)
        {
            error = "ipco or ipma box is missing.";
            return false;
        }

        int propertyCount = CountChildBoxes(data, ipcoStart + 8, ipcoStart + ipcoSize);
        int newPropertyIndex = propertyCount + 1;

        byte[] auxCBox = BuildAuxCBox(HdrGainMapUrn);
        byte[] newIpco = AppendChildBox(data, ipcoStart, ipcoSize, auxCBox);

        List<IpmaEntry> ipmaEntries = ParseIpma(data, ipmaBodyStart, ipmaBodyLen, out error);
        if (error != null) return false;

        AddPropertyAssociation(ipmaEntries, auxItemId, newPropertyIndex);
        int ipmaFlags = Read24(data, ipmaBodyStart + 1);
        byte[] newIpma = BuildIpma(ipmaEntries, ipmaFlags);

        byte[] auxlBox = BuildAuxlBox(auxItemId, primaryItemId);
        byte[] newIref = AppendChildBoxToFullBox(data, irefStart, irefSize, auxlBox);

        int delta = (newIpco.Length - ipcoSize) + (newIpma.Length - (ipmaBodyLen + 8)) + (newIref.Length - irefSize);

        byte[] newIloc = ShiftIlocBaseOffsets(data, ilocStart, ilocSize, metaStart + metaSize, delta, out error);
        if (error != null) return false;

        byte[] newIprp = RebuildIpcoAndIpmaBox(data, iprpStart, iprpSize, newIpco, newIpma);
        byte[] newMeta = RebuildMetaBox(
            data, metaStart, metaSize, metaBodyStart,
            newIloc, newIprp, newIref,
            ilocSize, iprpSize, irefSize);

        patched = new byte[metaStart + newMeta.Length + (data.Length - (metaStart + metaSize))];
        Array.Copy(data, 0, patched, 0, metaStart);
        Array.Copy(newMeta, 0, patched, metaStart, newMeta.Length);
        Array.Copy(data, metaStart + metaSize, patched, metaStart + newMeta.Length, data.Length - (metaStart + metaSize));
        return true;
    }

    private static List<ItemInfo> ParseIinf(byte[] data, int bodyStart, int bodyLen, out string? error)
    {
        error = null;
        var items = new List<ItemInfo>();
        int end = bodyStart + bodyLen;
        int p = bodyStart + 4;
        if (p > end)
        {
            error = "iinf too short.";
            return items;
        }

        int version = data[bodyStart] & 0xFF;
        uint count = version == 0
            ? HeifBoxParser.Read16(data, p)
            : HeifBoxParser.Read32(data, p);
        p += version == 0 ? 2 : 4;

        for (uint i = 0; i < count; i++)
        {
            if (p + 8 > end)
            {
                error = "iinf entry truncated.";
                return items;
            }

            int boxSize = checked((int)HeifBoxParser.Read32(data, p));
            if (!HeifBoxParser.ReadFourCc(data, p + 4).Equals("infe", StringComparison.Ordinal))
            {
                error = "Unexpected iinf child box.";
                return items;
            }

            int q = p + 8;
            int infeVersion = data[q] & 0xFF;
            uint itemId = infeVersion >= 3
                ? HeifBoxParser.Read32(data, q + 4)
                : HeifBoxParser.Read16(data, q + 4);
            int itemTypeOffset = infeVersion >= 3 ? 10 : 8;
            if (q + itemTypeOffset + 4 > p + boxSize)
            {
                error = "infe item_type truncated.";
                return items;
            }

            string itemType = HeifBoxParser.ReadFourCc(data, q + itemTypeOffset);
            items.Add(new ItemInfo(itemId, itemType));
            p += boxSize;
        }

        return items;
    }

    private static uint ReadPrimaryItemId(byte[] data, int bodyStart, int bodyLen)
    {
        int version = data[bodyStart] & 0xFF;
        int offset = bodyStart + 4;
        return version == 0
            ? HeifBoxParser.Read16(data, offset)
            : HeifBoxParser.Read32(data, offset);
    }

    private static uint FindAuxImageItemId(List<ItemInfo> items, uint primaryItemId)
    {
        foreach (ItemInfo item in items)
        {
            if (item.Id != primaryItemId && item.Type.Equals("hvc1", StringComparison.Ordinal))
            {
                return item.Id;
            }
        }

        return 0;
    }

    private static int CountChildBoxes(byte[] data, int start, int end)
    {
        int count = 0;
        int p = start;
        while (p + 8 <= end)
        {
            long size = HeifBoxParser.Read32(data, p);
            int header = 8;
            if (size == 1)
            {
                if (p + 16 > end) break;
                size = (long)HeifBoxParser.Read64(data, p + 8);
                header = 16;
            }
            else if (size == 0)
            {
                size = end - p;
            }

            if (size < header || p + size > end) break;
            count++;
            p += (int)size;
        }

        return count;
    }

    private static byte[] BuildAuxCBox(string urn)
    {
        byte[] urnBytes = Encoding.ASCII.GetBytes(urn + "\0");
        int payloadSize = 4 + urnBytes.Length;
        int boxSize = 8 + payloadSize;
        byte[] box = new byte[boxSize];
        WriteU32(box, 0, (uint)boxSize);
        WriteFourCc(box, 4, "auxC");
        WriteU32(box, 8, 0);
        Array.Copy(urnBytes, 0, box, 12, urnBytes.Length);
        return box;
    }

    private static byte[] AppendChildBox(byte[] data, int boxStart, int oldBoxSize, byte[] childBox)
    {
        int newSize = oldBoxSize + childBox.Length;
        byte[] result = new byte[newSize];
        WriteU32(result, 0, (uint)newSize);
        Array.Copy(data, boxStart + 4, result, 4, oldBoxSize - 4);
        Array.Copy(childBox, 0, result, oldBoxSize, childBox.Length);
        return result;
    }

    private static byte[] AppendChildBoxToFullBox(byte[] data, int boxStart, int oldBoxSize, byte[] childBox)
    {
        int newSize = oldBoxSize + childBox.Length;
        byte[] result = new byte[newSize];
        WriteU32(result, 0, (uint)newSize);
        Array.Copy(data, boxStart + 4, result, 4, oldBoxSize - 4);
        Array.Copy(childBox, 0, result, oldBoxSize, childBox.Length);
        return result;
    }

    private static List<IpmaEntry> ParseIpma(byte[] data, int bodyStart, int bodyLen, out string? error)
    {
        error = null;
        var entries = new List<IpmaEntry>();
        int end = bodyStart + bodyLen;
        int p = bodyStart + 4;
        if (p + 4 > end)
        {
            error = "ipma too short.";
            return entries;
        }

        int version = data[bodyStart] & 0xFF;
        int flags = Read24(data, bodyStart + 1);
        uint count = HeifBoxParser.Read32(data, p);
        p += 4;

        bool largePropertyIndex = (flags & 1) != 0;

        for (uint i = 0; i < count; i++)
        {
            if (p + 2 > end)
            {
                error = "ipma entry truncated.";
                return entries;
            }

            uint itemId = HeifBoxParser.Read16(data, p);
            p += 2;
            if (p + 1 > end)
            {
                error = "ipma association count truncated.";
                return entries;
            }

            int associationCount = data[p] & 0xFF;
            p += 1;
            var essential = new List<bool>(associationCount);
            var propertyIndexes = new List<int>(associationCount);

            for (int a = 0; a < associationCount; a++)
            {
                if (largePropertyIndex)
                {
                    if (p + 2 > end)
                    {
                        error = "ipma property index truncated.";
                        return entries;
                    }

                    byte high = data[p];
                    byte low = data[p + 1];
                    p += 2;
                    essential.Add((high & 0x80) != 0);
                    propertyIndexes.Add(((high & 0x7F) << 8) | low);
                }
                else
                {
                    if (p + 1 > end)
                    {
                        error = "ipma property index truncated.";
                        return entries;
                    }

                    byte value = data[p];
                    p += 1;
                    essential.Add((value & 0x80) != 0);
                    propertyIndexes.Add(value & 0x7F);
                }
            }

            entries.Add(new IpmaEntry(itemId, essential, propertyIndexes));
        }

        return entries;
    }

    private static void AddPropertyAssociation(List<IpmaEntry> entries, uint itemId, int propertyIndex)
    {
        foreach (IpmaEntry entry in entries)
        {
            if (entry.ItemId == itemId)
            {
                entry.Essential.Add(false);
                entry.PropertyIndexes.Add(propertyIndex);
                return;
            }
        }

        entries.Add(new IpmaEntry(itemId, [false], [propertyIndex]));
    }

    private static byte[] BuildIpma(List<IpmaEntry> entries, int flags)
    {
        bool largePropertyIndex = (flags & 1) != 0;
        int bodySize = 8 + 4 + 4; // version/flags + item_count
        foreach (IpmaEntry entry in entries)
        {
            bodySize += 2 + 1 + entry.PropertyIndexes.Count * (largePropertyIndex ? 2 : 1);
        }

        byte[] result = new byte[bodySize];
        WriteU32(result, 0, (uint)bodySize);
        WriteFourCc(result, 4, "ipma");
        result[8] = 0;
        Write24(result, 9, flags);
        WriteU32(result, 12, (uint)entries.Count);

        int p = 16;
        foreach (IpmaEntry entry in entries)
        {
            WriteU16(result, p, (ushort)entry.ItemId);
            p += 2;
            result[p] = (byte)entry.PropertyIndexes.Count;
            p += 1;

            for (int i = 0; i < entry.PropertyIndexes.Count; i++)
            {
                if (largePropertyIndex)
                {
                    byte high = (byte)((entry.Essential[i] ? 0x80 : 0x00) | ((entry.PropertyIndexes[i] >> 8) & 0x7F));
                    byte low = (byte)(entry.PropertyIndexes[i] & 0xFF);
                    result[p] = high;
                    result[p + 1] = low;
                    p += 2;
                }
                else
                {
                    result[p] = (byte)((entry.Essential[i] ? 0x80 : 0x00) | (entry.PropertyIndexes[i] & 0x7F));
                    p += 1;
                }
            }
        }

        return result;
    }

    private static byte[] BuildAuxlBox(uint auxItemId, uint primaryItemId)
    {
        if (auxItemId > ushort.MaxValue || primaryItemId > ushort.MaxValue)
        {
            throw new InvalidOperationException("auxl 32-bit item IDs are not supported.");
        }

        byte[] box = new byte[14];
        WriteU32(box, 0, 14);
        WriteFourCc(box, 4, "auxl");
        WriteU16(box, 8, (ushort)auxItemId);
        WriteU16(box, 10, 1);
        WriteU16(box, 12, (ushort)primaryItemId);
        return box;
    }

    private static byte[] ShiftIlocBaseOffsets(
        byte[] data, int boxStart, int boxSize, int insertionPoint, int delta, out string? error)
    {
        error = null;
        byte[] result = new byte[boxSize];
        Array.Copy(data, boxStart, result, 0, boxSize);

        int bodyStart = 8;
        int end = boxSize;
        int p = bodyStart + 4;
        if (p + 2 > end)
        {
            error = "iloc too short.";
            return result;
        }

        int version = result[bodyStart] & 0xFF;
        int b0 = result[p] & 0xFF;
        int b1 = result[p + 1] & 0xFF;
        p += 2;
        int offsetSize = (b0 >> 4) & 0x0F;
        int lengthSize = b0 & 0x0F;
        int baseOffsetSize = (b1 >> 4) & 0x0F;
        int indexSize = b1 & 0x0F;

        uint count = version < 2
            ? HeifBoxParser.Read16(result, p)
            : HeifBoxParser.Read32(result, p);
        p += version < 2 ? 2 : 4;

        for (uint i = 0; i < count; i++)
        {
            if (version < 2)
            {
                p += 2; // item_ID
            }
            else
            {
                p += 4; // item_ID
            }

            if (version == 1 || version == 2)
            {
                p += 2; // construction_method
            }

            p += 2; // data_reference_index

            if (p + baseOffsetSize > end)
            {
                error = "iloc base offset truncated.";
                return result;
            }

            long baseOffset = HeifBoxParser.ReadUInt(result, p, baseOffsetSize);
            if (baseOffset > insertionPoint)
            {
                baseOffset += delta;
                WriteUInt(result, p, baseOffset, baseOffsetSize);
            }

            p += baseOffsetSize;

            if (p + 2 > end)
            {
                error = "iloc extent count truncated.";
                return result;
            }

            int extentCount = HeifBoxParser.Read16(result, p);
            p += 2;

            for (int e = 0; e < extentCount; e++)
            {
                if ((version == 1 || version == 2) && indexSize > 0)
                {
                    p += indexSize;
                }

                p += offsetSize + lengthSize;
                if (p > end)
                {
                    error = "iloc extent truncated.";
                    return result;
                }
            }
        }

        return result;
    }

    private static byte[] RebuildIpcoAndIpmaBox(
        byte[] data, int iprpStart, int iprpSize, byte[] newIpco, byte[] newIpma)
    {
        int newSize = iprpSize + (newIpco.Length - FindChildSize(data, iprpStart, iprpSize, "ipco", 8))
            + (newIpma.Length - FindChildSize(data, iprpStart, iprpSize, "ipma", 8));

        byte[] result = new byte[newSize];
        WriteU32(result, 0, (uint)newSize);
        WriteFourCc(result, 4, "iprp");

        int dest = 8;
        int bodyStart = 8;
        int end = iprpSize;
        int p = bodyStart;
        while (p + 8 <= end)
        {
            int childSize = checked((int)HeifBoxParser.Read32(data, iprpStart + p));
            string childType = HeifBoxParser.ReadFourCc(data, iprpStart + p + 4);
            int childHeader = 8;
            if (childSize == 1)
            {
                childSize = checked((int)HeifBoxParser.Read64(data, iprpStart + p + 8));
                childHeader = 16;
            }

            if (childSize < childHeader || p + childSize > end)
            {
                break;
            }

            if (childType == "ipco")
            {
                Array.Copy(newIpco, 0, result, dest, newIpco.Length);
                dest += newIpco.Length;
            }
            else if (childType == "ipma")
            {
                Array.Copy(newIpma, 0, result, dest, newIpma.Length);
                dest += newIpma.Length;
            }
            else
            {
                Array.Copy(data, iprpStart + p, result, dest, childSize);
                dest += childSize;
            }

            p += childSize;
        }

        return result;
    }

    private static byte[] RebuildMetaBox(
        byte[] data, int metaStart, int metaSize, int metaBodyStart,
        byte[] newIloc, byte[] newIprp, byte[] newIref,
        int oldIlocSize, int oldIprpSize, int oldIrefSize)
    {
        int newSize = metaSize
            + (newIloc.Length - oldIlocSize)
            + (newIprp.Length - oldIprpSize)
            + (newIref.Length - oldIrefSize);

        byte[] result = new byte[newSize];
        WriteU32(result, 0, (uint)newSize);
        WriteFourCc(result, 4, "meta");
        Array.Copy(data, metaBodyStart, result, 8, 4); // meta FullBox version/flags

        int dest = 12;
        int childStart = metaBodyStart + 4;
        int childEnd = metaStart + metaSize;
        int p = childStart;
        while (p + 8 <= childEnd)
        {
            int childSize = checked((int)HeifBoxParser.Read32(data, p));
            string childType = HeifBoxParser.ReadFourCc(data, p + 4);
            int childHeader = 8;
            if (childSize == 1)
            {
                childSize = checked((int)HeifBoxParser.Read64(data, p + 8));
                childHeader = 16;
            }

            if (childSize < childHeader || p + childSize > childEnd)
            {
                break;
            }

            if (childType == "iloc")
            {
                Array.Copy(newIloc, 0, result, dest, newIloc.Length);
                dest += newIloc.Length;
            }
            else if (childType == "iprp")
            {
                Array.Copy(newIprp, 0, result, dest, newIprp.Length);
                dest += newIprp.Length;
            }
            else if (childType == "iref")
            {
                Array.Copy(newIref, 0, result, dest, newIref.Length);
                dest += newIref.Length;
            }
            else
            {
                Array.Copy(data, p, result, dest, childSize);
                dest += childSize;
            }

            p += childSize;
        }

        return result;
    }

    private static int FindChildSize(byte[] data, int parentStart, int parentSize, string wantedType, int bodyStart)
    {
        int end = parentSize;
        int p = bodyStart;
        while (p + 8 <= end)
        {
            int size = checked((int)HeifBoxParser.Read32(data, parentStart + p));
            string type = HeifBoxParser.ReadFourCc(data, parentStart + p + 4);
            int header = 8;
            if (size == 1)
            {
                size = checked((int)HeifBoxParser.Read64(data, parentStart + p + 8));
                header = 16;
            }

            if (size < header || p + size > end)
            {
                break;
            }

            if (type == wantedType)
            {
                return size;
            }

            p += size;
        }

        throw new InvalidOperationException($"Missing child box: {wantedType}");
    }

    private static int Read24(byte[] data, int offset)
    {
        return (data[offset] & 0xFF) << 16
            | (data[offset + 1] & 0xFF) << 8
            | (data[offset + 2] & 0xFF);
    }

    private static void WriteU16(byte[] data, int offset, ushort value)
    {
        data[offset] = (byte)(value >> 8);
        data[offset + 1] = (byte)value;
    }

    private static void Write24(byte[] data, int offset, int value)
    {
        data[offset] = (byte)(value >> 16);
        data[offset + 1] = (byte)(value >> 8);
        data[offset + 2] = (byte)value;
    }

    private static void WriteU32(byte[] data, int offset, uint value)
    {
        data[offset] = (byte)(value >> 24);
        data[offset + 1] = (byte)(value >> 16);
        data[offset + 2] = (byte)(value >> 8);
        data[offset + 3] = (byte)value;
    }

    private static void WriteUInt(byte[] data, int offset, long value, int size)
    {
        for (int i = size - 1; i >= 0; i--)
        {
            data[offset + i] = (byte)(value & 0xFF);
            value >>= 8;
        }
    }

    private static void WriteFourCc(byte[] data, int offset, string type)
    {
        data[offset] = (byte)type[0];
        data[offset + 1] = (byte)type[1];
        data[offset + 2] = (byte)type[2];
        data[offset + 3] = (byte)type[3];
    }
}
