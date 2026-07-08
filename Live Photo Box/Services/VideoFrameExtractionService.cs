/*
 * VideoFrameExtractionService.cs
 *
 * 视频帧提取服务 — 使用 ffmpeg（项目已有 Tools/ffmpeg.exe 定制版）
 * 将视频全部帧提取为缩略图 JPEG 文件，存储于临时目录。
 *
 * 参考 ThumbnailService.LoadVideoThumbnailDataAsync 的 Process.Start 模式，
 * 但这里提取全部帧（-vsync 0）而非仅第一帧。
 *
 * 所有帧一次性输出到临时目录（frame_000001.jpg ~ frame_NNNNNN.jpg），
 * 调用方负责按序读取并创建 BitmapImage，最后清理临时目录。
 */

using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

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
        /// 使用 ffmpeg 将视频全部帧提取为缩略图 JPEG，输出到临时目录。
        /// </summary>
        /// <param name="videoPath">视频文件路径</param>
        /// <param name="thumbWidth">缩略图宽度（px），高度按比例缩放</param>
        /// <param name="ct">取消令牌</param>
        /// <returns>提取结果（临时目录 + 帧数 + JPEG 路径列表），失败返回 null</returns>
        public static async Task<FrameExtractionResult?> ExtractAllFramesAsync(
            string videoPath, int thumbWidth, CancellationToken ct)
        {
            string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
            {
                LogService.FileOp("VideoFrameExtraction: ffmpeg not found", Models.LogLevel.Warning);
                return null;
            }

            // 创建临时输出目录
            string tempDir = Path.Combine(Path.GetTempPath(), $"lpb_frames_{Guid.NewGuid():N}");
            Directory.CreateDirectory(tempDir);

            try
            {
                ct.ThrowIfCancellationRequested();

                // ffmpeg 参数：
                //   -vsync 0      — 不丢帧不重复，每帧都输出
                //   scale=w:-1   — 宽度缩放到目标值，高度自动保持比例
                //   -q:v 3       — JPEG 质量（2-5，3=高质量小体积）
                //   frame_%06d   — 六位零填充序号（ffmpeg 从 1 开始编号）
                string outputPattern = Path.Combine(tempDir, "frame_%06d.jpg");
                string scaleFilter = $"scale={thumbWidth}:-1:force_original_aspect_ratio=decrease";

                string args = $"-i \"{videoPath}\" -vsync 0 -vf \"{scaleFilter}\" " +
                              $"-q:v 3 -f image2 \"{outputPattern}\" -y -loglevel error";

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

                // 等待 ffmpeg 完成，30 秒超时保护
                using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);

                try
                {
                    await process.WaitForExitAsync(linkedCts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch { }
                    CleanupTempDir(tempDir);
                    return null;
                }

                // 检查 ffmpeg 是否成功
                string stderr = await process.StandardError.ReadToEndAsync();
                if (process.ExitCode != 0 && !string.IsNullOrWhiteSpace(stderr))
                {
                    LogService.FileOp(
                        $"VideoFrameExtraction ffmpeg error (exit {process.ExitCode}): {stderr.Trim()}",
                        Models.LogLevel.Warning);
                }

                // 收集输出文件（按路径排序，frame_000001 在前）
                var jpegPaths = new List<string>();
                try
                {
                    var files = Directory.GetFiles(tempDir, "frame_*.jpg");
                    Array.Sort(files, StringComparer.OrdinalIgnoreCase);
                    jpegPaths.AddRange(files);
                }
                catch (Exception ex)
                {
                    LogService.FileOp($"VideoFrameExtraction: failed to list output files: {ex.Message}",
                        Models.LogLevel.Error, ex);
                    CleanupTempDir(tempDir);
                    return null;
                }

                if (jpegPaths.Count == 0)
                {
                    LogService.FileOp("VideoFrameExtraction: no frames extracted (empty output)",
                        Models.LogLevel.Warning);
                    CleanupTempDir(tempDir);
                    return null;
                }

                LogService.FileOp(
                    $"VideoFrameExtraction: {jpegPaths.Count} frames extracted to '{tempDir}'",
                    Models.LogLevel.Info);

                return new FrameExtractionResult
                {
                    TempDirectory = tempDir,
                    FrameCount = jpegPaths.Count,
                    JpegPaths = jpegPaths
                };
            }
            catch (OperationCanceledException)
            {
                CleanupTempDir(tempDir);
                throw;
            }
            catch (Exception ex)
            {
                LogService.FileOp($"VideoFrameExtraction failed: {ex.Message}",
                    Models.LogLevel.Error, ex);
                CleanupTempDir(tempDir);
                return null;
            }
        }

        /// <summary>安全清理临时目录</summary>
        private static void CleanupTempDir(string tempDir)
        {
            try
            {
                if (Directory.Exists(tempDir))
                    Directory.Delete(tempDir, recursive: true);
            }
            catch (Exception ex)
            {
                LogService.FileOp($"VideoFrameExtraction: failed to cleanup temp dir: {ex.Message}",
                    Models.LogLevel.Warning);
            }
        }
    }
}
