using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LivePhotoBox.Core.Tests.Support;

internal sealed record W3IndependentHeifItem(
    uint ItemId,
    string ItemType,
    IReadOnlyList<W3IndependentHeifRange> Ranges,
    IReadOnlyList<uint> Dependencies);

internal sealed record W3IndependentHeifRange(ulong Offset, ulong Length);

internal sealed record W3IndependentHeifReference(
    string Type,
    uint From,
    IReadOnlyList<uint> Targets);

internal sealed record W3IndependentHeifAuxiliary(
    uint ItemId,
    string ItemType,
    string Relationship,
    uint OwnerItemId,
    IReadOnlyList<uint> Dependencies,
    IReadOnlyList<W3IndependentHeifRange> Ranges);

internal sealed record W3IndependentHeifGraph(
    uint PrimaryItemId,
    string PrimaryItemType,
    IReadOnlyList<W3IndependentHeifItem> Items,
    IReadOnlyList<W3IndependentHeifReference> References,
    IReadOnlyList<W3IndependentHeifAuxiliary> Auxiliaries);

/// <summary>
/// Small independent ISO-BMFF/HEIF graph reader used only for evidence. It
/// intentionally does not call the product parser or rely on product facts.
/// </summary>
internal static class W3IndependentHeifParser
{
    private sealed record Box(int Start, int Size, int Header, string Type)
    {
        internal int BodyStart => Start + Header;
        internal int BodyEnd => Start + Size;
    }

    private sealed record Item(uint Id, string Type, string ContentType);
    private sealed record Location(uint Id, List<W3IndependentHeifRange> Ranges);

    internal static W3IndependentHeifGraph Parse(byte[] data)
    {
        List<Box> top = Children(data, 0, data.Length);
        Box meta = Single(top, "meta");
        List<Box> metaChildren = Children(data, meta.BodyStart + 4, meta.BodyEnd);
        Box pitm = Single(metaChildren, "pitm");
        Box iinf = Single(metaChildren, "iinf");
        Box iloc = Single(metaChildren, "iloc");
        Box? iref = metaChildren.SingleOrDefault(box => box.Type == "iref");
        Box? idat = metaChildren.SingleOrDefault(box => box.Type == "idat");
        Box? iprp = metaChildren.SingleOrDefault(box => box.Type == "iprp");

        uint primaryId = ReadPitm(data, pitm);
        List<Item> items = ReadItems(data, iinf);
        Dictionary<uint, Location> locations = ReadLocations(data, iloc, idat);
        List<W3IndependentHeifReference> references = iref == null
            ? []
            : ReadReferences(data, iref, items);
        Dictionary<uint, string> relationships = iprp == null
            ? []
            : ReadAuxiliaryTypes(data, iprp, items);

        var dependencyMap = references
            .Where(reference => reference.Type == "dimg")
            .ToDictionary(reference => reference.From, reference => reference.Targets);
        var flattenedCache = new Dictionary<uint, IReadOnlyList<uint>>();
        IReadOnlyList<uint> Flatten(uint id, HashSet<uint> path)
        {
            if (!dependencyMap.TryGetValue(id, out IReadOnlyList<uint>? direct)) return [];
            if (!path.Add(id)) throw new InvalidDataException("Independent HEIF graph contains a cycle.");
            var result = new List<uint>();
            foreach (uint child in direct)
            {
                if (!result.Contains(child)) result.Add(child);
                foreach (uint nested in Flatten(child, path))
                {
                    if (!result.Contains(nested)) result.Add(nested);
                }
            }
            path.Remove(id);
            return result;
        }

        var graphItems = items.Select(item =>
        {
            IReadOnlyList<uint> dependencies = Flatten(item.Id, new HashSet<uint>());
            return new W3IndependentHeifItem(
                item.Id,
                item.Type,
                locations.TryGetValue(item.Id, out Location? location) ? location.Ranges : [],
                dependencies);
        }).ToArray();

        var auxiliaries = new List<W3IndependentHeifAuxiliary>();
        foreach (W3IndependentHeifReference reference in references.Where(reference => reference.Type == "auxl"))
        {
            uint auxiliaryId = reference.From == primaryId && reference.Targets.Count == 1
                ? reference.Targets[0]
                : reference.Targets.Contains(primaryId) && reference.Targets.Count == 1
                    ? reference.From
                    : 0;
            if (auxiliaryId == 0) continue;
            Item item = items.Single(value => value.Id == auxiliaryId);
            auxiliaries.Add(new W3IndependentHeifAuxiliary(
                auxiliaryId,
                item.Type,
                relationships.TryGetValue(auxiliaryId, out string? relationship) ? relationship : "auxl",
                primaryId,
                Flatten(auxiliaryId, new HashSet<uint>()),
                locations[auxiliaryId].Ranges));
        }

        Item primary = items.Single(item => item.Id == primaryId);
        return new W3IndependentHeifGraph(primaryId, primary.Type, graphItems, references, auxiliaries);
    }

    private static uint ReadPitm(byte[] data, Box box)
    {
        return data[box.BodyStart] == 0
            ? U16(data, box.BodyStart + 4)
            : U32(data, box.BodyStart + 4);
    }

    private static List<Item> ReadItems(byte[] data, Box iinf)
    {
        int p = iinf.BodyStart + 4;
        uint count = data[iinf.BodyStart] == 0 ? U16(data, p) : U32(data, p);
        p += data[iinf.BodyStart] == 0 ? 2 : 4;
        List<Box> entries = Children(data, p, iinf.BodyEnd);
        if (entries.Count != count) throw new InvalidDataException("Independent HEIF iinf count mismatch.");
        var result = new List<Item>();
        foreach (Box entry in entries)
        {
            int q = entry.BodyStart + 4;
            byte version = data[entry.BodyStart];
            uint id = version == 2 ? U16(data, q) : U32(data, q);
            q += version == 2 ? 2 : 4;
            q += 2;
            string type = Ascii(data, q, 4);
            q += 4;
            _ = ReadCString(data, ref q, entry.BodyEnd);
            string contentType = string.Empty;
            if (type == "mime") contentType = ReadCString(data, ref q, entry.BodyEnd);
            result.Add(new Item(id, type, contentType));
        }
        return result;
    }

    private static Dictionary<uint, Location> ReadLocations(byte[] data, Box iloc, Box? idat)
    {
        int p = iloc.BodyStart;
        byte version = data[p++];
        p += 3;
        byte offsetSize = (byte)(data[p] >> 4);
        byte lengthSize = (byte)(data[p] & 0x0F);
        byte baseSize = (byte)(data[p + 1] >> 4);
        byte indexSize = version == 0 ? (byte)0 : (byte)(data[p + 1] & 0x0F);
        p += 2;
        uint count = version < 2 ? U16(data, p) : U32(data, p);
        p += version < 2 ? 2 : 4;
        var result = new Dictionary<uint, Location>();
        for (uint i = 0; i < count; i++)
        {
            uint id = version < 2 ? U16(data, p) : U32(data, p);
            p += version < 2 ? 2 : 4;
            ushort method = version == 0 ? (ushort)0 : U16(data, p);
            if (version != 0) p += 2;
            p += 2;
            ulong baseOffset = ReadSized(data, ref p, baseSize);
            ushort extentCount = U16(data, p);
            p += 2;
            var ranges = new List<W3IndependentHeifRange>();
            for (int j = 0; j < extentCount; j++)
            {
                if (version != 0) _ = ReadSized(data, ref p, indexSize);
                ulong extentOffset = ReadSized(data, ref p, offsetSize);
                ulong extentLength = ReadSized(data, ref p, lengthSize);
                ulong ownerBase = method == 1 ? (ulong)idat!.BodyStart : 0;
                ranges.Add(new W3IndependentHeifRange(ownerBase + baseOffset + extentOffset, extentLength));
            }
            result.Add(id, new Location(id, ranges));
        }
        return result;
    }

    private static List<W3IndependentHeifReference> ReadReferences(
        byte[] data,
        Box iref,
        IReadOnlyList<Item> items)
    {
        byte version = data[iref.BodyStart];
        int width = version == 0 ? 2 : 4;
        var result = new List<W3IndependentHeifReference>();
        foreach (Box box in Children(data, iref.BodyStart + 4, iref.BodyEnd))
        {
            int p = box.BodyStart;
            uint from = width == 2 ? U16(data, p) : U32(data, p);
            p += width;
            ushort count = U16(data, p);
            p += 2;
            var targets = new List<uint>(count);
            for (int i = 0; i < count; i++)
            {
                targets.Add(width == 2 ? U16(data, p) : U32(data, p));
                p += width;
            }
            result.Add(new W3IndependentHeifReference(box.Type, from, targets));
        }
        return result;
    }

    private static Dictionary<uint, string> ReadAuxiliaryTypes(
        byte[] data,
        Box iprp,
        IReadOnlyList<Item> items)
    {
        List<Box> children = Children(data, iprp.BodyStart, iprp.BodyEnd);
        Box ipco = children.Single(box => box.Type == "ipco");
        Box ipma = children.Single(box => box.Type == "ipma");
        var properties = new List<string?> { null };
        foreach (Box property in Children(data, ipco.BodyStart, ipco.BodyEnd))
        {
            if (property.Type != "auxC")
            {
                properties.Add(null);
                continue;
            }
            int propertyPosition = property.BodyStart + 4;
            properties.Add(ReadCString(data, ref propertyPosition, property.BodyEnd));
        }
        int p = ipma.BodyStart + 4;
        uint count = U32(data, p);
        p += 4;
        bool wideAssociations = ((uint)data[ipma.BodyStart + 1] << 16 |
            (uint)data[ipma.BodyStart + 2] << 8 | data[ipma.BodyStart + 3]) != 0;
        var result = new Dictionary<uint, string>();
        for (uint i = 0; i < count; i++)
        {
            uint id = data[ipma.BodyStart] == 0 ? U16(data, p) : U32(data, p);
            p += data[ipma.BodyStart] == 0 ? 2 : 4;
            byte associationCount = data[p++];
            for (int a = 0; a < associationCount; a++)
            {
                int propertyIndex = wideAssociations ? U16(data, p) & 0x7FFF : data[p] & 0x7F;
                p += wideAssociations ? 2 : 1;
                if (propertyIndex > 0 && propertyIndex < properties.Count && properties[propertyIndex] is string value)
                    result[id] = value;
            }
        }
        return result;
    }

    private static List<Box> Children(byte[] data, int start, int end)
    {
        var result = new List<Box>();
        int p = start;
        while (p < end)
        {
            uint size32 = U32(data, p);
            int header = 8;
            long size = size32;
            if (size32 == 1)
            {
                size = checked((long)U64(data, p + 8));
                header = 16;
            }
            if (size < header || size > end - p) throw new InvalidDataException("Independent HEIF box bounds are invalid.");
            result.Add(new Box(p, checked((int)size), header, Ascii(data, p + 4, 4)));
            p += checked((int)size);
        }
        if (p != end) throw new InvalidDataException("Independent HEIF child boxes do not fill their parent.");
        return result;
    }

    private static Box Single(IReadOnlyList<Box> boxes, string type) =>
        boxes.Single(box => box.Type == type);

    private static ulong ReadSized(byte[] data, ref int position, int size)
    {
        if (size == 0) return 0;
        ulong value = size switch
        {
            4 => U32(data, position),
            8 => U64(data, position),
            _ => throw new InvalidDataException("Independent HEIF iloc field size is unsupported.")
        };
        position += size;
        return value;
    }

    private static string ReadCString(byte[] data, ref int position, int end)
    {
        int start = position;
        while (position < end && data[position] != 0) position++;
        if (position >= end) throw new InvalidDataException("Independent HEIF string is unterminated.");
        string result = Encoding.UTF8.GetString(data, start, position - start);
        position++;
        return result;
    }

    private static string Ascii(byte[] data, int offset, int length) => Encoding.ASCII.GetString(data, offset, length);
    private static ushort U16(byte[] data, int offset) => (ushort)((data[offset] << 8) | data[offset + 1]);
    private static uint U32(byte[] data, int offset) =>
        ((uint)data[offset] << 24) | ((uint)data[offset + 1] << 16) | ((uint)data[offset + 2] << 8) | data[offset + 3];
    private static ulong U64(byte[] data, int offset) => ((ulong)U32(data, offset) << 32) | U32(data, offset + 4);
}
