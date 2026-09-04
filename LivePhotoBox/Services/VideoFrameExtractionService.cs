/*
 * VideoFrameExtractionService.cs
 *
 * 视频帧提取服务 — 使用 ffmpeg（项目已有 Tools/ffmpeg.exe 定制版）
 * 将视频全部帧提取为缩略图 JPEG 文件，存储于临时目录。
 *
 * 参考 ThumbnailService.LoadVideoThumbnailDataAsync 的 Process.Start 模式，
 * 但这里提取全部帧（-fps_mode passthrough）而非仅第一帧。
 *
 * 所有帧一次性输出到临时目录（frame_000001.jpg ~ frame_NNNNNN.jpg），
 * 调用方负责按序读取并创建 BitmapImage，最后清理临时目录。
 *
 * 单帧提取方法 ExtractFrameAtTimestampAsync 在 Core 层 LivePhotoMergeService 中，
 * 供 GUI 和 CLI 共用。
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services
{
    /// <summary>ffmpeg 帧提取结果</summary>
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
