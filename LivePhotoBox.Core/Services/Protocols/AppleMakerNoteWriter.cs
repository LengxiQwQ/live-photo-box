using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;

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
            contentId = null;
            error = null;
            try
            {
                byte[] data = File.ReadAllBytes(imagePath);
                // 扫描文件内全部 Apple MakerNote 签名：JPEG 注入路径会把新 MN 追加在旧 MN
                // （CID 已被剥离的空壳）之后，只读第一个会拿到空壳导致 CID 误判为缺失。
                // 取第一个带 0x0011 ContentIdentifier 条目的 MN。
                int searchFrom = 0;
                bool foundAny = false;
                while (true)
                {
                    int mnStart = FindAppleMakerNote(data, searchFrom);
                    if (mnStart < 0)
                    {
                        break;
                    }
                    foundAny = true;

                    if (mnStart + 16 > data.Length)
                    {
                        searchFrom = mnStart + 1;
                        continue;
                    }

                    // 布局：magic(12) + "MM"(2) + 条目数(2) + 条目(12×n) + next-IFD(4)，
                    // 值与偏移均按大端；值偏移相对 MakerNote 起点。
                    int count = Read16(data, mnStart + 14, bigEndian: true);
                    if (count > 4096)
                    {
                        searchFrom = mnStart + 1;
                        continue;
                    }
                    int p = mnStart + 16;
                    for (int i = 0; i < count && p + 12 <= data.Length; i++)
                    {
                        ushort tag = Read16(data, p, bigEndian: true);
                        ushort type = Read16(data, p + 2, bigEndian: true);
                        uint fcnt = (uint)Read32(data, p + 4, bigEndian: true);
                        if (tag == 0x0011 && type == 2 && fcnt > 0)
                        {
                            long valueOffset = Read32(data, p + 8, bigEndian: true);
                            int start = checked((int)(mnStart + valueOffset));
                            int len = checked((int)fcnt - 1);
                            if (start >= 0 && len > 0 && start + len <= data.Length)
                            {
                                contentId = Encoding.ASCII.GetString(data, start, len);
                                return true;
                            }
                        }
                        p += 12;
                    }
                    searchFrom = mnStart + 1;
                }

                error = foundAny
                    ? "ContentIdentifier (0x0011) entry not found in any Apple MakerNote."
                    : "No Apple MakerNote found.";
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // 将 MakerNote 块注入 JPEG 的 APP1 Exif 段。
        // 已有 0x927C 条目 → 重指向追加在段尾的新块；有 EXIF 但无条目 → 增长 ExifIFD
        // 新增条目（修正后续偏移）；无 EXIF → 新建最小 APP1 Exif。
        // 仅完全无法处理的结构返回 false。
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
                if (!Interop.NativeAppleMakerNoteWriter.TryStripLivePhotoEntries(data, out error))
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
        /// 若文件内已存在 Apple MakerNote（拆分链路中的 HEIC/JPEG 单文件保留了源苹果的
        /// 58 条目相机 MN，CID 值被清空但条目仍在），就地将其重建为 70 字节最小 MakerNote
        /// （仅 0x0011 ContentIdentifier，与 BuildMakerNote 产物一致）。
        /// MakerNote 起点不变、文件总长不变 → 不破坏 EXIF/ISOBMFF 任何偏移，无需容器手术。
        /// 旧 MN 的剩余字节不再被引用，作为无害垃圾保留。
        /// 未找到 Apple MakerNote 时返回 false（调用方可用 TryInjectIntoJpeg 兜底 JPEG）。
        /// </summary>
        public static bool TryWriteContentIdentifier(string imagePath, string contentId, out string? error)
        {
            error = null;
            try
            {
                byte[] data = File.ReadAllBytes(imagePath);
                if (!Interop.NativeAppleMakerNoteWriter.TryWriteContentIdentifier(data, contentId, out error))
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

        // 在 HEIC/HEIF 容器的 Exif item 中原位写入 Apple ContentIdentifier。
        // 不重编码像素：直接按 iloc 定位 Exif item，只重建其内部 TIFF 的 MakerNote，
        // 且要求新 TIFF 长度 ≤ 原 extent 长度，文件总长度与所有盒子偏移保持不变。
        // 因此 10-bit 子图、增益图（hdrgainmap）、辅助图、厂商私有数据全部原样保留。
        // 失败（未知结构 / 容量不足等）返回 false，交由上层回退 HDR 重编码。
        public static bool TryInjectAppleMakerNoteIntoHeic(string heicPath, string contentId, out string? error)
        {
            return TryInjectMakerNoteIntoHeic(heicPath, BuildMakerNote(contentId), out error);
        }

        public static bool TryInjectMakerNoteIntoHeic(string heicPath, byte[] makerNote, out string? error)
        {
            error = null;
            try
            {
                byte[] data = File.ReadAllBytes(heicPath);
                if (LivePhotoBox.Interop.NativeAppleMakerNoteWriter.TryInjectMakerNoteIntoHeic(data, makerNote, out byte[]? output, out error))
                {
                    if (output != null)
                    {
                        File.WriteAllBytes(heicPath, output);
                        return true;
                    }
                }
                return false;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // 在整文件中定位 Apple MakerNote：签名 "Apple iOS\0" + 0x00 0x01 + "MM"。
        private static int FindAppleMakerNote(byte[] data)
        {
            return FindAppleMakerNote(data, 0);
        }

        private static int FindAppleMakerNote(byte[] data, int start)
        {
            byte[] sig = "Apple iOS\0"u8.ToArray();
            for (int i = start; i + sig.Length + 4 <= data.Length; i++)
            {
                if (data[i] == (byte)'A' && data[i + 1] == (byte)'p')
                {
                    bool match = true;
                    for (int j = 0; j < sig.Length; j++)
                    {
                        if (data[i + j] != sig[j]) { match = false; break; }
                    }
                    if (match && data[i + sig.Length] == 0x00 && data[i + sig.Length + 1] == 0x01
                        && data[i + sig.Length + 2] == (byte)'M' && data[i + sig.Length + 3] == (byte)'M')
                    {
                        return i;
                    }
                    i += sig.Length - 1; // 跳过已检查前缀，加速扫描
                }
            }
            return -1;
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

            byte[]? cleaned = RemoveMakerNotesFromTiff(tiff, ifd0, bigEndian, out error);
            if (cleaned == null)
            {
                return null;
            }

            // 删除条目后重新读取 ExifIFD 指针（偏移可能因前移而变化）。
            int exifPtr = -1;
            int exifPtrValuePos = FindEntryValue(cleaned, 0, ifd0, 0x8769, bigEndian);
            if (exifPtrValuePos >= 0)
            {
                exifPtr = Read32(cleaned, exifPtrValuePos, bigEndian);
            }

            return InsertMakerNoteEntryIntoTiff(cleaned, ifd0, exifPtr, makerNote, bigEndian, out error);
        }

        // 在独立 TIFF 上删除所有 0x927C MakerNote 条目，并回收其 out-of-line 数据区。
        // 华为等机型的厂商 MakerNote 常达数百字节，仅删条目不够，原位注入 HEIC 时
        // 必须连同数据区一起回收，才能让新 Apple MakerNote 在 Exif item 内放下。
        private static byte[]? RemoveMakerNotesFromTiff(byte[] tiff, int ifd0, bool bigEndian, out string? error)
        {
            error = null;
            var entryStarts = new System.Collections.Generic.List<int>();
            var dataRanges = new System.Collections.Generic.List<(int Start, int Length)>();
            var countPositions = new System.Collections.Generic.List<int>();
            var fixups = new System.Collections.Generic.List<(int Pos, int Value)>();
            var visited = new System.Collections.Generic.HashSet<int>();

            CollectMakerNoteCleanup(
                tiff, 0, ifd0, bigEndian,
                entryStarts, countPositions, fixups, visited, dataRanges);

            if (entryStarts.Count == 0)
            {
                return tiff; // 无 0x927C，原样返回
            }

            // 条目（各 12 字节）+ 数据区（变长）合并为删除区间。
            var intervals = new System.Collections.Generic.List<(int Start, int Length)>();
            foreach (int s in entryStarts)
            {
                intervals.Add((s, 12));
            }
            foreach (var range in dataRanges)
            {
                if (range.Length > 0) intervals.Add(range);
            }

            intervals.Sort((a, b) => a.Start.CompareTo(b.Start));
            var merged = new System.Collections.Generic.List<(int Start, int Length)>();
            foreach (var (s, l) in intervals)
            {
                if (merged.Count == 0 || s > merged[^1].Start + merged[^1].Length)
                {
                    merged.Add((s, l));
                }
                else
                {
                    var last = merged[^1];
                    merged[^1] = (last.Start, Math.Max(last.Length, (s + l) - last.Start));
                }
            }

            int totalRemoved = 0;
            foreach (var m in merged) totalRemoved += m.Length;
            byte[] cleaned = new byte[tiff.Length - totalRemoved];
            int src = 0, dst = 0;
            foreach (var (s, l) in merged)
            {
                int len = s - src;
                if (len > 0) { Array.Copy(tiff, src, cleaned, dst, len); dst += len; }
                src = s + l;
            }
            if (src < tiff.Length)
            {
                Array.Copy(tiff, src, cleaned, dst, tiff.Length - src);
            }

            int MapAbs(int absPos)
            {
                int shift = 0;
                foreach (var (s, l) in merged)
                {
                    if (s < absPos) shift += l; else break;
                }
                return absPos - shift;
            }

            // 修正指向删除点之后的所有偏移（out-of-line 数据 / next-IFD / 嵌套 IFD 指针）。
            foreach (var (pos, val) in fixups)
            {
                int newVal = val;
                foreach (var (s, l) in merged)
                {
                    if (s < val) newVal -= l; else break;
                }
                Write32(cleaned, MapAbs(pos), newVal, bigEndian);
            }

            // 各受影响 IFD 的条目数 -1。
            foreach (int cp in countPositions)
            {
                int p = MapAbs(cp);
                int cnt = Read16(cleaned, p, bigEndian);
                if (cnt > 0)
                {
                    WriteU16(cleaned, p, cnt - 1, bigEndian);
                }
            }

            return cleaned;
        }

        // TIFF-only 版的 InsertMakerNoteEntry（无 JPEG APP1 外壳）。
        private static byte[]? InsertMakerNoteEntryIntoTiff(
            byte[] tiff, int ifd0, int exifPtr, byte[] makerNote, bool bigEndian, out string? error)
        {
            error = null;
            int targetIfd = exifPtr > 0 ? exifPtr : ifd0; // 优先 ExifIFD，缺省用 IFD0
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

        // 剥离 0x0011/0x0017/0x0025 条目：数据区清零 + 条目区压缩（保持 MN 总长不变）。
        // 无目标条目时返回原数组引用（不写盘）。
        private static byte[] StripAppleLiveEntriesFromMakerNote(byte[] data, int mnStart)
        {
            if (mnStart + 16 > data.Length) return data;
            int entryCount = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(mnStart + 14));
            if (entryCount == 0 || entryCount > 64) return data; // 防异常结构

            int entriesStart = mnStart + 16;
            int entriesLen = entryCount * 12;
            if (entriesStart + entriesLen + 4 > data.Length) return data;

            var keep = new System.Collections.Generic.List<int>(); // 保留条目下标
            for (int i = 0; i < entryCount; i++)
            {
                int e = entriesStart + i * 12;
                ushort tag = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(e));
                bool isLiveEntry = tag is 0x0011 or 0x0017 or 0x0025 or 0x002b;
                if (!isLiveEntry)
                {
                    keep.Add(i);
                    continue;
                }

                // 清零被删条目的数据区（仅当数据区不在条目区内）。
                ushort type = BinaryPrimitives.ReadUInt16BigEndian(data.AsSpan(e + 2));
                uint count = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(e + 4));
                uint offset = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(e + 8));
                int dataLen = TypeToDataLength(type, count);
                // 条目里的 offset 相对 MakerNote 块起点（非文件起点）——必须加 mnStart。
                // 且数据区必须位于条目区+next-IFD 之后，防止误清内联值/条目区。
                long absData = (long)mnStart + offset;
                if (dataLen > 0
                    && offset >= (uint)(entriesStart - mnStart + entriesLen + 4)
                    && absData + dataLen <= data.Length)
                {
                    Array.Clear(data, (int)absData, dataLen);
                }
            }

            if (keep.Count == entryCount) return data; // 没有目标条目

            // 压缩条目区：剩余条目前移，空位填 0。
            for (int k = 0; k < keep.Count; k++)
            {
                int src = entriesStart + keep[k] * 12;
                int dst = entriesStart + k * 12;
                if (src != dst)
                {
                    Array.Copy(data, src, data, dst, 12);
                }
            }
            int newCount = keep.Count;
            int newEntriesLen = newCount * 12;
            int tail = entriesStart + entriesLen;
            for (int i = entriesStart + newEntriesLen; i < tail; i++)
            {
                data[i] = 0;
            }
            BinaryPrimitives.WriteUInt16BigEndian(data.AsSpan(mnStart + 14), (ushort)newCount);
            return data;
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

        // 在内存字节上完成注入（测试友好）。成功返回新字节数组，失败返回 null。
        internal static byte[]? InjectIntoJpegBytes(byte[] data, byte[] makerNote, out string? error)
        {
            if (LivePhotoBox.Interop.NativeAppleMakerNoteWriter.TryInjectMakerNoteIntoJpeg(data, makerNote, out byte[]? output, out error))
            {
                return output;
            }
            return null;
        }

        // 删除所有 0x927C MakerNote 条目，返回新的字节数组；removedCount 为删除条数。
        // 删除会使后续字节前移 12 字节/条，因此同步修正各 IFD 的 next-IFD 指针与
        // out-of-line 数据偏移。未找到目标条目时返回原数组引用。
        private static byte[]? RemoveAllMakerNoteEntries(
            byte[] data, int tiff, int ifd0, bool bigEndian,
            out int removedCount, out string? error)
        {
            removedCount = 0;
            error = null;
            var removalStarts = new System.Collections.Generic.List<int>();
            var countPositions = new System.Collections.Generic.List<int>();
            var fixups = new System.Collections.Generic.List<(int Pos, int Value)>();
            var visited = new System.Collections.Generic.HashSet<int>();

            CollectMakerNoteCleanup(
                data, tiff, ifd0, bigEndian,
                removalStarts, countPositions, fixups, visited);

            if (removalStarts.Count == 0)
            {
                return data;
            }

            removalStarts.Sort();
            int[] removalRel = new int[removalStarts.Count];
            for (int i = 0; i < removalStarts.Count; i++)
            {
                removalRel[i] = removalStarts[i] - tiff;
            }

            // 重建去掉 12 字节条目区间的数组。
            byte[] cleaned = new byte[data.Length - 12 * removalStarts.Count];
            int src = 0, dst = 0;
            foreach (int r in removalStarts)
            {
                int len = r - src;
                if (len > 0)
                {
                    Array.Copy(data, src, cleaned, dst, len);
                    dst += len;
                }
                src = r + 12;
            }
            if (src < data.Length)
            {
                Array.Copy(data, src, cleaned, dst, data.Length - src);
            }

            // 绝对位置 → 删除后的新位置。
            int MapAbs(int absPos)
            {
                int shift = 0;
                foreach (int r in removalStarts)
                {
                    if (r < absPos) shift += 12;
                    else break;
                }
                return absPos - shift;
            }

            // 修正 out-of-line 偏移与 IFD 指针：指向删除点之后的值 -12/条。
            foreach (var (pos, val) in fixups)
            {
                int newVal = val;
                foreach (int r in removalRel)
                {
                    if (r < val) newVal -= 12;
                    else break;
                }
                Write32(cleaned, MapAbs(pos), newVal, bigEndian);
            }

            // 删除条目所在 IFD 的条目数 -1。
            foreach (int cp in countPositions)
            {
                int p = MapAbs(cp);
                int cnt = Read16(cleaned, p, bigEndian);
                if (cnt > 0)
                {
                    WriteU16(cleaned, p, cnt - 1, bigEndian);
                }
            }

            removedCount = removalStarts.Count;
            return cleaned;
        }

        // 递归收集 0x927C 条目位置、其 IFD count 位置，以及所有需要修正的
        // out-of-line 数据偏移 / next-IFD / ExifIFD/GPS/Interop/SubIFD 指针。
        private static void CollectMakerNoteCleanup(
            byte[] data, int tiff, int ifdRel, bool bigEndian,
            System.Collections.Generic.List<int> removalStarts,
            System.Collections.Generic.List<int> countPositions,
            System.Collections.Generic.List<(int Pos, int Value)> fixups,
            System.Collections.Generic.HashSet<int> visited,
            System.Collections.Generic.List<(int Start, int Length)>? dataRanges = null)
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
                fixups.Add((nextIfdPos, nextVal));
                if (nextVal > 0)
                {
                    CollectMakerNoteCleanup(
                        data, tiff, nextVal, bigEndian,
                        removalStarts, countPositions, fixups, visited, dataRanges);
                }
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

                if (tag == 0x927C)
                {
                    removalStarts.Add(e);
                    countPositions.Add(p);
                    if (dataRanges != null)
                    {
                        int mnDataLen = cnt < 0 ? 0 : TypeToDataLength(type, (uint)cnt);
                        if (mnDataLen > 4 && off >= 0 && (long)tiff + off + mnDataLen <= data.Length)
                        {
                            dataRanges.Add((tiff + off, mnDataLen));
                        }
                    }
                    continue; // 该条目的 value 不参与偏移修正（条目本身被删除）
                }

                if (tag is 0x8769 or 0x8825 or 0xA005 or 0x014A)
                {
                    fixups.Add((valuePos, off));
                    if (off > 0 && (tag != 0x014A || cnt == 1))
                    {
                        CollectMakerNoteCleanup(
                            data, tiff, off, bigEndian,
                            removalStarts, countPositions, fixups, visited, dataRanges);
                    }
                    continue;
                }

                int dataLen = cnt < 0 ? 0 : TypeToDataLength(type, (uint)cnt);
                if (dataLen > 4)
                {
                    fixups.Add((valuePos, off));
                }
            }
        }

        // 在已有 EXIF 中新增 0x927C MakerNote 条目（增长目标 IFD + 修正偏移 + 段尾追加数据）。
        // 返回新字节数组；失败返回 null 并设置 error。
        private static byte[]? InsertMakerNoteEntry(
            byte[] data, int tiff, int segLen, int ifd0, int exifPtr, byte[] makerNote,
            bool bigEndian, int segmentStart, out string? error)
        {
            error = null;
            int targetIfd = exifPtr > 0 ? exifPtr : ifd0; // 优先 ExifIFD（标准位置），缺失用 IFD0
            int p = tiff + targetIfd;
            if (p + 2 > data.Length)
            {
                error = "IFD out of range";
                return null;
            }
            int entryCount = Read16(data, p, bigEndian);
            if (entryCount <= 0 || entryCount > 256)
            {
                error = "Invalid IFD entry count";
                return null;
            }

            int insertAtRel = targetIfd + 2 + entryCount * 12; // 目标 IFD 的 next-IFD 字段位置
            int tiffLen = segLen - 8;                          // 当前 TIFF 长度
            if (insertAtRel <= 0 || insertAtRel >= tiffLen)
            {
                error = "MakerNote insertion point out of range";
                return null;
            }
            int insertAt = tiff + insertAtRel;                 // 绝对文件偏移
            int pad = (tiffLen % 2 == 0) ? 0 : 1;
            int mnOffset = tiffLen + 12 + pad;                 // 追加后的 MakerNote 偏移（相对 TIFF）

            // 1. 收集所有指向插入点之后的 TIFF 偏移（条目数据偏移 + next-IFD + 嵌套 IFD 指针）。
            var fixups = new System.Collections.Generic.List<(int Pos, int Value)>();
            var visited = new System.Collections.Generic.HashSet<int>();
            CollectIfdFixups(data, tiff, ifd0, insertAtRel, bigEndian, fixups, visited);
            if (targetIfd != ifd0)
            {
                CollectIfdFixups(data, tiff, targetIfd, insertAtRel, bigEndian, fixups, visited);
            }

            // 2. 构造新条目（12 字节）：tag 0x927C / type 7(UNDEFINED) / count / offset。
            byte[] entry = new byte[12];
            WriteU16(entry, 0, 0x927C, bigEndian);
            WriteU16(entry, 2, 7, bigEndian);
            Write32(entry, 4, makerNote.Length, bigEndian);
            Write32(entry, 8, mnOffset, bigEndian);

            // 3. 插入 12 字节条目（在 APP1 内部），随后在 APP1 段尾真正“插入”pad + MakerNote
            //    （尾部整体后移，不能覆盖后续 APP2/XMP 等段）。
            int app1End = segmentStart + 2 + segLen; // 原 APP1 段尾（绝对偏移）
            int mnInsertAt = app1End + 12;           // 段尾后移 12 后的插入点
            byte[] grown = new byte[data.Length + 12 + pad + makerNote.Length];
            Array.Copy(data, 0, grown, 0, insertAt);
            Array.Copy(entry, 0, grown, insertAt, 12);
            Array.Copy(data, insertAt, grown, insertAt + 12, app1End - insertAt);
            if (pad > 0) grown[mnInsertAt] = 0;
            Array.Copy(makerNote, 0, grown, mnInsertAt + pad, makerNote.Length);
            Array.Copy(data, app1End, grown, mnInsertAt + pad + makerNote.Length, data.Length - app1End);

            // 4. 修正偏移：插入点之后的值 +12。
            foreach (var (fixPos, fixVal) in fixups)
            {
                int newPos = fixPos < insertAt ? fixPos : fixPos + 12;
                Write32(grown, newPos, fixVal + 12, bigEndian);
            }

            // 5. 目标 IFD 条目数 +1（新条目已插入到原 next-IFD 位置）。
            WriteU16(grown, tiff + targetIfd, entryCount + 1, bigEndian);

            // 6. 更新 APP1 段长。
            Write16(grown, segmentStart + 2, segLen + 12 + pad + makerNote.Length);
            return grown;
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
