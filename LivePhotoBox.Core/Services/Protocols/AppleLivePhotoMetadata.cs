using LivePhotoBox.Models;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services.Protocols
{
    // ═════════════════════════════════════════════════════════════════════════════
    // AppleLivePhotoMetadata — 拆分输出「Apple Live Photo」双文件配对元数据写入。
    //
    // 拆分单文件实况照片、选 Apple 协议（protocolIndex==1）时，把输出的图片 + 视频
    // 打上 Apple 实况照片的配对标记，使 iOS / Apple Photos 将其识别为一对实况照片。
    //
    // 依据（唯一事实源）：docs/实况照片协议完整分析报告.md「Apple Live Photo」章节，
    // 以及真样本（iPhone 16 Pro Max 直出的 HEIC/MOV 与 JPG/MOV）的 exiftool dump。
    //
    // 分工（图片端二进制重建在 AppleMakerNoteWriter，视频端 mebx 轨在
    // AppleLivePhotoMebxWriter；本类负责编排 + exiftool/ffmpeg 可写部分）：
    //
    //   图片端：
    //     Apple MakerNote（仅 ContentIdentifier，与最小样本 IMG_6675.JPG 对齐）→
    //       AppleMakerNoteWriter 在格式转换前注入源 JPG（SplitAsync 里调用），
    //       heif-enc 原样保留，故 JPG/HEIC 输出都带上。
    //     Make/Model → exiftool（本类）。
    //   视频端：
    //     ContentIdentifier / Make / Model / Software / CreationDate → ffmpeg
    //       -movflags use_metadata_tags 写 mdta keys（exiftool 无法在新建 MOV 上
    //       创建 ContentIdentifier，实测 "nothing changed"）。
    //       mdta key 名以真样本 keys atom dump 为准：
    //         com.apple.quicktime.content.identifier / .make / .model / .software / .creationdate
    //     StillImageTime=-1 + TrackDuration（封面帧位置）+ ContentDescribes=Track 1
    //       → AppleLivePhotoMebxWriter 追加 mebx 静态图像轨。
    //
    // 封面帧时间戳来源（ResolveCoverSecondsAsync，按优先级）：
    //   1. Google MotionPhoto V2 / OPPO / vivo / 三星：XMP MotionPhotoPresentationTimestampUs（微秒）
    //   2. Google MicroVideo V1：XMP MicroVideoPresentationTimestampUs（微秒）
    //   3. OPPO：XMP MotionPhotoPrimaryPresentationTimestampUs（原始拍摄帧，微秒）
    //   4. 华为/荣耀：嵌入 MP4 udta com.openharmony.covertime（毫秒字符串），
    //      兜底读文件尾 60 字节 v6_fXX + PPP:QQQQ（帧号:总帧数）× 视频时长
    //   5. 兜底：视频时长中点（协议文档允许，但非正确位置）
    //
    // 已知限制（见报告）：
    //   a. （已解决）HEIC 源图片端 MakerNote：AppleMakerNoteWriter.TryWriteContentIdentifier
    //      对 JPEG/HEIC 均可就地重建 Apple MakerNote（实测 HEIC 输出 CID 与视频一致）。
    //   b. mebx 轨的 sample 复用的是样本 still-image-transform 元数据，不是真实封面
    //      帧图像，故「封面帧时间戳」正确、「封面帧图像内容」仍待抽取/嵌入。
    // ═════════════════════════════════════════════════════════════════════════════
    public static class AppleLivePhotoMetadata
    {
        private const string AppleSoftwareVersion = "17.0.2"; // 对齐最小样本 IMG_6675 的 Software

        private static readonly Regex MotionPhotoTimestampRegex = new(
            @"MotionPhotoPresentationTimestampUs[""=\s]+(-?\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        // Google MicroVideo V1 的封面帧时间戳字段（旧版 Pixel/早期小米等）。
        private static readonly Regex MicroVideoTimestampRegex = new(
            @"MicroVideoPresentationTimestampUs[""=\s]+(-?\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        // OPPO 的"原始拍摄帧"时间戳字段（换封面后仍指向按下快门那一帧）。
        private static readonly Regex OppoPrimaryTimestampRegex = new(
            @"MotionPhotoPrimaryPresentationTimestampUs[""=\s]+(-?\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        // 华为/荣耀 60 字节尾部：v6_fXX / v2_fXX（封面帧序号，0-based）+ PPP:QQQQ
        // （真机文件 PPP:QQQQ 并非帧号:总帧数（如 700:1300 / 499:1000），仅作格式校验，
        // 换算秒数用帧号 ÷ 视频帧率，见 TryResolveHuaweiCoverSecondsAsync）。
        private static readonly Regex HuaweiTailFrameRegex = new(
            @"v[26]_f(?<frame>\d+)\s+(?<frame2>\d+):(?<total>\d+)",
            RegexOptions.Compiled | RegexOptions.CultureInvariant | RegexOptions.IgnoreCase,
            TimeSpan.FromSeconds(2));

        // 入口：给拆分输出的一对图片 + 视频写入 Apple Live Photo 配对元数据。
        // contentId：由 SplitAsync 在格式转换前生成并已注入图片端 MakerNote；
        // HEIC 源（无预生成）传 null，此处兜底生成（图片端 MakerNote 缺失，属已知限制）。
        public static async Task WritePairMetadataAsync(
            string sourcePath,
            string metadataText,
            string imageOutputPath,
            string videoOutputPath,
            string? contentId,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            // 1. 配对 UUID（图片与视频两端一致）。
            contentId ??= Guid.NewGuid().ToString("D").ToUpperInvariant();

            // 2. 封面帧时间戳（按源协议解析：V2 / V1 / OPPO / 华为；全无则视频中点）。
            double coverSeconds = await ResolveCoverSecondsAsync(sourcePath, metadataText, videoOutputPath, token);

            // 3. 视频端：按 ISO/IEC 14496-12 规范化重建 Apple Live Photo MOV
            //    （ftyp + moov(4 轨 + meta) + wide + 单 mdat）。所有容器 box 由结构化代码
            //    生成、字段按规范计算；编码参数（hvcC/avcC、esds、stts/ctts/stss/尺寸/声道/采样率）
            //    全部解析自 ffmpeg 输出，不依赖任何样本的 hex 模板。
            if (videoOutputPath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            {
                if (AppleLivePhotoMovBuilderV2.TryRebuild(videoOutputPath, contentId, "", coverSeconds, out string? movError))
                {
                    LogService.Split(
                        $"Apple[video] rebuilt MOV (spec-based, CID={contentId}, cover={coverSeconds:F4}s)",
                        LogLevel.Debug);
                }
                else
                {
                    LogService.Split(
                        $"Apple[video] spec-based MOV rebuild failed ({movError}), falling back to ffmpeg mdta + mebx patch",
                        LogLevel.Warning);
                    await WriteVideoMetadataAsync(videoOutputPath, contentId, token);
                    string? mebxError = null;
                    bool mebxOk = coverSeconds > 0 &&
                        AppleLivePhotoMebxWriter.TryAppendStillImageTrack(videoOutputPath, coverSeconds, out mebxError);
                    if (mebxOk)
                    {
                        LogService.Split($"Apple[cover] fallback appended still-image track → {coverSeconds:F4}s", LogLevel.Debug);
                    }
                    else if (coverSeconds > 0)
                    {
                        LogService.Split($"Apple[cover] fallback still-image track append failed: {mebxError}", LogLevel.Warning);
                    }
                }
            }
            else
            {
                await WriteVideoMetadataAsync(videoOutputPath, contentId, token);
            }
        }

        // ── 视频端：ffmpeg -movflags use_metadata_tags 写 mdta keys ─────────
        // exiftool 无法在新建 MOV/MP4 上创建 ContentIdentifier，改用 ffmpeg 打进容器。
        private static async Task WriteVideoMetadataAsync(
            string videoOutputPath, string contentId, CancellationToken token)
        {
            string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                LogService.Split("Apple[video] ffmpeg not found; ContentIdentifier cannot be written", LogLevel.Warning);
                return;
            }

            string? dir = Path.GetDirectoryName(videoOutputPath);
            if (string.IsNullOrEmpty(dir)) dir = ".";
            string tempPath = Path.Combine(
                dir, $".lpb_apple_{Guid.NewGuid():N}{Path.GetExtension(videoOutputPath)}");

            try
            {
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                psi.ArgumentList.Add("-y");
                psi.ArgumentList.Add("-i");
                psi.ArgumentList.Add(videoOutputPath);
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("0:v:0");
                psi.ArgumentList.Add("-map");
                psi.ArgumentList.Add("0:a:0?");
                psi.ArgumentList.Add("-c");
                psi.ArgumentList.Add("copy");
                // 丢弃输入侧全局元数据（CopyMetadataToVideoAsync 已写入源 Make/Model 等），
                // 否则 ffmpeg 会把源键 + 我们的 Apple 键同时写进 mdta，产生重复 Make/Model。
                psi.ArgumentList.Add("-map_metadata");
                psi.ArgumentList.Add("-1");
                psi.ArgumentList.Add("-fflags");
                psi.ArgumentList.Add("+bitexact"); // 去掉 Encoder=Lavf 指纹（对齐最小样本）
                psi.ArgumentList.Add("-brand");
                psi.ArgumentList.Add("qt"); // ftyp major brand = qt（对齐最小样本）
                psi.ArgumentList.Add("-movflags");
                psi.ArgumentList.Add("+faststart+use_metadata_tags"); // moov 前置（fast-start）+ mdta keys
                psi.ArgumentList.Add("-metadata");
                psi.ArgumentList.Add($"com.apple.quicktime.content.identifier={contentId}");
                // 对齐最小样本 IMG_6675 的 keys：Software=17.0.2 + CreationDate（当前时间 ISO8601）。
                // 不写 LivePhotoAuto（那是实拍标记，软件生成的 Live Photo 不该有）。
                psi.ArgumentList.Add("-metadata");
                psi.ArgumentList.Add($"com.apple.quicktime.software={AppleSoftwareVersion}");
                var now = DateTimeOffset.Now;
                string creationDate = now.ToString("yyyy-MM-dd'T'HH:mm:ss") + now.ToString("zzz").Replace(":", "");
                psi.ArgumentList.Add("-metadata");
                psi.ArgumentList.Add($"com.apple.quicktime.creationdate={creationDate}");
                psi.ArgumentList.Add(tempPath);

                using var process = Process.Start(psi);
                if (process == null)
                {
                    LogService.Split("Apple[video] ffmpeg failed to start", LogLevel.Warning);
                    return;
                }

                string error = await process.StandardError.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);

                if (process.ExitCode != 0)
                {
                    LogService.Split(
                        $"Apple[video] ffmpeg metadata write failed (exit {process.ExitCode}): {error.Trim()}",
                        LogLevel.Warning);
                    return;
                }

                // 原子替换：仅在 ffmpeg 成功后覆盖原视频。
                File.Delete(videoOutputPath);
                File.Move(tempPath, videoOutputPath);

                LogService.Split(
                    $"Apple[video] ContentIdentifier={contentId}, Software={AppleSoftwareVersion}, CreationDate={creationDate}",
                    LogLevel.Debug);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Split($"Apple[video] ffmpeg metadata write failed: {ex.Message}", LogLevel.Warning);
            }
            finally
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { /* best-effort */ }
            }
        }

        // ── 封面帧时间戳 ────────────────────────────────────────────────────
        // 源单文件的封面帧时间戳 → 秒。不同协议字段不同：
        //   V2 系：MotionPhotoPresentationTimestampUs（微秒）
        //   V1 系：MicroVideoPresentationTimestampUs（微秒）
        //   OPPO：MotionPhotoPrimaryPresentationTimestampUs（原始拍摄帧，微秒）
        //   华为：com.openharmony.covertime（毫秒）/ 尾部 v6_fXX + PPP:QQQQ
        // 全无则用视频时长的中点兜底（参照协议文档）。
        private static async Task<double> ResolveCoverSecondsAsync(
            string sourcePath, string metadataText, string videoOutputPath, CancellationToken token)
        {
            // 1. V2 当前封面帧（Google V2 / OPPO / vivo / 三星）。
            if (TryMatchPresentationUs(MotionPhotoTimestampRegex, metadataText, out double seconds))
            {
                return seconds;
            }

            // 2. V1 MicroVideo 封面帧。
            if (TryMatchPresentationUs(MicroVideoTimestampRegex, metadataText, out seconds))
            {
                return seconds;
            }

            // 3. OPPO 原始拍摄帧（当前封面字段缺失时）。
            if (TryMatchPresentationUs(OppoPrimaryTimestampRegex, metadataText, out seconds))
            {
                return seconds;
            }

            // 4. 华为/荣耀：嵌入 MP4 的 com.openharmony.covertime，或尾部 v6_fXX + PPP:QQQQ。
            double? huaweiSeconds = await TryResolveHuaweiCoverSecondsAsync(sourcePath, videoOutputPath, token);
            if (huaweiSeconds.HasValue && huaweiSeconds.Value > 0)
            {
                return huaweiSeconds.Value;
            }

            // 5. 兜底：视频时长中点。
            double? duration = await ReadVideoDurationSecondsAsync(videoOutputPath, token);
            if (duration.HasValue && duration.Value > 0)
            {
                seconds = duration.Value / 2.0;
                LogService.Split($"Apple[cover] no source timestamp, using video midpoint: {seconds:F4}s (duration={duration.Value:F4}s)", LogLevel.Debug);
                return seconds;
            }

            LogService.Split("Apple[cover] could not resolve cover timestamp (no source field, no duration)", LogLevel.Warning);
            return 0;
        }

        // 从 XMP 文本匹配「xxxPresentationTimestampUs」字段（微秒）→ 秒。
        private static bool TryMatchPresentationUs(Regex regex, string? metadataText, out double seconds)
        {
            seconds = 0;
            var m = regex.Match(metadataText ?? "");
            if (m.Success &&
                long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) &&
                us > 0)
            {
                seconds = us / 1_000_000.0;
                LogService.Split($"Apple[cover] source {m.Value} → {seconds:F4}s", LogLevel.Debug);
                return true;
            }
            return false;
        }

        // ── 华为/荣耀封面帧时间戳 ──────────────────────────────────────────
        // 华为单文件不使用 XMP：封面时间在嵌入 MP4 的 udta 元数据
        // com.openharmony.covertime（毫秒字符串，如 "1500.000000"），
        // 或文件尾 60 字节的 v6_fXX + PPP:QQQQ（帧号:总帧数，旧版相册）。
        private static async Task<double?> TryResolveHuaweiCoverSecondsAsync(
            string sourcePath, string videoOutputPath, CancellationToken token)
        {
            // a. 优先读 com.openharmony.covertime（新版 HarmonyOS 相册权威字段）。
            if (TryReadHuaweiCovertimeMilliseconds(sourcePath, out double ms))
            {
                double seconds = ms / 1000.0;
                LogService.Split($"Apple[cover] huawei com.openharmony.covertime={ms:F0}ms → {seconds:F4}s", LogLevel.Debug);
                return seconds;
            }

            // b. 兜底：尾部 v6_fXX / v2_fXX → 帧号 ÷ 视频帧率（旧版相册字段）。
            //    真机文件的 PPP:QQQQ 不是"帧号:总帧数"（如 700:1300），不能按比例换算。
            if (TryReadHuaweiTailFrame(sourcePath, out int frame, out _) && frame >= 0)
            {
                double? duration = await ReadVideoDurationSecondsAsync(videoOutputPath, token);
                double fps = await ReadVideoFrameRateAsync(videoOutputPath, token) ?? 30.0;
                if (duration.HasValue && duration.Value > 0 && fps > 0)
                {
                    double seconds = frame / fps;
                    if (seconds > duration.Value)
                    {
                        // 帧号换算越界（异常尾部）→ 退回视频中点。
                        seconds = duration.Value / 2.0;
                    }
                    LogService.Split($"Apple[cover] huawei tail frame={frame} @ {fps:F2}fps → {seconds:F4}s (dur={duration.Value:F4}s)", LogLevel.Debug);
                    return seconds;
                }
            }
            return null;
        }

        // 在源文件尾部区域内按 ISO BMFF 结构解析 com.openharmony.covertime 的值（毫秒）。
        // 华为文件的嵌入 MP4 位于文件尾部（moov 在 mp4 末尾、60 字节尾部之前），
        // 只扫描最后 8MB 足够且避免大文件全量读入内存。
        // 结构：moov/meta/keys 里第 N 个键名 + moov/meta/ilst 里第 N 个条目（index=N）
        // 的 data box 载荷。真机华为：type=0x17（float32，大端，毫秒，如 44A28B44→1300.35ms）；
        // 本软件合成：type=1（UTF-8 毫秒字符串，如 "1500.000000"）。
        private static bool TryReadHuaweiCovertimeMilliseconds(string sourcePath, out double milliseconds)
        {
            milliseconds = 0;
            try
            {
                const int TailWindowBytes = 8 * 1024 * 1024;
                using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long fileSize = fs.Length;
                if (fileSize < 4096) return false;
                int readLen = (int)Math.Min(fileSize, TailWindowBytes);
                byte[] data = new byte[readLen];
                fs.Seek(fileSize - readLen, SeekOrigin.Begin);
                int got = fs.Read(data, 0, readLen);
                if (got < 16) return false;

                string key = "com.openharmony.covertime";
                byte[] keyBytes = Encoding.UTF8.GetBytes(key);
                int keyPos = IndexOfBytes(data, got, keyBytes);
                if (keyPos < 0) return false;

                // 1. 从键名向前找到所属 keys box 起点（size + 'keys'）。
                int keysStart = FindBoxStartBackward(data, got, keyPos, "keys");
                if (keysStart < 0) return false;

                // 2. 数出该键在 keys 里的序号（1-based）：entry = [size]['mdta'][namespace(4)][name\0]。
                int entryPos = keysStart + 16; // 跳过 box 头(8) + count(4) + 首条 size 字段
                int keyIndex = -1;
                int entryOrdinal = 0;
                while (entryPos + 8 <= got)
                {
                    int entrySize = ReadBe32(data, entryPos);
                    if (entrySize < 16 || entryPos + entrySize > got) break;
                    entryOrdinal++;
                    if (keyPos < entryPos + entrySize)
                    {
                        keyIndex = entryOrdinal;
                        break;
                    }
                    entryPos += entrySize;
                }
                if (keyIndex <= 0) return false;

                // 3. keys box 之后找 ilst box，按其条目顺序取第 keyIndex 个条目。
                int keysSize = ReadBe32(data, keysStart);
                int ilstStart = FindBoxStartForward(data, got, keysStart + keysSize, "ilst");
                if (ilstStart < 0) return false;

                int itemPos = ilstStart + 8;
                int itemOrdinal = 0;
                while (itemPos + 8 <= got)
                {
                    int itemSize = ReadBe32(data, itemPos);
                    if (itemSize < 16 || itemPos + itemSize > got) break;
                    itemOrdinal++;
                    if (itemOrdinal == keyIndex)
                    {
                        // 4. 条目内找 data box：条目头 [size][index(4)]，其后是子 box。
                        int itemEnd = itemPos + itemSize;
                        int childPos = itemPos + 8;
                        while (childPos + 8 <= itemEnd)
                        {
                            int childSize = ReadBe32(data, childPos);
                            if (childSize < 16 || childPos + childSize > itemEnd) break;
                            if (data[childPos + 4] == (byte)'d' &&
                                data[childPos + 5] == (byte)'a' &&
                                data[childPos + 6] == (byte)'t' &&
                                data[childPos + 7] == (byte)'a')
                            {
                                // data box 载荷 = [type(4)][locale(4)][值]。
                                int dataType = ReadBe32(data, childPos + 8);
                                int valueStart = childPos + 16;
                                int valueLen = childSize - 16;
                                if (dataType == 0x17 && valueLen == 4)
                                {
                                    // IEEE-754 大端 float32（毫秒）。
                                    int bits = ReadBe32(data, valueStart);
                                    float ms = BitConverter.Int32BitsToSingle(bits);
                                    if (ms > 0)
                                    {
                                        milliseconds = ms;
                                        return true;
                                    }
                                }
                                else if (valueLen > 0 && valueLen <= 32)
                                {
                                    string value = Encoding.UTF8.GetString(data, valueStart, valueLen).TrimEnd('\0', ' ');
                                    if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out double ms) && ms > 0)
                                    {
                                        milliseconds = ms;
                                        return true;
                                    }
                                }
                            }
                            childPos += childSize;
                        }
                    }
                    itemPos += itemSize;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* 非华为源或解析失败 → 交给后续兜底 */ }
            return false;
        }

        // 从 pos 向前（最多 1KB）找 type 为指定四字符的 box 起点（size 字段位置）。
        private static int FindBoxStartBackward(byte[] data, int length, int pos, string type)
        {
            int searchFrom = Math.Max(0, pos - 1024);
            for (int i = pos - 4; i >= searchFrom; i--)
            {
                if (data[i] == (byte)type[0] && data[i + 1] == (byte)type[1] &&
                    data[i + 2] == (byte)type[2] && data[i + 3] == (byte)type[3])
                {
                    int boxStart = i - 4;
                    if (boxStart >= 0)
                    {
                        int size = ReadBe32(data, boxStart);
                        if (size >= 16 && boxStart + size <= length && boxStart + size > pos)
                            return boxStart;
                    }
                }
            }
            return -1;
        }

        // 从 pos 向后找 type 为指定四字符的 box 起点（size 字段位置），最多扫 4KB。
        private static int FindBoxStartForward(byte[] data, int length, int pos, string type)
        {
            int limit = Math.Min(length - 4, pos + 4096);
            for (int i = pos; i <= limit; i++)
            {
                if (data[i] == (byte)type[0] && data[i + 1] == (byte)type[1] &&
                    data[i + 2] == (byte)type[2] && data[i + 3] == (byte)type[3])
                {
                    int boxStart = i - 4;
                    if (boxStart >= 0)
                    {
                        int size = ReadBe32(data, boxStart);
                        if (size >= 16 && boxStart + size <= length)
                            return boxStart;
                    }
                }
            }
            return -1;
        }

        private static int ReadBe32(byte[] data, int offset)
            => (data[offset] << 24) | (data[offset + 1] << 16) | (data[offset + 2] << 8) | data[offset + 3];

        // 解析华为/荣耀文件尾 60 字节：v6_fXX + PPP:QQQQ（帧号:总帧数）。
        private static bool TryReadHuaweiTailFrame(string sourcePath, out int frame, out int total)
        {
            frame = -1;
            total = 0;
            try
            {
                using var fs = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                long fileSize = fs.Length;
                if (fileSize < 64) return false;
                byte[] tail = new byte[64];
                fs.Seek(fileSize - 64, SeekOrigin.Begin);
                int got = fs.Read(tail, 0, 64);
                string text = Encoding.ASCII.GetString(tail, 0, got);
                var m = HuaweiTailFrameRegex.Match(text);
                if (m.Success &&
                    int.TryParse(m.Groups["frame"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out frame) &&
                    int.TryParse(m.Groups["total"].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out total))
                {
                    return true;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { }
            return false;
        }

        private static int IndexOfBytes(byte[] data, int length, byte[] pattern)
        {
            if (pattern.Length == 0 || length < pattern.Length) return -1;
            for (int i = 0; i <= length - pattern.Length; i++)
            {
                bool match = true;
                for (int j = 0; j < pattern.Length; j++)
                {
                    if (data[i + j] != pattern[j]) { match = false; break; }
                }
                if (match) return i;
            }
            return -1;
        }

        // ── 读取辅助 ────────────────────────────────────────────────────────

        // 读取视频时长（秒），用于封面帧中点兜底。优先 MediaDuration，回退 Duration。
        private static async Task<double?> ReadVideoDurationSecondsAsync(string videoOutputPath, CancellationToken token)
        {
            try
            {
                // JsonDocument 必须在访问完 RootElement 之后才释放（曾在此处把 RootElement
                // 带出 using 作用域，调用方访问时抛 ObjectDisposedException，导致中点兜底
                // 永远失败、封面时间戳退化为 0）。
                using var doc = await ReadExifToolJsonAsync(videoOutputPath, token, "-n", "-MediaDuration", "-Duration");
                if (doc == null ||
                    doc.RootElement.ValueKind != JsonValueKind.Array ||
                    doc.RootElement.GetArrayLength() == 0)
                {
                    return null;
                }

                var root = doc.RootElement[0];
                if (TryGetJsonNumber(root, "MediaDuration", out double media) && media > 0)
                    return media;
                if (TryGetJsonNumber(root, "Duration", out double dur) && dur > 0)
                    return dur;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Split($"Apple[cover] read video duration failed: {ex.Message}", LogLevel.Debug);
            }
            return null;
        }

        // 读取视频帧率（fps），用于华为/荣耀尾部帧号 → 秒的换算；读不到返回 null。
        private static async Task<double?> ReadVideoFrameRateAsync(string videoOutputPath, CancellationToken token)
        {
            try
            {
                using var doc = await ReadExifToolJsonAsync(videoOutputPath, token, "-n", "-VideoFrameRate");
                if (doc == null ||
                    doc.RootElement.ValueKind != JsonValueKind.Array ||
                    doc.RootElement.GetArrayLength() == 0)
                {
                    return null;
                }

                if (TryGetJsonNumber(doc.RootElement[0], "VideoFrameRate", out double fps) && fps > 0)
                {
                    return fps;
                }
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Split($"Apple[cover] read video frame rate failed: {ex.Message}", LogLevel.Debug);
            }
            return null;
        }

        // 运行 exiftool -j 读取 JSON（一次性模式），返回解析后的 JsonDocument。
        // 注意：返回的是 doc 本身（不是 RootElement），调用方负责 using 释放，
        // 且必须在释放前完成所有 JsonElement 访问——RootElement 脱离 doc 即失效。
        private static async Task<JsonDocument?> ReadExifToolJsonAsync(
            string filePath, CancellationToken token, params string[] tags)
        {
            string? exifToolPath = ExternalToolLocator.FindExifTool();
            if (string.IsNullOrEmpty(exifToolPath) || !File.Exists(exifToolPath))
                throw new InvalidOperationException("exiftool not found");

            var psi = new ProcessStartInfo
            {
                FileName = exifToolPath,
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true
            };
            psi.ArgumentList.Add("-j");
            foreach (var tag in tags) psi.ArgumentList.Add(tag);
            psi.ArgumentList.Add(filePath);

            string stdout;
            using (var process = Process.Start(psi))
            {
                if (process == null) throw new InvalidOperationException("exiftool failed to start");
                stdout = await process.StandardOutput.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);
            }

            if (string.IsNullOrWhiteSpace(stdout) || !stdout.TrimStart().StartsWith("["))
                return null;

            return JsonDocument.Parse(stdout);
        }

        private static string TryGetJsonString(JsonElement element, string propertyName)
        {
            if (element.TryGetProperty(propertyName, out var prop) && prop.ValueKind == JsonValueKind.String)
                return prop.GetString() ?? "";
            return "";
        }

        private static bool TryGetJsonNumber(JsonElement element, string propertyName, out double value)
        {
            value = 0;
            if (element.TryGetProperty(propertyName, out var prop) &&
                prop.ValueKind == JsonValueKind.Number &&
                prop.TryGetDouble(out double v))
            {
                value = v;
                return true;
            }
            // 部分版本 exiftool -j 仍返回字符串（如 "2.90 s"），再剥数字兜底。
            if (element.TryGetProperty(propertyName, out var prop2) &&
                prop2.ValueKind == JsonValueKind.String)
            {
                var m = Regex.Match(prop2.GetString() ?? "", @"([\d.]+)");
                if (m.Success && double.TryParse(m.Groups[1].Value, NumberStyles.Any, CultureInfo.InvariantCulture, out double v2))
                {
                    value = v2;
                    return true;
                }
            }
            return false;
        }
    }
}
