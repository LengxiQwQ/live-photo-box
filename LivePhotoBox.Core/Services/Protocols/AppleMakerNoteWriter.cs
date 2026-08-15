using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

namespace LivePhotoBox.Services.Protocols
{
    // ═════════════════════════════════════════════════════════════════════════════
    // AppleMakerNoteWriter — 二进制重建 Apple MakerNote 并注入图片 EXIF。
    //
    // 背景：exiftool 只能在「已有 Apple MakerNote」的文件上写 ContentIdentifier /
    // LivePhotoVideoIndex / ImageCaptureType（实测对非 Apple 的 JPG/HEIC 均 "0 updated"，
    // 因为这些字段挂在 Apple 私有 MakerNote IFD 里，exiftool 不会凭空创建该结构）。
    // 拆分输出（来自 Google/小米/华为等单文件实况）的图片没有 Apple MakerNote，
    // 故本类按真样本反推的字节格式自建一个最小 Apple MakerNote，patch 进图片 EXIF。
    //
    // Apple MakerNote 字节格式（exiftool MakerNotes.pm: Start=valuePtr+14, Base=valuePtr）：
    //   [0..9]   "Apple iOS\0"（10 字节头）
    //   [10..11] 0x00 0x01
    //   [12..13] "MM"（大端，Apple 样本固定 MM）
    //   [14..15] IFD 条目数（uint16 BE）
    //   [16..]   IFD 条目（每条 12 字节：tag/type/count/value-or-offset）
    //   ... 4 字节 next-IFD 偏移（0）
    //   ... 数据区（ASCII 串 / int64 值），偏移相对整个 MakerNote 块起点
    //
    // 字段 tag（Apple.pm）—— 最小样本 IMG_6675.JPG 的 MakerNote 只有一条：
    //   0x0011 ContentIdentifier type=2(ASCII) 37 字节（36 字符 UUID + \0）
    // （之前多写 MakerNoteVersion / ImageCaptureType / LivePhotoVideoIndex，与最小样本
    //   不对齐；已精简为只写 ContentIdentifier，产物与最小样本逐字节一致，70 字节。）
    //
    // 注入（JPEG）：把 MakerNote 条目（tag 0x927C，位于 ExifIFD）的 count/offset 指向
    // 追加在 APP1 Exif 段末尾的新块，并增长 APP1 段长。旧 MakerNote 成为孤儿字节，无害。
    // ═════════════════════════════════════════════════════════════════════════════
    public static class AppleMakerNoteWriter
    {
        // 构造最小 Apple MakerNote 块（返回 70 字节，与最小样本 IMG_6675.JPG 一致）。
        public static byte[] BuildMakerNote(string contentId)
        {
            byte[] cidBytes = Encoding.ASCII.GetBytes(contentId + "\0"); // 37 bytes（UUID 36 + \0）

            // 头 10 + 版本 2 + "MM" 2 + 条目数 2 + 1 条 12 + next-IFD 4 = 32
            const int dataOffset = 10 + 2 + 2 + 2 + 12 + 4;
            int total = dataOffset + cidBytes.Length;      // 32 + 37 = 69
            int pad = (total % 2 == 0) ? 0 : 1;            // 对齐到偶数 = 70

            var ms = new MemoryStream(total + pad);
            ms.Write(Encoding.ASCII.GetBytes("Apple iOS\0"));
            ms.WriteByte(0x00);
            ms.WriteByte(0x01);
            ms.Write(Encoding.ASCII.GetBytes("MM"));

            WriteU16Be(ms, 1);                                                       // entry count = 1
            WriteEntry(ms, 0x0011, 2, (uint)cidBytes.Length, (uint)dataOffset);      // ContentIdentifier
            WriteU32Be(ms, 0);                                                      // next IFD = 0

            ms.Write(cidBytes);
            if (pad > 0) ms.WriteByte(0);

            return ms.ToArray();
        }

        // 将 MakerNote 块注入 JPEG 的 APP1 Exif 段。找不到 MakerNote 条目时返回 false。
        public static bool TryInjectIntoJpeg(string jpegPath, byte[] makerNote, out string? error)
        {
            error = null;
            try
            {
                byte[] data = File.ReadAllBytes(jpegPath);
                byte[]? grown = InjectIntoJpegBytes(data, makerNote, out error);
                if (grown == null)
                    return false;
                File.WriteAllBytes(jpegPath, grown);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // 在内存字节上完成注入（测试友好）。成功返回新字节数组，失败返回 null。
        internal static byte[]? InjectIntoJpegBytes(byte[] data, byte[] makerNote, out string? error)
        {
            error = null;
            int pos = 2; // after SOI
            bool foundExif = false;
            while (pos + 4 <= data.Length)
            {
                if (data[pos] != 0xFF)
                {
                    break; // 损坏或已进入熵编码，交给下方“无 Exif”分支决定
                }
                byte marker = data[pos + 1];
                if (marker == 0xD8) { pos += 2; continue; }
                if (marker >= 0xD0 && marker <= 0xD9) { pos += 2; continue; }
                if (marker == 0xDA || marker == 0xD9) break; // SOS / EOI：段扫描结束
                if (pos + 4 > data.Length) break;

                int segLen = (data[pos + 2] << 8) | data[pos + 3];
                if (segLen < 2 || pos + 2 + segLen > data.Length)
                {
                    break;
                }

                bool isExif = marker == 0xE1 && pos + 10 <= data.Length
                    && data[pos + 4] == (byte)'E' && data[pos + 5] == (byte)'x'
                    && data[pos + 6] == (byte)'i' && data[pos + 7] == (byte)'f'
                    && data[pos + 8] == 0 && data[pos + 9] == 0;

                if (isExif)
                {
                    foundExif = true;
                    int tiff = pos + 10;
                    if (tiff + 8 > data.Length)
                    {
                        error = "Truncated EXIF TIFF header";
                        return null;
                    }
                    bool bigEndian = data[tiff] == (byte)'M' && data[tiff + 1] == (byte)'M';
                    if (!bigEndian && !(data[tiff] == (byte)'I' && data[tiff + 1] == (byte)'I'))
                    {
                        error = "Unrecognized EXIF byte order";
                        return null;
                    }

                    int ifd0 = Read32(data, tiff + 4, bigEndian); // IFD0 偏移是 4 字节
                    // 在 IFD0 与 ExifIFD 里找 MakerNote 条目（tag 0x927C）。
                    int makerNoteValuePos = FindEntryValue(data, tiff, ifd0, 0x927C, bigEndian);
                    if (makerNoteValuePos < 0)
                    {
                        int exifPtrValuePos = FindEntryValue(data, tiff, ifd0, 0x8769, bigEndian);
                        if (exifPtrValuePos >= 0)
                        {
                            int exifPtr = Read32(data, exifPtrValuePos, bigEndian); // ExifIFD 偏移（值）
                            makerNoteValuePos = FindEntryValue(data, tiff, exifPtr, 0x927C, bigEndian);
                        }
                    }

                    if (makerNoteValuePos < 0)
                    {
                        error = "MakerNote entry not found";
                        return null;
                    }

                    // makerNoteValuePos 指向条目 value 字段；往回 8 字节是条目起始，
                    // count 在 +4、value/offset 在 +8。
                    int entryStart = makerNoteValuePos - 8;
                    int tiffLen = segLen - 8;
                    int pad = (tiffLen % 2 == 0) ? 0 : 1;
                    int newOffset = tiffLen + pad;

                    Write32(data, entryStart + 4, makerNote.Length, bigEndian); // count
                    Write32(data, entryStart + 8, newOffset, bigEndian);         // offset

                    // 在 APP1 Exif 段末尾插入新块（非文件末尾）。
                    int insertAt = pos + 2 + segLen;
                    byte[] insert = new byte[pad + makerNote.Length];
                    Array.Copy(makerNote, 0, insert, pad, makerNote.Length);

                    byte[] grown = new byte[data.Length + insert.Length];
                    Array.Copy(data, 0, grown, 0, insertAt);
                    Array.Copy(insert, 0, grown, insertAt, insert.Length);
                    Array.Copy(data, insertAt, grown, insertAt + insert.Length, data.Length - insertAt);

                    Write16(grown, pos + 2, segLen + insert.Length); // 更新段长
                    return grown;
                }

                pos += 2 + segLen;
            }

            // 源 JPEG 没有 Exif（或结构不含可定位的 APP1）：新建最小 APP1 Exif，
            // 内含 IFD0 → tag 0x927C(MakerNote) → 70 字节 MakerNote 块。
            if (!foundExif)
            {
                byte[] app1 = BuildFreshExifApp1(makerNote);
                byte[] grown = new byte[data.Length + app1.Length];
                Array.Copy(data, 0, grown, 0, 2);                 // SOI
                Array.Copy(app1, 0, grown, 2, app1.Length);
                Array.Copy(data, 2, grown, 2 + app1.Length, data.Length - 2);
                return grown;
            }

            error = "EXIF APP1 found but MakerNote entry not found";
            return null;
        }

        /// <summary>构造最小 APP1 Exif 段（含 FFE1 标记）：Exif\0\0 + TIFF(IFD0 → 0x927C → MakerNote)。</summary>
        private static byte[] BuildFreshExifApp1(byte[] makerNote)
        {
            const int tiffHeaderLen = 8;
            const int ifd0Len = 2 + 12 + 4;          // count + 1 entry + next-IFD
            int makerOffset = tiffHeaderLen + ifd0Len; // 26
            int tiffLen = makerOffset + makerNote.Length;
            int pad = tiffLen % 2;
            byte[] tiff = new byte[tiffLen + pad];
            tiff[0] = (byte)'M';
            tiff[1] = (byte)'M';
            Write16(tiff, 2, 0x002A);
            Write32(tiff, 4, 8, true);                // IFD0 偏移
            Write16(tiff, 8, 1);                      // entry count
            Write16(tiff, 10, 0x927C);                // MakerNote tag
            Write16(tiff, 12, 7);                     // type UNDEFINED
            Write32(tiff, 14, makerNote.Length, true);
            Write32(tiff, 18, makerOffset, true);
            Write32(tiff, 22, 0, true);               // next IFD
            Array.Copy(makerNote, 0, tiff, makerOffset, makerNote.Length);

            byte[] payload = new byte[6 + tiff.Length];
            payload[0] = (byte)'E'; payload[1] = (byte)'x'; payload[2] = (byte)'i';
            payload[3] = (byte)'f'; payload[4] = 0; payload[5] = 0;
            Array.Copy(tiff, 0, payload, 6, tiff.Length);

            byte[] seg = new byte[4 + payload.Length]; // FFE1 + 段长(2) + payload
            seg[0] = 0xFF;
            seg[1] = 0xE1;
            Write16(seg, 2, 2 + payload.Length);
            Array.Copy(payload, 0, seg, 4, payload.Length);
            return seg;
        }

        // 在 IFD 里找指定 tag 条目，返回其 value 字段的绝对文件偏移；找不到返回 -1。
        private static int FindEntryValue(byte[] data, int tiff, int ifdRel, ushort tag, bool bigEndian)
        {
            int p = tiff + ifdRel;
            if (p + 2 > data.Length) return -1;
            int count = Read16(data, p, bigEndian);
            for (int i = 0; i < count; i++)
            {
                int e = p + 2 + i * 12;
                if (e + 12 > data.Length) return -1;
                ushort t = Read16(data, e, bigEndian);
                if (t == tag) return e + 8; // value 字段位置
            }
            return -1;
        }

        private static void WriteEntry(Stream ms, ushort tag, ushort type, uint count, uint value)
        {
            WriteU16Be(ms, tag);
            WriteU16Be(ms, type);
            WriteU32Be(ms, count);
            WriteU32Be(ms, value);
        }

        private static void WriteU16Be(Stream ms, int v)
        {
            Span<byte> b = stackalloc byte[2];
            BinaryPrimitives.WriteUInt16BigEndian(b, (ushort)v);
            ms.Write(b);
        }

        private static void WriteU32Be(Stream ms, uint v)
        {
            Span<byte> b = stackalloc byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(b, v);
            ms.Write(b);
        }

        private static ushort Read16(byte[] d, int off, bool bigEndian)
            => bigEndian ? BinaryPrimitives.ReadUInt16BigEndian(d.AsSpan(off))
                         : BinaryPrimitives.ReadUInt16LittleEndian(d.AsSpan(off));

        private static int Read32(byte[] d, int off, bool bigEndian)
            => bigEndian ? BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(off))
                         : BinaryPrimitives.ReadInt32LittleEndian(d.AsSpan(off));

        private static void Write16(byte[] d, int off, int v)
            => BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(off), (ushort)v); // 段长字段恒为大端

        private static void Write32(byte[] d, int off, int v, bool bigEndian)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(off), (uint)v);
            else BinaryPrimitives.WriteUInt32LittleEndian(d.AsSpan(off), (uint)v);
        }
    }
}
