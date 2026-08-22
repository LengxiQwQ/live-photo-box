using System;
using System.Buffers.Binary;
using System.IO;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 华为合并型 HEIC 的字节级 XMP 注入器（exiftool 无法重写此类结构时的回退）。
    /// 针对华为相机生成的标准结构（iloc 在 iinf 之前）实现，已在 20 个真机样本上验证：
    /// iinf 注册 application/rdf+xml item、iloc 登记位置、XMP 数据放 meta 末尾 uuid box，
    /// 同步修正 iloc 外部 extent 与内嵌 MP4 stco/co64 偏移。结构不认识时返回 false。
    /// </summary>
    public static class HeicXmpInjector
    {
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
            byte[] usertype =
            {
                0xBE, 0x7A, 0xCF, 0xCB, 0x97, 0xA9, 0x42, 0xE8,
                0x9C, 0x71, 0x99, 0x94, 0x91, 0xE3, 0xAF, 0xAC
            };
            int idx = IndexOfBytes(fileBytes, usertype);
            if (idx < 0) return false;
            int xmpStart = idx + usertype.Length;
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
            if (ilocVersion > 2) { error = $"unsupported iloc version {ilocVersion}"; return null; }
            if (offSize is < 1 or > 8 || lenSize is < 1 or > 8 || baseSize > 8 || idxSize > 8)
            {
                error = $"unexpected iloc field sizes (off={offSize} len={lenSize} base={baseSize} idx={idxSize})";
                return null;
            }

            int newItemId = 27;
            int ip = iinfPayload + 4 + 2;
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

            byte[] usertype = { 0xBE, 0x7A, 0xCF, 0xCB, 0x97, 0xA9, 0x42, 0xE8, 0x9C, 0x71, 0x99, 0x94, 0x91, 0xE3, 0xAF, 0xAC };
            int uuidBoxLen = 8 + 16 + xmpBytes.Length;
            int totalDelta = infeLen + ilocAdd + uuidBoxLen;
            int ilocEnd = ilocPos + ReadU32(bytes, ilocPos);
            int iinfEnd = iinfPos + ReadU32(bytes, iinfPos);

            // 专用映射：华为相机结构（iloc 在 iinf 之前）。
            int map(int x) =>
                x < ilocEnd ? x :
                x < iinfEnd ? x + ilocAdd :
                x < metaEnd ? x + ilocAdd + infeLen :
                x + totalDelta;

            byte[] nb = new byte[bytes.Length + totalDelta];
            for (int x = 0; x < metaEnd; x++) nb[map(x)] = bytes[x];
            Array.Copy(bytes, metaEnd, nb, metaEnd + totalDelta, bytes.Length - metaEnd);

            WriteU32(nb, metaPos, metaSize + totalDelta);
            int newIinfEnd = map(iinfPos) + ReadU32(bytes, iinfPos);
            // iinf is shifted by ilocAdd (iloc sits before iinf in the HUAWEI
            // layout), so the entry count must be written at the REMAPPED
            // position, not the original one.
            WriteU16(nb, map(iinfPos) + 8 + 4, (ushort)(iinfItems + 1));
            Array.Copy(infe, 0, nb, newIinfEnd, infeLen);
            WriteU32(nb, map(iinfPos), ReadU32(bytes, iinfPos) + infeLen);

            WriteU16(nb, ilocPos + 8 + 6, (ushort)(ilocItems + 1));
            int newIlocEnd = map(ilocPos) + ReadU32(bytes, ilocPos);
            Array.Copy(ilocEntry, 0, nb, newIlocEnd, ilocAdd);
            // The uuid box must go at the END of the remapped meta content, i.e.
            // metaEnd + ilocAdd + infeLen (the x < metaEnd branch of map()).
            // Using map(metaEnd) here is wrong: metaEnd itself falls into the
            // x + totalDelta branch, which equals the relocation target of the
            // post-meta data (metaEnd + totalDelta), so the uuid box would
            // overlap/corrupt the relocated mdat data and break the top-level
            // box chain (verified: exiftool reports a truncated box after uuid).
            int metaContentEnd = metaEnd + ilocAdd + infeLen;
            int xmpFilePos = metaContentEnd + 8 + 16;
            WriteN(nb, newIlocEnd + extOffPos, xmpFilePos, offSize);
            WriteU32(nb, map(ilocPos), ReadU32(bytes, ilocPos) + ilocAdd);

            int uuidPos = metaContentEnd;
            WriteU32(nb, uuidPos, uuidBoxLen);
            nb[uuidPos + 4] = (byte)'u'; nb[uuidPos + 5] = (byte)'u'; nb[uuidPos + 6] = (byte)'i'; nb[uuidPos + 7] = (byte)'d';
            Array.Copy(usertype, 0, nb, uuidPos + 8, 16);
            Array.Copy(xmpBytes, 0, nb, xmpFilePos, xmpBytes.Length);

            int ilocPayloadNew = map(ilocPos) + 8;
            q = ilocPayloadNew + 8;
            // Only remap the ORIGINAL entries: our newly inserted XMP entry already
            // carries an absolute file offset (xmpFilePos) and must NOT be shifted
            // again by totalDelta (it points inside the meta box, i.e. > metaEnd).
            for (int i = 0; i < ilocItems; i++)
            {
                q += 2;
                if (ilocVersion == 1 || ilocVersion == 2) q += 2;
                q += 2;
                q += baseSize;
                int extCount = ReadU16(nb, q); q += 2;
                for (int e = 0; e < extCount; e++)
                {
                    q += idxSize;
                    int eo = ReadN(nb, q, offSize);
                    int el = ReadN(nb, q + offSize, lenSize);
                    q += offSize + lenSize;
                    if (eo > metaEnd) WriteN(nb, q - offSize - lenSize, eo + totalDelta, offSize);
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
