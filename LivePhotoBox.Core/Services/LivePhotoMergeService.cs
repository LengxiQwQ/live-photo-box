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
                    ? selectedModeIndex switch
                    {
                        0 => "fusion",
                        1 => "microvideo",
                        2 => "motionphoto",
                        3 => "oppo",
                        4 => "vivo",
                        5 => "samsung",
                        6 => "huawei",
                        _ => null
                    }
                    : null;

                name = protocolSuffix != null ? baseName + protocolSuffix : baseName;
            }

            // 清理首尾冗余分隔符 + 非法文件名字符
            name = SanitizeFileName(name).Trim('_', '-', ' ', '+');

            // HEIC 输出：需 HEIC 源 + V2/Huawei 协议 + 用户选了 HEIC 格式
            bool wantHeic = outputFormatIndex is 2 or 3;
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

        // 将命名模板字符串渲染为实际文件名。
        // template: 含 token 的模板，如 "{name}_{protocol}" 或 "LivePhoto_{date}_{counter:D3}"
        // baseName: 原文件基本名（不含扩展名）
        // protocolIndex: 协议索引
        // taskIndex: 任务序号（1-based，用于 counter token）
        // sourceImagePath: 原图片路径（用于 {exif_date}/{exif_time} 读取文件时间）
        public static string RenderNamingTemplate(string template, string baseName, int protocolIndex, int taskIndex,
            string? sourceImagePath = null)
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
                    "protocol" => protocolIndex switch
                    {
                        0 => "fusion",
                        1 => "microvideo",
                        2 => "motionphoto",
                        3 => "oppo",
                        4 => "vivo",
                        5 => "samsung",
                        6 => "huawei",
                        _ => "",
                    },
                    "date" => DateTime.Now.ToString(format ?? "yyyyMMdd"),
                    "time" => DateTime.Now.ToString(format ?? "HHmmss"),
                    "exif_date" => GetCaptureTime().ToString(format ?? "yyyyMMdd"),
                    "exif_time" => GetCaptureTime().ToString(format ?? "HHmmss"),
                    "counter" => format != null ? taskIndex.ToString(format) : taskIndex.ToString(),
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
            if (HeicConverterService.IsHeicFile(sourceImg) && protocol is SamsungMotionPhotoProtocol samHeic)
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
            if (HeicConverterService.IsHeicFile(sourceImg) && protocol is MotionPhotoV2Protocol v2)
            {
                string videoMime = DetectVideoMime(sourceVid);
                byte[] xmpBytes = v2.BuildXmpMetadata(videoSize, presentationTimestampUs, "image/heic", "8", videoMime);
                await WriteHeicNativeAsync(sourceImg, sourceVid, targetPath, xmpBytes, token);
                return;
            }

            // ── HUAWEI native path (no XMP, uses LIVE_ tail marker) ──
            if (protocol is HuaweiMovingPhotoProtocol)
            {
                bool isHarmonyOS4 = outputFormatIndex == ProtocolFormatMatrix.FormatJpgMp4HarmonyOS4;
                bool isHeicOutput = !isHarmonyOS4 && HeicConverterService.IsHeicFile(sourceImg);
                int coverFrame = presentationTimestampUs > 0
                    ? (int)Math.Round(presentationTimestampUs / 1_000_000.0 * 30)
                    : 0;
                string tailPrefix = isHarmonyOS4 ? "v3_f" : "v6_f";
                await WriteHuaweiNativeAsync(sourceImg, sourceVid, targetPath,
                    isHeicOutput, coverFrame, token,
                    tailPrefix: tailPrefix,
                    patchToHarmonyOS4: isHarmonyOS4);
                return;
            }

            // ── JPEG path (all protocols) ──
            // For Motion Photo V2 and its subclasses (vivo), use the
            // actual video container MIME so the XMP matches the appended data.
            // For other protocols (V1, OPPO), keep the default behaviour.
            byte[] jpegXmpBytes;
            if (protocol is MotionPhotoV2Protocol v2jpeg)
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
        // JPEG output: writes Make=HUAWEI in EXIF via exiftool (also non-essential).
        //
        // sourceImg: Still image path (JPEG or HEIC, already in target format).
        // sourceVid: MP4 video path (caller ensures MP4 container).
        // targetPath: Output file path.
        // isHeicOutput: Whether the still image is HEIC (vs JPEG).
        // coverFrame: Cover frame number (0 = still image itself).
        // token: Cancellation token.
        internal static async Task WriteHuaweiNativeAsync(
            string sourceImg,
            string sourceVid,
            string targetPath,
            bool isHeicOutput,
            int coverFrame,
            CancellationToken token,
            int originalCoverMs = 0,
            int originalDurationMs = 0,
            string tailPrefix = "v6_f",
            bool patchToHarmonyOS4 = false)
        {
            long videoSize = new FileInfo(sourceVid).Length;

            // 1. Get total video frame count (exiftool MediaDuration × 30fps)
            int totalFrames = await DetectVideoFrameCountAsync(sourceVid, token);

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

            // 3.5 HMOS4: pre-process source MP4 (strip mdat header + fix ftyp)
            if (patchToHarmonyOS4)
            {
                try
                {
                    videoSize = PatchSourceMp4ForHarmonyOS4(sourceVid);
                    LogService.Merge($"HMOS4: patched MP4 → {videoSize}B", LogLevel.Info);

                    // HMOS4 tail format differs from standard v6:
                    // [+0..+7]  "v3_f{XX}_c"   (8 chars, _c suffix = cover frame marker)
                    // [+20..+27] "{PPP}:{QQQQ}" (milliseconds — read-only history)
                    // For a fresh export, the original cover is at time 0 (the still image
                    // is the first frame). The _c suffix already tells the phone which
                    // frame to use as the cover — PPP:QQQQ records the original source.
                    int durationMs = totalFrames > 0
                        ? (int)Math.Round(totalFrames * 1000.0 / 30) : 0;

                    tail = HuaweiMovingPhotoProtocol.BuildTail(
                        coverFrame, totalFrames, videoSize,
                        originalCoverMs: 0, durationMs, tailPrefix);

                    // Overwrite [+0..+7] with "v3_f{XX}_c" — 8 chars with _c suffix
                    string vfWithSuffix = $"{tailPrefix}{coverFrame}_c";
                    byte[] vfBytes = Encoding.ASCII.GetBytes(vfWithSuffix);
                    int vfLen = Math.Min(vfBytes.Length, 8);
                    for (int i = 0; i < vfLen; i++) tail[i] = vfBytes[i];
                }
                catch (Exception ex)
                {
                    LogService.Merge(
                        $"HMOS4 MP4 pre-patch failed: {ex.GetType().Name}: {ex.Message}",
                        LogLevel.Error);
                }
            }

            // 4. Append MP4 video
            long videoSizeBeforeAppend = new FileInfo(sourceVid).Length;
            System.Console.Error.WriteLine(
                $"[DEBUG AppendMP4] measured={videoSize} actual_now={videoSizeBeforeAppend}");
            using (var targetFs = new FileStream(
                targetPath, FileMode.Append, FileAccess.Write, FileShare.None,
                bufferSize: 8192, useAsync: true))
            using (var vidFs = new FileStream(
                sourceVid, FileMode.Open, FileAccess.Read, FileShare.Read,
                bufferSize: 8192, useAsync: true))
            {
                await vidFs.CopyToAsync(targetFs, token);
                System.Console.Error.WriteLine(
                    $"[DEBUG WriteTail] tailLen={tail.Length} tail48={Encoding.ASCII.GetString(tail, 0, Math.Min(tail.Length, 48))}");
                await targetFs.WriteAsync(tail, 0, tail.Length, token);
            }

            // 6. JPEG post-processing: write HUAWEI EXIF Make tag
            if (!isHeicOutput)
            {
                try
                {
                    await LivePhotoRepairService.RunExifToolAsync(token,
                        "-overwrite_original", "-Make=HUAWEI", targetPath);
                }
                catch (Exception ex)
                {
                    // Best-effort — Make tag is non-essential
                    LogService.Merge(
                        $"HUAWEI EXIF Make write failed (non-fatal): {ex.Message}",
                        LogLevel.Warning);
                }
            }

            LogService.Merge(
                $"HUAWEI Moving Photo written: {Path.GetFileName(targetPath)} " +
                $"(format={(isHeicOutput ? "HEIC" : "JPEG")}, " +
                $"video={videoSize} bytes, tail=LIVE_{videoSize + 20})");
        }

        // Estimate total video frame count. Prefers ffprobe nb_frames (exact),
        // then exiftool MediaDuration (30fps approximation), then falls back to 1.
        public static async Task<int> DetectVideoFrameCountAsync(string videoPath, CancellationToken token)
        {
            // 1. Try ffprobe nb_frames (exact, matches Python pipeline)
            try
            {
                string? ffprobePath = null;
                string? ffmpegDir = ExternalToolLocator.FindFFmpeg();
                if (!string.IsNullOrEmpty(ffmpegDir))
                {
                    string candidate = Path.Combine(Path.GetDirectoryName(ffmpegDir)!, "ffprobe.exe");
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
                if (string.IsNullOrEmpty(exifToolPath)) return 1;

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
                if (process == null) return 1;

                string output = await process.StandardOutput.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);

                string raw = output.Trim();
                if (string.IsNullOrWhiteSpace(raw)) return 1;

                double duration = ParseMediaDuration(raw);
                if (duration <= 0) return 1;

                int frames = (int)Math.Ceiling(duration * 30);
                return Math.Max(1, frames);
            }
            catch (OperationCanceledException) { throw; }
            catch
            {
                return 1;
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

        // "openharmony6" — 12 bytes, exactly replaces ffmpeg's "LavfXX.XX.XXX"
        private static readonly byte[] OpenharmonyTooBytes =
            Encoding.ASCII.GetBytes("openharmony6");

        // ffmpeg writes ©too = "LavfXX.XX.XXX" in the MP4 moov/udta.
        // Patch it to "openharmony6" so Huawei Gallery recognizes the file.
        // Strategy: search the file for the unique "Lavf" marker (appears exactly
        // once in the entire combined file, inside the MP4's ©too atom).
        internal static void PatchMp4TooAtom(string targetPath)
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
                int toRead = (int)Math.Min(buf.Length, fileSize - pos);
                fs.Seek(pos, SeekOrigin.Begin);
                int actual = fs.Read(buf, 0, toRead);

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
                pos += actual - 3; // overlap to catch cross-chunk match
            }

            if (lavfPos < 0) return; // No "Lavf" — nothing to patch

            // Write "openharmony6" over "Lavf62.3.100"
            fs.Seek(lavfPos, SeekOrigin.Begin);
            fs.Write(OpenharmonyTooBytes, 0, 12);
            fs.Flush();

            LogService.Merge("©too patched: Lavf → openharmony6", LogLevel.Debug);
        }

        // ── HarmonyOS 4.0 MP4 source pre-processing ──

        // Modify a standalone MP4 file to match HarmonyOS 4.0 format:
        // 1. ftyp: 32→24B (2 compat brands)
        // 2. Remove mdat box header (8B) — video data becomes raw
        // 3. Adjust all stco/co64 offsets in moov by -(8 + ftypDiff)
        // Returns the new file size.
        internal static long PatchSourceMp4ForHarmonyOS4(string mp4Path)
        {
            long fileSize = new FileInfo(mp4Path).Length;
            byte[] all = File.ReadAllBytes(mp4Path);


            // Find ftyp
            int ftypOff = -1;
            for (int i = 0; i < Math.Min(all.Length, 65536); i++)
            {
                if (all[i] == 'f' && all[i+1] == 't' && all[i+2] == 'y' && all[i+3] == 'p')
                { ftypOff = i - 4; break; }
            }

            uint origFtypSize = BinaryPrimitives.ReadUInt32BigEndian(
                new ReadOnlySpan<byte>(all, ftypOff, 4));
            int ftypDiff = (int)origFtypSize - 24;
            if (ftypDiff < 0) return fileSize;

            // Parse boxes to find mdat and moov
            int pos = ftypOff + (int)origFtypSize;
            int mdatOff = -1, moovOff = -1;
            uint moovSize = 0;
            while (pos < all.Length - 8)
            {
                uint bs = BinaryPrimitives.ReadUInt32BigEndian(
                    new ReadOnlySpan<byte>(all, pos, 4));
                if (bs < 8) break;
                string cc = Encoding.ASCII.GetString(all, pos + 4, 4);
                if (cc == "mdat") { mdatOff = pos; }
                if (cc == "moov") { moovOff = pos; moovSize = bs; }
                if (cc == "free" || cc == "wide") { pos += (int)bs; continue; }
                pos += (int)bs;
                if (pos > all.Length - 100) break;
            }
            if (mdatOff < 0 || moovOff < 0) return fileSize;

            int totalGap = ftypDiff; // ftyp shrink only — keep mdat box intact (Mate 60 requires it)

            // Adjust stco/co64 offsets in moov
            for (int mi = moovOff + 8; mi < moovOff + (int)moovSize - 12; mi++)
            {
                bool isStco = all[mi] == 's' && all[mi+1] == 't' && all[mi+2] == 'c' && all[mi+3] == 'o';
                bool isCo64 = all[mi] == 'c' && all[mi+1] == 'o' && all[mi+2] == '6' && all[mi+3] == '4';
                if (!isStco && !isCo64) continue;

                int ec = BinaryPrimitives.ReadInt32BigEndian(
                    new ReadOnlySpan<byte>(all, mi + 8, 4));
                if (ec <= 0 || ec > 100000) continue;

                if (isStco)
                {
                    for (int e = 0; e < ec; e++)
                    {
                        int o = mi + 12 + e * 4;
                        if (o + 4 > all.Length) break;
                        uint v = BinaryPrimitives.ReadUInt32BigEndian(
                            new ReadOnlySpan<byte>(all, o, 4));
                        v = (uint)(v - totalGap);
                        all[o] = (byte)(v >> 24); all[o+1] = (byte)(v >> 16);
                        all[o+2] = (byte)(v >> 8); all[o+3] = (byte)v;
                    }
                }
                else
                {
                    for (int e = 0; e < ec; e++)
                    {
                        int o = mi + 12 + e * 8;
                        if (o + 8 > all.Length) break;
                        ulong v = BinaryPrimitives.ReadUInt64BigEndian(
                            new ReadOnlySpan<byte>(all, o, 8));
                        v -= (ulong)totalGap;
                        all[o] = (byte)(v >> 56); all[o+1] = (byte)(v >> 48);
                        all[o+2] = (byte)(v >> 40); all[o+3] = (byte)(v >> 32);
                        all[o+4] = (byte)(v >> 24); all[o+5] = (byte)(v >> 16);
                        all[o+6] = (byte)(v >> 8); all[o+7] = (byte)v;
                    }
                }
                mi += 8 + ec * (isStco ? 4 : 8);
            }

            // Build new file: [24B ftyp] + [everything after old ftyp, including mdat box]
            // Mate 60 reference samples wrap video data in an mdat box (with extended size).
            // Stripping it breaks playback — the phone expects to find an mdat box after moov.
            int restLen = (int)(fileSize - (ftypOff + (int)origFtypSize));
            int newSize = 24 + restLen;

            byte[] result = new byte[newSize];
            Array.Copy(HarmonyOS4Ftyp, 0, result, 0, 24);
            Array.Copy(all, ftypOff + (int)origFtypSize, result, 24, restLen);

            File.WriteAllBytes(mp4Path, result);

            // HMOS4: remove ©too atom (reference samples have none; our ffmpeg encodes "Lavf…")
            RemoveMp4TooAtom(mp4Path);

            return newSize;
        }

        // ── HarmonyOS 4.0 MP4 ©too removal ──

        internal static void RemoveMp4TooAtom(string targetPath)
        {
            using var fs = new FileStream(targetPath, FileMode.Open,
                FileAccess.ReadWrite, FileShare.None, bufferSize: 4096);
            long fileSize = fs.Length;
            if (fileSize < 1024) return;

            // ©too marker: 0xA9 + "too"
            byte[] tooMarker = { 0xA9, (byte)'t', (byte)'o', (byte)'o' };
            byte[] scanBuf = new byte[65536];
            long pos = 0;
            long tooFilePos = -1;
            int tooAtomSize = 0;

            // Scan entire file for ©too (it's in the MP4 moov/udta, anywhere in the file)
            while (pos < fileSize - 4)
            {
                int toRead = (int)Math.Min(scanBuf.Length, fileSize - pos);
                fs.Seek(pos, SeekOrigin.Begin);
                int actual = fs.Read(scanBuf, 0, toRead);

                for (int i = 0; i <= actual - 4; i++)
                {
                    if (scanBuf[i] == tooMarker[0] && scanBuf[i + 1] == tooMarker[1]
                        && scanBuf[i + 2] == tooMarker[2] && scanBuf[i + 3] == tooMarker[3])
                    {
                        tooFilePos = pos + i - 4; // atom starts at size field
                        tooAtomSize = BinaryPrimitives.ReadInt32BigEndian(
                            new ReadOnlySpan<byte>(scanBuf, i - 4, 4));
                        break;
                    }
                }
                if (tooFilePos >= 0) break;
                pos += actual - 3;
            }

            if (tooFilePos < 0 || tooAtomSize < 8) return;

            // Zero out the ©too atom
            byte[] zeros = new byte[tooAtomSize];
            fs.Seek(tooFilePos, SeekOrigin.Begin);
            fs.Write(zeros, 0, tooAtomSize);
            fs.Flush();

            LogService.Merge("©too removed for HarmonyOS 4.0", LogLevel.Debug);
        }

        // ── HarmonyOS 4.0 MP4 format patch ──

        // Pre-built 24-byte HarmonyOS 4.0 ftyp box: mp42, minor=0, compat=[isom, mp42]
        private static readonly byte[] HarmonyOS4Ftyp = new byte[24]
        {
            0x00, 0x00, 0x00, 0x18, // size = 24
            0x66, 0x74, 0x79, 0x70, // "ftyp"
            0x6D, 0x70, 0x34, 0x32, // major brand: "mp42"
            0x00, 0x00, 0x00, 0x00, // minor version: 0
            0x69, 0x73, 0x6F, 0x6D, // compat 1: "isom"
            0x6D, 0x70, 0x34, 0x32, // compat 2: "mp42"
        };

        // Patch MP4 ftyp to match HarmonyOS 4.0: 24 bytes, 2 compat brands.
        // Rebuilds the file: [JPEG] + [new ftyp(24) + rest of old MP4 after ftyp] + [tail(60)]
        internal static void PatchMp4ToHarmonyOS4Format(string targetPath)
        {
            long fileSize = new FileInfo(targetPath).Length;
            if (fileSize < 1024) return;

            string tempPath = targetPath + ".hw4tmp";

            using (var src = new FileStream(targetPath, FileMode.Open,
                FileAccess.Read, FileShare.None, bufferSize: 4096))
            using (var dst = new FileStream(tempPath, FileMode.Create,
                FileAccess.Write, FileShare.None, bufferSize: 4096))
            {
                // Find MP4 ftyp
                byte[] scanBuf = new byte[65536];
                long searchPos = 0;

                Span<byte> first4 = stackalloc byte[4];
                src.Seek(0, SeekOrigin.Begin);
                src.ReadExactly(first4);
                uint firstVal = BinaryPrimitives.ReadUInt32BigEndian(first4);
                searchPos = (firstVal >= 8 && firstVal <= fileSize) ? firstVal : 0;

                long mp4FtypOff = -1;
                while (searchPos < fileSize - 8)
                {
                    int toRead = (int)Math.Min(scanBuf.Length, fileSize - searchPos);
                    src.Seek(searchPos, SeekOrigin.Begin);
                    int actual = src.Read(scanBuf, 0, toRead);
                    bool found = false;
                    for (int i = 0; i <= actual - 8; i++)
                    {
                        if (scanBuf[i] == 'f' && scanBuf[i + 1] == 't'
                            && scanBuf[i + 2] == 'y' && scanBuf[i + 3] == 'p')
                        {
                            mp4FtypOff = searchPos + i - 4;
                            found = true;
                            break;
                        }
                    }
                    if (found) break;
                    searchPos += actual - 3;
                }

                if (mp4FtypOff < 0)
                {
                    // Fallback: just copy the file unchanged
                    src.Seek(0, SeekOrigin.Begin);
                    src.CopyTo(dst);
                    File.Move(tempPath, targetPath, true);
                    return;
                }

                // Read original ftyp size
                src.Seek(mp4FtypOff, SeekOrigin.Begin);
                Span<byte> sz4 = stackalloc byte[4];
                src.ReadExactly(sz4);
                long origFtypSize = BinaryPrimitives.ReadUInt32BigEndian(sz4);
                if (origFtypSize < 24)
                {
                    src.Seek(0, SeekOrigin.Begin);
                    src.CopyTo(dst);
                    File.Move(tempPath, targetPath, true);
                    return;
                }

                // Build new file:
                // [0 .. mp4FtypOff)  = JPEG image (unchanged)
                // [24B new ftyp]
                // [mp4FtypOff + origFtypSize .. fileSize - 60) = rest of MP4 after old ftyp
                // [fileSize - 60 .. fileSize) = 60B tail (unchanged)

                // Part 1: JPEG image
                src.Seek(0, SeekOrigin.Begin);
                CopyBytes(src, dst, mp4FtypOff);

                // Part 2: New 24-byte ftyp
                dst.Write(HarmonyOS4Ftyp, 0, 24);

                // Part 3: Rest of MP4 (skip old ftyp, include up to tail start)
                long afterFtypStart = mp4FtypOff + origFtypSize;
                long tailStart = fileSize - 60;
                long mp4RestLen = tailStart - afterFtypStart;
                src.Seek(afterFtypStart, SeekOrigin.Begin);
                CopyBytes(src, dst, mp4RestLen);

                // Part 4: Tail
                src.Seek(tailStart, SeekOrigin.Begin);
                CopyBytes(src, dst, 60);
            }

            File.Move(tempPath, targetPath, true);

            LogService.Merge(
                "HarmonyOS 4.0 MP4 patch: rebuilt file, ftyp 32→24B",
                LogLevel.Debug);
        }

        private static void CopyBytes(FileStream src, FileStream dst, long count)
        {
            byte[] buf = new byte[65536];
            long remain = count;
            while (remain > 0)
            {
                int toRead = (int)Math.Min(buf.Length, remain);
                int actual = src.Read(buf, 0, toRead);
                if (actual <= 0) break;
                dst.Write(buf, 0, actual);
                remain -= actual;
            }
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
