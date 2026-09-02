/*
 * LivePhotoVideoExtractor.cs
 *
 * 实况照片视频提取共享方法。
 * 按协议从实况照片文件中提取嵌入的视频数据到临时文件，
 * 供灯箱、Edit 页面等统一使用，避免三份重复代码。
 *
 * 覆盖协议：
 * - 华为/荣耀：GetHuaweiEmbeddedVideoRange（moov/ftyp 定位中间段）
 * - 三星/融合 JPEG：FindSamsungJpegVideoRange（MotionPhoto_Data 标签定位）
 * - 三星/融合/Google V2 HEIC：mpvd box 定位
 * - Google V1/V2/OPPO/vivo/小米：文件尾部切取（AppendedVideoLength）
 */

using LivePhotoBox.Models;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    public static class LivePhotoVideoExtractor
    {
        /// <summary>
        /// 按视频存储位置顺序自动探测并提取嵌入的视频到临时文件。
        /// 不依赖协议枚举，按固定顺序尝试：
        ///   ① 尾部含 "LIVE_" → 华为 moov 定位（视频在文件中间）
        ///   ② HEIC/HEIF 含 mpvd box → 从 box 提取
        ///   ③ 尾部含 SEFH+SEFT → MotionPhoto_Data 标签提取（三星 JPEG）
        ///   ④ appendedVideoLength &gt; 0 → 文件末尾前推切取（Google V1/V2/OPPO/vivo/小米）
        /// </summary>
        /// <param name="filePath">实况照片文件路径</param>
        /// <param name="appendedVideoLength">尾部视频长度（尾部追加协议的值，未知传 0）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>临时视频文件路径，失败返回 null</returns>
        public static async Task<string?> ExtractVideoAutoAsync(
            string filePath,
            long appendedVideoLength,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            if (ProcessingBackendSettingsService.Load().Mode == ProcessingPipelineMode.Rebuilt)
                return await ExtractVideoRebuiltAsync(filePath, ct).ConfigureAwait(false);

            await Task.Yield();

            try
            {
                // ── ① 华为/荣耀：文件尾 60B 有 LIVE_ 标记，视频在文件中间 ──
                //    必须先探测华为——它的视频后面还有 60B 尾标，不能按尾部切取
                if (HasTailMarker(filePath, "LIVE_"u8))
                {
                    var range = LivePhotoSplitService.GetHuaweiEmbeddedVideoRange(filePath);
                    if (range != null)
                    {
                        var (videoStart, _, videoLength) = range.Value;
                        return await ExtractRangeToTempAsync(filePath, videoStart, videoLength, ct);
                    }
                }

                // ── ② 三星/Google V2 HEIC：mpvd box 内嵌视频 ──
                if (IsHeicFile(filePath))
                {
                    long mpvdLen = LivePhotoMergeService.GetMpvdVideoLength(filePath);
                    if (mpvdLen > 0)
                    {
                        long mpvdStart = LivePhotoMergeService.GetMpvdVideoStart(filePath);
                        return await ExtractRangeToTempAsync(filePath, mpvdStart, mpvdLen, ct);
                    }
                }

                // ── ③ 三星 JPEG：SEFH+SEFT 验证区 + MotionPhoto_Data 标签 ──
                //    视频在 Trailer 内（SEF 区之前），不能直接切尾
                if (!IsHeicFile(filePath) && HasTailMarker(filePath, "SEFH"u8))
                {
                    var samsungRange = LivePhotoSplitService.FindSamsungJpegVideoRange(filePath);
                    if (samsungRange != null)
                    {
                        var (videoStart, videoLength) = samsungRange.Value;
                        return await ExtractRangeToTempAsync(filePath, videoStart, videoLength, ct);
                    }
                }

                // ── ④ 尾部追加协议（Google V1/V2/OPPO/vivo/小米）──
                //    最通用兜底：从文件末尾前推 appendedVideoLength 字节切取
                if (appendedVideoLength > 0)
                {
                    return await ExtractTailToTempAsync(filePath, appendedVideoLength, ct);
                }

                return null;
            }
            catch (Exception ex)
            {
                LogService.Split($"LivePhotoVideoExtractor failed: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        private static async Task<string?> ExtractVideoRebuiltAsync(string filePath, CancellationToken ct)
        {
            try
            {
                SourceMediaFacts facts = await new SourceInspector()
                    .InspectAsync(filePath, null, ct).ConfigureAwait(false);
                if (facts.MotionVideo is not { IsPresent: true } videoFacts
                    || videoFacts.ByteLength <= 0)
                    return null;

                string extension = videoFacts.Container == VideoContainer.Mov ? ".mov" : ".mp4";
                string outputPath = Path.Combine(Path.GetTempPath(), $"lpb_live_{Guid.NewGuid():N}{extension}");
                await NativeMediaService.ExtractMediaAsync(
                    filePath,
                    null,
                    facts,
                    outputImagePath: null,
                    outputVideoPath: outputPath,
                    outputGainmapPath: null,
                    ct).ConfigureAwait(false);

                if (File.Exists(outputPath) && new FileInfo(outputPath).Length == videoFacts.ByteLength)
                    return outputPath;

                try { if (File.Exists(outputPath)) File.Delete(outputPath); } catch { }
                return null;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                LogService.Split($"Rebuilt Native video extraction failed: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        /// <summary>读取文件末尾 4KB 并检查是否包含指定字节标记</summary>
        private static bool HasTailMarker(string filePath, ReadOnlySpan<byte> marker)
        {
            try
            {
                using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                if (fs.Length < marker.Length) return false;
                int readSize = (int)Math.Min(fs.Length, 4096);
                byte[] tailBuf = new byte[readSize];
                fs.Seek(-readSize, SeekOrigin.End);
                fs.ReadExactly(tailBuf, 0, readSize);
                return tailBuf.AsSpan().IndexOf(marker) >= 0;
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 按协议从实况照片文件中提取嵌入的视频到临时文件。
        /// </summary>
        /// <param name="filePath">实况照片文件路径</param>
        /// <param name="protocol">检测到的协议类型</param>
        /// <param name="appendedVideoLength">尾部视频长度（仅尾部追加协议有效，华为/三星/融合传 0）</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>临时视频文件路径，失败返回 null</returns>
        public static async Task<string?> ExtractVideoAsync(
            string filePath,
            LivePhotoProtocolType protocol,
            long appendedVideoLength,
            CancellationToken ct = default)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            await Task.Yield();

            try
            {
                // ── 华为/荣耀：视频在文件中间，靠 moov/ftyp 定位 ──
                if (protocol == LivePhotoProtocolType.Huawei)
                {
                    var range = LivePhotoSplitService.GetHuaweiEmbeddedVideoRange(filePath);
                    if (range != null)
                    {
                        var (videoStart, _, videoLength) = range.Value;
                        return await ExtractRangeToTempAsync(filePath, videoStart, videoLength, ct);
                    }
                    // 定位失败 → 走通用尾部兜底（极少情况）
                }

                // ── 三星/融合 JPEG：视频在 MotionPhoto_Data 标签内 ──
                if (protocol is LivePhotoProtocolType.Samsung or LivePhotoProtocolType.Fusion
                    && !IsHeicFile(filePath))
                {
                    var samsungRange = LivePhotoSplitService.FindSamsungJpegVideoRange(filePath);
                    if (samsungRange != null)
                    {
                        var (videoStart, videoLength) = samsungRange.Value;
                        return await ExtractRangeToTempAsync(filePath, videoStart, videoLength, ct);
                    }
                    // 定位失败 → 走通用尾部兜底
                }

                // ── HEIC mpvd box（三星/融合/Google V2 HEIC） ──
                if (IsHeicFile(filePath))
                {
                    long mpvdLen = LivePhotoMergeService.GetMpvdVideoLength(filePath);
                    if (mpvdLen > 0)
                    {
                        long mpvdStart = LivePhotoMergeService.GetMpvdVideoStart(filePath);
                        return await ExtractRangeToTempAsync(filePath, mpvdStart, mpvdLen, ct);
                    }
                    // 无 mpvd box → 继续走尾部切取（HUAWEI HEIC 无 mpvd，但上面已处理）
                }

                // ── 尾部追加协议（Google V1/V2/OPPO/vivo/小米） ──
                if (appendedVideoLength > 0)
                {
                    return await ExtractTailToTempAsync(filePath, appendedVideoLength, ct);
                }

                return null;
            }
            catch (Exception ex)
            {
                LogService.Split($"LivePhotoVideoExtractor failed: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        /// <summary>从文件指定偏移提取指定长度的字节到临时 mp4 文件</summary>
        private static async Task<string?> ExtractRangeToTempAsync(
            string filePath, long start, long length, CancellationToken ct)
        {
            if (length <= 0) return null;

            string tempPath = Path.Combine(Path.GetTempPath(), $"lpb_live_{Guid.NewGuid():N}.mp4");
            await Task.Run(() =>
            {
                using var src = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                src.Seek(start, SeekOrigin.Begin);
                using var dst = new FileStream(tempPath, FileMode.Create, FileAccess.Write);
                var buf = new byte[81920];
                long remain = length;
                while (remain > 0)
                {
                    ct.ThrowIfCancellationRequested();
                    int r = src.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                    if (r == 0) break;
                    dst.Write(buf, 0, r);
                    remain -= r;
                }
            }, ct);

            return File.Exists(tempPath) ? tempPath : null;
        }

        /// <summary>从文件尾部切取指定长度的视频到临时 mp4 文件</summary>
        private static async Task<string?> ExtractTailToTempAsync(
            string filePath, long length, CancellationToken ct)
        {
            if (length <= 0) return null;

            var fileInfo = new FileInfo(filePath);
            long offset = fileInfo.Length - length;
            if (offset < 0) return null;

            return await ExtractRangeToTempAsync(filePath, offset, length, ct);
        }

        /// <summary>判断是否为 HEIC/HEIF 文件</summary>
        private static bool IsHeicFile(string path)
        {
            string ext = Path.GetExtension(path);
            return ext.Equals(".heic", StringComparison.OrdinalIgnoreCase)
                || ext.Equals(".heif", StringComparison.OrdinalIgnoreCase);
        }
    }
}
