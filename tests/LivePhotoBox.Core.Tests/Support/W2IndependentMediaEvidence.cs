using System;
using System.Collections.Generic;
using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Core.Tests.Support;

internal sealed record SamsungSefEntryEvidence(
    ushort Marker,
    string Name,
    long PayloadOffset,
    long PayloadLength);

internal sealed record SamsungSefEvidence(
    long SeffHeaderOffset,
    long IndexTrailerLength,
    IReadOnlyList<SamsungSefEntryEvidence> PreservedEntries);

internal sealed record HeifAuxiliaryEvidence(
    long AuxiliaryPropertyOffset,
    long AuxiliaryPropertyLength,
    string Relationship,
    bool HasAuxlReference,
    bool HasPrimaryItemBox,
    bool HasItemInfoBox);

/// <summary>
/// Test-side evidence readers. These deliberately do not call Native or the
/// product Inspector; they only parse the bounded structures needed to check
/// W2 handoff identities and source-range hashes.
/// </summary>
internal static class W2IndependentMediaEvidence
{
    internal static async Task<string> ComputeSliceSha256Async(
        string path,
        long offset,
        long length,
        CancellationToken cancellationToken = default)
    {
        if (offset < 0 || length <= 0)
            throw new ArgumentOutOfRangeException(nameof(offset));

        await using var stream = new FileStream(
            path,
            FileMode.Open,
            FileAccess.Read,
            FileShare.ReadWrite,
            64 * 1024,
            useAsync: true);
        if (offset > stream.Length || length > stream.Length - offset)
            throw new InvalidDataException("Slice is outside the source file.");

        stream.Position = offset;
        using IncrementalHash hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        byte[] buffer = new byte[64 * 1024];
        long remaining = length;
        while (remaining > 0)
        {
            int read = await stream.ReadAsync(
                buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)),
                cancellationToken).ConfigureAwait(false);
            if (read == 0) throw new EndOfStreamException("Source slice ended before its declared length.");
            hash.AppendData(buffer, 0, read);
            remaining -= read;
        }
        return Convert.ToHexString(hash.GetHashAndReset());
    }

    internal static void AssertJpegRange(byte[] source, long offset, long length)
    {
        if (offset < 0 || length < 4 || offset + length > source.LongLength)
            throw new InvalidDataException("Independent JPEG range is outside the source.");

        int start = checked((int)offset);
        int end = checked((int)(offset + length));
        if (source[start] != 0xFF || source[start + 1] != 0xD8 ||
            source[end - 2] != 0xFF || source[end - 1] != 0xD9)
        {
            throw new InvalidDataException("Independent JPEG range does not have SOI/EOI markers.");
        }
    }

    internal static bool TryParseSamsungSef(
        byte[] source,
        long trailerOffset,
        out SamsungSefEvidence evidence)
    {
        evidence = null!;
        if (trailerOffset < 0 || trailerOffset > source.LongLength ||
            source.LongLength - trailerOffset < 16)
        {
            return false;
        }

        int start = checked((int)trailerOffset);
        int footer = source.Length - 8;
        if (!FourCc(source, footer + 4, "SEFT")) return false;

        uint totalSize = ReadLe32(source, footer);
        if (totalSize < 12 || totalSize > footer - start) return false;
        int sefh = footer - checked((int)totalSize);
        if (sefh < start || sefh + 12 > footer || !FourCc(source, sefh, "SEFH")) return false;

        uint count = ReadLe32(source, sefh + 8);
        long tableEnd = (long)sefh + 12 + (long)count * 12;
        if (tableEnd != footer) return false;

        var entries = new List<SamsungSefEntryEvidence>();
        var ownedRanges = new List<(int Start, int End)>();
        for (uint i = 0; i < count; i++)
        {
            int directoryEntry = checked(sefh + 12 + (int)i * 12);
            ushort prefix = ReadLe16(source, directoryEntry);
            ushort marker = ReadLe16(source, directoryEntry + 2);
            uint backOffset = ReadLe32(source, directoryEntry + 4);
            uint payloadSize = ReadLe32(source, directoryEntry + 8);
            if (payloadSize < 8 || backOffset > (uint)(sefh - start) || payloadSize > backOffset)
                return false;

            int payload = sefh - checked((int)backOffset);
            int payloadEnd = checked(payload + (int)payloadSize);
            if (payload < start || payloadEnd > sefh ||
                ReadLe16(source, payload) != prefix ||
                ReadLe16(source, payload + 2) != marker)
            {
                return false;
            }

            foreach ((int priorStart, int priorEnd) in ownedRanges)
            {
                if (payload < priorEnd && priorStart < payloadEnd) return false;
            }
            ownedRanges.Add((payload, payloadEnd));

            uint nameSize = ReadLe32(source, payload + 4);
            if (nameSize > payloadSize - 8) return false;
            string name = Encoding.UTF8.GetString(source, payload + 8, checked((int)nameSize));
            if (marker is 0x0A30 or 0x0A31) continue;
            entries.Add(new SamsungSefEntryEvidence(marker, name, payload, payloadSize));
        }

        evidence = new SamsungSefEvidence(
            sefh,
            source.LongLength - sefh,
            entries);
        return true;
    }

    internal static bool TryFindSamsungHdrGainMapAuxiliary(
        byte[] source,
        out HeifAuxiliaryEvidence evidence)
    {
        evidence = null!;
        List<IsoBox> topLevel = ParseBoxes(source, 0, source.Length);
        IsoBox? meta = topLevel.Find(box => box.Type == "meta");
        if (meta is null) return false;

        var nested = new List<IsoBox>();
        CollectKnownChildren(source, meta.Value, nested);
        IsoBox? auxC = nested.Find(box =>
            box.Type == "auxC" &&
            ReadAuxiliaryType(source, box, out string? type) &&
            string.Equals(type, "urn:com:samsung:photo:2024:aux:hdrgainmap", StringComparison.Ordinal));
        if (auxC is null) return false;

        IsoBox? iref = nested.Find(box => box.Type == "iref");
        bool hasAuxl = iref is not null && ContainsChildType(source, iref.Value, "auxl");
        bool hasPitm = nested.Exists(box => box.Type == "pitm");
        bool hasIinf = nested.Exists(box => box.Type == "iinf");
        if (!hasAuxl || !hasPitm || !hasIinf ||
            !ReadAuxiliaryType(source, auxC.Value, out string? relationship))
        {
            return false;
        }

        evidence = new HeifAuxiliaryEvidence(
            auxC.Value.Offset,
            auxC.Value.Size,
            relationship!,
            hasAuxl,
            hasPitm,
            hasIinf);
        return true;
    }

    private readonly record struct IsoBox(string Type, int Offset, int Size, int HeaderSize, int PayloadOffset, int End);

    private static List<IsoBox> ParseBoxes(byte[] data, int start, int end)
    {
        var result = new List<IsoBox>();
        int cursor = start;
        while (cursor + 8 <= end)
        {
            uint smallSize = ReadBe32(data, cursor);
            string type = Encoding.ASCII.GetString(data, cursor + 4, 4);
            long size = smallSize;
            int header = 8;
            if (smallSize == 1)
            {
                if (cursor + 16 > end) break;
                size = checked((long)ReadBe64(data, cursor + 8));
                header = 16;
            }
            else if (smallSize == 0)
            {
                size = end - cursor;
            }

            if (size < header || size > end - cursor || size > int.MaxValue) break;
            int boxSize = (int)size;
            result.Add(new IsoBox(type, cursor, boxSize, header, cursor + header, cursor + boxSize));
            cursor += boxSize;
        }
        return result;
    }

    private static void CollectKnownChildren(byte[] data, IsoBox parent, List<IsoBox> result)
    {
        int childStart = parent.PayloadOffset;
        if (parent.Type is "meta" or "iref" or "iinf") childStart += 4;
        foreach (IsoBox child in ParseBoxes(data, childStart, parent.End))
        {
            result.Add(child);
            if (child.Type is "iprp" or "ipco" or "meta" or "iref" or "iinf")
            {
                CollectKnownChildren(data, child, result);
            }
        }
    }

    private static bool ContainsChildType(byte[] data, IsoBox parent, string type)
    {
        int childStart = parent.PayloadOffset + 4;
        return ParseBoxes(data, childStart, parent.End).Exists(box => box.Type == type);
    }

    private static bool ReadAuxiliaryType(byte[] data, IsoBox box, out string? value)
    {
        value = null;
        int start = box.PayloadOffset + 4;
        if (start > box.End) return false;
        int end = Array.IndexOf(data, (byte)0, start, box.End - start);
        if (end < 0) end = box.End;
        if (end == start) return false;
        value = Encoding.UTF8.GetString(data, start, end - start);
        return true;
    }

    private static bool FourCc(byte[] data, int offset, string value) =>
        offset >= 0 && offset + 4 <= data.Length &&
        data[offset] == value[0] && data[offset + 1] == value[1] &&
        data[offset + 2] == value[2] && data[offset + 3] == value[3];

    private static ushort ReadLe16(byte[] data, int offset) =>
        (ushort)(data[offset] | (data[offset + 1] << 8));

    private static uint ReadLe32(byte[] data, int offset) =>
        (uint)(data[offset] | (data[offset + 1] << 8) |
               (data[offset + 2] << 16) | (data[offset + 3] << 24));

    private static uint ReadBe32(byte[] data, int offset) =>
        (uint)((data[offset] << 24) | (data[offset + 1] << 16) |
               (data[offset + 2] << 8) | data[offset + 3]);

    private static ulong ReadBe64(byte[] data, int offset) =>
        ((ulong)ReadBe32(data, offset) << 32) | ReadBe32(data, offset + 4);
}
