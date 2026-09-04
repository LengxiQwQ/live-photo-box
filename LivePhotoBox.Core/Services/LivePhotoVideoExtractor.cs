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

            return await ExtractVideoRebuiltAsync(filePath, ct).ConfigureAwait(false);
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
                    null,
                    outputPath,
                    null,
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

        /// <summary>
        /// 按协议从实况照片文件中提取嵌入的视频到临时文件。
        /// </summary>
        public static Task<string?> ExtractVideoAsync(
            string filePath,
            LivePhotoProtocolType protocol,
            long appendedVideoLength,
            CancellationToken ct = default) =>
            ExtractVideoAutoAsync(filePath, appendedVideoLength, ct);
    }
}
