using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;
using LivePhotoBox.Services.Protocols;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.Services
{
    // Live photo merge (composition) service.
    // Combines a still image and a video into a platform-compliant live photo file.
    // Core method <see cref="WriteLivePhotoAsync"/> builds XMP metadata per protocol,
    // then delegates to either the JPEG writer (<see cref="WriteNativeAsync"/> — SOI +
    // APP1 XMP segment) or the HEIC writer (<see cref="WriteHeicNativeAsync"/> —
    // exiftool XMP injection into ISOBMFF meta box + mpvd box with video).
    // XMP already contains the LivePhotoBox namespace marker (injected by WrapXmp),
    // so no post-write exiftool dc:subject patch is needed.
    public static class LivePhotoMergeService
    {
        // Generate the live photo output filename.
        // Returns ".heic" when the source image is HEIC and the selected protocol is
        // Motion Photo V2 (which supports native HEIC primary images per Google spec).
        // baseName: Filename base (without extension).
        // selectedModeIndex: Protocol index.
        // sourceImagePath: Source image path — used to detect HEIC input.
        // outputFormatIndex: User-selected output format (0=JPG+MP4, 1=JPG+MOV, 2=HEIC+MP4, 3=HEIC+MOV).
        // namingRuleIndex: 0=keep original name, 1=append protocol suffix, 2=custom template.
        // customPattern: Template string with {name}/{protocol}/{date}/{time}/{counter} tokens (used when namingRuleIndex==2).
        // taskIndex: 1-based task index for {counter} token (used when namingRuleIndex==2).
        public static string CreateOutputFileName(string baseName, int selectedModeIndex,
            string? sourceImagePath = null, int outputFormatIndex = 0, int namingRuleIndex = 0,
            string? customPattern = null, int? taskIndex = null)
        {
            string name;

            if (namingRuleIndex == 2 && !string.IsNullOrWhiteSpace(customPattern))
            {
                // 自定义模板渲染
                name = RenderNamingTemplate(customPattern, baseName, selectedModeIndex, taskIndex ?? 1, sourceImagePath);
            }
            else
            {
                // 协议后缀（命名规则 = 添加协议后缀时使用）
                string? protocolSuffix = namingRuleIndex == 1
                    ? GetProtocolSuffixName(selectedModeIndex)
                    : null;

                name = protocolSuffix != null ? baseName + protocolSuffix : baseName;
            }

            // 清理首尾冗余分隔符 + 非法文件名字符
            name = SanitizeFileName(name).Trim('_', '-', ' ', '+');

            // HEIC 输出：需 HEIC 源 + V2/Huawei 协议 + 用户选了 HEIC 格式
            bool wantHeic = outputFormatIndex is 2 or 3
                or ProtocolFormatMatrix.FormatHeicMp4H265;
            if (wantHeic && sourceImagePath != null
                && HeicConverterService.IsHeicFile(sourceImagePath)
                && LivePhotoProtocol.FromIndex(selectedModeIndex) is MotionPhotoV2Protocol or HuaweiMovingPhotoProtocol)
            {
                return $"{name}.heic";
            }
            return $"{name}.jpg";
        }

        // ── 命名模板引擎 ──────────────────────────────────────────────────

        // Token 匹配正则: {name}, {protocol}, {date}, {date:format}, {time}, {time:format}, {counter}, {counter:format}
        private static readonly Regex NamingTokenRegex = new(
            @"\{(\w+)(?::([^}]+))?\}",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 非法文件名字符正则
        private static readonly Regex IllegalFileNameChars = new(
            @"[\\/:*?""<>|]",
            RegexOptions.Compiled | RegexOptions.CultureInvariant);

        // 清理文件名中的非法字符。
        private static string SanitizeFileName(string name)
        {
            return IllegalFileNameChars.Replace(name, "_");
        }

        // 协议后缀名称 —— 命名规则(suffix)、{protocol} token、CLI 批量输出文件夹名共用同一映射。
        // 已知协议返回小写后缀名；未知/越界索引返回 null（不追加后缀）。
        public static string? GetProtocolSuffixName(int protocolIndex) => protocolIndex switch
        {
            0 => "fusion",
            1 => "microvideo",
            2 => "motionphoto",
            3 => "oppo",
            4 => "vivo",
            5 => "samsung",
            6 => "huawei",
            _ => null
        };

        // 将命名模板字符串渲染为实际文件名。
        // template: 含 token 的模板，如 "{name}_{protocol}" 或 "LivePhoto_{date}_{counter:D3}"
        // baseName: 原文件基本名（不含扩展名）
        // protocolIndex: 协议索引
        // taskIndex: 任务序号（1-based，用于 counter token）
        // sourceImagePath: 原图片路径（用于 {exif_date}/{exif_time} 读取文件时间）
        public static string RenderNamingTemplate(string template, string baseName, int protocolIndex, int taskIndex,
            string? sourceImagePath = null, int frameNumber = 0)
        {
            // 解析拍摄日期/时间（懒加载，仅在需要时读取）
            DateTime? _captureTime = null;
            DateTime GetCaptureTime()
            {
                if (_captureTime == null && !string.IsNullOrEmpty(sourceImagePath) && File.Exists(sourceImagePath))
                    _captureTime = File.GetLastWriteTime(sourceImagePath);
                return _captureTime ?? DateTime.Now;
            }

            return NamingTokenRegex.Replace(template, match =>
            {
                string token = match.Groups[1].Value;
                string? format = match.Groups[2].Success ? match.Groups[2].Value : null;

                return token switch
                {
                    "name" => baseName,
                    "protocol" => GetProtocolSuffixName(protocolIndex) ?? "",
                    "date" => DateTime.Now.ToString(format ?? "yyyyMMdd"),
                    "time" => DateTime.Now.ToString(format ?? "HHmmss"),
                    "exif_date" => GetCaptureTime().ToString(format ?? "yyyyMMdd"),
                    "exif_time" => GetCaptureTime().ToString(format ?? "HHmmss"),
                    "counter" => format != null ? taskIndex.ToString(format) : taskIndex.ToString(),
                    "frame" => format != null ? frameNumber.ToString(format) : frameNumber.ToString(),
                    _ => match.Value, // 未知 token：保留原样
                };
            });
        }

        // 将模板字符串解析为 NamingSegment 列表（用于从已保存模板恢复 UI 状态）。
        public static List<NamingSegment> ParseNamingPattern(string template)
        {
            var segments = new List<NamingSegment>();

            if (string.IsNullOrWhiteSpace(template))
            {
                segments.Add(new NamingSegment(NamingSegmentType.OriginalName));
                return segments;
            }

            int lastIndex = 0;
            foreach (Match match in NamingTokenRegex.Matches(template))
            {
                // 在 token 之前的纯文本作为 Literal segment
                if (match.Index > lastIndex)
                {
                    string literal = template.Substring(lastIndex, match.Index - lastIndex);
                    if (!string.IsNullOrEmpty(literal))
                        segments.Add(new NamingSegment(NamingSegmentType.Literal, literal));
                }

                string token = match.Groups[1].Value;
                string? format = match.Groups[2].Success ? match.Groups[2].Value : null;

                segments.Add(token switch
                {
                    "name" => new NamingSegment(NamingSegmentType.OriginalName),
                    "protocol" => new NamingSegment(NamingSegmentType.Protocol),
                    "date" => new NamingSegment(NamingSegmentType.Date, format ?? "yyyyMMdd"),
                    "time" => new NamingSegment(NamingSegmentType.Time, format ?? "HHmmss"),
                    "exif_date" => new NamingSegment(NamingSegmentType.ExifDate, format ?? "yyyyMMdd"),
                    "exif_time" => new NamingSegment(NamingSegmentType.ExifTime, format ?? "HHmmss"),
                    "counter" => new NamingSegment(NamingSegmentType.Counter, format ?? "D3"),
                    _ => new NamingSegment(NamingSegmentType.Literal, match.Value),
                });

                lastIndex = match.Index + match.Length;
            }

            // 模板末尾剩余文本
            if (lastIndex < template.Length)
            {
                string trailing = template.Substring(lastIndex);
                if (!string.IsNullOrEmpty(trailing))
                    segments.Add(new NamingSegment(NamingSegmentType.Literal, trailing));
            }

            // 空模板 → 默认添加原文件名 segment
            if (segments.Count == 0)
                segments.Add(new NamingSegment(NamingSegmentType.OriginalName));

            return segments;
        }

        // Combine an image and video into a live photo file.
        // Detects HEIC input → dispatches to the HEIC-native writer when the protocol
        // is Motion Photo V2; otherwise falls back to the JPEG writer.
        // XMP already contains the LivePhotoBox namespace marker (WrapXmp), so no
        // post-write exiftool dc:subject patch is needed.
        // sourceImg: Source image path (JPEG or HEIC).
        // sourceVid: Source video path (MOV or MP4) — already in the target container
        //            by the time this is called (caller handles transcode/remux).
        // targetPath: Output file path.
        // selectedModeIndex: Protocol index.
        // token: Cancellation token.
        public static async Task WriteLivePhotoAsync(
            string sourceImg,
            string sourceVid,
            string targetPath,
            int selectedModeIndex,
            CancellationToken token,
            long presentationTimestampUs = 0,
            int outputFormatIndex = 0)
        {
            var protocol = LivePhotoProtocol.FromIndex(selectedModeIndex);
            long videoSize = new FileInfo(sourceVid).Length;

            // ── Samsung HEIC path (mpvd + sefd box with Samsung Trailer) ──
            // 仅在用户明确选择 HEIC 输出（格式 2）时走 HEIC；请求 jpg+* 时源图
            // 已被 runner 转成 JPEG，这里加格式门控作为防御（P1-2）。
            if (HeicConverterService.IsHeicFile(sourceImg)
                && protocol is SamsungMotionPhotoProtocol samHeic
                && outputFormatIndex == ProtocolFormatMatrix.FormatHeicMp4)
            {
                string videoMime = DetectVideoMime(sourceVid);
                await WriteSamsungHeicAsync(sourceImg, sourceVid, targetPath, videoSize, videoMime, samHeic, token, presentationTimestampUs);
                return;
            }

            // ── Samsung JPEG path (V2 XMP + Samsung Trailer with embedded video) ──
            if (protocol is SamsungMotionPhotoProtocol samJpeg)
            {
                string videoMime = DetectVideoMime(sourceVid);
                await WriteSamsungJpegAsync(sourceImg, sourceVid, targetPath, videoSize, videoMime, samJpeg, token, presentationTimestampUs);
                return;
            }

            // ── HEIC native path (Motion Photo V2 with HEIC primary) ──
            // 仅在用户选择 HEIC 输出（格式 2/3）时走 HEIC 原生路径（P1-2 防御性门控）。
            if (HeicConverterService.IsHeicFile(sourceImg)
                && protocol is MotionPhotoV2Protocol v2
                && outputFormatIndex is ProtocolFormatMatrix.FormatHeicMp4 or ProtocolFormatMatrix.FormatHeicMov)
            {
                string videoMime = DetectVideoMime(sourceVid);
                byte[] xmpBytes = v2.BuildXmpMetadata(videoSize, presentationTimestampUs, "image/heic", "8", videoMime);
                await WriteHeicNativeAsync(sourceImg, sourceVid, targetPath, xmpBytes, token);
                return;
            }

            // ── HUAWEI native path (no XMP, uses LIVE_ tail marker) ──
            if (protocol is HuaweiMovingPhotoProtocol)
            {
                // HEIC+H.265 format: force HEIC output regardless of source format
                // 其余情况严格按用户请求（P1-2）：heic+mp4（2）/heic+mp4-h265（4）→ HEIC；
                // jpg+mp4（0）→ JPEG（源图已被 runner 转成 JPEG）。
                bool isHeicOutput = outputFormatIndex == ProtocolFormatMatrix.FormatHeicMp4H265
                    || (outputFormatIndex is ProtocolFormatMatrix.FormatHeicMp4 or ProtocolFormatMatrix.FormatHeicMov
                        && HeicConverterService.IsHeicFile(sourceImg));
                // Pass raw presentation timestamp (microseconds) — WriteHuaweiNativeAsync
                // converts it to a frame number using the actual video FPS.
                await WriteHuaweiNativeAsync(sourceImg, sourceVid, targetPath,
                    isHeicOutput, presentationTimestampUs, token);
                return;
            }

            // ── JPEG path (all protocols) ──
            // For Motion Photo V2 and its subclasses (vivo), use the
            // actual video container MIME so the XMP matches the appended data.
            // For other protocols (V1, OPPO), keep the default behaviour.
            byte[] jpegXmpBytes;
            if (protocol is VivoLivePhotoProtocol vivoProtocol)
            {
                // vivo X300+ needs the 3-item Ultra HDR XMP (Primary/GainMap/MotionPhoto)
                // with the real gain-map byte length. When the source image carries an
                // embedded gain map (Ultra HDR JPEG), WriteNativeAsync preserves it in the
                // output; MeasureGainMapLength reports its size for the XMP item.
                string videoMime = DetectVideoMime(sourceVid);
                long gainMapLength = MeasureGainMapLength(sourceImg);
                jpegXmpBytes = vivoProtocol.BuildXmpMetadata(
                    videoSize, presentationTimestampUs, gainMapLength, "image/jpeg", videoMime);
            }
            else if (protocol is MotionPhotoV2Protocol v2jpeg)
            {
                string videoMime = DetectVideoMime(sourceVid);
                jpegXmpBytes = v2jpeg.BuildXmpMetadata(videoSize, presentationTimestampUs, "image/jpeg", "0", videoMime);
            }
            else
            {
                jpegXmpBytes = protocol.BuildXmpMetadata(videoSize, presentationTimestampUs);
            }
            await WriteNativeAsync(sourceImg, sourceVid, targetPath, jpegXmpBytes, token);
        }

        // Detect the MIME type for a video file based on its container format.
        // Checks the file extension first, then falls back to the ISOBMFF ftyp box.
        // Returns "video/quicktime" for MOV, "video/mp4" for MP4 and unknown formats.
        private static string DetectVideoMime(string videoPath)
        {
            // Fast path: extension-based detection (caller already ensured correct container)
            string ext = Path.GetExtension(videoPath).ToLowerInvariant();
            if (ext == ".mov") return "video/quicktime";
            if (ext == ".mp4" || ext == ".m4v") return "video/mp4";

            // Slow path: read ftyp box for files with non-standard extensions
            try
            {
                using var fs = new FileStream(
                    videoPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 16, options: FileOptions.SequentialScan);
                byte[] header = new byte[12];
                if (fs.Read(header, 0, 12) >= 12)
                {
                    string majorBrand = Encoding.ASCII.GetString(header, 8, 4);
                    if (majorBrand.StartsWith("qt", StringComparison.OrdinalIgnoreCase))
                        return "video/quicktime";
                }
            }
            catch { /* best-effort — fall through to default */ }

            return "video/mp4";
        }

        // Measure the byte length of the Ultra HDR gain map appended after the
        // primary JPEG's EOI. Returns 0 when the source image is a plain JPEG
        // (no embedded gain map) or when the trailing bytes are not another JPEG
        // (e.g. re-merging an existing live photo whose trailing bytes are video).
        // Used by the vivo protocol to populate the GainMap Container:Item.
        private static long MeasureGainMapLength(string imagePath)
        {
            try
            {
                using var fs = new FileStream(
                    imagePath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 4096, options: FileOptions.SequentialScan);

                // Check SOI
                if (fs.ReadByte() != 0xFF || fs.ReadByte() != 0xD8)
                    return 0;

                var buf = new byte[2];
                while (true)
                {
                    // Find the next marker (0xFF)
                    int b;
                    while ((b = fs.ReadByte()) != 0xFF)
                    {
                        if (b < 0) return 0;
                    }

                    // Skip padding 0xFF bytes
                    while ((b = fs.ReadByte()) == 0xFF) { }
                    if (b < 0) return 0;

                    // EOI — primary image end
                    if (b == 0xD9)
                    {
                        long trailing = fs.Length - fs.Position;
                        // Only report a gain map when the trailing bytes are another
                        // JPEG (Ultra HDR gain map). A trailing MP4 (from re-merging an
                        // existing live photo) is not a gain map.
                        if (trailing >= 2)
                        {
                            int peek0 = fs.ReadByte();
                            int peek1 = fs.ReadByte();
                            if (peek0 != 0xFF || peek1 != 0xD8)
                                return 0;
                        }
                        return trailing;
                    }

                    // Escaped 0xFF (0xFF 0x00) inside entropy data
                    if (b == 0x00)
                        continue;

                    // Restart markers (0xFF 0xD0–0xD7) — no length segment
                    if (b >= 0xD0 && b <= 0xD7)
                        continue;

                    // All other markers: read 2-byte length and skip the segment
                    int n = fs.Read(buf, 0, 2);
                    if (n < 2) return 0;
                    int segLen = (buf[0] << 8) | buf[1];
                    if (segLen > 2)
                    {
                        fs.Seek(segLen - 2, SeekOrigin.Current);
                    }
                }
            }
            catch
            {
                // Best-effort — a plain JPEG (or unreadable file) yields 0.
                return 0;
            }
        }

        // ── HUAWEI native writer ────────────────────────────────────────

        // Write a HUAWEI Moving Photo file in the SDK simplified format:
        //   [still image (JPEG or HEIC)] + [MP4 video] + [60-byte LIVE_ tail]
        //
        // Unlike Google V2, HUAWEI does NOT use XMP metadata at all.
        // The LIVE_ tail marker at the end of the file is the ONLY live-photo
        // detection marker that HUAWEI Gallery checks.
        //
        // HEIC output: patches the ftyp box to include "tmap" compatible brand
        //   (non-essential per the protocol doc, but present on all HUAWEI camera HEICs).
        // JPEG output (方案 A，当前默认)：不改写 EXIF Make，保留来源图片的 Make 值。
        //   原因：
        //   1) 华为协议不是华为独占，荣耀（Honor）也用同一套（LIVE_ 尾标 + 嵌入 MP4 +
        //      com.openharmony.covertime），实况照片判定不依赖 Make；Make 仅"辅助识别"。
        //   2) 来源图片可能残留 Apple MakerNote 实况条目（0x0017 LivePhotoVideoIndex、
        //      0x0025 等）。exiftool 解 Apple MakerNote 以 Make 为分派依据：Make=HUAWEI 时
        //      会对这些 type=16 条目报 "Bad format (16)" 且读不出 LivePhotoVideoIndex；
        //      已用 exiv2/MediaInfo/ImageMagick/piexif/GDI+/exifread 多工具验证，数据本身
        //      完好、与 OPPO 输出逐字节一致，纯属 exiftool 解析行为。保留 Apple Make 则
        //      exiftool 正常解码、无警告。
        //
        //   后备方案 B（若实测发现华为/荣耀相册确实依赖 Make=HUAWEI/HONOR 时启用）：
        //   1) 恢复合成后写 Make：LivePhotoRepairService.RunExifToolAsync(token,
        //      "-overwrite_original", "-Make=HUAWEI", targetPath)（荣耀场景用 "-Make=HONOR"）。
        //   2) 同时必须清洗图片中残留的 Apple 实况 MakerNote 条目，否则 exiftool 会再次报
        //      Bad format：实测 exiftool 无法删除这两个 type=16 条目（-LivePhotoVideoIndex=
        //      / -StillImageTime= / -MakerNote:0x0017= 均无效），必须字节级改写 MakerNote——
        //      删除 0x0017（LivePhotoVideoIndex）与 0x0025 两个 entry（各 12 字节 + 8 字节数据），
        //      并同步修正 IFD 中后续条目指向的数据偏移。可参照 AppleMakerNoteWriter 的字节
        //      处理模式实现（注意 -ContentIdentifier= 只能清配对 UUID，清不掉这两项）。
        //   3) 改回后重新构建并跑一轮 merge → split 往返测试，确认华为/荣耀相册可播且
        //      exiftool 无 Bad format 警告。
        //
        // sourceImg: Still image path (JPEG or HEIC, already in target format).
        // sourceVid: MP4 video path (caller ensures MP4 container).
        // targetPath: Output file path.
        // isHeicOutput: Whether the still image is HEIC (vs JPEG).
        // presentationTimestampUs: Cover frame timestamp in microseconds (0 = still image itself).
        //   Converted to a frame number using actual video FPS inside this method.
        // token: Cancellation token.
        internal static async Task WriteHuaweiNativeAsync(
            string sourceImg,
            string sourceVid,
            string targetPath,
            bool isHeicOutput,
            long presentationTimestampUs,
            CancellationToken token,
            int originalCoverMs = 0,
            int originalDurationMs = 0,
            string tailPrefix = "v6_f")
        {
            long videoSize = new FileInfo(sourceVid).Length;

            // 1. Get total video frame count (ffprobe nb_frames preferred, exiftool fallback)
            int totalFrames = await DetectVideoFrameCountAsync(sourceVid, token);

            // 1.5 Convert presentation timestamp (µs) to frame number using actual video FPS
            int coverFrame = 0;
            if (presentationTimestampUs > 0)
            {
                double fps = await DetectVideoFpsAsync(sourceVid, token);
                if (fps > 0)
                {
                    double timestampSec = presentationTimestampUs / 1_000_000.0;
                    coverFrame = (int)Math.Round(timestampSec * fps);
                    coverFrame = Math.Clamp(coverFrame, 0, totalFrames);
                }
            }

            // 1.6 Write com.openharmony.covertime to MP4 metadata.
            // Huawei Gallery reads this tag (in milliseconds) to position the cover frame.
            // Without it, the cover defaults to the first frame regardless of the tail values.
            string? covertimeMp4ToCleanup = null;
            if (presentationTimestampUs > 0)
            {
                int covertimeMs = (int)(presentationTimestampUs / 1000);
                sourceVid = await WriteMp4CovertimeMetadataAsync(sourceVid, targetPath, covertimeMs, token);
                covertimeMp4ToCleanup = sourceVid;
                videoSize = new FileInfo(sourceVid).Length; // remux may change MP4 size
            }

            // 1.7 嵌入视频 ftyp 品牌修正（P2-8）：真机华为 HEIC 实况的嵌入 MP4 为
            //     major=mp42 / compat=[iso2, mp42]，而 ffmpeg 默认写 isom/[isom,iso2,avc1,mp41]。
            //     字节级改写品牌（保持 box 尺寸不变，避免破坏 moov/stco 偏移），
            //     与真机结构一致，拆分定位逻辑（按 ftyp 定位）不受影响。
            sourceVid = await PatchMp4FtypBrandAsync(sourceVid, targetPath, token);
            string? patchedMp4ToCleanup = sourceVid;
            videoSize = new FileInfo(sourceVid).Length; // 品牌改写不改变尺寸，保险起见重读

            // 2. Build 60-byte tail (preserve original PPP:QQQQ when provided)
            byte[] tail = originalDurationMs > 0
                ? HuaweiMovingPhotoProtocol.BuildTail(coverFrame, totalFrames, videoSize,
                    originalCoverMs, originalDurationMs, tailPrefix)
                : HuaweiMovingPhotoProtocol.BuildTail(coverFrame, totalFrames, videoSize, tailPrefix);

            // 3. Write still image → target
            if (isHeicOutput)
            {
                // Read source HEIC, patch ftyp to include "tmap" brand
                byte[] heicData = await File.ReadAllBytesAsync(sourceImg, token);
                byte[] patched = InsertTmapBrand(heicData);
                await File.WriteAllBytesAsync(targetPath, patched, token);
            }
            else
            {
                // JPEG: copy directly
                using var imgFs = new FileStream(
                    sourceImg, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 8192, useAsync: true);
                using var targetFs = new FileStream(
                    targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 8192, useAsync: true);
                await imgFs.CopyToAsync(targetFs, token);
            }

            // 4. Append MP4 video
            using (var targetFs = new FileStream(
                targetPath, FileMode.Append, FileAccess.Write, FileShare.None,
                bufferSize: 8192, useAsync: true))
            using (var vidFs = new FileStream(
                sourceVid, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 8192, useAsync: true))
            {
                await vidFs.CopyToAsync(targetFs, token);
                await targetFs.WriteAsync(tail, 0, tail.Length, token);
            }
            // Clean up temp MP4s created by covertime injection / ftyp brand patch
            if (covertimeMp4ToCleanup != null)
            {
                try { if (File.Exists(covertimeMp4ToCleanup)) File.Delete(covertimeMp4ToCleanup); } catch { }
            }
            if (patchedMp4ToCleanup != null)
            {
                try { if (File.Exists(patchedMp4ToCleanup)) File.Delete(patchedMp4ToCleanup); } catch { }
            }

            // 5. ©too 补丁：真机华为 HEIC 实况的 moov/udta/meta/ilst 含
            //    ©too = "Openharmony6.1"。ffmpeg 默认写 "LavfXX.XX.XXX" 且
            //    -metadata too=... 无效（实测仍为 Lavf），需字节级改写。
            PatchMp4TooAtom(targetPath, token);

            // 6. JPEG 后处理：方案 A——不改写 EXIF Make（保留来源 Make，避免 exiftool
            //    对残留 Apple MakerNote 实况条目报 Bad format）。详见方法头注释的方案 A/B。

            LogService.Merge(
                $"HUAWEI Moving Photo written: {Path.GetFileName(targetPath)} " +
                $"(format={(isHeicOutput ? "HEIC" : "JPEG")}, " +
                $"video={videoSize} bytes, tail=LIVE_{videoSize + 20})");
        }

        // Estimate total video frame count. Prefers ffprobe nb_frames (exact),
        // then exiftool MediaDuration (30fps approximation),
        // then ffmpeg frame-by-frame count (exact, slower), then falls back to 1.
        public static async Task<int> DetectVideoFrameCountAsync(string videoPath, CancellationToken token)
        {
            // 1. Try ffprobe nb_frames (exact, matches Python pipeline)
            try
            {
                string? ffprobePath = null;
                string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
                if (!string.IsNullOrEmpty(ffmpegPath))
                {
                    string candidate = Path.Combine(Path.GetDirectoryName(ffmpegPath)!, "ffprobe.exe");
                    if (File.Exists(candidate)) ffprobePath = candidate;
                }

                if (!string.IsNullOrEmpty(ffprobePath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = ffprobePath,
                        Arguments = $"-v error -select_streams v:0 -show_entries stream=nb_frames -of csv=p=0 \"{videoPath}\"",
                        UseShellExecute = false, CreateNoWindow = true,
                        RedirectStandardOutput = true, RedirectStandardError = true
                    };
                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        string output = await process.StandardOutput.ReadToEndAsync(token);
                        await process.WaitForExitAsync(token);
                        string raw = output.Trim();
                        if (!string.IsNullOrWhiteSpace(raw) && int.TryParse(raw, out int nb) && nb > 0)
                            return nb;
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* fall through to exiftool */ }

            // 2. Fallback: exiftool MediaDuration × 30fps (legacy)
            try
            {
                string? exifToolPath = ExternalToolLocator.FindExifTool();
                if (!string.IsNullOrEmpty(exifToolPath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exifToolPath,
                        Arguments = $"-MediaDuration -s -s -S \"{videoPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };

                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        string output = await process.StandardOutput.ReadToEndAsync(token);
                        await process.WaitForExitAsync(token);

                        string raw = output.Trim();
                        if (!string.IsNullOrWhiteSpace(raw))
                        {
                            double duration = ParseMediaDuration(raw);
                            if (duration > 0)
                            {
                                int frames = (int)Math.Ceiling(duration * 30);
                                return Math.Max(1, frames);
                            }
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* fall through to ffmpeg count */ }

            // 3. Fallback: ffmpeg frame-by-frame count (exact for HEVC/VFR).
            try
            {
                return await CountFramesViaFfmpegAsync(videoPath, token);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return 1;
            }
        }

        private static async Task<int> CountFramesViaFfmpegAsync(string videoPath, CancellationToken token)
        {
            string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
                return 1;

            string frameDir = Path.Combine(Path.GetTempPath(), $"lpb_framecount_{Guid.NewGuid():N}");
            Directory.CreateDirectory(frameDir);
            try
            {
                string outputPattern = Path.Combine(frameDir, "frame_%06d.jpg");
                string args = $"-i \"{videoPath}\" -vsync 0 " +
                              $"-q:v 3 -f image2 \"{outputPattern}\" -y -loglevel error";

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using var process = Process.Start(psi);
                if (process == null)
                    return 1;

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(120));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

                try
                {
                    await process.WaitForExitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch { /* best effort */ }
                    throw;
                }

                if (process.ExitCode != 0)
                    return 1;

                int count = Directory.GetFiles(frameDir, "frame_*.jpg").Length;
                return Math.Max(1, count);
            }
            finally
            {
                try { Directory.Delete(frameDir, recursive: true); } catch { /* best effort */ }
            }
        }

        // Detect actual video FPS from exiftool VideoFrameRate tag.
        // Returns frames per second as double, or 30.0 on failure.
        public static async Task<double> DetectVideoFpsAsync(string videoPath, CancellationToken token)
        {
            try
            {
                string? exifToolPath = ExternalToolLocator.FindExifTool();
                if (string.IsNullOrEmpty(exifToolPath)) return 30.0;

                var psi = new ProcessStartInfo
                {
                    FileName = exifToolPath,
                    Arguments = $"-VideoFrameRate -s -s -S \"{videoPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = Process.Start(psi);
                if (process == null) return 30.0;

                string output = await process.StandardOutput.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);
                string raw = output.Trim();
                if (double.TryParse(raw,
                    System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture,
                    out double fps) && fps > 0)
                    return fps;
            }
            catch (OperationCanceledException) { throw; }
            catch { /* fall through to default */ }

            return 30.0; // fallback: assume 30fps
        }

        // Write com.openharmony.covertime (milliseconds) to MP4 udta metadata.
        // Uses ffmpeg -c copy remux to inject the tag without re-encoding.
        // Returns the path to the tagged MP4 (temp file — caller should clean up after use).
        public static async Task<string> WriteMp4CovertimeMetadataAsync(
            string mp4Path, string targetPath, int covertimeMs, CancellationToken token)
        {
            string taggedPath = Path.Combine(Path.GetTempPath(),
                $"lpb_ct_{Guid.NewGuid():N}.mp4");

            try
            {
                string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    LogService.Merge("covertime: ffmpeg not found, keeping original MP4", LogLevel.Warning);
                    return mp4Path;
                }

                // Remux with -c copy, injecting the covertime metadata tag into udta.
                // -movflags use_metadata_tags is required for custom tag names to be written.
                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-y -v error -i \"{mp4Path}\" -c copy " +
                                $"-movflags use_metadata_tags " +
                                $"-metadata com.openharmony.covertime=\"{covertimeMs}.000000\" " +
                                $"\"{taggedPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using var proc = Process.Start(psi);
                if (proc == null) return mp4Path;

                string stderr = await proc.StandardError.ReadToEndAsync(token);
                await proc.WaitForExitAsync(token);

                if (proc.ExitCode == 0 && File.Exists(taggedPath) && new FileInfo(taggedPath).Length > 0)
                {
                    LogService.Merge(
                        $"covertime: wrote {covertimeMs}ms → {Path.GetFileName(taggedPath)}",
                        LogLevel.Info);
                    return taggedPath;
                }

                LogService.Merge(
                    $"covertime: ffmpeg failed (exit {proc.ExitCode}): {stderr.Trim()}",
                    LogLevel.Warning);
                return mp4Path;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Merge($"covertime: error: {ex.Message}", LogLevel.Warning);
                return mp4Path;
            }
        }

        /// <summary>
        /// 使用 ffmpeg 在指定时间戳提取一帧 JPEG，输出到指定目录。
        /// 使用 -ss 输入索引模式（快速定位）+ -frames:v 1 精确截取一帧。
        /// GUI（EditViewModel）与 CLI（CoverCommand）共用。
        /// </summary>
        /// <param name="videoPath">视频文件路径</param>
        /// <param name="outputDir">输出目录（必须存在，调用方负责创建和清理）</param>
        /// <param name="timestampSec">时间戳（秒）</param>
        /// <param name="token">取消令牌</param>
        /// <returns>JPEG 文件路径，失败返回 null</returns>
        public static async Task<string?> ExtractFrameAtTimestampAsync(
            string videoPath, string outputDir, double timestampSec, CancellationToken token)
        {
            string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                LogService.Merge("ExtractFrameAtTimestamp: ffmpeg not found", LogLevel.Warning);
                return null;
            }

            try
            {
                token.ThrowIfCancellationRequested();

                string outputPath = Path.Combine(outputDir, "cover_frame.jpg");

                // -ss 在前（输入索引模式）快速定位，-frames:v 1 只取一帧
                // -q:v 2 高质量 JPEG
                string args = $"-y -ss {timestampSec:F3} -i \"{videoPath}\" " +
                              $"-frames:v 1 -q:v 2 -f image2 \"{outputPath}\" -loglevel error";

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

                try
                {
                    await process.WaitForExitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch { }
                    return null;
                }

                string stderr = await process.StandardError.ReadToEndAsync(token);
                if (process.ExitCode != 0)
                {
                    LogService.Merge(
                        $"ExtractFrameAtTimestamp: ffmpeg error (exit {process.ExitCode}) at {timestampSec:F3}s: {stderr.Trim()}",
                        LogLevel.Warning);
                    return null;
                }

                if (!File.Exists(outputPath) || new FileInfo(outputPath).Length == 0)
                {
                    LogService.Merge(
                        $"ExtractFrameAtTimestamp: output file is empty at {timestampSec:F3}s",
                        LogLevel.Warning);
                    return null;
                }

                LogService.Merge(
                    $"ExtractFrameAtTimestamp: frame at {timestampSec:F3}s -> {outputPath}",
                    LogLevel.Info);

                return outputPath;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Merge($"ExtractFrameAtTimestamp failed: {ex.Message}", LogLevel.Error, ex);
                return null;
            }
        }

        // Parse exiftool MediaDuration PrintConv string to seconds.
        // Reuses the same parsing logic as LivePhotoRepairService.
        // Handles: "2.35 s", "0:01:05", "2.35" (raw numeric).
        private static double ParseMediaDuration(string raw)
        {
            // Timecode format: "HH:MM:SS" or "MM:SS"
            var tcMatch = System.Text.RegularExpressions.Regex.Match(raw,
                @"^(?:(\d+):)?(\d+):(\d+(?:\.\d+)?)$");
            if (tcMatch.Success)
            {
                int hours = tcMatch.Groups[1].Success ? int.Parse(tcMatch.Groups[1].Value) : 0;
                int minutes = int.Parse(tcMatch.Groups[2].Value);
                double secs = double.Parse(tcMatch.Groups[3].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                return hours * 3600 + minutes * 60 + secs;
            }

            // "2.35 s" format
            var sMatch = System.Text.RegularExpressions.Regex.Match(raw, @"^([\d.]+)\s*s");
            if (sMatch.Success && double.TryParse(sMatch.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double seconds))
                return seconds;

            // Raw numeric
            if (double.TryParse(raw,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double rawVal))
                return rawVal;

            return 0;
        }

        /// <summary>
        /// 检测视频时长（秒）。用 exiftool MediaDuration（不带 ffprobe）。
        /// GUI 与 CLI 共用；返回 0 表示无法探测。
        /// </summary>
        public static async Task<double> DetectVideoDurationAsync(string videoPath, CancellationToken token)
        {
            try
            {
                string? exifToolPath = ExternalToolLocator.FindExifTool();
                if (!string.IsNullOrEmpty(exifToolPath))
                {
                    var psi = new ProcessStartInfo
                    {
                        FileName = exifToolPath,
                        Arguments = $"-MediaDuration -s -s -S \"{videoPath}\"",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    };
                    using var process = Process.Start(psi);
                    if (process != null)
                    {
                        string output = await process.StandardOutput.ReadToEndAsync(token);
                        await process.WaitForExitAsync(token);
                        string raw = output.Trim();
                        if (!string.IsNullOrEmpty(raw) && raw != "N/A" && raw != "unknown")
                        {
                            double durationSec = ParseMediaDuration(raw);
                            if (durationSec > 0)
                                return durationSec;
                        }
                    }
                }
            }
            catch (OperationCanceledException) { throw; }
            catch { /* fall through to ffmpeg */ }

            // Fallback: parse Duration from ffmpeg -i output.
            try
            {
                string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
                    return 0;

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = $"-hide_banner -i \"{videoPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true,
                    RedirectStandardOutput = true
                };
                using var process = Process.Start(psi);
                if (process == null)
                    return 0;

                string stderr = await process.StandardError.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);

                var match = System.Text.RegularExpressions.Regex.Match(
                    stderr,
                    @"Duration:\s*(\d+):(\d+):(\d+(?:\.\d+)?)");
                if (match.Success &&
                    int.TryParse(match.Groups[1].Value, out int hours) &&
                    int.TryParse(match.Groups[2].Value, out int minutes) &&
                    double.TryParse(
                        match.Groups[3].Value,
                        System.Globalization.NumberStyles.Float,
                        System.Globalization.CultureInfo.InvariantCulture,
                        out double seconds))
                {
                    return hours * 3600 + minutes * 60 + seconds;
                }

                return 0;
            }
            catch (OperationCanceledException) { throw; }
            catch { return 0; }
        }

        // Insert "tmap" as the last compatible brand in the HEIC ftyp box.
        // The ftyp box is always at offset 0 in a freshly encoded HEIC file.
        // Replaces the last 4 bytes of the ftyp box (the last compatible brand slot).
        public static byte[] InsertTmapBrand(byte[] heicData)
        {
            if (heicData.Length < 16) return heicData;

            // Read ftyp box size (big-endian uint32 at offset 0)
            uint boxSize = BinaryPrimitives.ReadUInt32BigEndian(
                new ReadOnlySpan<byte>(heicData, 0, 4));

            if (boxSize < 16 || boxSize > heicData.Length)
                return heicData; // Sanity check — return unmodified

            // Replace the last 4 bytes of ftyp box with "tmap"
            int lastBrandOffset = (int)boxSize - 4;
            heicData[lastBrandOffset + 0] = (byte)'t';
            heicData[lastBrandOffset + 1] = (byte)'m';
            heicData[lastBrandOffset + 2] = (byte)'a';
            heicData[lastBrandOffset + 3] = (byte)'p';

            return heicData;
        }

        // 改写嵌入 MP4 的 ftyp 品牌为华为真机结构：major=mp42 / minor=0 /
        // compat[0..1]=iso2,mp42（P2-8）。ffmpeg 默认写 isom/[isom,iso2,avc1,mp41]，
        // 与真机华为 HEIC 实况不一致。
        // 只改写字节、保持 box 尺寸不变——不改变后续 box 偏移，避免破坏 moov/stco 等
        // 绝对偏移引用。返回值是品牌修正后的临时 MP4 路径（调用方负责清理）。
        internal static async Task<string> PatchMp4FtypBrandAsync(
            string mp4Path, string targetPath, CancellationToken token)
        {
            string patchedPath = Path.Combine(Path.GetTempPath(),
                $"lpb_fb_{Guid.NewGuid():N}.mp4");
            try
            {
                byte[] data = await File.ReadAllBytesAsync(mp4Path, token);
                if (data.Length < 24 || data[4] != (byte)'f' || data[5] != (byte)'t'
                    || data[6] != (byte)'y' || data[7] != (byte)'p')
                {
                    LogService.Merge("ftyp patch: video does not start with ftyp box, skipping",
                        LogLevel.Warning);
                    return mp4Path;
                }

                // major brand → "mp42"，minor → 0
                data[8] = (byte)'m'; data[9] = (byte)'p';
                data[10] = (byte)'4'; data[11] = (byte)'2';
                data[12] = 0; data[13] = 0; data[14] = 0; data[15] = 0;

                // compat[0] → "iso2"，compat[1] → "mp42"（保留后续 brand，无害）
                if (data.Length >= 24)
                {
                    data[16] = (byte)'i'; data[17] = (byte)'s';
                    data[18] = (byte)'o'; data[19] = (byte)'2';
                    data[20] = (byte)'m'; data[21] = (byte)'p';
                    data[22] = (byte)'4'; data[23] = (byte)'2';
                }

                await File.WriteAllBytesAsync(patchedPath, data, token);
                return patchedPath;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                LogService.Merge($"ftyp brand patch failed: {ex.Message}", LogLevel.Warning);
                try { if (File.Exists(patchedPath)) File.Delete(patchedPath); } catch { }
                return mp4Path;
            }
        }

        // "openharmony6" — 12 bytes, exactly replaces ffmpeg's "LavfXX.XX.XXX"
        private static readonly byte[] OpenharmonyTooBytes =
            Encoding.ASCII.GetBytes("openharmony6");

        // ffmpeg writes ©too = "LavfXX.XX.XXX" in the MP4 moov/udta.
        // Patch it to "openharmony6" so Huawei Gallery recognizes the file.
        // Strategy: search the file for the unique "Lavf" marker (appears exactly
        // once in the entire combined file, inside the MP4's ©too atom).
        internal static void PatchMp4TooAtom(string targetPath, CancellationToken token)
        {
            using var fs = new FileStream(targetPath, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None, bufferSize: 4096);
            long fileSize = fs.Length;
            if (fileSize < 1024) return;

            // Read entire file in chunks to find "Lavf"
            byte[] buf = new byte[65536];
            long pos = 0;
            long lavfPos = -1;

            while (pos < fileSize)
            {
                token.ThrowIfCancellationRequested();
                int toRead = (int)Math.Min(buf.Length, fileSize - pos);
                fs.Seek(pos, SeekOrigin.Begin);
                int actual = fs.Read(buf, 0, toRead);
                if (actual == 0) break;

                for (int i = 0; i <= actual - 4; i++)
                {
                    if (buf[i] == 'L' && buf[i + 1] == 'a'
                        && buf[i + 2] == 'v' && buf[i + 3] == 'f')
                    {
                        lavfPos = pos + i;
                        break;
                    }
                }
                if (lavfPos >= 0) break;
                // Keep a 3-byte overlap so a marker split across chunks is found.
                // Math.Max guarantees progress when the final read contains 1-3 bytes;
                // the previous code added zero for 3 bytes and looped forever.
                pos += Math.Max(1, actual - 3);
            }

            if (lavfPos < 0 || lavfPos + OpenharmonyTooBytes.Length > fileSize)
                return; // No complete "LavfXX.XX.XXX" value — nothing to patch

            // Write "openharmony6" over "Lavf62.3.100"
            fs.Seek(lavfPos, SeekOrigin.Begin);
            fs.Write(OpenharmonyTooBytes, 0, 12);
            fs.Flush();
        }

        // Write the combined JPEG + XMP + video file.
        // Image pre-processing (e.g. OPPO EXIF injection) is expected to be handled
        // by the caller via <see cref="LivePhotoProtocol.PrepareImageAsync"/>.
        public static async Task WriteNativeAsync(
            string sourceImg,
            string sourceVid,
            string targetPath,
            byte[] xmpBytes,
            CancellationToken token)
        {
            int segmentLength = 2 + XmpHeader.Length + xmpBytes.Length;
            if (segmentLength > ushort.MaxValue)
            {
                LogService.Merge($"XMP metadata too large: {segmentLength} bytes", LogLevel.Error);
                throw new InvalidOperationException(
                    ResourceService.Format("Error_XmpMetadataTooLarge", segmentLength));
            }

            // Validate source image (async-safe — no sync ReadByte on async stream)
            byte[] soiCheck = new byte[2];
            {
                using var imgCheckFs = new FileStream(
                    sourceImg, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 4096, useAsync: true);
                if (imgCheckFs.Length < 2)
                {
                    LogService.Merge($"Empty or invalid JPEG file: {sourceImg}", LogLevel.Error);
                    throw new InvalidDataException(ResourceService.GetString("Error_InvalidJpegFile"));
                }
                await imgCheckFs.ReadExactlyAsync(soiCheck, 0, 2, token);
                if (soiCheck[0] != 0xFF || soiCheck[1] != 0xD8)
                {
                    LogService.Merge($"Invalid JPEG file (no SOI): {sourceImg}", LogLevel.Error);
                    throw new InvalidDataException(ResourceService.GetString("Error_InvalidJpegFile"));
                }
            }

            // Build the complete JPEG prefix: SOI + APP1 marker + segment length + XMP header
            // as a SINGLE byte array written with one WriteAsync call.
            // Do NOT mix sync WriteByte with async WriteAsync on the same FileStream —
            // they use different I/O code paths and the OS may reorder the writes,
            // causing the XMP segment to land AFTER the source image data instead of before it.
            byte[] prefix = new byte[4 + 2 + XmpHeader.Length];
            prefix[0] = 0xFF; prefix[1] = 0xD8;               // SOI
            prefix[2] = 0xFF; prefix[3] = 0xE1;               // APP1 marker
            prefix[4] = (byte)(segmentLength >> 8);           // segment length hi
            prefix[5] = (byte)(segmentLength & 0xFF);         // segment length lo
            Array.Copy(XmpHeader, 0, prefix, 6, XmpHeader.Length);   // XMP header

            using var targetFs = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 8192, useAsync: true);

            await targetFs.WriteAsync(prefix, 0, prefix.Length, token);
            await targetFs.WriteAsync(xmpBytes, 0, xmpBytes.Length, token);

            // Copy the rest of the source JPEG (skipping its SOI which we already wrote)
            using var imgFs = new FileStream(
                sourceImg, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 8192, useAsync: true);
            imgFs.Position = 2;  // skip source JPEG's SOI
            await imgFs.CopyToAsync(targetFs, token);

            // Append video
            using var vidFs = new FileStream(
                sourceVid, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 8192, useAsync: true);
            await vidFs.CopyToAsync(targetFs, token);
        }

        // ── HEIC native writer ──────────────────────────────────────────

        // Write a combined HEIC + XMP (injected via exiftool into the ISOBMFF meta box)
        // + mpvd box (wrapping the video) file.
        //
        // Unlike JPEG (where XMP goes into an APP1 segment), the Google Motion Photo
        // HEIC spec requires XMP inside the ISOBMFF meta box and the video wrapped in
        // an mpvd box (4-byte big-endian size + 4-byte FourCC 'mpvd' + video data).
        //
        // exiftool handles the ISOBMFF box manipulation correctly for HEIC — its
        // documented namespace-stripping issue is JPEG-specific (APP segment rewrite).
        // For HEIC, XMP is stored as an opaque uuid box inside meta and preserved as-is.
        private static async Task WriteHeicNativeAsync(
            string sourceHeic,
            string sourceVid,
            string targetPath,
            byte[] xmpBytes,
            CancellationToken token)
        {
            // exiftool is required for HEIC XMP injection
            if (string.IsNullOrEmpty(ExternalToolLocator.FindExifTool()))
            {
                LogService.Merge("exiftool not found — cannot inject XMP into HEIC", LogLevel.Warning);
                throw new InvalidOperationException("exiftool is required for HEIC live photo creation.");
            }

            string tempDir = Path.Combine(Path.GetTempPath(),
                "LivePhotoBox_HeicMerge_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // 1. Write XMP bytes to a temporary .xmp file
                string tempXmp = Path.Combine(tempDir, "temp.xmp");
                await File.WriteAllBytesAsync(tempXmp, xmpBytes, token);

                // 2. Run exiftool: read source HEIC, inject XMP, write temp output
                //    Using '-o' (not -overwrite_original) avoids copying the source file
                string tempHeic = Path.Combine(tempDir, "temp_with_xmp.heic");
                await LivePhotoRepairService.RunExifToolAsync(token,
                    $"-xmp<={tempXmp}",
                    "-o", tempHeic,
                    sourceHeic);

                if (!File.Exists(tempHeic))
                {
                    throw new InvalidOperationException("exiftool did not produce output HEIC file.");
                }

                // 3. Copy exiftool output (HEIC with XMP) to target, then append mpvd box with video
                using var tempHeicFs = new FileStream(
                    tempHeic, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 8192, useAsync: true);
                using var targetFs = new FileStream(
                    targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                    bufferSize: 8192, useAsync: true);

                await tempHeicFs.CopyToAsync(targetFs, token);

                // 4. Write mpvd box header (8 bytes) + video data
                long videoSize = new FileInfo(sourceVid).Length;
                byte[] mpvdHeader = BuildMpvdHeader(videoSize);
                await targetFs.WriteAsync(mpvdHeader, 0, mpvdHeader.Length, token);

                using var vidFs = new FileStream(
                    sourceVid, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 8192, useAsync: true);
                await vidFs.CopyToAsync(targetFs, token);

                LogService.Merge(
                    $"HEIC Motion Photo written: {Path.GetFileName(targetPath)} " +
                    $"(HEIC + XMP + mpvd[{videoSize} bytes])");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        // ── Samsung write paths ───────────────────────────────────────────

        /// <summary>
        /// Write a Samsung Motion Photo with JPEG primary image.
        /// Output: [JPEG with V2 XMP] + [Samsung Trailer: tags + SEFH/SEFT]
        /// The video is embedded inside the MotionPhoto_Data tag within the Trailer.
        /// XMP Item:Padding is set to 24 (tag header size) so Google Photos can
        /// skip the tag wrapper and reach the MP4 bytes directly.
        /// </summary>
        private static async Task WriteSamsungJpegAsync(
            string sourceImg,
            string sourceVid,
            string targetPath,
            long videoSize,
            string videoMime,
            MotionPhotoV2Protocol protocol,
            CancellationToken token,
            long presentationTimestampUs)
        {
            // Read MP4 video bytes
            byte[] videoData = await File.ReadAllBytesAsync(sourceVid, token);

            // Build Samsung Trailer first to know its total size (needed for XMP)
            byte[] trailer = SamsungMotionPhotoProtocol.BuildTrailer(videoData, "jpg");
            long trailerSize = trailer.Length;

            // V2 XMP for Google Photos compat: 24-byte tag header is treated as
            // "padding" so readers skip it and find the MP4 starting at offset 24.
            // Item:Length = trailer minus the tag wrapper, so Google Photos can
            // extract a valid MP4 (trailing version tag + SEF are ignored by ISOBMFF parsers).
            const int tagHeaderPadding = 24; // [00 00][marker][name_len][name] = 2+2+4+16
            long xmpVideoLength = trailerSize - tagHeaderPadding;

            // Build XMP through the actual protocol instance (supports fusion protocol
            // which needs the pure videoSize for OpCamera:VideoLength).
            byte[] xmpBytes;
            if (protocol is MotionPhotoFusionProtocol fusion)
            {
                xmpBytes = fusion.BuildXmpMetadata(xmpVideoLength, presentationTimestampUs,
                    "image/jpeg", tagHeaderPadding.ToString(), videoMime, videoSize);
            }
            else
            {
                xmpBytes = protocol.BuildXmpMetadata(xmpVideoLength, presentationTimestampUs,
                    "image/jpeg", tagHeaderPadding.ToString(), videoMime);
            }

            // Inject XMP into JPEG — write directly (same pattern as WriteNativeAsync),
            // NOT via exiftool which parses and strips unknown XMP namespaces
            // (OpCamera, VCamera, LivePhotoBox).
            int segmentLength = 2 + XmpHeader.Length + xmpBytes.Length;
            if (segmentLength > ushort.MaxValue)
            {
                LogService.Merge($"XMP metadata too large: {segmentLength} bytes", LogLevel.Error);
                throw new InvalidOperationException(
                    ResourceService.Format("Error_XmpMetadataTooLarge", segmentLength));
            }

            byte[] prefix = new byte[4 + 2 + XmpHeader.Length];
            prefix[0] = 0xFF; prefix[1] = 0xD8;                 // SOI
            prefix[2] = 0xFF; prefix[3] = 0xE1;                 // APP1 marker
            prefix[4] = (byte)(segmentLength >> 8);              // segment length hi
            prefix[5] = (byte)(segmentLength & 0xFF);            // segment length lo
            Array.Copy(XmpHeader, 0, prefix, 6, XmpHeader.Length); // XMP namespace header

            byte[] jpegData;
            using (var imgFs = new FileStream(
                sourceImg, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 8192, useAsync: true))
            {
                // Skip source JPEG's SOI (we write our own)
                imgFs.Position = 2;
                using var ms = new MemoryStream();
                await ms.WriteAsync(prefix, 0, prefix.Length, token);
                await ms.WriteAsync(xmpBytes, 0, xmpBytes.Length, token);
                await imgFs.CopyToAsync(ms, token);
                jpegData = ms.ToArray();
            }

            // Write: JPEG (with injected XMP) + Trailer
            using (var targetFs = new FileStream(
                targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 8192, useAsync: true))
            {
                await targetFs.WriteAsync(jpegData, 0, jpegData.Length, token);
                await targetFs.WriteAsync(trailer, 0, trailer.Length, token);
            }

            LogService.Merge(
                $"Samsung JPEG Motion Photo written: {Path.GetFileName(targetPath)} " +
                $"(JPEG + XMP + Samsung Trailer[{trailer.Length} bytes], video={videoSize} bytes)");
        }

        /// <summary>
        /// Write a Samsung Motion Photo with HEIC primary image.
        /// Output: [HEIC with V2 XMP] + [mpvd box: mpvd + MP4 + sefd + tags + SEFH/SEFT]
        /// The mpvd box IS the Samsung Trailer for HEIC.
        /// </summary>
        private static async Task WriteSamsungHeicAsync(
            string sourceHeic,
            string sourceVid,
            string targetPath,
            long videoSize,
            string videoMime,
            MotionPhotoV2Protocol protocol,
            CancellationToken token,
            long presentationTimestampUs)
        {
            if (string.IsNullOrEmpty(ExternalToolLocator.FindExifTool()))
            {
                LogService.Merge("exiftool not found — cannot inject XMP into HEIC", LogLevel.Warning);
                throw new InvalidOperationException("exiftool is required for HEIC live photo creation.");
            }

            // Read MP4 video bytes
            byte[] videoData = await File.ReadAllBytesAsync(sourceVid, token);

            // Build XMP metadata through the actual protocol instance
            byte[] xmpBytes;
            if (protocol is MotionPhotoFusionProtocol fusion)
            {
                xmpBytes = fusion.BuildXmpMetadata(videoSize, presentationTimestampUs, "image/heic", "8", videoMime, videoSize);
            }
            else
            {
                xmpBytes = protocol.BuildXmpMetadata(videoSize, presentationTimestampUs, "image/heic", "8", videoMime);
            }

            string tempDir = Path.Combine(Path.GetTempPath(),
                "LivePhotoBox_SamsungHeic_" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(tempDir);
            try
            {
                // 1. Write XMP to temp file
                string tempXmp = Path.Combine(tempDir, "temp.xmp");
                await File.WriteAllBytesAsync(tempXmp, xmpBytes, token);

                // 2. Inject XMP into HEIC via exiftool
                string tempHeic = Path.Combine(tempDir, "temp_with_xmp.heic");
                await LivePhotoRepairService.RunExifToolAsync(token,
                    $"-xmp<={tempXmp}",
                    "-o", tempHeic,
                    sourceHeic);

                if (!File.Exists(tempHeic))
                    throw new InvalidOperationException("exiftool did not produce output HEIC file.");

                // 3. Copy HEIC (with XMP) to target, get image size for mpv2 offset
                long imageSize;
                using (var tempHeicFs = new FileStream(
                    tempHeic, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 8192, useAsync: true))
                {
                    imageSize = tempHeicFs.Length;
                    using var targetFs = new FileStream(
                        targetPath, FileMode.Create, FileAccess.Write, FileShare.None,
                        bufferSize: 8192, useAsync: true);
                    await tempHeicFs.CopyToAsync(targetFs, token);

                    // 4. Build Samsung Trailer (HEIC: complete mpvd box)
                    byte[] trailer = SamsungMotionPhotoProtocol.BuildTrailer(videoData, "heic", imageSize);

                    // 5. Append trailer (= mpvd box) to target
                    await targetFs.WriteAsync(trailer, 0, trailer.Length, token);
                }

                LogService.Merge(
                    $"Samsung HEIC Motion Photo written: {Path.GetFileName(targetPath)} " +
                    $"(HEIC + XMP + mpvd[sefd], image={imageSize}, video={videoSize} bytes)");
            }
            finally
            {
                try { Directory.Delete(tempDir, recursive: true); } catch { }
            }
        }

        // Build the 8-byte mpvd (Motion Photo Video Data) ISOBMFF box header.
        // Structure: [4 bytes big-endian box size] [4 bytes FourCC 'mpvd']
        // The box size includes the 8-byte header itself plus the video payload.
        private static byte[] BuildMpvdHeader(long videoSize)
        {
            uint boxSize = (uint)(8 + videoSize);
            return new byte[]
            {
                (byte)(boxSize >> 24),
                (byte)(boxSize >> 16),
                (byte)(boxSize >> 8),
                (byte)(boxSize),
                (byte)'m', (byte)'p', (byte)'v', (byte)'d'
            };
        }

        // Adobe XMP APP1 segment header (29 bytes including NUL).
        private static readonly byte[] XmpHeader =
            Encoding.ASCII.GetBytes("http://ns.adobe.com/xap/1.0/\0");

        // ── mpvd box helpers ─────────────────────────────────────────────

        /// <summary>
        /// 从 HEIC 文件的 mpvd box 中获取嵌入视频的字节长度。
        /// 返回 0 表示未找到 mpvd box 或视频长度无效。
        /// </summary>
        public static long GetMpvdVideoLength(string heicPath)
        {
            byte[] data;
            try { data = File.ReadAllBytes(heicPath); }
            catch { return 0; }

            for (int i = 0; i < data.Length - 8; i++)
            {
                if (data[i] == 'm' && data[i + 1] == 'p' &&
                    data[i + 2] == 'v' && data[i + 3] == 'd')
                {
                    if (i < 4) continue;
                    uint boxSize = (uint)(data[i - 4] << 24 | data[i - 3] << 16 |
                                          data[i - 2] << 8 | data[i - 1]);
                    long payloadStart = i + 4;
                    long boxEnd = i - 4 + boxSize;
                    if (boxEnd > data.Length) boxEnd = data.Length;

                    long videoEnd = boxEnd;
                    for (int j = (int)payloadStart; j < Math.Min(boxEnd, data.Length - 4); j++)
                    {
                        if (data[j] == 's' && data[j + 1] == 'e' &&
                            data[j + 2] == 'f' && data[j + 3] == 'd')
                        {
                            if (j >= 4) videoEnd = j - 4;
                            break;
                        }
                    }

                    long videoLength = videoEnd - payloadStart;
                    return videoLength > 0 ? videoLength : 0;
                }
            }
            return 0;
        }

        /// <summary>获取 mpvd box 中视频的起始字节偏移，0 表示未找到。</summary>
        public static long GetMpvdVideoStart(string heicPath)
        {
            byte[] data;
            try { data = File.ReadAllBytes(heicPath); }
            catch { return 0; }

            for (int i = 0; i < data.Length - 8; i++)
            {
                if (data[i] == 'm' && data[i + 1] == 'p' &&
                    data[i + 2] == 'v' && data[i + 3] == 'd')
                {
                    // mpvd fourcc 后面紧跟视频数据
                    return i + 4;
                }
            }
            return 0;
        }

        // ── Cover frame timestamp helpers ─────────────────────────────────

        /// <summary>
        /// Read the cover frame timestamp from a dual-file source (Apple or vivo old)
        /// and return it in microseconds. Returns 0 if no cover frame info is found
        /// or the source is not a known dual-file format.
        /// </summary>
        /// <remarks>
        /// MUST be called BEFORE ffmpeg transcode — the Apple mebx track is
        /// discarded by ffmpeg's -map 0:V:0 selector.
        /// </remarks>
        public static long ReadSourceCoverTimestamp(string videoPath)
        {
            // ── Apple Live Photo ──
            if (videoPath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase))
            {
                double? stillTimeSec = EditTimingService.ReadAppleStillImageTime(videoPath);
                if (stillTimeSec.HasValue && stillTimeSec.Value > 0)
                    return (long)(stillTimeSec.Value * 1_000_000);
                return 0;
            }

            // ── vivo old (MP4 with vivoMediaExtInfo uuid box) ──
            return ReadVivoImageTime(videoPath);
        }

        /// <summary>
        /// Extract com.android.camera.imageTime from the vivo JSON tail
        /// inside the MP4's vivoMediaExtInfo uuid box. Returns microseconds.
        /// </summary>
        private static long ReadVivoImageTime(string videoPath)
        {
            if (!videoPath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                return 0;

            try
            {
                using var fs = new FileStream(
                    videoPath, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 4096, options: FileOptions.SequentialScan);
                long fileLen = fs.Length;
                int tailLen = (int)Math.Min(fileLen, 4096);
                fs.Seek(-tailLen, SeekOrigin.End);
                byte[] tail = new byte[tailLen];
                fs.ReadExactly(tail, 0, tailLen);

                // Search backwards for "vivo{" marker
                int idx = -1;
                for (int i = tailLen - 6; i >= 0; i--)
                {
                    if (tail[i] == 'v' && tail[i + 1] == 'i' &&
                        tail[i + 2] == 'v' && tail[i + 3] == 'o' &&
                        tail[i + 4] == '{')
                    {
                        idx = i;
                        break;
                    }
                }
                if (idx < 0) return 0;

                // Extract JSON portion: vivo{ ... }
                int jsonStart = idx + 4; // skip "vivo"
                int depth = 0, jsonEnd = -1;
                for (int i = jsonStart; i < tailLen; i++)
                {
                    if (tail[i] == '{') depth++;
                    else if (tail[i] == '}')
                    {
                        if (depth == 0) { jsonEnd = i; break; }
                        depth--;
                    }
                }
                if (jsonEnd < 0) return 0;

                string json = Encoding.UTF8.GetString(
                    tail, jsonStart, jsonEnd - jsonStart + 1);

                // Lightweight regex — avoid full JSON parse overhead
                var match = Regex.Match(
                    json, @"""imageTime"":\s*(-?\d+)",
                    RegexOptions.CultureInvariant);
                if (match.Success &&
                    long.TryParse(match.Groups[1].Value, out long imageTime) &&
                    imageTime > 0)
                {
                    return imageTime * 1000; // milliseconds → microseconds
                }

                return 0;
            }
            catch
            {
                return 0; // Best-effort — non-critical metadata
            }
        }
    }
}
