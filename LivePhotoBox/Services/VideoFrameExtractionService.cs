/*
 * VideoFrameExtractionService.cs
 *
 * 视频帧提取服务（占位接口，当前 Native 完整实现尚未就绪，安全返回 null）。
 * 外部 FFmpeg 进程已移除。
 */

using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services
{
    /// <summary>视频帧提取结果</summary>
    public sealed class FrameExtractionResult
    {
        /// <summary>临时目录路径（调用方用完后需删除）</summary>
        public required string TempDirectory { get; init; }

        /// <summary>实际提取的帧数</summary>
        public int FrameCount { get; init; }

        /// <summary>按帧序号排序的 JPEG 文件路径列表</summary>
        public required List<string> JpegPaths { get; init; }
    }

    public static class VideoFrameExtractionService
    {
        /// <summary>
        /// 使用 Native 提取视频帧（当前未开放，安全返回 null）。
        /// </summary>
        public static Task<FrameExtractionResult?> ExtractAllFramesAsync(
            string videoPath, CancellationToken ct)
        {
            LogService.FileOp(
                "VideoFrameExtraction skipped: Rebuilt uses Native media execution; FFmpeg has been removed.",
                Models.LogLevel.Info);
            return Task.FromResult<FrameExtractionResult?>(null);
        }
    }
}
