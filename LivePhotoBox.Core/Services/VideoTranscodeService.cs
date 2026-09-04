using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LivePhotoBox.Models;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Video;

namespace LivePhotoBox.Services
{
    // 视频转码服务 - 已迁移至 Native 引擎
    // 不再调用外部 FFmpeg/ExifTool 等工具
    public static class VideoTranscodeService
    {
        // 目标视频格式
        public enum VideoFormat
        {
            MP4,
            MOV
        }

        // 视频转码结果
        public class TranscodeResult
        {
            public bool Success { get; set; }
            public string? OutputPath { get; set; }
            public string? ErrorMessage { get; set; }
            public TimeSpan Duration { get; set; }
            public bool WasRemux { get; set; }
        }

        // 检查编码器是否可用（外部 FFmpeg 已移除，返回 false）
        private static bool IsEncoderAvailable(string encoder) => false;

        // 快速容器转换（Remux）- 通过 Native VideoConverter 转换
        public static async Task<TranscodeResult> RemuxAsync(
            string inputPath,
            string outputPath,
            CancellationToken token = default,
            bool useFaststart = true)
        {
            var sw = Stopwatch.StartNew();
            if (!File.Exists(inputPath))
            {
                return new TranscodeResult
                {
                    Success = false,
                    ErrorMessage = $"Input file not found: {inputPath}",
                    WasRemux = true
                };
            }

            try
            {
                var converter = new VideoConverter();
                VideoFacts probe = await converter.ProbeAsync(inputPath, token).ConfigureAwait(false);
                bool isMp4 = Path.GetExtension(outputPath).Equals(".mp4", StringComparison.OrdinalIgnoreCase);

                string outDir = Path.GetDirectoryName(outputPath) ?? ".";
                Directory.CreateDirectory(outDir);

                var result = await converter.ConvertAsync(new VideoConversionRequest
                {
                    SourceArtifact = new MediaArtifact
                    {
                        Path = inputPath,
                        Kind = MediaArtifactKind.MotionVideo,
                        VideoContainer = probe.Container,
                        VideoCodec = probe.Codec,
                        ByteLength = new FileInfo(inputPath).Length
                    },
                    TargetContainer = isMp4 ? VideoContainer.Mp4 : VideoContainer.Mov,
                    TargetCodec = probe.Codec,
                    TargetDirectory = outDir
                }, token).ConfigureAwait(false);

                sw.Stop();
                if (result.Success && result.OutputArtifact != null)
                {
                    File.Copy(result.OutputArtifact.Path, outputPath, overwrite: true);
                    try { File.Delete(result.OutputArtifact.Path); } catch { }
                    return new TranscodeResult
                    {
                        Success = true,
                        OutputPath = outputPath,
                        Duration = sw.Elapsed,
                        WasRemux = true
                    };
                }

                return new TranscodeResult
                {
                    Success = false,
                    ErrorMessage = result.ErrorMessage,
                    Duration = sw.Elapsed,
                    WasRemux = true
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new TranscodeResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    Duration = sw.Elapsed,
                    WasRemux = true
                };
            }
        }

        // 将视频转换为 MP4 格式
        public static async Task<TranscodeResult> TranscodeToMp4Async(
            string inputPath,
            string outputPath,
            CancellationToken token = default,
            bool useFaststart = true,
            string videoCodec = "h264")
        {
            return await TranscodeInternalAsync(inputPath, outputPath, VideoContainer.Mp4, videoCodec, token).ConfigureAwait(false);
        }

        // 将视频转换为 MOV 格式
        public static async Task<TranscodeResult> TranscodeToMovAsync(
            string inputPath,
            string outputPath,
            CancellationToken token = default,
            string videoCodec = "h264",
            int? keyframeInterval = null)
        {
            return await TranscodeInternalAsync(inputPath, outputPath, VideoContainer.Mov, videoCodec, token).ConfigureAwait(false);
        }

        private static async Task<TranscodeResult> TranscodeInternalAsync(
            string inputPath,
            string outputPath,
            VideoContainer targetContainer,
            string videoCodec,
            CancellationToken token)
        {
            var sw = Stopwatch.StartNew();
            if (!File.Exists(inputPath))
            {
                return new TranscodeResult
                {
                    Success = false,
                    ErrorMessage = $"Input file not found: {inputPath}"
                };
            }

            try
            {
                var converter = new VideoConverter();
                VideoFacts probe = await converter.ProbeAsync(inputPath, token).ConfigureAwait(false);
                string outDir = Path.GetDirectoryName(outputPath) ?? ".";
                Directory.CreateDirectory(outDir);

                VideoCodec targetCodec = string.Equals(videoCodec, "hevc", StringComparison.OrdinalIgnoreCase)
                    ? VideoCodec.Hevc
                    : VideoCodec.H264;

                var result = await converter.ConvertAsync(new VideoConversionRequest
                {
                    SourceArtifact = new MediaArtifact
                    {
                        Path = inputPath,
                        Kind = MediaArtifactKind.MotionVideo,
                        VideoContainer = probe.Container,
                        VideoCodec = probe.Codec,
                        ByteLength = new FileInfo(inputPath).Length
                    },
                    TargetContainer = targetContainer,
                    TargetCodec = targetCodec,
                    TargetDirectory = outDir,
                    Crf = 23
                }, token).ConfigureAwait(false);

                sw.Stop();
                if (result.Success && result.OutputArtifact != null)
                {
                    File.Copy(result.OutputArtifact.Path, outputPath, overwrite: true);
                    try { File.Delete(result.OutputArtifact.Path); } catch { }
                    return new TranscodeResult
                    {
                        Success = true,
                        OutputPath = outputPath,
                        Duration = sw.Elapsed,
                        WasRemux = result.ExecutionRecord?.RemuxUsed ?? false
                    };
                }

                return new TranscodeResult
                {
                    Success = false,
                    ErrorMessage = result.ErrorMessage ?? "Native video conversion failed.",
                    Duration = sw.Elapsed
                };
            }
            catch (Exception ex)
            {
                sw.Stop();
                return new TranscodeResult
                {
                    Success = false,
                    ErrorMessage = ex.Message,
                    Duration = sw.Elapsed
                };
            }
        }

        // GIF 导出（已移除外部 FFmpeg，返回不支持）
        public static Task<TranscodeResult> TranscodeToGifAsync(
            string inputPath,
            string outputPath,
            int fps,
            int width,
            int height,
            int loopCount,
            CancellationToken token = default)
        {
            return Task.FromResult(new TranscodeResult
            {
                Success = false,
                ErrorMessage = "GIF export requires external FFmpeg which has been removed."
            });
        }

        // 保证视频为 MP4
        public static async Task<(string Path, bool WasTranscoded)> EnsureMp4Async(
            string inputPath,
            string workDir,
            CancellationToken token = default,
            bool forceMp4 = true,
            bool useFaststart = true,
            string videoCodec = "h264")
        {
            string actionLabel = videoCodec == "copy" ? "Remuxing (HEVC passthrough)" : "Auto-transcoding";
            LogService.Merge(
                $"{actionLabel} to MP4: '{Path.GetFileName(inputPath)}'",
                LogLevel.Debug);

            // 临时文件名由 TempFileService 分配（GUID 后缀），并发任务互不冲突，
            // 无需再先删除可能存在的旧文件。
            string tempPath = TempFileService.AllocateTempPath(workDir, "merge_trans", "mp4");

            var result = await TranscodeToMp4Async(inputPath, tempPath, token, useFaststart, videoCodec);

            if (!result.Success)
            {
                string msg = result.ErrorMessage ?? "Unknown error";
                LogService.Merge(
                    $"Transcode failed: {msg}", LogLevel.Error);
                throw new InvalidOperationException(
                    $"Failed to transcode video to MP4: {msg}");
            }

            string label = result.WasRemux ? "remuxed" : "transcoded";
            LogService.Merge(
                $"Video {label} ({result.Duration.TotalSeconds:F1}s): " +
                $"{Path.GetFileName(inputPath)} → {Path.GetFileName(tempPath)}",
                LogLevel.Debug);

            return (tempPath, true);
        }
    }
}

