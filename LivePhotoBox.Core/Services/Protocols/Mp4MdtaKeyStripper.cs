using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace LivePhotoBox.Services.Protocols
{
    /*
     * Mp4MdtaKeyStripper.cs
     *
     * 按 key 名/值成对剔除 MP4/MOV moov>udta>meta 的 keys/ilst 中厂商实况照片 mdta 键，
     * 避免无协议拆分（keep 原样抽视频）产物仍带厂商实况数据。
     *
     *   - 不动视频编码与其它元数据
     *   - 成对删除 keys/ilst 命中条目，重算各级 box 大小，必要时修正 stco/co64 绝对偏移
     */
    public static class Mp4MdtaKeyStripper
    {
        /// <summary>
        /// 剔除 HUAWEI 实况照片私有键：key 名以 com.openharmony 开头，
        /// 或值包含 openharmony（覆盖 ©too=openharmony6 补丁产物）。
        /// </summary>
        public static bool TryStripHuaweiKeys(string path, out string? error)
            => TryStripMdtaKeys(path, 
                ["com.openharmony"], [], ["openharmony"],
                static (name, value) =>
                name.StartsWith("com.openharmony", StringComparison.OrdinalIgnoreCase)
                || value.Contains("openharmony", StringComparison.OrdinalIgnoreCase), out error);

        /// <summary>
        /// 删除顶层 uuid box（user type 匹配，如 vivo ≤X200 的 vivoMediaExtInfo）。
        /// 无命中返回 true 且不写盘；命中时重写文件并修正 stco/co64 绝对偏移。
        /// </summary>
        public static bool TryStripUuidBox(string path, string userType, out string? error)
        {
            error = null;
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception ex) { error = ex.Message; return false; }

            try
            {
                if (Interop.NativeMp4MdtaKeyStripper.TryStripUuidBox(data, userType, out byte[]? nativeResult, out string? nativeError))
                {
                    if (nativeResult != null)
                    {
                        string tmpPath = path + ".lpb_uuid_strip.tmp";
                        File.WriteAllBytes(tmpPath, nativeResult);
                        File.Move(tmpPath, path, overwrite: true);
                    }
                    return true;
                }
                
                LogService.Merge($"Native UUID strip failed: {nativeError}, falling back to legacy", Models.LogLevel.Warning);

                byte[]? result = StripUuidBoxes(data, userType);
                if (result == null) return true;
                string tmpPath2 = path + ".lpb_uuid_strip.tmp";
                File.WriteAllBytes(tmpPath2, result);
                File.Move(tmpPath2, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 删除 moov 中 stsd 含指定键片段的 meta 元数据轨（供拆分端清华为
        /// com.openharmony.timed_metadata.movingphoto 等单文件协议轨）。
        /// 无命中返回 true 且不写盘。
        /// </summary>
        public static bool TryStripTracks(string path, string[] stsdKeyFragments, out string? error)
        {
            error = null;
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception ex) { error = ex.Message; return false; }

            try
            {
                if (Interop.NativeMp4MdtaKeyStripper.TryStripTracks(data, stsdKeyFragments, out byte[]? nativeResult, out string? nativeError))
                {
                    if (nativeResult != null)
                    {
                        string tmpPath = path + ".lpb_track_strip.tmp";
                        File.WriteAllBytes(tmpPath, nativeResult);
                        File.Move(tmpPath, path, overwrite: true);
                    }
                    return true;
                }
                
                LogService.Merge($"Native track strip failed: {nativeError}, falling back to legacy", Models.LogLevel.Warning);

                byte[]? result = StripTracksWithKeys(data, stsdKeyFragments);
                if (result == null) return true;
                string tmpPath2 = path + ".lpb_track_strip.tmp";
                File.WriteAllBytes(tmpPath2, result);
                File.Move(tmpPath2, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        /// <summary>
        /// 删除 moov 中 Apple Live Photo 的时序元数据轨（ContentDescribes /
        /// 封面轨：meta handler + stsd 含 live-photo-info 或 still-image-time）。
        /// </summary>
        public static bool TryStripMebxTracks(string path, out string? error)
            => TryStripTracks(path,
                ["com.apple.quicktime.live-photo-info", "com.apple.quicktime.still-image-time"],
                out error);

        /// <summary>
        /// 从文件剔除满足 <paramref name="fallbackShouldRemove"/> 的 mdta 键（成对删除 keys/ilst 条目）。
        /// 无命中时返回 true 且不写盘；命中时原地重写文件。失败返回 false 并给出 error。
        /// </summary>
        public static bool TryStripMdtaKeys(string path, string[] nameStarts, string[] nameContains, string[] valueContains, Func<string, string, bool> fallbackShouldRemove, out string? error)
        {
            error = null;
            byte[] data;
            try { data = File.ReadAllBytes(path); }
            catch (Exception ex) { error = ex.Message; return false; }

            try
            {
                if (Interop.NativeMp4MdtaKeyStripper.TryStripMdtaKeys(data, nameStarts, nameContains, valueContains, out byte[]? nativeResult, out string? nativeError))
                {
                    if (nativeResult != null)
                    {
                        string tmpPath = path + ".lpb_mdta_strip.tmp";
                        File.WriteAllBytes(tmpPath, nativeResult);
                        File.Move(tmpPath, path, overwrite: true);
                    }
                    return true;
                }
                
                LogService.Merge($"Native MDTA key strip failed: {nativeError}, falling back to legacy", Models.LogLevel.Warning);

                byte[]? stripped = Strip(data, fallbackShouldRemove);
                if (stripped == null)
                    return true; // 无命中，无需改动

                string tmpPath2 = path + ".lpb_mdta_strip.tmp";
                File.WriteAllBytes(tmpPath2, stripped);
                File.Move(tmpPath2, path, overwrite: true);
                return true;
            }
            catch (Exception ex)
            {
                error = ex.Message;
                return false;
            }
        }

        // 返回清理后的新字节数组；无命中返回 null。
        private static byte[]? Strip(byte[] data, Func<string, string, bool> shouldRemove)
        {
            int moov = FindTopLevelBox(data, 0, data.Length, "moov");
            if (moov < 0) return null;
            int moovSize = ReadBE32(data, moov);
            int moovEnd = moov + moovSize;

            // meta 的位置：ffmpeg 产物在 moov/udta/meta，iPhone 原厂在 moov/meta（直挂）。
            // meta 的子 box 起始：ffmpeg 产物带 version/flags（+12），iPhone 原厂不带（+8）。
            bool metaUnderUdta = false;
            int meta = -1, metaEnd = -1, metaChildrenStart = -1;
            int udta = FindChildBox(data, moov + 8, moovEnd, "udta");
            if (udta >= 0)
            {
                int udtaEnd = udta + ReadBE32(data, udta);
                meta = FindChildBox(data, udta + 8, udtaEnd, "meta");
            }
            if (meta < 0)
            {
                meta = FindChildBox(data, moov + 8, moovEnd, "meta");
            }
            else
            {
                metaUnderUdta = true;
            }
            if (meta < 0) return null;
            metaEnd = meta + ReadBE32(data, meta);
            metaChildrenStart = GetMetaChildrenStart(data, meta, metaEnd);

            int keys = FindChildBox(data, metaChildrenStart, metaEnd, "keys");
            int ilst = FindChildBox(data, metaChildrenStart, metaEnd, "ilst");
            if (keys < 0 || ilst < 0) return null;
            int keysEnd = keys + ReadBE32(data, keys);
            int ilstEnd = ilst + ReadBE32(data, ilst);

            // 1. 解析 keys 条目（key 名）
            var keyEntries = new List<BoxEntry>();
            int keyCount = ReadBE32(data, keys + 12);
            int p = keys + 16;
            for (int i = 0; i < keyCount && p + 8 <= keysEnd; i++)
            {
                int entrySize = ReadBE32(data, p);
                if (entrySize < 8 || p + entrySize > keysEnd) break;
                keyEntries.Add(new BoxEntry(p, entrySize, ReadKeyName(data, p, entrySize)));
                p += entrySize;
            }

            // 2. 解析 ilst 条目（index + data 值）
            var ilstItems = new List<IlstItem>();
            int ip = ilst + 8;
            while (ip + 8 <= ilstEnd)
            {
                int itemSize = ReadBE32(data, ip);
                if (itemSize < 12 || ip + itemSize > ilstEnd) break;
                int index = ReadBE32(data, ip + 4);
                string value = ReadIlstValue(data, ip + 8, ip + itemSize);
                ilstItems.Add(new IlstItem(ip, itemSize, index, value));
                ip += itemSize;
            }

            // 3. 配对判定：优先按 index 对齐，index 异常时退化为按顺序对齐
            var removeKey = new bool[keyEntries.Count];
            bool any = false;
            for (int i = 0; i < keyEntries.Count; i++)
            {
                int itemPos = -1;
                if (i < ilstItems.Count)
                {
                    int idx = ilstItems[i].Index;
                    itemPos = (idx >= 1 && idx <= ilstItems.Count && ilstItems[idx - 1].Index == idx) ? idx - 1 : i;
                }
                string value = itemPos >= 0 ? ilstItems[itemPos].Value : "";
                if (shouldRemove(keyEntries[i].Name, value))
                {
                    removeKey[i] = true;
                    any = true;
                }
            }
            if (!any) return null;

            // 4. 重建 keys
            byte[] newKeys = RebuildKeys(data, keyEntries, removeKey);

            // 5. 重建 ilst（index 重新按 1..N 编号）
            byte[] newIlst = RebuildIlst(data, ilstItems, keyEntries, removeKey);

            // 6. 重建 meta / udta / moov（保留其余子 box 原字节）
            byte[] newMeta = RebuildContainer(data, meta, metaEnd, metaChildrenStart, newKeys, keys, newIlst, ilst);
            byte[] newMoov;
            if (metaUnderUdta)
            {
                int udtaEnd = udta + ReadBE32(data, udta);
                byte[] newUdta = RebuildContainer(data, udta, udtaEnd, udta + 8, newMeta, meta, null, -1);
                newMoov = RebuildContainer(data, moov, moovEnd, moov + 8, newUdta, udta, null, -1);
            }
            else
            {
                newMoov = RebuildContainer(data, moov, moovEnd, moov + 8, newMeta, meta, null, -1);
            }

            // 7. 组装新文件
            int removedBytes = moovSize - newMoov.Length;
            byte[] result = new byte[data.Length - removedBytes];
            Array.Copy(data, 0, result, 0, moov);
            Array.Copy(newMoov, 0, result, moov, newMoov.Length);
            Array.Copy(data, moovEnd, result, moov + newMoov.Length, data.Length - moovEnd);

            // 8. 若删除后仍有指向 moov 之后数据的绝对偏移（moov 后置布局），
            //    stco/co64 需要同步减去被删除的字节数。
            if (removedBytes > 0)
                AdjustChunkOffsets(result, moov, moov, removedBytes);

            return result;
        }

        private static byte[]? StripUuidBoxes(byte[] data, string userType)
        {
            var targets = new List<(int Start, int Size)>();
            int pos = 0;
            while (pos + 8 <= data.Length)
            {
                uint size32 = BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(pos, 4));
                long size;
                int headerSize = 8;
                if (size32 == 1)
                {
                    if (pos + 16 > data.Length) break;
                    size = BinaryPrimitives.ReadInt64BigEndian(data.AsSpan(pos + 8, 8));
                    headerSize = 16;
                }
                else
                {
                    size = size32 == 0 ? data.Length - pos : size32;
                }

                if (size < headerSize || size > int.MaxValue || pos + size > data.Length) break;
                int boxSize = (int)size;
                if (IsType(data, pos, "uuid") && boxSize >= 24 && MatchesUserType(data, pos, userType))
                    targets.Add((pos, boxSize));
                pos += boxSize;
            }
            if (targets.Count == 0) return null;

            int removed = 0;
            using var ms = new MemoryStream(data.Length - targets[0].Size);
            int src = 0;
            int earliestStart = int.MaxValue;
            foreach (var t in targets)
            {
                ms.Write(data, src, t.Start - src);
                src = t.Start + t.Size;
                removed += t.Size;
                if (t.Start < earliestStart) earliestStart = t.Start;
            }
            ms.Write(data, src, data.Length - src);
            byte[] result = ms.ToArray();

            int moov = FindTopLevelBox(result, 0, result.Length, "moov");
            if (moov >= 0 && removed > 0)
                AdjustChunkOffsets(result, moov, earliestStart, removed);
            return result;
        }

        private static bool MatchesUserType(byte[] data, int boxStart, string userType)
        {
            for (int i = 0; i < userType.Length && i < 16; i++)
            {
                if (data[boxStart + 8 + i] != (byte)userType[i])
                    return false;
            }
            return true;
        }

        private static byte[]? StripTracksWithKeys(byte[] data, string[] stsdKeyFragments)
        {
            int moov = FindTopLevelBox(data, 0, data.Length, "moov");
            if (moov < 0) return null;
            int moovSize = ReadBE32(data, moov);
            int moovEnd = moov + moovSize;

            var remove = new HashSet<int>();
            int pos = moov + 8;
            while (pos + 8 <= moovEnd)
            {
                int size = ReadBE32(data, pos);
                if (size < 8 || pos + size > moovEnd) break;
                if (IsType(data, pos, "trak") && TrackContainsKeyFragment(data, pos, pos + size, stsdKeyFragments))
                    remove.Add(pos);
                pos += size;
            }
            if (remove.Count == 0) return null;

            using var ms = new MemoryStream(moovSize);
            ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); // size 占位
            ms.Write(data, moov + 4, 4);                                       // type
            int p = moov + 8;
            while (p + 8 <= moovEnd)
            {
                int size = ReadBE32(data, p);
                if (size < 8 || p + size > moovEnd) break;
                if (!remove.Contains(p))
                    ms.Write(data, p, size);
                p += size;
            }
            byte[] newMoov = ms.ToArray();
            WriteBE32(newMoov, 0, newMoov.Length);

            int removedBytes = moovSize - newMoov.Length;
            byte[] result = new byte[data.Length - removedBytes];
            Array.Copy(data, 0, result, 0, moov);
            Array.Copy(newMoov, 0, result, moov, newMoov.Length);
            Array.Copy(data, moovEnd, result, moov + newMoov.Length, data.Length - moovEnd);
            if (removedBytes > 0)
                AdjustChunkOffsets(result, moov, moov, removedBytes);
            return result;
        }

        // 判断 trak 是否为 meta 元数据轨，且 stsd 文本含任一指定键片段。
        private static bool TrackContainsKeyFragment(byte[] data, int trakStart, int trakEnd, string[] fragments)
        {
            int mdia = FindChildBox(data, trakStart + 8, trakEnd, "mdia");
            if (mdia < 0) return false;
            int mdiaEnd = mdia + ReadBE32(data, mdia);
            int hdlr = FindChildBox(data, mdia + 8, mdiaEnd, "hdlr");
            if (hdlr < 0 || hdlr + 20 > mdiaEnd) return false;
            // hdlr: size+type(8) + version/flags(4) + pre_defined(4) + handler_type(4)
            bool metaHandler = data[hdlr + 16] == (byte)'m' && data[hdlr + 17] == (byte)'e'
                && data[hdlr + 18] == (byte)'t' && data[hdlr + 19] == (byte)'a';
            if (!metaHandler) return false;

            string text = Encoding.ASCII.GetString(data, trakStart, trakEnd - trakStart);
            foreach (string fragment in fragments)
            {
                if (text.Contains(fragment, StringComparison.Ordinal))
                    return true;
            }
            return false;
        }

        // ── 解析辅助 ──────────────────────────────────────────────────

        // meta 可能是 FullBox（子 box 从 +12 开始，ffmpeg 产物）或普通容器（+8，iPhone 原厂）。
        // 以 meta+8 处是否为合法子 box（size 合理 + 类型可打印）判断。
        private static int GetMetaChildrenStart(byte[] data, int metaStart, int metaEnd)
        {
            if (metaStart + 16 <= metaEnd)
            {
                int probeSize = ReadBE32(data, metaStart + 8);
                if (probeSize >= 8 && metaStart + 8 + probeSize <= metaEnd && IsPrintableType(data, metaStart + 12))
                    return metaStart + 8;
            }
            return metaStart + 12;
        }

        private static bool IsPrintableType(byte[] data, int off)
        {
            for (int i = 0; i < 4; i++)
            {
                byte c = data[off + i];
                if (c < 0x20 || c > 0x7E) return false;
            }
            return true;
        }

        // ffmpeg 风格：name 直接跟在 "mdta" 后（长度 = entrySize - 8）。
        // Apple 风格：entrySize 内为 [namespace 4][keySize 2][key]，检测到则按此解析。
        private static string ReadKeyName(byte[] data, int entryStart, int entrySize)
        {
            int nameLen = entrySize - 8;
            if (nameLen >= 6 && entrySize >= 14 + 2)
            {
                int appleLen = ReadBE16(data, entryStart + 12);
                if (appleLen > 0 && entryStart + 14 + appleLen == entryStart + entrySize)
                    return Encoding.UTF8.GetString(data, entryStart + 14, appleLen);
            }
            return nameLen > 0
                ? Encoding.UTF8.GetString(data, entryStart + 8, nameLen)
                : "";
        }

        private static string ReadIlstValue(byte[] data, int start, int end)
        {
            int cp = start;
            while (cp + 8 <= end)
            {
                int childSize = ReadBE32(data, cp);
                if (childSize < 8 || cp + childSize > end) break;
                if (IsType(data, cp, "data") && childSize >= 16)
                {
                    int valueLen = childSize - 16;
                    return valueLen > 0
                        ? Encoding.UTF8.GetString(data, cp + 16, valueLen)
                        : "";
                }
                cp += childSize;
            }
            return "";
        }

        private static byte[] RebuildKeys(byte[] data, List<BoxEntry> entries, bool[] remove)
        {
            int kept = 0;
            for (int i = 0; i < entries.Count; i++)
                if (!remove[i]) kept++;

            byte[] result = new byte[16 + TotalKeptBytes(entries, remove)];
            WriteBE32(result, 0, result.Length);
            WriteType(result, 4, "keys");
            WriteBE32(result, 8, 0); // version/flags
            WriteBE32(result, 12, kept);
            int pos = 16;
            for (int i = 0; i < entries.Count; i++)
            {
                if (remove[i]) continue;
                Array.Copy(data, entries[i].Start, result, pos, entries[i].Size);
                pos += entries[i].Size;
            }
            return result;
        }

        private static byte[] RebuildIlst(byte[] data, List<IlstItem> items, List<BoxEntry> keys, bool[] removeKey)
        {
            // 与 keys 按顺序对齐：keys[i] ↔ items[i]
            byte[] result = new byte[8 + TotalIlstKeptBytes(data, items, keys, removeKey)];
            WriteBE32(result, 0, result.Length);
            WriteType(result, 4, "ilst");
            int pos = 8;
            int newIndex = 1;
            for (int i = 0; i < items.Count; i++)
            {
                bool remove = i < keys.Count && removeKey[i];
                if (remove) continue;
                Array.Copy(data, items[i].Start, result, pos, items[i].Size);
                WriteBE32(result, pos + 4, newIndex); // 重新编号
                pos += items[i].Size;
                newIndex++;
            }
            return result;
        }

        // 重建容器 box（moov/udta/meta）：保留原顺序的全部子 box，仅替换指定两个。
        private static byte[] RebuildContainer(
            byte[] data, int boxStart, int boxEnd, int childrenStart,
            byte[]? replaceA, int replaceAPos, byte[]? replaceB, int replaceBPos)
        {
            using var ms = new MemoryStream(boxEnd - boxStart + 64);
            ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); ms.WriteByte(0); // size 占位
            ms.Write(data, boxStart + 4, 4);                                   // type

            // 子 box 从 +12 开始说明是 FullBox（带 version/flags），补上这 4 字节；
            // iPhone 原厂 meta 无 version/flags（子 box 从 +8 开始），不补。
            if (childrenStart == boxStart + 12)
                ms.Write(data, boxStart + 8, 4);

            int pos = childrenStart;
            while (pos + 8 <= boxEnd)
            {
                int childSize = ReadBE32(data, pos);
                if (childSize < 8 || pos + childSize > boxEnd) break;
                if (pos == replaceAPos && replaceA != null)
                    ms.Write(replaceA, 0, replaceA.Length);
                else if (pos == replaceBPos && replaceB != null)
                    ms.Write(replaceB, 0, replaceB.Length);
                else
                    ms.Write(data, pos, childSize);
                pos += childSize;
            }

            byte[] result = ms.ToArray();
            WriteBE32(result, 0, result.Length);
            return result;
        }

        // 修正 stco/co64：删除位置（threshold）之后的绝对偏移统一减 removedBytes。
        private static void AdjustChunkOffsets(byte[] data, int moovStart, int threshold, int removedBytes)
        {
            int moovSize = ReadBE32(data, moovStart);
            int moovEnd = moovStart + moovSize;
            int pos = moovStart + 8;
            while (pos + 8 <= moovEnd)
            {
                int size = ReadBE32(data, pos);
                if (size < 8 || pos + size > moovEnd) break;
                if (IsType(data, pos, "trak"))
                    AdjustTrakChunkOffsets(data, pos, pos + size, threshold, removedBytes);
                pos += size;
            }
        }

        private static void AdjustTrakChunkOffsets(byte[] data, int trakStart, int trakEnd, int threshold, int removedBytes)
        {
            int mdia = FindChildBox(data, trakStart + 8, trakEnd, "mdia");
            if (mdia < 0) return;
            int mdiaEnd = mdia + ReadBE32(data, mdia);
            int minf = FindChildBox(data, mdia + 8, mdiaEnd, "minf");
            if (minf < 0) return;
            int minfEnd = minf + ReadBE32(data, minf);
            int stbl = FindChildBox(data, minf + 8, minfEnd, "stbl");
            if (stbl < 0) return;
            int stblEnd = stbl + ReadBE32(data, stbl);

            int stco = FindChildBox(data, stbl + 8, stblEnd, "stco");
            if (stco >= 0)
            {
                int count = ReadBE32(data, stco + 12);
                for (int i = 0; i < count; i++)
                {
                    int field = stco + 16 + i * 4;
                    if (field + 4 > stblEnd) break;
                    long off = ReadBE32(data, field);
                    if (off > threshold) WriteBE32(data, field, (int)(off - removedBytes));
                }
            }

            int co64 = FindChildBox(data, stbl + 8, stblEnd, "co64");
            if (co64 >= 0)
            {
                int count = ReadBE32(data, co64 + 12);
                for (int i = 0; i < count; i++)
                {
                    int field = co64 + 16 + i * 8;
                    if (field + 8 > stblEnd) break;
                    long off = ReadBE64(data, field);
                    if (off > threshold) WriteBE64(data, field, off - removedBytes);
                }
            }
        }

        private static int TotalKeptBytes(List<BoxEntry> entries, bool[] remove)
        {
            int total = 0;
            for (int i = 0; i < entries.Count; i++)
                if (!remove[i]) total += entries[i].Size;
            return total;
        }

        private static int TotalIlstKeptBytes(byte[] data, List<IlstItem> items, List<BoxEntry> keys, bool[] removeKey)
        {
            int total = 0;
            for (int i = 0; i < items.Count; i++)
            {
                if (i < keys.Count && removeKey[i]) continue;
                total += items[i].Size;
            }
            return total;
        }

        private static int FindTopLevelBox(byte[] data, int start, int end, string type)
        {
            int pos = start;
            while (pos + 8 <= end)
            {
                int size = ReadBE32(data, pos);
                if (size < 8 || pos + size > end) break;
                if (IsType(data, pos, type)) return pos;
                pos += size;
            }
            return -1;
        }

        private static int FindChildBox(byte[] data, int start, int end, string type)
        {
            int pos = start;
            while (pos + 8 <= end)
            {
                int size = ReadBE32(data, pos);
                if (size < 8 || pos + size > end) break;
                if (IsType(data, pos, type)) return pos;
                pos += size;
            }
            return -1;
        }

        private static bool IsType(byte[] data, int off, string type)
            => data[off + 4] == type[0] && data[off + 5] == type[1]
            && data[off + 6] == type[2] && data[off + 7] == type[3];

        private static void WriteType(byte[] data, int off, string type)
        {
            data[off] = (byte)type[0];
            data[off + 1] = (byte)type[1];
            data[off + 2] = (byte)type[2];
            data[off + 3] = (byte)type[3];
        }

        private static int ReadBE32(byte[] d, int off)
            => BinaryPrimitives.ReadInt32BigEndian(d.AsSpan(off));

        private static int ReadBE16(byte[] d, int off)
            => BinaryPrimitives.ReadInt16BigEndian(d.AsSpan(off));

        private static long ReadBE64(byte[] d, int off)
            => BinaryPrimitives.ReadInt64BigEndian(d.AsSpan(off));

        private static void WriteBE32(byte[] d, int off, int v)
            => BinaryPrimitives.WriteInt32BigEndian(d.AsSpan(off), v);

        private static void WriteBE64(byte[] d, int off, long v)
            => BinaryPrimitives.WriteInt64BigEndian(d.AsSpan(off), v);

        private readonly record struct BoxEntry(int Start, int Size, string Name);

        private readonly record struct IlstItem(int Start, int Size, int Index, string Value);
    }
}
