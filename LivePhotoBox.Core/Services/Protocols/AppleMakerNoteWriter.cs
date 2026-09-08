using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using LivePhotoBox.Interop;

namespace LivePhotoBox.Services.Protocols
{
    /*
     * AppleMakerNoteWriter.cs
     *
     * 二进制重建最小 Apple MakerNote 并注入图片 EXIF（exiftool 无法凭空创建该结构，
     * 只会对已存在的 MakerNote 写字段）。
     *
     *   - 按真样本字节格式自建最小 Apple MakerNote，仅含 ContentIdentifier（0x0011），
     *     与最小样本 IMG_6675.JPG 逐字节对齐（70 字节）
     *   - 注入（JPEG）：把 MakerNote 条目（tag 0x927C，位于 ExifIFD）的 count/offset
     *     指向追加在 APP1 Exif 段末尾的新块并增长 APP1 段长；旧 MakerNote 成为孤儿字节，无害
     */
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

        // 构造含 HDRHeadroom(0x21) 与 HDRGain(0x30) 的 Apple MakerNote；
        // 传入 contentId 时额外携带 ContentIdentifier(0x0011)（顺序与真样本一致：0x0011 < 0x0021 < 0x0030）。
        // HDR 条目均为 SRATIONAL（type 10，8 字节 = 分子 int32 + 分母 int32）。
        // 无 CID 布局与最小样本一致：头 10 + 版本 2 + "MM" 2 + 条目数 2 + 2×12 + next-IFD 4 = 44，
        // 数据区自偏移 44 开始，每条 8 字节，总长 60。
        // 带 CID 布局：头 10 + 版本 2 + "MM" 2 + 条目数 2 + 3×12 + next-IFD 4 = 56，
        // 数据区自偏移 56 开始：CID（37 字节，按偶数对齐）→ HDRHeadroom 8 → HDRGain 8，总长 110。
        internal static byte[] BuildHdrMakerNote(
            HdrSignedRational hdrHeadroom, HdrSignedRational hdrGain, string? contentId = null)
        {
            if (!string.IsNullOrEmpty(contentId))
            {
                byte[] cidBytes = Encoding.ASCII.GetBytes(contentId + "\0"); // 37 bytes（UUID 36 + \0）
                const int entryCount = 3;
                const int ifdBytes = 2 + entryCount * 12 + 4; // count + 3×12 + next-IFD = 42
                const int dataStart = 10 + 2 + 2 + ifdBytes;  // 56
                int cidOffset = dataStart;
                int headroomOffset = (cidOffset + cidBytes.Length + 1) & ~1; // 偶数对齐
                int gainOffset = headroomOffset + 8;
                int totalWithCid = gainOffset + 8; // 110

                var msWithCid = new MemoryStream(totalWithCid);
                msWithCid.Write(Encoding.ASCII.GetBytes("Apple iOS\0"));
                msWithCid.WriteByte(0x00);
                msWithCid.WriteByte(0x01);
                msWithCid.Write(Encoding.ASCII.GetBytes("MM"));

                WriteU16Be(msWithCid, entryCount);
                WriteEntry(msWithCid, 0x0011, 2, (uint)cidBytes.Length, (uint)cidOffset);  // ContentIdentifier
                WriteEntry(msWithCid, 0x0021, 10, 1, (uint)headroomOffset);                // HDRHeadroom
                WriteEntry(msWithCid, 0x0030, 10, 1, (uint)gainOffset);                    // HDRGain
                WriteU32Be(msWithCid, 0);                                                  // next IFD = 0

                while (msWithCid.Position < cidOffset)
                {
                    msWithCid.WriteByte(0);
                }
                msWithCid.Write(cidBytes);
                while (msWithCid.Position < headroomOffset)
                {
                    msWithCid.WriteByte(0);
                }
                WriteU32Be(msWithCid, (uint)hdrHeadroom.Numerator);
                WriteU32Be(msWithCid, (uint)hdrHeadroom.Denominator);
                WriteU32Be(msWithCid, (uint)hdrGain.Numerator);
                WriteU32Be(msWithCid, (uint)hdrGain.Denominator);
                return msWithCid.ToArray();
            }

            const int headroomDataOffset = 10 + 2 + 2 + 2 + 2 * 12 + 4; // 44
            const int gainDataOffset = headroomDataOffset + 8;           // 52
            const int total = gainDataOffset + 8;                        // 60

            var ms = new MemoryStream(total);
            ms.Write(Encoding.ASCII.GetBytes("Apple iOS\0"));
            ms.WriteByte(0x00);
            ms.WriteByte(0x01);
            ms.Write(Encoding.ASCII.GetBytes("MM"));

            WriteU16Be(ms, 2);                                                         // entry count = 2
            WriteEntry(ms, 0x0021, 10, 1, (uint)headroomDataOffset);                  // HDRHeadroom
            WriteEntry(ms, 0x0030, 10, 1, (uint)gainDataOffset);                      // HDRGain
            WriteU32Be(ms, 0);                                                        // next IFD = 0

            WriteU32Be(ms, (uint)hdrHeadroom.Numerator);
            WriteU32Be(ms, (uint)hdrHeadroom.Denominator);
            WriteU32Be(ms, (uint)hdrGain.Numerator);
            WriteU32Be(ms, (uint)hdrGain.Denominator);

            return ms.ToArray();
        }

        /// <summary>
        /// 从图片（JPEG 或 HEIC）的 Apple MakerNote 中读取 ContentIdentifier（tag 0x0011，ASCII）。
        /// 供 HDR 转换在替换 MakerNote 前保留配对 UUID，避免 HDR 条目覆盖 CID。
        /// </summary>
        public static bool TryReadContentIdentifierFromImage(string imagePath, out string? contentId, out string? error)
        {
            return NativeAppleMakerNoteWriter.TryReadContentIdentifierFromImage(
                imagePath, out contentId, out error);
        }

        // Inject only through the Native formal ExifIFD owner path.
        // Missing or non-formal ownership fails closed.
        public static bool TryInjectIntoJpeg(string jpegPath, byte[] makerNote, out string? error)
        {
            error = null;
            try
            {
                byte[] data = File.ReadAllBytes(jpegPath);
                if (!NativeAppleMakerNoteWriter.TryInjectMakerNoteIntoJpeg(
                        data, makerNote, out byte[]? rewritten, out error)
                    || rewritten is null)
                {
                    return false;
                }
                File.WriteAllBytes(jpegPath, rewritten);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 从图片文件（JPEG 或 HEIC）中字节级剥离 Apple 实况照片 MakerNote 条目：
        /// 0x0011 ContentIdentifier、0x0017 LivePhotoVideoIndex、0x0025（同类型 8 字节条目）、
        /// 0x002b PhotoIdentifier 及其数据区，保留非实况相机元数据。
        /// 保持 MakerNote 总长度不变（条目区前移、空位填 0x00、被删条目数据区清零），
        /// 因此不改变文件任何偏移——EXIF/ISOBMFF 结构完全不受影响。
        /// JPEG 与 HEIC 统一处理：直接定位 "Apple iOS\0" 签名块（MN 长度不变 → 无需解析容器）。
        /// 成功返回 true（无论是否找到目标条目）；失败返回 false 并给出 error。
        /// </summary>
        public static bool TryStripAppleLivePhotoEntries(string imagePath, out string? error)
        {
            error = null;
            try
            {
                byte[] data = File.ReadAllBytes(imagePath);
                if (!NativeAppleMakerNoteWriter.TryStripLivePhotoEntries(data, out error))
                {
                    return false;
                }
                File.WriteAllBytes(imagePath, data);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 向图片（JPEG 或 HEIC）写入 Apple ContentIdentifier（P2-6，HEIC 源苹果拆分配对）。
        /// The formally-owned MakerNote is rewritten in place while preserving its extent.
        /// MakerNote 起点不变、文件总长不变 → 不破坏 EXIF/ISOBMFF 任何偏移，无需容器手术。
        /// Missing or non-formal ownership fails closed.
        /// </summary>
        public static bool TryWriteContentIdentifier(string imagePath, string contentId, out string? error)
        {
            error = null;
            try
            {
                byte[] data = File.ReadAllBytes(imagePath);
                // Rebuild from a clone so a stale/replayed 0x0011 entry can
                // never survive beside the replacement CID. The Native writer
                // owns the ExifIFD hierarchy and drops every existing live
                // entry before adding the single replacement CID.
                byte[] rewritten = (byte[])data.Clone();
                if (!NativeAppleMakerNoteWriter.TryPrepareContentIdentifierWrite(rewritten, out error))
                {
                    return false;
                }
                if (!NativeAppleMakerNoteWriter.TryWriteContentIdentifier(rewritten, contentId, out error))
                {
                    return false;
                }
                File.WriteAllBytes(imagePath, rewritten);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // 在 HEIC/HEIF 容器的 Exif item 中原位写入 Apple ContentIdentifier。
        // 不重编码像素：直接按 iloc 定位 Exif item，只重建其内部 TIFF 的 MakerNote，
        // 且要求新 TIFF 长度 ≤ 原 extent 长度，文件总长度与所有盒子偏移保持不变。
        // 因此 10-bit 子图、增益图（hdrgainmap）、辅助图、厂商私有数据全部原样保留。
        // 失败（未知结构 / 容量不足等）返回 false，交由上层回退 HDR 重编码。
        public static bool TryInjectAppleMakerNoteIntoHeic(string heicPath, string contentId, out string? error)
        {
            return TryInjectMakerNoteIntoHeic(heicPath, BuildMakerNote(contentId), out error);
        }

        // 将任意已构造的 Apple MakerNote 块原位注入 HEIC 的 Exif item。
        // 不重编码像素：按 iloc 定位 Exif item，重建其内部 TIFF 的 MakerNote，
        // 要求新 TIFF 长度 <= 原 extent 长度，文件总长度与所有盒子偏移保持不变。
        public static bool TryInjectMakerNoteIntoHeic(string heicPath, byte[] makerNote, out string? error)
        {
            error = null;
            try
            {
                if (!HeifBoxParser.TryLocateExifItem(heicPath, out long exifOffset, out long exifLength, out string? locateError))
                {
                    error = $"Exif item locate failed: {locateError}";
                    return false;
                }

                byte[] data = File.ReadAllBytes(heicPath);
                int itemStart = checked((int)exifOffset);
                int itemLen = checked((int)exifLength);
                if (itemStart + itemLen > data.Length)
                {
                    error = "Exif extent out of range.";
                    return false;
                }
                if (!NativeAppleMakerNoteWriter.TryPrepareContentIdentifierWrite(data, out error))
                {
                    return false;
                }

                // Exif item payload: [32bit exif_tiff_header_offset]["Exif\0\0"][TIFF]
                if (itemLen < 10)
                {
                    error = "Exif item too short.";
                    return false;
                }

                int tiffHeaderOffset = BinaryPrimitives.ReadInt32BigEndian(data.AsSpan(itemStart, 4));
                int tiffStart = itemStart + 4 + tiffHeaderOffset;
                if (tiffStart + 8 > itemStart + itemLen)
                {
                    error = "Exif TIFF offset out of range.";
                    return false;
                }

                bool bigEndian = data[tiffStart] == (byte)'M' && data[tiffStart + 1] == (byte)'M';
                bool littleEndian = data[tiffStart] == (byte)'I' && data[tiffStart + 1] == (byte)'I';
                if (!bigEndian && !littleEndian)
                {
                    error = "Exif TIFF byte order unknown.";
                    return false;
                }

                int tiffLen = (itemStart + itemLen) - tiffStart;
                byte[] tiff = new byte[tiffLen];
                Array.Copy(data, tiffStart, tiff, 0, tiffLen);

                byte[]? newTiff = InjectMakerNoteIntoTiff(tiff, makerNote, out string? tiffError);
                if (newTiff == null)
                {
                    error = $"TIFF MakerNote injection failed: {tiffError}";
                    return false;
                }
                if (newTiff.Length > tiffLen)
                {
                    // 容量不足：把完整 Exif item payload（[offset=6]["Exif\0\0"][TIFF]）
                    // 追加到 mdat 末尾并重指 Exif item 的 iloc。追加的必须是完整 payload
                    // （含 4 字节 offset 头），否则后续解析器会把 TIFF 头当 offset 读错。
                    // 保留源 EXIF 全部数据（含缩略图 / UserComment / 厂商私有字段），
                    // 不移动其它 item 的数据。mdat 必须是最后一个顶层 box。
                    byte[] fullPayload = new byte[4 + 6 + newTiff.Length];
                    BinaryPrimitives.WriteInt32BigEndian(fullPayload.AsSpan(0, 4), 6);
                    "Exif\0\0"u8.CopyTo(fullPayload.AsSpan(4, 6));
                    newTiff.CopyTo(fullPayload.AsSpan(10));
                    if (TryRelocateExifToMdatEnd(
                        data, itemStart, itemLen, fullPayload, out byte[] relocated, out string? relocateError))
                    {
                        File.WriteAllBytes(heicPath, relocated);
                        return true;
                    }

                    error = $"New TIFF ({newTiff.Length} bytes) exceeds Exif item capacity ({tiffLen} bytes)"
                        + $" and relocation failed: {relocateError}";
                    return false;
                }

                // 原位写回，尾部零填充；文件长度与所有盒子偏移不变。
                Array.Copy(newTiff, 0, data, tiffStart, newTiff.Length);
                for (int i = tiffStart + newTiff.Length; i < itemStart + itemLen; i++)
                {
                    data[i] = 0;
                }

                File.WriteAllBytes(heicPath, data);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // 在独立 TIFF 字节数组上：清除所有 0x927C MakerNote，再插入唯一一条 Apple MakerNote。
        private static byte[]? InjectMakerNoteIntoTiff(byte[] tiff, byte[] makerNote, out string? error)
        {
            error = null;
            if (tiff.Length < 8)
            {
                error = "TIFF too short.";
                return null;
            }

            bool bigEndian = tiff[0] == (byte)'M' && tiff[1] == (byte)'M';
            bool littleEndian = tiff[0] == (byte)'I' && tiff[1] == (byte)'I';
            if (!bigEndian && !littleEndian)
            {
                error = "TIFF byte order unknown.";
                return null;
            }

            int ifd0 = Read32(tiff, 4, bigEndian);
            if (ifd0 <= 0 || ifd0 + 2 > tiff.Length)
            {
                error = "IFD0 offset invalid.";
                return null;
            }

            int exifPtrValuePos = FindEntryValue(tiff, 0, ifd0, 0x8769, bigEndian);
            if (exifPtrValuePos < 0)
            {
                error = "MakerNote must be owned by the IFD0 ExifIFD pointer.";
                return null;
            }
            int exifPtr = Read32(tiff, exifPtrValuePos, bigEndian);
            if (exifPtr <= 0)
            {
                error = "ExifIFD pointer is invalid.";
                return null;
            }

            int makerValuePos = FindEntryValue(tiff, 0, exifPtr, 0x927C, bigEndian);
            if (makerValuePos < 0)
            {
                error = "ExifIFD has no formally-owned MakerNote.";
                return null;
            }
            int makerEntry = makerValuePos - 8;
            ushort makerType = Read16(tiff, makerEntry + 2, bigEndian);
            int makerLength = Read32(tiff, makerEntry + 4, bigEndian);
            int makerOffset = Read32(tiff, makerValuePos, bigEndian);
            if (makerType != 7 || makerLength < makerNote.Length || makerOffset < 0 ||
                makerOffset > tiff.Length || makerNote.Length > tiff.Length - makerOffset)
            {
                error = "Owned MakerNote extent cannot preserve the replacement.";
                return null;
            }

            Array.Copy(makerNote, 0, tiff, makerOffset, makerNote.Length);
            Array.Clear(tiff, makerOffset + makerNote.Length, makerLength - makerNote.Length);
            return tiff;
        }

        // TIFF-only 版的 InsertMakerNoteEntry（无 JPEG APP1 外壳）。
        private static byte[]? InsertMakerNoteEntryIntoTiff(
            byte[] tiff, int ifd0, int exifPtr, byte[] makerNote, bool bigEndian, out string? error)
        {
            error = null;
            if (exifPtr <= 0)
            {
                error = "MakerNote must be owned by a valid ExifIFD pointer.";
                return null;
            }
            int targetIfd = exifPtr;
            int p = targetIfd;
            if (p + 2 > tiff.Length)
            {
                error = "IFD out of range.";
                return null;
            }
            int entryCount = Read16(tiff, p, bigEndian);
            if (entryCount <= 0 || entryCount > 256)
            {
                error = "Invalid IFD entry count.";
                return null;
            }

            int insertAtRel = targetIfd + 2 + entryCount * 12; // 原 next-IFD 位置
            int tiffLen = tiff.Length;
            if (insertAtRel <= 0 || insertAtRel >= tiffLen)
            {
                error = "MakerNote insertion point out of range.";
                return null;
            }
            int insertAt = insertAtRel; // tiff 基址为 0

            int pad = (tiffLen % 2 == 0) ? 0 : 1;
            int mnOffset = tiffLen + 12 + pad; // 相对 TIFF 起点的偏移

            // 1. 收集指向插入点之后的所有 TIFF 偏移（条目数据偏移 + next-IFD + 嵌套 IFD 指针）。
            var fixups = new System.Collections.Generic.List<(int Pos, int Value)>();
            var visited = new System.Collections.Generic.HashSet<int>();
            CollectIfdFixups(tiff, 0, ifd0, insertAtRel, bigEndian, fixups, visited);
            if (targetIfd != ifd0)
            {
                CollectIfdFixups(tiff, 0, targetIfd, insertAtRel, bigEndian, fixups, visited);
            }

            // 2. 构造新条目：tag 0x927C / type 7(UNDEFINED) / count / offset。
            byte[] entry = new byte[12];
            WriteU16(entry, 0, 0x927C, bigEndian);
            WriteU16(entry, 2, 7, bigEndian);
            Write32(entry, 4, makerNote.Length, bigEndian);
            Write32(entry, 8, mnOffset, bigEndian);

            // 3. 增长数组：插入 12 字节条目，TIFF 尾部追加 pad + MakerNote。
            byte[] grown = new byte[tiff.Length + 12 + pad + makerNote.Length];
            Array.Copy(tiff, 0, grown, 0, insertAt);
            Array.Copy(entry, 0, grown, insertAt, 12);
            Array.Copy(tiff, insertAt, grown, insertAt + 12, tiff.Length - insertAt);
            int mnInsertAt = tiffLen + 12; // 原 TIFF 末尾
            if (pad > 0) grown[mnInsertAt] = 0;
            Array.Copy(makerNote, 0, grown, mnInsertAt + pad, makerNote.Length);

            // 4. 修正插入点之后的偏移 +12。
            foreach (var (fixPos, fixVal) in fixups)
            {
                int newPos = fixPos < insertAt ? fixPos : fixPos + 12;
                Write32(grown, newPos, fixVal + 12, bigEndian);
            }

            // 5. 目标 IFD 条目数 +1。
            WriteU16(grown, targetIfd, entryCount + 1, bigEndian);

            return grown;
        }

        // EXIF type → 数据区字节数（type 2 ASCII 含 \0；16 = int64）。内联值（≤4 字节）返回 0。
        private static int TypeToDataLength(ushort type, uint count)
        {
            int unit = type switch
            {
                1 or 2 or 7 => 1,
                3 or 8 => 2,
                4 or 9 => 4,
                5 or 10 => 8,
                6 or 11 => 4,
                12 => 8,
                13 or 14 => 4,
                16 => 8,
                _ => 0
            };
            if (unit == 0) return 0;
            long len = (long)unit * count;
            return len > 4 ? (int)len : 0; // ≤4 字节时值内联在 value 字段，无独立数据区
        }

        // 递归收集 IFD 内指向插入点之后的所有偏移（条目 out-of-line 数据偏移、
        // next-IFD 指针、ExifIFD/GPS/Interop/单值 SubIFD 指针），用于插入后统一 +12。
        private static void CollectIfdFixups(
            byte[] data, int tiff, int ifdRel, int insertAtRel, bool bigEndian,
            System.Collections.Generic.List<(int Pos, int Value)> fixups,
            System.Collections.Generic.HashSet<int> visited)
        {
            if (ifdRel <= 0 || !visited.Add(ifdRel)) return;
            int p = tiff + ifdRel;
            if (p + 2 > data.Length) return;
            int count = Read16(data, p, bigEndian);
            if (count <= 0 || count > 512) return;

            int nextIfdPos = p + 2 + count * 12;
            if (nextIfdPos + 4 <= data.Length)
            {
                int nextVal = Read32(data, nextIfdPos, bigEndian);
                if (nextVal >= insertAtRel) fixups.Add((nextIfdPos, nextVal));
                if (nextVal > 0) CollectIfdFixups(data, tiff, nextVal, insertAtRel, bigEndian, fixups, visited);
            }

            for (int i = 0; i < count; i++)
            {
                int e = p + 2 + i * 12;
                if (e + 12 > data.Length) break;
                ushort tag = Read16(data, e, bigEndian);
                ushort type = Read16(data, e + 2, bigEndian);
                int cnt = Read32(data, e + 4, bigEndian);
                int valuePos = e + 8;
                int off = Read32(data, valuePos, bigEndian);

                // 指向其他 IFD 的指针：ExifIFD / GPS / Interop / 单值 SubIFD。
                // 即使值内联（type=LONG, count=1）它也是偏移，必须修正并递归。
                if (tag is 0x8769 or 0x8825 or 0xA005 or 0x014A)
                {
                    if (off >= insertAtRel) fixups.Add((valuePos, off));
                    if (off > 0 && (tag != 0x014A || cnt == 1))
                    {
                        CollectIfdFixups(data, tiff, off, insertAtRel, bigEndian, fixups, visited);
                    }
                    continue;
                }

                int dataLen = cnt < 0 ? 0 : TypeToDataLength(type, (uint)cnt);
                if (dataLen <= 4) continue; // 普通内联值，无偏移
                if (off >= insertAtRel)
                {
                    fixups.Add((valuePos, off));
                }
            }
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

        private static void WriteU16(byte[] d, int off, int v, bool bigEndian)
        {
            if (bigEndian) BinaryPrimitives.WriteUInt16BigEndian(d.AsSpan(off), (ushort)v);
            else BinaryPrimitives.WriteUInt16LittleEndian(d.AsSpan(off), (ushort)v);
        }

        // ── Exif item 扩容（mdat 末尾追加 + 重指 iloc）──────────────────────

        // 新 TIFF 超过原 Exif item 容量时，把新 TIFF 追加到 mdat 末尾（mdat 必须是
        // 最后一个顶层 box），再把 Exif item 的 iloc base_offset/extent length 指向
        // 追加的数据。其余 item 的数据与偏移完全不动；追加发生在 mdat 内部，
        // 顶层 box 链保持合法。旧 Exif 数据留在 mdat 内成为无害孤儿字节。
        private static bool TryRelocateExifToMdatEnd(
            byte[] data,
            long exifOffset,
            long exifLength,
            byte[] newTiff,
            out byte[] patched,
            out string? error)
        {
            patched = Array.Empty<byte>();
            error = null;
            try
            {
                // 1) 找 mdat 并确认它是最后一个顶层 box。
                int p = 0;
                int mdatStart = -1;
                long mdatSize = 0;
                int mdatHeader = 8;
                while (p + 8 <= data.Length)
                {
                    long size = HeifBoxParser.Read32(data, p);
                    string type = HeifBoxParser.ReadFourCc(data, p + 4);
                    int header = 8;
                    if (size == 1)
                    {
                        if (p + 16 > data.Length)
                        {
                            error = "Top-level 64-bit box header truncated.";
                            return false;
                        }
                        size = (long)HeifBoxParser.Read64(data, p + 8);
                        header = 16;
                    }
                    else if (size == 0)
                    {
                        size = data.Length - p;
                    }

                    if (size < header || p + size > data.Length)
                    {
                        error = "Top-level box malformed.";
                        return false;
                    }

                    if (type == "mdat")
                    {
                        mdatStart = p;
                        mdatSize = size;
                        mdatHeader = header;
                        if (p + size != data.Length)
                        {
                            error = "mdat is not the last top-level box; cannot relocate Exif item.";
                            return false;
                        }
                        break;
                    }
                    p += (int)size;
                }
                if (mdatStart < 0)
                {
                    error = "No mdat box found.";
                    return false;
                }

                // 2) 定位 meta -> iloc 里 Exif item 的 base_offset / extent length 字段。
                if (!HeifBoxParser.TryFindBox(
                    data, 0, data.Length, "meta", out int metaStart, out int metaLen, out int metaBodyStart))
                {
                    error = "No meta box found.";
                    return false;
                }

                int ilocBody = -1, ilocLen = 0;
                HeifBoxParser.TryWalkBoxes(
                    data, metaBodyStart + 4, metaStart + metaLen,
                    (type, body, len) =>
                    {
                        if (type == "iloc")
                        {
                            ilocBody = body;
                            ilocLen = len;
                        }
                    });
                if (ilocBody < 0)
                {
                    error = "No iloc box found.";
                    return false;
                }

                if (!TryFindExifIlocFields(
                    data, ilocBody, ilocLen, exifOffset, exifLength,
                    out int baseFieldPos, out int lengthFieldPos,
                    out int baseSize, out int lengthSize, out error))
                {
                    return false;
                }

                if (baseSize < 4)
                {
                    error = $"iloc base_offset size ({baseSize}) too small for relocated Exif item.";
                    return false;
                }
                if (lengthSize < 4)
                {
                    error = $"iloc extent length size ({lengthSize}) too small for new Exif item.";
                    return false;
                }

                // 3) 组装：原数据 + 新 TIFF 追加在 mdat 末尾。
                long newBase = mdatStart + mdatSize; // 原 mdat 末尾 = 追加位置
                patched = new byte[data.Length + newTiff.Length];
                Array.Copy(data, 0, patched, 0, data.Length);
                Array.Copy(newTiff, 0, patched, data.Length, newTiff.Length);

                // 4) 更新 mdat 长度字段（32-bit 或 64-bit）。
                if (mdatHeader == 8)
                {
                    long oldSize = HeifBoxParser.Read32(patched, mdatStart);
                    if (oldSize == 0)
                    {
                        // 原 size=0（延伸到 EOF）：改为显式 32-bit 长度。
                        long explicitSize = data.Length - mdatStart + newTiff.Length;
                        if (explicitSize > uint.MaxValue)
                        {
                            error = "mdat too large for 32-bit size field.";
                            return false;
                        }
                        WriteU32(patched, mdatStart, (uint)explicitSize);
                    }
                    else if (oldSize == 1)
                    {
                        error = "Unexpected 32-bit mdat size field.";
                        return false;
                    }
                    else
                    {
                        WriteU32(patched, mdatStart, (uint)(oldSize + newTiff.Length));
                    }
                }
                else
                {
                    long oldSize = (long)HeifBoxParser.Read64(patched, mdatStart + 8);
                    WriteU64(patched, mdatStart + 8, (ulong)(oldSize + newTiff.Length));
                }

                // 5) 重指 Exif item。
                WriteUInt(patched, baseFieldPos, newBase, baseSize);
                WriteUInt(patched, lengthFieldPos, newTiff.Length, lengthSize);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // 把 HEIC 的 Exif item 规范化为标准布局：
        //   [32-bit exif_tiff_header_offset=6]["Exif\0\0"][TIFF]
        // heif-enc（libheif 定制版）对大端（MM）源 TIFF 会写成
        //   [32-bit offset=0][TIFF]（缺 "Exif\0\0" 前缀），部分解析器（iOS ImageIO
        // 等）按规范找不到前缀会放弃解析 EXIF，导致 MakerNote/ContentIdentifier
        // 无法读取、实况照片无法配对。此函数把非标准布局统一转为标准布局。
        // 已在标准布局时原样返回 true；需要改写时复用 mdat 末尾追加方案
        // （TryRelocateExifToMdatEnd），不移动其它 item 数据。
        public static bool TryNormalizeExifItem(string heicPath, out string? error)
        {
            error = null;
            try
            {
                if (!HeifBoxParser.TryLocateExifItem(heicPath, out long exifOffset, out long exifLength, out string? locateError))
                {
                    error = $"Exif item locate failed: {locateError}";
                    return false;
                }

                byte[] data = File.ReadAllBytes(heicPath);
                int itemStart = checked((int)exifOffset);
                int itemLen = checked((int)exifLength);
                if (itemStart + itemLen > data.Length)
                {
                    error = "Exif extent out of range.";
                    return false;
                }
                if (itemLen < 10)
                {
                    error = "Exif item too short.";
                    return false;
                }

                // 已带 "Exif\0\0" 前缀（标准）→ 无需处理。
                if (itemLen >= 10 && data.AsSpan(itemStart + 4, 6).SequenceEqual("Exif\0\0"u8))
                {
                    return true;
                }

                // 定位 TIFF 魔数（MM\0* / II*\0）。兼容三种 payload：
                //   [offset][TIFF]（heif-enc 原始输出）
                //   [offset]["Exif\0\0"][TIFF]（标准布局）
                //   [TIFF]（历史重定位产物——裸 TIFF，无 offset 头）
                // 一律从魔数起取 TIFF，重建标准布局，避免把 TIFF 头剥坏。
                int tiffStart = -1;
                for (int i = 0; i + 4 <= itemLen; i++)
                {
                    if (data[itemStart + i] == (byte)'M' && data[itemStart + i + 1] == (byte)'M'
                        && data[itemStart + i + 2] == 0 && data[itemStart + i + 3] == 0x2A)
                    {
                        tiffStart = i;
                        break;
                    }
                    if (data[itemStart + i] == (byte)'I' && data[itemStart + i + 1] == (byte)'I'
                        && data[itemStart + i + 2] == 0x2A && data[itemStart + i + 3] == 0)
                    {
                        tiffStart = i;
                        break;
                    }
                }
                if (tiffStart < 0)
                {
                    error = "Exif item contains no TIFF header.";
                    return false;
                }

                byte[] payload = data.AsSpan(itemStart + tiffStart, itemLen - tiffStart).ToArray();
                byte[] normalized = new byte[4 + 6 + payload.Length];
                BinaryPrimitives.WriteInt32BigEndian(normalized.AsSpan(0, 4), 6);
                "Exif\0\0"u8.CopyTo(normalized.AsSpan(4, 6));
                payload.CopyTo(normalized.AsSpan(10));

                if (!TryRelocateExifToMdatEnd(
                    data, itemStart, itemLen, normalized, out byte[] patched, out string? relocateError))
                {
                    error = $"Exif normalization failed: {relocateError}";
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

        // 在 iloc 里找绝对偏移与长度匹配 Exif item 的条目，返回其 base_offset
        // 字段与 extent length 字段的字节位置（供重写）。
        private static bool TryFindExifIlocFields(
            byte[] data,
            int body,
            int len,
            long wantOffset,
            long wantLength,
            out int baseFieldPos,
            out int lengthFieldPos,
            out int baseSize,
            out int lengthSize,
            out string? error)
        {
            baseFieldPos = -1;
            lengthFieldPos = -1;
            baseSize = 0;
            lengthSize = 0;
            error = null;

            int end = body + len;
            if (body + 6 > end)
            {
                error = "iloc too short.";
                return false;
            }

            int version = data[body] & 0xFF;
            int p = body + 4;
            int b0 = data[p] & 0xFF;
            int b1 = data[p + 1] & 0xFF;
            p += 2;
            int offsetSize = (b0 >> 4) & 0x0F;
            int lengthSizeX = b0 & 0x0F;
            int baseOffsetSize = (b1 >> 4) & 0x0F;
            int indexSize = b1 & 0x0F;

            int count;
            if (version < 2)
            {
                if (p + 2 > end)
                {
                    error = "iloc truncated.";
                    return false;
                }
                count = HeifBoxParser.Read16(data, p);
                p += 2;
            }
            else
            {
                if (p + 4 > end)
                {
                    error = "iloc truncated.";
                    return false;
                }
                count = checked((int)HeifBoxParser.Read32(data, p));
                p += 4;
            }

            for (int i = 0; i < count; i++)
            {
                if (version < 2)
                {
                    if (p + 2 > end)
                    {
                        error = "iloc item truncated.";
                        return false;
                    }
                    p += 2;
                }
                else
                {
                    if (p + 4 > end)
                    {
                        error = "iloc item truncated.";
                        return false;
                    }
                    p += 4;
                }

                if (version == 1 || version == 2)
                {
                    if (p + 2 > end)
                    {
                        error = "iloc item truncated.";
                        return false;
                    }
                    p += 2; // construction_method
                }

                if (p + 2 > end)
                {
                    error = "iloc item truncated.";
                    return false;
                }
                p += 2; // data_reference_index

                int baseField = p;
                long baseOffset = baseOffsetSize > 0 ? HeifBoxParser.ReadUInt(data, p, baseOffsetSize) : 0;
                p += baseOffsetSize;

                if (p + 2 > end)
                {
                    error = "iloc item truncated.";
                    return false;
                }
                int extentCount = HeifBoxParser.Read16(data, p);
                p += 2;

                for (int e = 0; e < extentCount; e++)
                {
                    if ((version == 1 || version == 2) && indexSize > 0)
                    {
                        if (p + indexSize > end)
                        {
                            error = "iloc extent truncated.";
                            return false;
                        }
                        p += indexSize;
                    }

                    long extentOffset = offsetSize > 0 ? HeifBoxParser.ReadUInt(data, p, offsetSize) : 0;
                    p += offsetSize;

                    int lengthField = p;
                    long extentLength = lengthSizeX > 0 ? HeifBoxParser.ReadUInt(data, p, lengthSizeX) : 0;
                    p += lengthSizeX;

                    if (e == 0 && baseOffset + extentOffset == wantOffset && extentLength == wantLength)
                    {
                        baseFieldPos = baseField;
                        lengthFieldPos = lengthField;
                        baseSize = baseOffsetSize;
                        lengthSize = lengthSizeX;
                        return true;
                    }
                }
            }

            error = "Exif item not found in iloc.";
            return false;
        }

        private static void WriteU64(byte[] d, int off, ulong v)
            => BinaryPrimitives.WriteUInt64BigEndian(d.AsSpan(off), v);

        private static void WriteU32(byte[] d, int off, uint v)
            => BinaryPrimitives.WriteUInt32BigEndian(d.AsSpan(off), v);

        private static void WriteUInt(byte[] d, int off, long v, int size)
        {
            for (int i = size - 1; i >= 0; i--)
            {
                d[off + i] = (byte)(v & 0xFF);
                v >>= 8;
            }
        }
    }
}
