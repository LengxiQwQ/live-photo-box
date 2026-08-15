using LivePhotoBox.Models;
using System;
using System.Diagnostics;
using System.Globalization;
using System.IO;
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
    // 已知限制（见报告）：
    //   a. HEIC 源（sourceImageIsHeic）无 JPG 可预注入 MakerNote，图片端 Apple
    //      MakerNote 缺失（视频端仍完整）；需 HEIC 二进制注入或 -TagsFromFile 兜底。
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

            // 2. 封面帧时间戳（源 XMP 微秒 → 秒；无字段则视频中点）。
            double coverSeconds = await ResolveCoverSecondsAsync(metadataText, videoOutputPath, token);

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
        // 源单文件的封面帧时间戳（MotionPhotoPresentationTimestampUs，微秒）→ 秒；
        // 无此字段则用视频时长的中点兜底（参照协议文档）。
        private static async Task<double> ResolveCoverSecondsAsync(
            string metadataText, string videoOutputPath, CancellationToken token)
        {
            // 1. 优先读 XMP MotionPhotoPresentationTimestampUs（微秒）。
            var m = MotionPhotoTimestampRegex.Match(metadataText ?? "");
            if (m.Success &&
                long.TryParse(m.Groups[1].Value, NumberStyles.Integer, CultureInfo.InvariantCulture, out long us) &&
                us > 0)
            {
                double seconds = us / 1_000_000.0;
                LogService.Split($"Apple[cover] source MotionPhotoPresentationTimestampUs={us}us → {seconds:F4}s", LogLevel.Debug);
                return seconds;
            }

            // 2. 兜底：视频时长中点。
            double? duration = await ReadVideoDurationSecondsAsync(videoOutputPath, token);
            if (duration.HasValue && duration.Value > 0)
            {
                double seconds = duration.Value / 2.0;
                LogService.Split($"Apple[cover] no source timestamp, using video midpoint: {seconds:F4}s (duration={duration.Value:F4}s)", LogLevel.Debug);
                return seconds;
            }

            LogService.Split("Apple[cover] could not resolve cover timestamp (no source field, no duration)", LogLevel.Warning);
            return 0;
        }

        // ── 读取辅助 ────────────────────────────────────────────────────────

        // 读取视频时长（秒），用于封面帧中点兜底。优先 MediaDuration，回退 Duration。
        private static async Task<double?> ReadVideoDurationSecondsAsync(string videoOutputPath, CancellationToken token)
        {
            try
            {
                var root = await ReadExifToolJsonAsync(videoOutputPath, token, "-n", "-MediaDuration", "-Duration");
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

        // 运行 exiftool -j 读取 JSON（一次性模式），返回首个对象的 JsonElement。
        private static async Task<JsonElement> ReadExifToolJsonAsync(
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
                throw new InvalidOperationException("exiftool returned no JSON");

            using var doc = JsonDocument.Parse(stdout);
            if (doc.RootElement.ValueKind != JsonValueKind.Array || doc.RootElement.GetArrayLength() == 0)
                throw new InvalidOperationException("exiftool returned empty array");
            return doc.RootElement[0];
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
