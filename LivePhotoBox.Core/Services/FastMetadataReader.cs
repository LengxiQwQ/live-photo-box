/*
 * FastMetadataReader.cs
 *
 * 不依赖 exiftool，从文件二进制头直接读取宽高与 EXIF 拍摄日期的快速读取器。
 *
 *   - 扫描目录时替代 exiftool 逐文件查询（数千文件 < 0.1s）
 *   - 支持 JPEG（SOF 标记 + APP1 EXIF IFD）、GIF、BMP、WebP
 *   - HEIC 等不支持的格式返回 (0,0,null)，调用方回退到 exiftool
 */

using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace LivePhotoBox.Services;

public static class FastMetadataReader
{
    // ═══════════════════════════════════════════════════
    //  公开入口 — 按扩展名分发
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// 从文件头读取 (宽, 高, EXIF日期)。
    /// 宽或高为 0 表示该格式不支持或读取失败；
    /// 日期为 null 表示没有 EXIF 日期或读取失败。
    /// </summary>
    public static (int Width, int Height, string? DateTaken) Read(string filePath)
    {
        var ext = Path.GetExtension(filePath).ToLowerInvariant();
        try
        {
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            return ext switch
            {
                ".jpg" or ".jpeg" => ReadJpeg(fs),
                ".gif"            => ReadGif(fs),
                ".bmp"            => ReadBmp(fs),
                ".webp"           => ReadWebP(fs),
                _                 => (0, 0, null) // HEIC 等回退 exiftool
            };
        }
        catch
        {
            return (0, 0, null);
        }
    }

    // ═══════════════════════════════════════════════════
    //  JPEG: SOF 标记 → 宽高 / APP1 → EXIF DateTimeOriginal
    // ═══════════════════════════════════════════════════

    private static (int Width, int Height, string? DateTaken) ReadJpeg(FileStream fs)
    {
        // 读前 64KB — 足够包含所有 JPEG 头部标记
        int len = (int)Math.Min(fs.Length, 65536);
        byte[] buf = new byte[len];
        fs.ReadExactly(buf, 0, len);

        // 必须从 SOI (FF D8) 开始
        if (buf.Length < 4 || buf[0] != 0xFF || buf[1] != 0xD8)
            return (0, 0, null);

        int w = 0, h = 0;
        string? date = null;
        int pos = 2; // 跳过 SOI

        while (pos < len - 1)
        {
            // 各标记段之间可能有填充字节（非 0xFF），跳过
            if (buf[pos] != 0xFF)
            {
                pos++;
                continue;
            }
            // 跳过重复的 0xFF 填充（FF FF FF ... 直到非 FF 的标记类型字节）
            byte marker = buf[pos + 1];
            if (marker == 0xFF) { pos++; continue; }
            pos += 2;

            // EOI (FF D9) 或 SOS (FF DA→图像数据开始) 之后不再有元数据
            if (marker is 0xD9 or 0xDA) break;

            if (pos + 2 > len) break;
            int segLen = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(pos));
            // 损坏文件保护：segLen 至少为 2（包含自身长度字段）
            if (segLen < 2) break;
            int dataStart = pos + 2;
            int dataLen = segLen - 2;
            pos += segLen; // 移到下一个标记位置

            if (dataStart + dataLen > len) break;

            if (w == 0 && marker is 0xC0 or 0xC1 or 0xC2) // SOF0/SOF1/SOF2
            {
                if (dataLen >= 7)
                {
                    // +0: precision (1B), +1: height (2B BE), +3: width (2B BE)
                    h = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(dataStart + 1));
                    w = BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(dataStart + 3));
                }
            }
            else if (date == null && marker == 0xE1) // APP1 — 通常包含 EXIF
            {
                if (dataLen >= 6 &&
                    buf[dataStart] == 'E' && buf[dataStart + 1] == 'x' &&
                    buf[dataStart + 2] == 'i' && buf[dataStart + 3] == 'f' &&
                    buf[dataStart + 4] == 0 && buf[dataStart + 5] == 0)
                {
                    // TIFF 数据从 "Exif\0\0" 之后开始
                    date = ParseExifDateTimeOriginal(buf, dataStart + 6, dataLen - 6);
                }
            }

            // 两个都拿到了就提前退出
            if (w > 0 && date != null) break;
        }

        return (w, h, date);
    }

    // ═══════════════════════════════════════════════════
    //  TIFF / EXIF 解析（JPEG APP1 内部）
    // ═══════════════════════════════════════════════════

    /// <summary>
    /// 从 TIFF 字节数据中提取 DateTimeOriginal (tag 0x9003)。
    /// DateTimeOriginal 在 ExifIFD 子 IFD 中：IFD0 → tag 0x8769 → ExifIFD → tag 0x9003。
    /// </summary>
    private static string? ParseExifDateTimeOriginal(byte[] buf, int tiffStart, int tiffLen)
    {
        try
        {
            int end = tiffStart + tiffLen;
            if (tiffStart + 8 > end) return null;

            // 字节序：'II' = little-endian, 'MM' = big-endian
            bool isLE = buf[tiffStart] == 'I' && buf[tiffStart + 1] == 'I';
            bool isBE = buf[tiffStart] == 'M' && buf[tiffStart + 1] == 'M';
            if (!isLE && !isBE) return null;

            // TIFF magic: 0x002A
            ushort magic = ReadU16(buf, tiffStart + 2, isBE);
            if (magic != 0x002A) return null;

            // 第一个 IFD 偏移
            uint ifd0Offset = ReadU32(buf, tiffStart + 4, isBE);
            uint exifIfdOffset = 0;

            // ── 遍历 IFD0：找 tag 0x8769 (ExifIFD 指针) ──
            // ifd0Offset 是相对于 TIFF 头的偏移，WalkIfd 需要绝对位置
            WalkIfd(buf, tiffStart, tiffStart + (int)ifd0Offset, isBE, end,
                (tag, valueOffset) =>
                {
                    if (tag == 0x8769) // ExifIFD 指针（LONG 类型，值 ≤ 4 字节，valueOffset 即为 ExifIFD 偏移）
                        exifIfdOffset = valueOffset;
                });

            if (exifIfdOffset == 0) return null;

            // ── 遍历 ExifIFD：找 tag 0x9003 (DateTimeOriginal) ──
            string? dateStr = null;
            WalkIfd(buf, tiffStart, tiffStart + (int)exifIfdOffset, isBE, end,
                (tag, valueOffset) =>
                {
                    if (tag == 0x9003) // DateTimeOriginal
                    {
                        dateStr = ReadAsciiString(buf, tiffStart + (int)valueOffset, end);
                    }
                });

            return dateStr;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 遍历一个 TIFF IFD，对每个条目调用 action(tag, valueOrOffset)。
    /// valueOrOffset: 如果值 ≤ 4 字节则在条目中直接存储，否则存储的是文件偏移。
    /// </summary>
    private static void WalkIfd(byte[] buf, int tiffBase, int ifdOffset,
        bool isBE, int end, Action<ushort, uint> action)
    {
        if (ifdOffset < 0 || ifdOffset + 2 > end) return;
        ushort entryCount = ReadU16(buf, ifdOffset, isBE);
        int entryStart = ifdOffset + 2;

        for (int i = 0; i < entryCount; i++)
        {
            int entryPos = entryStart + i * 12;
            if (entryPos + 12 > end) break;

            ushort tag = ReadU16(buf, entryPos, isBE);
            ushort type = ReadU16(buf, entryPos + 2, isBE);
            uint count = ReadU32(buf, entryPos + 4, isBE);
            // 如果值 ≤ 4 字节，直接存储在 entryPos+8；否则存储 4 字节偏移
            uint valueOrOffset = ReadU32(buf, entryPos + 8, isBE);

            uint typeSize = type switch
            {
                1 or 6 or 2 => 1,  // BYTE, SBYTE, ASCII
                3 => 2,             // SHORT
                4 or 9 => 4,        // LONG, SLONG
                5 or 10 => 8,       // RATIONAL, SRATIONAL
                _ => 1
            };
            uint totalSize = count * typeSize;

            if (totalSize <= 4)
                action(tag, valueOrOffset); // 值就在这 4 字节里... 但对 tag 0x8769 和 0x9003，
                                            // valueOrOffset 总是偏移（因为这俩是 LONG 和 ASCII）
            else
                action(tag, valueOrOffset); // 存的是偏移
        }
    }

    // ── TIFF 字节序辅助 ──

    private static ushort ReadU16(byte[] buf, int offset, bool isBE) =>
        isBE ? BinaryPrimitives.ReadUInt16BigEndian(buf.AsSpan(offset))
             : BinaryPrimitives.ReadUInt16LittleEndian(buf.AsSpan(offset));

    private static uint ReadU32(byte[] buf, int offset, bool isBE) =>
        isBE ? BinaryPrimitives.ReadUInt32BigEndian(buf.AsSpan(offset))
             : BinaryPrimitives.ReadUInt32LittleEndian(buf.AsSpan(offset));

    private static string? ReadAsciiString(byte[] buf, int offset, int end)
    {
        if (offset >= end) return null;
        int len = 0;
        while (offset + len < end && buf[offset + len] != 0) len++;
        return len > 0 ? Encoding.ASCII.GetString(buf, offset, len) : null;
    }

    // ═══════════════════════════════════════════════════
    //  GIF: Logical Screen Descriptor
    // ═══════════════════════════════════════════════════

    private static (int Width, int Height, string? DateTaken) ReadGif(FileStream fs)
    {
        // GIF header: 6 bytes signature + 7 bytes logical screen descriptor
        // offset 6: width (2B LE), offset 8: height (2B LE)
        Span<byte> header = stackalloc byte[10];
        fs.ReadExactly(header);

        if (header[0] != 'G' || header[1] != 'I' || header[2] != 'F')
            return (0, 0, null);

        int w = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(6));
        int h = BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(8));
        return (w, h, null);
    }

    // ═══════════════════════════════════════════════════
    //  BMP: DIB header
    // ═══════════════════════════════════════════════════

    private static (int Width, int Height, string? DateTaken) ReadBmp(FileStream fs)
    {
        // BMP header: 14 bytes file header + DIB header
        // offset 18: width (4B LE), offset 22: height (4B LE)
        Span<byte> header = stackalloc byte[26];
        fs.ReadExactly(header);

        if (header[0] != 'B' || header[1] != 'M')
            return (0, 0, null);

        int w = BinaryPrimitives.ReadInt32LittleEndian(header.Slice(18));
        int h = Math.Abs(BinaryPrimitives.ReadInt32LittleEndian(header.Slice(22))); // 负值 = top-down
        return (w, h, null);
    }

    // ═══════════════════════════════════════════════════
    //  WebP: VP8 / VP8L / VP8X chunk
    // ═══════════════════════════════════════════════════

    private static (int Width, int Height, string? DateTaken) ReadWebP(FileStream fs)
    {
        // RIFF header: 12 bytes + chunk header: 4 type + 4 size = 8 → data at offset 20
        Span<byte> header = stackalloc byte[30];
        int read = fs.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);

        if (read < 30) return (0, 0, null);
        if (header[0] != 'R' || header[1] != 'I' || header[2] != 'F' || header[3] != 'F')
            return (0, 0, null);
        if (header[8] != 'W' || header[9] != 'E' || header[10] != 'B' || header[11] != 'P')
            return (0, 0, null);

        return header[12] switch
        {
            (byte)'V' when header[13] == 'P' && header[14] == '8' && header[15] == ' ' =>
                // Lossy VP8: width/height 在 offset 26-29（14-bit LE，取低 14 位）
                (BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(26)) & 0x3FFF,
                 BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(28)) & 0x3FFF, null),

            (byte)'V' when header[13] == 'P' && header[14] == '8' && header[15] == 'L' =>
                // Lossless VP8L: 1B signature + 4B bitfield
                (1 + (BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(21)) & 0x3FFF),
                 1 + ((BinaryPrimitives.ReadUInt16LittleEndian(header.Slice(23)) >> 2) & 0x3FFF), null),

            (byte)'V' when header[13] == 'P' && header[14] == '8' && header[15] == 'X' =>
                // Extended VP8X: width/height + 1, 3 bytes each, at offset 24 and 27
                (ReadU24LE(header, 24) + 1,
                 ReadU24LE(header, 27) + 1, null),

            _ => (0, 0, null)
        };
    }

    private static int ReadU24LE(ReadOnlySpan<byte> buf, int offset)
        => buf[offset] | (buf[offset + 1] << 8) | (buf[offset + 2] << 16);
}
