using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace LivePhotoBox.Core.Tests.Support;

internal sealed record W3SyntheticItem(
    ushort Id,
    string Type,
    uint Offset,
    uint Length,
    string? AuxiliaryType = null);

internal sealed record W3SyntheticReference(
    string Type,
    ushort From,
    IReadOnlyList<ushort> Targets);

internal static class W3HeifSyntheticBuilder
{
    internal static byte[] Build(
        IReadOnlyList<W3SyntheticItem> items,
        IReadOnlyList<W3SyntheticReference> references,
        ushort primaryId = 1)
    {
        if (items.Count == 0) throw new ArgumentException("At least one item is required.", nameof(items));

        byte[] ftyp = Box("ftyp", Concat(Ascii("heic"), U32(0), Ascii("mif1")));
        byte[] metaWithoutOffsets = BuildMeta(items, references, primaryId, payloadStart: 0);
        uint payloadStart = checked((uint)(ftyp.Length + metaWithoutOffsets.Length + 8));
        byte[] meta = BuildMeta(items, references, primaryId, payloadStart);
        if (meta.Length != metaWithoutOffsets.Length)
            throw new InvalidOperationException("Synthetic meta size changed after iloc offset calculation.");

        uint payloadLength = items.Count == 0
            ? 0
            : items.Max(item => checked(item.Offset + item.Length));
        byte[] payload = new byte[Math.Max(payloadLength, 32)];
        for (int i = 0; i < payload.Length; i++) payload[i] = checked((byte)(0x20 + (i % 0x5f)));
        byte[] mdat = Box("mdat", payload);
        return Concat(ftyp, meta, mdat);
    }

    private static byte[] BuildMeta(
        IReadOnlyList<W3SyntheticItem> items,
        IReadOnlyList<W3SyntheticReference> references,
        ushort primaryId,
        uint payloadStart)
    {
        var children = new List<byte[]>
        {
            Box("pitm", Concat(new byte[] { 0, 0, 0, 0 }, U16(primaryId))),
            BuildIinf(items),
            BuildIref(references),
            BuildIprp(items),
            BuildIloc(items, payloadStart)
        };
        return Box("meta", Concat(new byte[] { 0, 0, 0, 0 }, Concat(children.ToArray())));
    }

    private static byte[] BuildIinf(IReadOnlyList<W3SyntheticItem> items)
    {
        var infe = items.Select(item =>
            Box("infe", Concat(
                new byte[] { 2, 0, 0, 0 },
                U16(item.Id),
                U16(0),
                Ascii(item.Type),
                new byte[] { 0 }))).ToArray();
        return Box("iinf", Concat(new byte[] { 0, 0, 0, 0 }, U16(checked((ushort)items.Count)), Concat(infe)));
    }

    private static byte[] BuildIloc(IReadOnlyList<W3SyntheticItem> items, uint payloadStart)
    {
        var body = new List<byte>
        {
            0, 0, 0, 0,
            0x44, 0x00
        };
        body.AddRange(U16(checked((ushort)items.Count)));
        foreach (W3SyntheticItem item in items)
        {
            body.AddRange(U16(item.Id));
            body.AddRange(U16(0)); // data_reference_index
            body.AddRange(U16(1)); // one contiguous extent
            body.AddRange(U32(checked(payloadStart + item.Offset)));
            body.AddRange(U32(item.Length));
        }
        return Box("iloc", body.ToArray());
    }

    private static byte[] BuildIref(IReadOnlyList<W3SyntheticReference> references)
    {
        var boxes = references.Select(reference =>
            Box(reference.Type, Concat(
                U16(reference.From),
                U16(checked((ushort)reference.Targets.Count)),
                Concat(reference.Targets.Select(U16).ToArray())))).ToArray();
        return Box("iref", Concat(new byte[] { 0, 0, 0, 0 }, Concat(boxes)));
    }

    private static byte[] BuildIprp(IReadOnlyList<W3SyntheticItem> items)
    {
        var auxiliaryItems = items.Where(item => !string.IsNullOrWhiteSpace(item.AuxiliaryType)).ToArray();
        var properties = auxiliaryItems.Select(item =>
            Box("auxC", Concat(new byte[] { 0, 0, 0, 0 }, Encoding.UTF8.GetBytes(item.AuxiliaryType!), new byte[] { 0 }))).ToArray();
        byte[] ipco = Box("ipco", Concat(properties));

        var ipmaBody = new List<byte> { 0, 0, 0, 0 };
        ipmaBody.AddRange(U32(checked((uint)auxiliaryItems.Length)));
        for (int i = 0; i < auxiliaryItems.Length; i++)
        {
            ipmaBody.AddRange(U16(auxiliaryItems[i].Id));
            ipmaBody.Add(1);
            ipmaBody.Add(checked((byte)(i + 1)));
        }
        byte[] ipma = Box("ipma", ipmaBody.ToArray());
        return Box("iprp", Concat(ipco, ipma));
    }

    private static byte[] Box(string type, byte[] body)
    {
        if (type.Length != 4) throw new ArgumentException("Box type must be four bytes.", nameof(type));
        return Concat(U32(checked((uint)(8 + body.Length))), Ascii(type), body);
    }

    private static byte[] Ascii(string value) => Encoding.ASCII.GetBytes(value);
    private static byte[] U16(ushort value) => [(byte)(value >> 8), (byte)value];
    private static byte[] U32(uint value) => [(byte)(value >> 24), (byte)(value >> 16), (byte)(value >> 8), (byte)value];

    private static byte[] Concat(params byte[][] parts)
    {
        int length = parts.Sum(part => part.Length);
        byte[] result = new byte[length];
        int offset = 0;
        foreach (byte[] part in parts)
        {
            Buffer.BlockCopy(part, 0, result, offset, part.Length);
            offset += part.Length;
        }
        return result;
    }
}
