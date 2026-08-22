using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 华为合并型 HEIC 的字节级 XMP 注入器与读取器（exiftool 无法读写此类结构时的回退）。
    /// 针对华为相机生成的标准结构（iloc 在 iinf 之前）实现，已在 20 个真机样本上验证：
    /// iinf 注册 application/rdf+xml item、iloc 登记位置、XMP 数据放 meta 末尾 uuid box，
    /// 同步修正 iloc 外部 extent 与内嵌 MP4 stco/co64 偏移。结构不认识时返回 false。
    /// </summary>
    public static class HeicXmpInjector
    {
        /// <summary>Adobe XMP 的标准 usertype（uuid box 的 16 字节标识）。</summary>
        private static readonly byte[] AdobeXmpUsertype =
        {
            0xBE, 0x7A, 0xCF, 0xCB, 0x97, 0xA9, 0x42, 0xE8,
            0x9C, 0x71, 0x99, 0x94, 0x91, 0xE3, 0xAF, 0xAC
        };

        /// <summary>
        /// 字节级读取 HEIC meta 内 uuid box 的 XMP 文本（注入器的反向操作）。
        /// 返回原始 xpacket 包装文本；文件无 Adobe XMP uuid box 或结构不认识时返回 null。
        /// 华为合并型 HEIC 的 XMP 只有字节级保证（exiftool 读不到），历史页与
        /// 历史继承读取都依赖本方法作为回退。
        /// </summary>
        public static async Task<string?> TryReadXmpTextAsync(
            string filePath, CancellationToken token)
        {
            try
            {
                byte[] bytes = await File.ReadAllBytesAsync(filePath, token);
                byte[]? payload = ExtractXmpPayload(bytes);
                if (payload == null || payload.Length == 0) return null;

                // 去掉尾部 NUL 填充（某些工具会按 4 字节对齐补齐）。
                int end = payload.Length;
                while (end > 0 && payload[end - 1] == 0) end--;
                if (end == 0) return null;

                return Encoding.UTF8.GetString(payload, 0, end);
            }
            catch (OperationCanceledException) { throw; }
            catch { return null; }
        }

        /// <summary>
        /// 在 HEIC 字节中定位 meta box，查找 usertype 为 Adobe XMP 的 uuid box，
        /// 返回其 payload（完整 xpacket 包装的 XMP）。找不到返回 null。
        /// </summary>
        private static byte[]? ExtractXmpPayload(byte[] bytes)
        {
            int p = 0;
            while (p + 8 <= bytes.Length)
            {
                var (size, next) = ReadBoxHeader(bytes, p, bytes.Length);
                string type = Encoding.ASCII.GetString(bytes, p + 4, 4);
                if (type == "meta")
                {
                    long metaEnd = size == 0 ? bytes.Length : next;
                    // meta 是 FullBox：跳过 version/flags（4 字节），子 box 从这里开始。
                    int q = p + 12;
                    while (q + 8 <= metaEnd)
                    {
                        var (boxSize, boxNext) = ReadBoxHeader(bytes, q, metaEnd);
                        string childType = Encoding.ASCII.GetString(bytes, q + 4, 4);
                        if (childType == "uuid")
                        {
                            int usertypeStart = q + 8;
                            if (usertypeStart + 16 > metaEnd) break;
                            if (bytes.AsSpan(usertypeStart, 16).SequenceEqual(AdobeXmpUsertype))
                            {
                                long payloadEnd = boxSize == 0 ? metaEnd : boxNext;
                                int payloadStart = usertypeStart + 16;
                                int payloadLen = (int)(payloadEnd - payloadStart);
                                if (payloadLen <= 0) return null;
                                var payload = new byte[payloadLen];
                                Array.Copy(bytes, payloadStart, payload, 0, payloadLen);
                                return payload;
                            }
                        }
                        if (boxSize <= 0) break;
                        q = (int)boxNext;
                    }
                    return null;
                }
                if (size <= 0) break;
                p = (int)next;
            }
            return null;
        }

        /// <summary>
        /// 读取 box 头：(size, 下一个 box 的偏移)。size==0 表示延伸到容器末尾；
        /// size==1 表示 64 位长度。超大或不认识的长度返回 (0, off) 由调用方终止。
        /// </summary>
        private static (int Size, long Next) ReadBoxHeader(byte[] a, int off, long limit)
        {
            if (off + 8 > limit) return (0, off);
            int size = ReadU32(a, off);
            long next = off + size;
            if (size == 1)
            {
                if (off + 16 > limit) return (0, off);
                long size64 = BinaryPrimitives.ReadInt64BigEndian(a.AsSpan(off + 8, 8));
                if (size64 < 16 || size64 > int.MaxValue) return (0, off);
                next = off + size64;
                return ((int)size64, next);
            }
            if (size == 0) return (0, limit); // 延伸到父容器末尾
            return (size, next);
        }

        /// <summary>
        /// 尝试向 HEIC 注入 XMP（原子替换：先写临时文件，成功后覆盖）。
        /// </summary>
        public static async Task<(bool Success, string? Error)> TryInjectXmpAsync(
            string filePath, byte[] xmpBytes, CancellationToken token)
        {
            string? error = null;
            try
            {
                byte[] bytes = File.ReadAllBytes(filePath);
                byte[]? result = BuildInjected(bytes, xmpBytes, out error);
                if (result == null) return (false, error);

                string dir = Path.GetDirectoryName(filePath) ?? AppContext.BaseDirectory;
                string temp = Path.Combine(dir, $".lpb_heic_xmp_{Guid.NewGuid():N}.heic");
                try
                {
                    File.WriteAllBytes(temp, result);
                    File.Move(temp, filePath, overwrite: true);

                    // 写后验证：exiftool 必须能读回我们的 XMP，否则回滚成未注入文件。
                    // Post-write verification: confirm the injected XMP bytes are
                    // physically present inside the uuid box. Must NOT rely on
                    // exiftool read-back: HUAWEI HEIC meta layout (iloc before iinf)
                    // makes exiftool itself fail with "Terminator found in Meta", so a
                    // correct injection would be wrongly rolled back.
                    byte[] written = File.ReadAllBytes(filePath);
                    if (!VerifyInjectedXmp(written, xmpBytes))
                    {
                        File.WriteAllBytes(filePath, bytes); // 回滚
                        error = "XMP verification failed after injection; rolled back";
                        return (false, error);
                    }
                }
                finally
                {
                    try { if (File.Exists(temp)) File.Delete(temp); } catch { }
                }
                return (true, null);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                return (false, ex.Message);
            }
        }

        /// <summary>
        /// Byte-level verification: the file must contain the written XMP uuid box
        /// (standard Adobe XMP usertype) and its payload must match xmpBytes exactly.
        /// Does not depend on exiftool parsing.
        /// </summary>
        private static bool VerifyInjectedXmp(byte[] fileBytes, byte[] xmpBytes)
        {
            int idx = IndexOfBytes(fileBytes, AdobeXmpUsertype);
            if (idx < 0) return false;
            int xmpStart = idx + AdobeXmpUsertype.Length;
            if (xmpStart + xmpBytes.Length > fileBytes.Length) return false;
            return fileBytes.AsSpan(xmpStart, xmpBytes.Length).SequenceEqual(xmpBytes);
        }

        private static int IndexOfBytes(byte[] haystack, byte[] needle)
        {
            if (needle.Length == 0 || needle.Length > haystack.Length) return -1;
            for (int i = 0; i <= haystack.Length - needle.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < needle.Length; j++)
                {
                    if (haystack[i + j] != needle[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        private static byte[]? BuildInjected(byte[] bytes, byte[] xmpBytes, out string? error)
        {
            error = null;

            int metaPos = -1, metaSize = 0;
            int p = 0;
            while (p + 8 <= bytes.Length)
            {
                int sz = ReadU32(bytes, p);
                string type = Encoding.ASCII.GetString(bytes, p + 4, 4);
                if (type == "meta") { metaPos = p; metaSize = sz; break; }
                if (sz <= 0) break;
                p += sz;
            }
            if (metaPos < 0) { error = "meta box not found"; return null; }
            int metaEnd = metaPos + metaSize;

            int ilocPos = -1, ilocVersion = -1, offSize = 0, lenSize = 0, baseSize = 0, idxSize = 0, ilocItems = 0;
            int iinfPos = -1, iinfItems = 0, iinfPayload = -1;
            p = metaPos + 12;
            while (p + 8 <= metaEnd)
            {
                int sz = ReadU32(bytes, p);
                string type = Encoding.ASCII.GetString(bytes, p + 4, 4);
                if (type == "iloc")
                {
                    ilocPos = p;
                    int pl = p + 8;
                    ilocVersion = bytes[pl];
                    byte b1 = bytes[pl + 4], b2 = bytes[pl + 5];
                    offSize = b1 >> 4; lenSize = b1 & 0x0F;
                    baseSize = b2 >> 4; idxSize = b2 & 0x0F;
                    ilocItems = ReadU16(bytes, pl + 6);
                }
                else if (type == "iinf")
                {
                    iinfPos = p;
                    iinfPayload = p + 8;
                    iinfItems = ReadU16(bytes, iinfPayload + 4);
                }
                if (sz <= 0) break;
                p += sz;
            }
            if (ilocPos < 0 || iinfPos < 0) { error = "iloc/iinf not found"; return null; }
            // 分段偏移映射与 box 顺序无关：华为结构（iloc 在 iinf 前）和
            // 标准结构（iinf 在前）都按边界排序处理，均支持。
            if (ilocVersion > 2) { error = $"unsupported iloc version {ilocVersion}"; return null; }
            if (offSize is < 1 or > 8 || lenSize is < 1 or > 8 || baseSize > 8 || idxSize > 8)
            {
                error = $"unexpected iloc field sizes (off={offSize} len={lenSize} base={baseSize} idx={idxSize})";
                return null;
            }

            int ilocEnd = ilocPos + ReadU32(bytes, ilocPos);
            int iinfEnd = iinfPos + ReadU32(bytes, iinfPos);

            // ── 查找旧 XMP（mime 条目 + 旧 uuid box），新注入前先移除，保证单 XMP ──
            // 1) iinf 中的 XMP mime item（item_type="mime" 且 content_type="application/rdf+xml"）
            int oldInfePos = -1, oldInfeLen = 0, oldXmpItemId = -1;
            int ip = iinfPayload + 4 + 2; // 跳过 version/flags + item count
            for (int i = 0; i < iinfItems; i++)
            {
                if (ip + 20 > metaEnd) break;
                int infeSize = ReadU32(bytes, ip);
                if (infeSize < 24) { ip += Math.Max(infeSize, 8); continue; }
                int infeVersion = bytes[ip + 8];
                int itemTypePos = infeVersion == 2 ? 16 : infeVersion == 3 ? 14 : -1;
                int namePos = infeVersion == 2 ? 20 : infeVersion == 3 ? 22 : -1;
                if (itemTypePos > 0 && namePos > 0 &&
                    Encoding.ASCII.GetString(bytes, ip + itemTypePos, 4) == "mime")
                {
                    int nameStart = ip + namePos;
                    // item_name 可以为空（第一个字节就是 NUL），nameEnd == nameStart 合法。
                    int nameEnd = Array.IndexOf(bytes, (byte)0, nameStart, ip + infeSize - nameStart);
                    if (nameEnd >= nameStart && nameEnd + 1 < ip + infeSize)
                    {
                        string contentType = Encoding.ASCII.GetString(
                            bytes, nameEnd + 1, ip + infeSize - (nameEnd + 1));
                        if (contentType.StartsWith("application/rdf+xml", StringComparison.OrdinalIgnoreCase))
                        {
                            oldInfePos = ip;
                            oldInfeLen = infeSize;
                            oldXmpItemId = ReadU16(bytes, ip + 12);
                        }
                    }
                }
                ip += infeSize;
            }

            // 2) iloc 中旧 XMP 条目（item_ID 匹配）
            int oldIlocEntryPos = -1, oldIlocEntryLen = 0;
            if (oldXmpItemId >= 0)
            {
                int ep = ilocPos + 16; // iloc payload 头 8 + version/flags 4 + sizes 2 + count 2
                for (int i = 0; i < ilocItems; i++)
                {
                    int entryStart = ep;
                    ep += 2; // item_ID
                    if (ilocVersion == 1 || ilocVersion == 2) ep += 2; // construction_method
                    ep += 2; // data_reference_index
                    ep += baseSize;
                    int extCount = ReadU16(bytes, ep); ep += 2;
                    ep += extCount * (idxSize + offSize + lenSize);
                    if (ReadU16(bytes, entryStart) == oldXmpItemId)
                    {
                        oldIlocEntryPos = entryStart;
                        oldIlocEntryLen = ep - entryStart;
                        break;
                    }
                }
            }

            // 3) meta 内旧的 Adobe XMP uuid box（可能多个，例如重复注入的历史产物）
            var oldUuidRanges = new List<(int Pos, int Len)>();
            {
                int uq = metaPos + 12;
                while (uq + 8 <= metaEnd)
                {
                    int ubox = ReadU32(bytes, uq);
                    if (ubox < 8) break;
                    if (bytes[uq + 4] == (byte)'u' && bytes[uq + 5] == (byte)'u' &&
                        bytes[uq + 6] == (byte)'i' && bytes[uq + 7] == (byte)'d' &&
                        uq + 24 <= metaEnd &&
                        bytes.AsSpan(uq + 8, 16).SequenceEqual(AdobeXmpUsertype))
                    {
                        oldUuidRanges.Add((uq, ubox));
                    }
                    uq += ubox;
                }
            }

            int newItemId = 27;
            ip = iinfPayload + 4 + 2;
            for (int i = 0; i < iinfItems; i++)
            {
                if (ip + 8 > metaEnd) break;
                int infeSize = ReadU32(bytes, ip);
                if (infeSize < 14) break;
                int infeId = ReadU16(bytes, ip + 12);
                if (infeId >= newItemId) newItemId = infeId + 1;
                ip += infeSize;
            }
            if (newItemId > ushort.MaxValue) { error = "no free item ID"; return null; }

            byte[] content = Encoding.UTF8.GetBytes("application/rdf+xml");
            // infe (version 2) layout: item_ID(2) + protection(2) + item_type(4)
            //   + item_name (null-terminated, may be empty) + content_type (null-terminated).
            // The HUAWEI camera always emits an item_name before the content type
            // (e.g. "DfxData" + "application/vnd.huawei"); without it libheif parses
            // the content type as the item name and leaves content_type empty, so the
            // item is not recognized as XMP and decoding fails. Match exiftool's
            // layout: empty item name, then the content type.
            int infeLen = 8 + 4 + 2 + 2 + 4 + 1 + content.Length + 1;
            byte[] infe = new byte[infeLen];
            WriteU32(infe, 0, infeLen);
            infe[4] = (byte)'i'; infe[5] = (byte)'n'; infe[6] = (byte)'f'; infe[7] = (byte)'e';
            infe[8] = 2;
            WriteU16(infe, 12, (ushort)newItemId);
            WriteU16(infe, 14, 0);
            infe[16] = (byte)'m'; infe[17] = (byte)'i'; infe[18] = (byte)'m'; infe[19] = (byte)'e';
            infe[20] = 0; // empty item_name
            Array.Copy(content, 0, infe, 21, content.Length);
            infe[21 + content.Length] = 0;

            int ilocAdd = 2 + 2 + 2 + baseSize + 2 + offSize + lenSize;
            byte[] ilocEntry = new byte[ilocAdd];
            int q = 0;
            WriteU16(ilocEntry, q, (ushort)newItemId); q += 2;
            // construction_method must be 0 (data lives in the file, extent_offset
            // is an absolute file offset). The HUAWEI camera uses 0; 1 means the
            // data lives in idat and libheif would read the XMP from there,
            // overrunning the small idat box and failing with "Unexpected end of file".
            WriteU16(ilocEntry, q, 0); q += 2;
            WriteU16(ilocEntry, q, 0); q += 2;
            WriteN(ilocEntry, q, 0, baseSize); q += baseSize;
            WriteU16(ilocEntry, q, 1); q += 2;
            int extOffPos = q; q += offSize;
            WriteN(ilocEntry, q, xmpBytes.Length, lenSize);

            int uuidBoxLen = 8 + 16 + xmpBytes.Length;
            int removeIloc = oldIlocEntryPos >= 0 ? oldIlocEntryLen : 0;
            int removeInfe = oldInfePos >= 0 ? oldInfeLen : 0;
            int removeUuid = 0;
            foreach (var (_, len) in oldUuidRanges) removeUuid += len;

            int deltaBeforeUuid = ilocAdd - removeIloc + infeLen - removeInfe - removeUuid;
            int totalDelta = deltaBeforeUuid + uuidBoxLen;

            // 分段偏移映射：每个边界位置起累计一个增量，Map(x) = x + 所有 pos<=x 的增量之和。
            // 旧 iloc 条目/旧 infe/旧 uuid 是"移除"（负增量），新 iloc 条目/新 infe 是"插入"（正增量），
            // 新 uuid 单独放在 meta 内容末尾（不参与 Map）。
            var boundaries = new List<(int Pos, int Delta)>();
            if (oldIlocEntryPos >= 0) boundaries.Add((oldIlocEntryPos, -removeIloc));
            boundaries.Add((ilocEnd, ilocAdd));
            if (oldInfePos >= 0) boundaries.Add((oldInfePos, -removeInfe));
            boundaries.Add((iinfEnd, infeLen));
            foreach (var (pos, len) in oldUuidRanges) boundaries.Add((pos, -len));
            boundaries.Sort((a, b) => a.Pos.CompareTo(b.Pos));

            int DeltaAt(int x)
            {
                int d = 0;
                foreach (var (pos, delta) in boundaries)
                {
                    if (pos <= x) d += delta; else break;
                }
                return d;
            }
            int Map(int x) => x + DeltaAt(x);

            bool InRemovedRange(int x)
            {
                if (oldIlocEntryPos >= 0 && x >= oldIlocEntryPos && x < oldIlocEntryPos + removeIloc) return true;
                if (oldInfePos >= 0 && x >= oldInfePos && x < oldInfePos + removeInfe) return true;
                foreach (var (pos, len) in oldUuidRanges)
                {
                    if (x >= pos && x < pos + len) return true;
                }
                return false;
            }

            long newLength = (long)bytes.Length + totalDelta;
            if (newLength < 0 || newLength > int.MaxValue)
            {
                error = $"output size out of range ({newLength})";
                return null;
            }
            byte[] nb = new byte[newLength];
            for (int x = 0; x < metaEnd; x++)
            {
                if (InRemovedRange(x)) continue;
                nb[Map(x)] = bytes[x];
            }
            // meta 之后的数据整体平移到 uuid box 之后。
            Array.Copy(bytes, metaEnd, nb, metaEnd + totalDelta, bytes.Length - metaEnd);

            // meta 尺寸
            WriteU32(nb, metaPos, metaSize + totalDelta);

            // iinf：计数/尺寸更新，新 infe 追加到映射后 iinf 内容末尾
            WriteU16(nb, Map(iinfPos) + 8 + 4, (ushort)(iinfItems + 1 - (oldInfePos >= 0 ? 1 : 0)));
            int newIinfEnd = Map(iinfEnd) - infeLen;
            Array.Copy(infe, 0, nb, newIinfEnd, infeLen);
            WriteU32(nb, Map(iinfPos), ReadU32(bytes, iinfPos) - removeInfe + infeLen);

            // iloc：计数/尺寸更新，新条目追加到映射后 iloc 内容末尾
            WriteU16(nb, Map(ilocPos) + 8 + 6, (ushort)(ilocItems + 1 - (oldIlocEntryPos >= 0 ? 1 : 0)));
            int newIlocEnd = Map(ilocEnd) - ilocAdd;
            Array.Copy(ilocEntry, 0, nb, newIlocEnd, ilocAdd);
            WriteU32(nb, Map(ilocPos), ReadU32(bytes, ilocPos) - removeIloc + ilocAdd);

            // uuid box 放在映射后 meta 内容的末尾（不含 uuid 自身增量的位置）。
            int uuidPos = metaEnd + deltaBeforeUuid;
            int xmpFilePos = uuidPos + 8 + 16;
            WriteN(nb, newIlocEnd + extOffPos, xmpFilePos, offSize);
            WriteU32(nb, uuidPos, uuidBoxLen);
            nb[uuidPos + 4] = (byte)'u'; nb[uuidPos + 5] = (byte)'u'; nb[uuidPos + 6] = (byte)'i'; nb[uuidPos + 7] = (byte)'d';
            Array.Copy(AdobeXmpUsertype, 0, nb, uuidPos + 8, 16);
            Array.Copy(xmpBytes, 0, nb, xmpFilePos, xmpBytes.Length);

            // 重新映射原 iloc 条目的外部 extent 偏移（跳过被移除的旧 XMP 条目）。
            // 新条目已带绝对偏移（xmpFilePos，位于 meta 内），不再二次平移。
            int eq = ilocPos + 16;
            for (int i = 0; i < ilocItems; i++)
            {
                int entryStart = eq;
                eq += 2;
                if (ilocVersion == 1 || ilocVersion == 2) eq += 2;
                eq += 2;
                eq += baseSize;
                int extCount = ReadU16(bytes, eq); eq += 2;
                for (int e = 0; e < extCount; e++)
                {
                    eq += idxSize;
                    int eo = ReadN(bytes, eq, offSize);
                    int el = ReadN(bytes, eq + offSize, lenSize);
                    int offFieldPos = eq;
                    eq += offSize + lenSize;
                    if (entryStart == oldIlocEntryPos) continue; // 旧 XMP 条目已删除
                    if (eo > metaEnd) WriteN(nb, Map(offFieldPos), eo + totalDelta, offSize);
                    // extent 越界校验：修正后必须落在新文件范围内，否则不写。
                    int correctedOff = eo > metaEnd ? eo + totalDelta : eo;
                    if ((long)correctedOff + el > nb.Length)
                    {
                        error = $"extent out of range after injection (item {i}, extent {e}, off={correctedOff}, len={el}, file={nb.Length})";
                        return null;
                    }
                }
            }

            for (int i = metaEnd + totalDelta; i < nb.Length - 8; i++)
            {
                if (nb[i] == (byte)'s' && nb[i + 1] == (byte)'t' && nb[i + 2] == (byte)'c' && nb[i + 3] == (byte)'o')
                {
                    int boxStart = i - 4;
                    int boxSize = ReadU32(nb, boxStart);
                    if (boxSize > 8 && boxStart + boxSize <= nb.Length)
                    {
                        int count = ReadU32(nb, i + 8);
                        for (int k = 0; k < count; k++)
                        {
                            int off = i + 12 + k * 4;
                            if (off + 4 > nb.Length) break;
                            int chunkOff = ReadU32(nb, off);
                            if (chunkOff > metaEnd) WriteU32(nb, off, chunkOff + totalDelta);
                        }
                        i = boxStart + boxSize - 1;
                    }
                }
                else if (nb[i] == (byte)'c' && nb[i + 1] == (byte)'o' && nb[i + 2] == (byte)'6' && nb[i + 3] == (byte)'4')
                {
                    int boxStart = i - 4;
                    int boxSize = ReadU32(nb, boxStart);
                    if (boxSize > 8 && boxStart + boxSize <= nb.Length)
                    {
                        int count = ReadU32(nb, i + 8);
                        for (int k = 0; k < count; k++)
                        {
                            int off = i + 12 + k * 8;
                            if (off + 8 > nb.Length) break;
                            long chunkOff = BinaryPrimitives.ReadInt64BigEndian(nb.AsSpan(off, 8));
                            if (chunkOff > metaEnd) BinaryPrimitives.WriteInt64BigEndian(nb.AsSpan(off, 8), chunkOff + totalDelta);
                        }
                        i = boxStart + boxSize - 1;
                    }
                }
            }

            return nb;
        }

        private static int ReadU32(byte[] a, int off) => BinaryPrimitives.ReadInt32BigEndian(a.AsSpan(off, 4));
        private static void WriteU32(byte[] a, int off, int val) => BinaryPrimitives.WriteInt32BigEndian(a.AsSpan(off, 4), val);
        private static ushort ReadU16(byte[] a, int off) => BinaryPrimitives.ReadUInt16BigEndian(a.AsSpan(off, 2));
        private static void WriteU16(byte[] a, int off, ushort val) => BinaryPrimitives.WriteUInt16BigEndian(a.AsSpan(off, 2), val);

        private static int ReadN(byte[] a, int off, int n)
        {
            int v = 0;
            for (int j = 0; j < n; j++) v = (v << 8) | a[off + j];
            return v;
        }

        private static void WriteN(byte[] a, int off, int val, int n)
        {
            for (int j = n - 1; j >= 0; j--)
            {
                a[off + j] = (byte)(val & 0xFF);
                val >>= 8;
            }
        }
    }
}
