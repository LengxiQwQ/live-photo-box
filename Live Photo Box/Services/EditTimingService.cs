/*
 * EditTimingService.cs
 *
 * 实况照片关键帧时间读取服务。
 *
 * 不同协议对"静态照片位置"和"封面帧位置"的定义不同：
 *   • Google V1/V2: 只有一个时间戳，照片=封面
 *   • OPPO O-Live: 有分离概念 — MotionPhotoPrimaryTimestamp(照片) vs MotionPhotoTimestamp(封面)
 *     且改封面后原始高清图被移到 GContainer 的 "Original" item 中
 *   • Apple: 后续扩展
 *
 * 设计原则：通过 XMP 文本检测协议 → 按协议读取对应标签 → 返回 (photoTime, coverTime)。
 * 协议检测使用已缓存的 XMP 文本（与 JpegProtocolMap / GetProtocolName 一致），
 * 无需额外 exiftool 调用。
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace LivePhotoBox.Services
{
    /// <summary>Cover 时机信息（⭐ 照片位置 + 🔵 封面位置 + 原始照片数据）</summary>
    public readonly struct EditTimingInfo
    {
        /// <summary>静态照片在视频时间轴中的时间偏移（秒），对应 ⭐ 位置</summary>
        public double PhotoTimeSeconds { get; init; }

        /// <summary>封面帧 / Cover 的时间偏移（秒），对应 🔵 选中位置</summary>
        public double CoverTimeSeconds { get; init; }

        /// <summary>照片和封面是否不同（true=协议支持分离且用户改了封面）</summary>
        public bool IsSplit => Math.Abs(CoverTimeSeconds - PhotoTimeSeconds) > 0.001;

        /// <summary>OPPO 改封面后原始高清图被移到 Original item，需要单独提取</summary>
        public bool HasOriginalPhoto { get; init; }
    }

    public static class EditTimingService
    {
        // OPPO XMP 中的原始照片时间戳标签（OpCamera 命名空间）
        private static readonly Regex OppoPrimaryTimestampRegex = new(
            @"MotionPhotoPrimaryPresentationTimestampUs[""=\s]+(\d+)",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        // OPPO 协议检测关键字（与 JpegProtocolMap 一致）
        private const string OppoMarker = "OpCamera:VideoLength";

        // 容器中 GainMap / Original / MotionPhoto item 的长度解析
        // 匹配 Item:Length="数字" 模式（按顺序）
        private static readonly Regex ItemLengthRegex = new(
            @"Item:Length=""(\d+)""",
            RegexOptions.Compiled | RegexOptions.IgnoreCase);

        /// <summary>
        /// 从 XMP 文本和标准 cover 时间，按协议计算分离后的 timing。
        /// </summary>
        public static EditTimingInfo Resolve(
            double standardKeyPhotoTimeSeconds,
            string? xmpText)
        {
            double photoTime = standardKeyPhotoTimeSeconds;
            double coverTime = standardKeyPhotoTimeSeconds;
            bool hasOriginal = false;

            if (!string.IsNullOrWhiteSpace(xmpText))
            {
                // ── OPPO O-Live Photo ──
                if (xmpText.Contains(OppoMarker))
                {
                    // 检测是否改过封面（有 Original item）
                    hasOriginal = xmpText.Contains("Item:Semantic=\"Original\"");

                    var match = OppoPrimaryTimestampRegex.Match(xmpText);
                    if (match.Success &&
                        long.TryParse(match.Groups[1].Value, out long primaryUs) &&
                        primaryUs > 0)
                    {
                        double primarySec = primaryUs / 1_000_000.0;
                        if (Math.Abs(primarySec - standardKeyPhotoTimeSeconds) > 0.001)
                        {
                            photoTime = primarySec;
                            coverTime = standardKeyPhotoTimeSeconds;
                            LogService.FileOp(
                                $"KeyPhotoTiming[OPPO] Split: Photo={photoTime:F4}s (Primary), " +
                                $"Cover={coverTime:F4}s (MotionPhoto), HasOriginal={hasOriginal}",
                                Models.LogLevel.Info);
                        }
                    }

                    if (hasOriginal)
                    {
                        LogService.FileOp(
                            $"KeyPhotoTiming[OPPO] Original photo detected in container — " +
                            "star thumbnail will use Original item",
                            Models.LogLevel.Info);
                    }
                }
            }

            return new EditTimingInfo
            {
                PhotoTimeSeconds = photoTime,
                CoverTimeSeconds = coverTime,
                HasOriginalPhoto = hasOriginal
            };
        }

        /// <summary>
        /// 从 Apple Live Photo 的 MOV 文件中读取 Still Image 元数据轨的时间。
        /// Apple MOV 的 PosterTime 永远为 0，真正的封面/照片时间藏在 mebx 元数据轨中，
        /// 通过 exiftool -ee 提取 StillImageTime 所在轨道的 TrackDuration 获得。
        ///
        /// 为什么不用 ffprobe：ffprobe 不在项目的 Tools 打包目录中，分发后不可用。
        /// exiftool 在 Tools 目录中随 app 分发，保证可用性。
        /// </summary>
        /// <param name="movPath">MOV 视频文件路径</param>
        /// <returns>Still Image 轨的 TrackDuration（秒），失败返回 null</returns>
        public static double? ReadAppleStillImageTime(string movPath)
        {
            try
            {
                string? exifToolPath = ExternalToolLocator.FindExifTool();
                if (string.IsNullOrEmpty(exifToolPath))
                    return null;

                if (!File.Exists(movPath))
                    return null;

                var psi = new ProcessStartInfo
                {
                    FileName = exifToolPath,
                    Arguments = $"-api LargeFileSupport=1 -ee -a -G1 -s " +
                                $"-StillImageTime -TrackDuration \"{movPath}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return null;

                string stdout = proc.StandardOutput.ReadToEnd();
                proc.WaitForExit(10000);

                if (string.IsNullOrWhiteSpace(stdout))
                    return null;

                // exiftool -ee 输出格式（逐行）：
                //   [TrackN]  StillImageTime  : -1      ← 有 StillImageTime 的轨
                //   [Track1]  TrackDuration   : 2.77 s
                //   [TrackN]  TrackDuration   : 1.30 s  ← 同一轨的 Duration 就是照片/封面位置
                //
                // 逐行解析：先找有 StillImageTime 的轨编号，再找同一轨的 TrackDuration。

                string? trackWithStill = null;
                double? result = null;
                foreach (var rawLine in stdout.Split('\n', StringSplitOptions.RemoveEmptyEntries))
                {
                    string line = rawLine.Trim();

                    // 匹配 [TrackN]  标签（如 [Track4]）
                    var trackMatch = Regex.Match(line, @"^\[(Track\d+)\]\s+");
                    if (!trackMatch.Success)
                        continue;

                    string currentTrack = trackMatch.Groups[1].Value;
                    string valuePart = line.Substring(trackMatch.Index + trackMatch.Length).Trim();

                    // 如果这一行是 StillImageTime，记录轨编号
                    if (valuePart.StartsWith("StillImageTime"))
                    {
                        trackWithStill = currentTrack;
                    }
                    // 如果这一行是 TrackDuration，并且这是我们要找的轨，取值
                    else if (valuePart.StartsWith("TrackDuration") && currentTrack == trackWithStill)
                    {
                        var durMatch = Regex.Match(valuePart, @"([\d.]+)");
                        if (durMatch.Success &&
                            double.TryParse(durMatch.Groups[1].Value,
                                System.Globalization.NumberStyles.Any,
                                System.Globalization.CultureInfo.InvariantCulture,
                                out double dur) &&
                            dur > 0)
                        {
                            result = dur;
                            break;
                        }
                    }
                }

                if (result.HasValue)
                {
                    LogService.FileOp(
                        $"KeyPhotoTiming[Apple] MOV still image track {trackWithStill}: {result.Value:F4}s",
                        Models.LogLevel.Info);
                    return result.Value;
                }

                return null;
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"KeyPhotoTiming[Apple] Failed to read MOV still time: {ex.Message}",
                    Models.LogLevel.Warning);
                return null;
            }
        }

        /// <summary>
        /// 二进制 patch Apple Live Photo MOV 的封面时间戳。
        ///
        /// === 背景 ===
        /// Apple 实况照片的封面位置存在 MOV 的 mebx 轨 edit list (elst) 里。
        /// exiftool 源码硬编码 Writable=0 写不了，ffmpeg remux 会丢弃整个 mebx 轨。
        /// 唯一可行方案：直接改二进制。
        ///
        /// === Apple 的结构 ===
        /// mebx 轨用 edit list 实现"空位 → 样本"：
        ///   elst[0] = { trackDur=封面时间×600, mediaTime=-1 }  ← 空位长度 = 封面位置
        ///   elst[1] = { trackDur=1, mediaTime=0 }               ← 元数据样本本身
        ///   tkhd.duration = elst[0].trackDur + elst[1].trackDur
        ///
        /// === 实现 ===
        /// 1. 在 moov 原子树中递归搜索，找 elst 符合 [空位(mediaTime=-1), 样本] 模式的 trak
        /// 2. Patch elst[0].trackDur  ← 改 4 字节
        /// 3. Patch tkhd.duration     ← 改 4 字节
        /// 4. 写回文件（毫秒级，不动其他 99.9% 的数据）
        /// </summary>
        /// <param name="movPath">MOV 文件路径</param>
        /// <param name="newTimeSeconds">新的封面时间（秒）</param>
        public static void PatchAppleStillImageTime(string movPath, double newTimeSeconds)
        {
            byte[] data = File.ReadAllBytes(movPath);

            // ── 1. 找 moov atom ──
            int moovIdx = IndexOfBytes(data, "moov"u8);
            if (moovIdx < 4)
                throw new InvalidDataException("MOV: moov atom not found");
            int moovStart = moovIdx - 4;
            int moovSize = ReadBigEndianInt32(data, moovStart);
            int moovEnd = moovStart + moovSize;

            // ── 2. 获取 mvhd timescale ──
            int mvhdOff = FindAtom(data, moovStart + 8, moovEnd, "mvhd"u8);
            if (mvhdOff < 0)
                throw new InvalidDataException("MOV: mvhd atom not found");
            int mvTimescale = ReadBigEndianInt32(data, mvhdOff + 20); // version 0

            // ── 3. 遍历所有 trak，找 mebx 轨（通过 elst 模式识别） ──
            //     Apple Live Photo 的 mebx 轨 elst 固定为 2 条目：
            //     [0]: trackDur=封面时间, mediaTime=-1 (空位)
            //     [1]: trackDur=1, mediaTime=0 (元数据样本)
            //     不依赖 hdlr subtype（Apple 用 'meta' 不是 'mebx'）。
            int pos = moovStart + 8;
            int trakIndex = 0;
            bool found = false;
            while (pos < moovEnd - 7)
            {
                int atomSize = ReadBigEndianInt32(data, pos);
                if (atomSize < 8 || atomSize > moovEnd - pos) break;

                if (IsAtomType(data, pos, "trak"u8))
                {
                    trakIndex++;
                    int trakStart = pos;
                    int trakEnd = pos + atomSize;

                    // 找 elst（递归，因为 elst 在 trak→edts 下面）
                    int elstOff = FindAtom(data, trakStart + 8, trakEnd, "elst"u8);
                    if (elstOff < 0) { pos += atomSize; continue; }

                    // 验证 elst 结构：2 条目、第 1 条 mediaTime = -1
                    int elstVer = data[elstOff + 8];
                    int elstEntryCount = ReadBigEndianInt32(data, elstOff + 12);
                    if (elstEntryCount != 2) { pos += atomSize; continue; }

                    int entrySize = elstVer == 0 ? 12 : 20;
                    int mediaTime0 = ReadBigEndianInt32(data, elstOff + 16 + 4); // entry[0].mediaTime
                    if (mediaTime0 != -1) { pos += atomSize; continue; }

                    // ── 4. 确认是 mebx 轨 → patch elst[0].trackDur 和 tkhd.duration ──
                    int oldTrackDur0 = ReadBigEndianInt32(data, elstOff + 16);
                    int trackDur1 = ReadBigEndianInt32(data, elstOff + 16 + entrySize); // entry[1].trackDur

                    // 写新的 elst[0].trackDur
                    int newTrackDur0 = (int)Math.Round(newTimeSeconds * mvTimescale);
                    WriteBigEndianInt32(data, elstOff + 16, newTrackDur0);

                    // 写新的 tkhd.duration
                    int tkhdOff = FindAtom(data, trakStart + 8, trakEnd, "tkhd"u8);
                    if (tkhdOff < 0)
                        throw new InvalidDataException("MOV: mebx track has no tkhd");
                    int tkhdVer = data[tkhdOff + 8];
                    int tkhdDurOff = tkhdVer == 0 ? tkhdOff + 28 : tkhdOff + 36;
                    int newTkhdDuration = newTrackDur0 + trackDur1;
                    WriteBigEndianInt32(data, tkhdDurOff, newTkhdDuration);

                    LogService.FileOp(
                        $"KeyPhotoTiming[Apple] Patched MOV Track{trakIndex}: " +
                        $"elst[0].trackDur {oldTrackDur0}→{newTrackDur0}, " +
                        $"tkhd.duration→{newTkhdDuration} " +
                        $"→ still image at {newTimeSeconds:F3}s",
                        Models.LogLevel.Info);
                    found = true;
                    break;
                }
                pos += atomSize;
            }

            if (!found)
                throw new InvalidDataException("MOV: mebx track not found");

            // ── 5. 写回 ──
            File.WriteAllBytes(movPath, data);
        }

        // ══════════════════════════════════════════════════════════════
        //  辅助方法：原子查找、字节读取、字节写入
        // ══════════════════════════════════════════════════════════════

        /// <summary>在字节数组中递归查找指定类型的 atom（在容器原子中递归搜索子级）</summary>
        private static int FindAtom(byte[] data, int start, int end, ReadOnlySpan<byte> atomType)
        {
            int pos = start;
            while (pos < end - 7)
            {
                int size = ReadBigEndianInt32(data, pos);
                if (size < 8 || size > end - pos) break;

                if (IsAtomType(data, pos, atomType))
                    return pos;

                // 对容器型原子递归搜索
                if (IsContainerAtom(data, pos))
                {
                    int found = FindAtom(data, pos + 8, pos + size, atomType);
                    if (found >= 0) return found;
                }

                pos += size;
            }
            return -1;
        }

        private static bool IsContainerAtom(byte[] data, int offset)
        {
            // MOV/MP4 容器原子：moov, trak, mdia, minf, stbl, edts, dinf, udta, meta
            byte b4 = data[offset + 4], b5 = data[offset + 5], b6 = data[offset + 6], b7 = data[offset + 7];
            return (b4 == (byte)'m' && b5 == (byte)'o' && b6 == (byte)'o' && b7 == (byte)'v') ||
                   (b4 == (byte)'t' && b5 == (byte)'r' && b6 == (byte)'a' && b7 == (byte)'k') ||
                   (b4 == (byte)'m' && b5 == (byte)'d' && b6 == (byte)'i' && b7 == (byte)'a') ||
                   (b4 == (byte)'m' && b5 == (byte)'i' && b6 == (byte)'n' && b7 == (byte)'f') ||
                   (b4 == (byte)'s' && b5 == (byte)'t' && b6 == (byte)'b' && b7 == (byte)'l') ||
                   (b4 == (byte)'e' && b5 == (byte)'d' && b6 == (byte)'t' && b7 == (byte)'s') ||
                   (b4 == (byte)'d' && b5 == (byte)'i' && b6 == (byte)'n' && b7 == (byte)'f') ||
                   (b4 == (byte)'u' && b5 == (byte)'d' && b6 == (byte)'t' && b7 == (byte)'a') ||
                   (b4 == (byte)'m' && b5 == (byte)'e' && b6 == (byte)'t' && b7 == (byte)'a');
        }

        private static bool IsAtomType(byte[] data, int offset, ReadOnlySpan<byte> atomType)
        {
            return data[offset + 4] == atomType[0] &&
                   data[offset + 5] == atomType[1] &&
                   data[offset + 6] == atomType[2] &&
                   data[offset + 7] == atomType[3];
        }

        private static int IndexOfBytes(byte[] data, ReadOnlySpan<byte> pattern)
        {
            for (int i = 0; i <= data.Length - pattern.Length; i++)
            {
                int j;
                for (j = 0; j < pattern.Length; j++)
                    if (data[i + j] != pattern[j]) break;
                if (j == pattern.Length) return i;
            }
            return -1;
        }

        private static int ReadBigEndianInt32(byte[] data, int offset)
        {
            return (data[offset] << 24) | (data[offset + 1] << 16) |
                   (data[offset + 2] << 8) | data[offset + 3];
        }

        private static void WriteBigEndianInt32(byte[] data, int offset, int value)
        {
            data[offset] = (byte)(value >> 24);
            data[offset + 1] = (byte)(value >> 16);
            data[offset + 2] = (byte)(value >> 8);
            data[offset + 3] = (byte)value;
        }

        /// <summary>
        /// 从 OPPO 容器中提取 Original 原始高清照片的 JPEG 字节。
        /// 仅在 HasOriginalPhoto=true 时调用。
        /// 返回 null 表示提取失败，调用方应回退到 SelectedFileThumbnail。
        /// </summary>
        public static byte[]? ReadOriginalPhotoBytes(string filePath)
        {
            try
            {
                // 读取 XMP 文本
                string xmpText = LivePhotoSplitService.ReadMetadataTextSync(filePath);
                if (string.IsNullOrWhiteSpace(xmpText))
                    return null;

                // 解析容器 item 长度列表（跳过 Primary 的 0）
                var lengths = new List<long>();
                foreach (Match m in ItemLengthRegex.Matches(xmpText))
                {
                    if (long.TryParse(m.Groups[1].Value, out long len))
                        lengths.Add(len);
                }

                // 需要至少 [Primary=0, GainMap, Original, MotionPhoto]
                // 找到 Original 的位置：检查 Semantic 列表
                if (!xmpText.Contains("Item:Semantic=\"Original\""))
                    return null;

                // 找到 Primary JPEG 的结束位置
                long primaryEnd = FindPrimaryJpegEnd(filePath);
                if (primaryEnd <= 0) return null;

                // 计算 GainMap 长度（lengths[1] 如果存在，否则为 0）
                long gainMapLen = lengths.Count > 1 ? lengths[1] : 0;

                // Original 起始偏移 = Primary结束 + GainMap长度
                long originalOffset = primaryEnd + gainMapLen;

                // Original 长度 = lengths[2]（如果存在）
                long originalLen = lengths.Count > 2 ? lengths[2] : 0;
                if (originalLen <= 0) return null;

                // 读取 Original JPEG
                using var fs = new FileStream(filePath, FileMode.Open,
                    FileAccess.Read, FileShare.Read);
                if (originalOffset + originalLen > fs.Length)
                    return null;

                fs.Seek(originalOffset, SeekOrigin.Begin);
                byte[] data = new byte[originalLen];
                int totalRead = 0;
                while (totalRead < originalLen)
                {
                    int r = fs.Read(data, totalRead,
                        (int)Math.Min(originalLen - totalRead, int.MaxValue));
                    if (r == 0) break;
                    totalRead += r;
                }

                LogService.FileOp(
                    $"KeyPhotoTiming[OPPO] Original photo extracted: " +
                    $"{totalRead} bytes from offset {originalOffset} " +
                    $"(primaryEnd={primaryEnd}, gainMap={gainMapLen})",
                    Models.LogLevel.Info);

                return totalRead == originalLen ? data : null;
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"KeyPhotoTiming[OPPO] Failed to extract Original photo: {ex.Message}",
                    Models.LogLevel.Warning);
                return null;
            }
        }

        /// <summary>
        /// 找到 JPEG 文件中主图结束位置（EOI 标记之后）。
        /// 跳过所有 JPEG marker 段（APPn, DQT, SOF, DHT, SOS 等），
        /// 遇到 EOI (0xFF 0xD9) 返回其后的文件位置。
        /// 不处理 RST 标记之间的转义——对于 Motion Photo 容器中的 Primary JPEG，
        /// 第一个 EOI 标记就是图像结尾（后面紧跟着 GainMap 或 video）。
        /// </summary>
        private static long FindPrimaryJpegEnd(string filePath)
        {
            using var fs = new FileStream(filePath, FileMode.Open,
                FileAccess.Read, FileShare.Read);

            // 检查 SOI
            if (fs.ReadByte() != 0xFF || fs.ReadByte() != 0xD8)
                return -1; // 不是 JPEG

            var buf = new byte[2];
            while (true)
            {
                // 找下一个 marker (0xFF)
                int b;
                while ((b = fs.ReadByte()) != 0xFF)
                {
                    if (b < 0) return fs.Length;
                }

                // 跳过填充的 0xFF
                while ((b = fs.ReadByte()) == 0xFF) { }
                if (b < 0) return fs.Length;

                // EOI — 图像结束
                if (b == 0xD9)
                    return fs.Position;

                // 转义的 0xFF (0xFF 0x00)
                if (b == 0x00)
                    continue;

                // RST 标记 (0xFF 0xD0–0xD7)，无长度段
                if (b >= 0xD0 && b <= 0xD7)
                    continue;

                // 所有其他 marker：读取 2 字节长度并跳过段体
                int n = fs.Read(buf, 0, 2);
                if (n < 2) return fs.Length;
                int segLen = (buf[0] << 8) | buf[1];
                if (segLen > 2)
                {
                    try { fs.Seek(segLen - 2, SeekOrigin.Current); }
                    catch { return fs.Length; }
                }
            }
        }
    }
}
