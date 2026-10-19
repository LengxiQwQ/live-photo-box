/*
 * LivePhotoVideoExtractor.cs
 *
 * 实况照片视频提取共享方法。
 * 通过 Rebuilt 单一权威链路（SourceInspector.InspectWithPlanAsync +
 * NativeMediaService.ExtractMediaAsync）将内嵌实况视频提取到受验证的临时文件，
 * 供灯箱、Edit 页面等 UI 预览统一使用。
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
        /// 按 Rebuilt 单一权威计划自动探测并提取嵌入的视频到临时文件。
        /// </summary>
        /// <param name="filePath">实况照片文件路径</param>
        /// <param name="appendedVideoLength">保留兼容参数（Rebuilt 链路以 Inspector 权威范围为准）</param>
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
                using var inspected = await new SourceInspector()
                    .InspectWithPlanAsync(filePath, null, ct).ConfigureAwait(false);
                SourceMediaFacts facts = inspected.Facts;
                if (facts.MotionVideo is not { IsPresent: true } videoFacts
                    || videoFacts.ByteLength <= 0)
                    return null;

                string extension = videoFacts.Container == VideoContainer.Mov ? ".mov" : ".mp4";
                string outputPath = Path.Combine(Path.GetTempPath(), $"lpb_live_{Guid.NewGuid():N}{extension}");
                await NativeMediaService.ExtractMediaAsync(
                    inspected.ExtractionPlan,
                    filePath,
                    null,
                    null,
                    outputPath,
                    null,
                    ct).ConfigureAwait(false);

                return File.Exists(outputPath) && new FileInfo(outputPath).Length == videoFacts.ByteLength
                    ? outputPath
                    : null;
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
