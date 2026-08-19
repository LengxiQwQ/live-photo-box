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
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    public static class LivePhotoVideoExtractor
    {
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