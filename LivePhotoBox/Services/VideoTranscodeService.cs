using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 视频转码服务 - 使用 FFmpeg 进行视频格式转换
    /// 支持硬件加速 (NVENC/QSV/AMF) 和多线程处理
    /// </summary>
    public static class VideoTranscodeService
    {
        /// <summary>
        /// 目标视频格式
        /// </summary>
        public enum VideoFormat
        {
            MP4,
            MOV
        }

        /// <summary>
        /// 视频转码结果
        /// </summary>
        public class TranscodeResult
        {
            public bool Success { get; set; }
            public string? OutputPath { get; set; }
            public string? ErrorMessage { get; set; }
            public TimeSpan Duration { get; set; }
            public bool WasRemux { get; set; }
        }

        /// <summary>
        /// 获取当前选择的硬件编码器
        /// </summary>
        private static string? GetCurrentEncoder()
        {
            return AppSettingsService.GetValue<string?>("SplitHardwareEncoder", null);
        }

        /// <summary>
        /// 获取当前线程数设置
        /// </summary>
        private static int GetThreadCount()
        {
            return AppSettingsService.GetValue<int>("SplitThreadCount", Environment.ProcessorCount);
        }

        /// <summary>
        /// 快速容器转换（Remux）- 无损转换视频容器格式，完整保留 HDR 和所有元数据
        /// </summary>
        /// <param name="inputPath">输入视频路径</param>
        /// <param name="outputPath">输出视频路径</param>
        /// <param name="token">取消令牌</param>
        /// <returns>转换结果</returns>
        public static async Task<TranscodeResult> RemuxAsync(
            string inputPath,
            string outputPath,
            CancellationToken token = default)
        {
            var result = new TranscodeResult { WasRemux = true };
            var stopwatch = Stopwatch.StartNew();

            if (!File.Exists(inputPath))
            {
                result.Success = false;
                result.ErrorMessage = $"Input file not found: {inputPath}";
                AppLogService.Split($"Remux failed: {result.ErrorMessage}", LogLevel.Error);
                return result;
            }

            string? ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                result.Success = false;
                result.ErrorMessage = "FFmpeg not found.";
                AppLogService.Split("Remux failed: FFmpeg not found", LogLevel.Error);
                return result;
            }

            AppLogService.Split($"Starting remux: {Path.GetFileName(inputPath)}");

            try
            {
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? string.Empty);

                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                // Remux 参数说明:
                // -c copy: 直接复制视频和音频流，不重新编码（完全无损）
                // -map 0: 保留所有流（视频、音频、字幕、数据等）
                // -map_metadata 0: 保留全局元数据
                // -map_metadata 0:v: 复制视频流元数据（包含 HDR 信息）
                // -movflags: 容器特定选项
                //
                // 这种方式会完整保留：
                // - HDR 元数据和色彩空间
                // - 所有视频参数（分辨率、帧率、比特率）
                // - 音频质量（原始编码）
                // - EXIF 元数据（拍摄日期、经纬度等）
                // - 任何附加数据流

                string extension = Path.GetExtension(outputPath).ToLowerInvariant();
                string movflags = extension == ".mp4" ? "+faststart" : "";

                string arguments = string.IsNullOrEmpty(movflags)
                    ? $"-y -i \"{inputPath}\" -c copy -map 0 -map_metadata 0 -map_metadata 0:v \"{outputPath}\""
                    : $"-y -i \"{inputPath}\" -c copy -map 0 -map_metadata 0 -map_metadata 0:v -movflags {movflags} \"{outputPath}\"";

                using var process = new Process();
                process.StartInfo.FileName = ffmpegPath;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.RedirectStandardOutput = true;

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => tcs.TrySetResult(true);

                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Failed to start FFmpeg: {ex.Message}";
                    AppLogService.Split($"Remux failed: {result.ErrorMessage}", LogLevel.Error, ex);
                    return result;
                }

                using var registration = token.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                    }
                    catch { }
                    tcs.TrySetCanceled();
                });

                await tcs.Task.ConfigureAwait(false);

                if (token.IsCancellationRequested)
                {
                    result.Success = false;
                    result.ErrorMessage = "Remux cancelled by user";
                    return result;
                }

                stopwatch.Stop();
                result.Duration = stopwatch.Elapsed;

                if (process.ExitCode == 0 && File.Exists(outputPath))
                {
                    result.Success = true;
                    result.OutputPath = outputPath;
                    AppLogService.Split($"Remux completed: {Path.GetFileName(outputPath)} ({result.Duration.TotalSeconds:F1}s)", LogLevel.Info);
                }
                else
                {
                    string errorOutput = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                    result.Success = false;
                    result.ErrorMessage = $"FFmpeg exited with code {process.ExitCode}. Output: {errorOutput}";
                    AppLogService.Split($"Remux failed: {result.ErrorMessage}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.Duration = stopwatch.Elapsed;
                result.Success = false;
                result.ErrorMessage = ex.Message;
                AppLogService.Split($"Remux error: {ex.Message}", LogLevel.Error, ex);
            }

            return result;
        }

        /// <summary>
        /// 将视频转换为 MP4 格式 (H.264/AAC)
        /// </summary>
        /// <param name="inputPath">输入视频路径</param>
        /// <param name="outputPath">输出视频路径</param>
        /// <param name="token">取消令牌</param>
        /// <returns>转码结果</returns>
        public static async Task<TranscodeResult> TranscodeToMp4Async(
            string inputPath,
            string outputPath,
            CancellationToken token = default)
        {
            return await TranscodeAsync(inputPath, outputPath, VideoFormat.MP4, token);
        }

        /// <summary>
        /// 将视频转换为 MOV 格式 (H.264/AAC)
        /// </summary>
        /// <param name="inputPath">输入视频路径</param>
        /// <param name="outputPath">输出视频路径</param>
        /// <param name="token">取消令牌</param>
        /// <returns>转码结果</returns>
        public static async Task<TranscodeResult> TranscodeToMovAsync(
            string inputPath,
            string outputPath,
            CancellationToken token = default)
        {
            return await TranscodeAsync(inputPath, outputPath, VideoFormat.MOV, token);
        }

        /// <summary>
        /// 通用视频转码方法
        /// </summary>
        private static async Task<TranscodeResult> TranscodeAsync(
            string inputPath,
            string outputPath,
            VideoFormat targetFormat,
            CancellationToken token)
        {
            var result = new TranscodeResult();
            var stopwatch = Stopwatch.StartNew();

            if (!File.Exists(inputPath))
            {
                result.Success = false;
                result.ErrorMessage = $"Input file not found: {inputPath}";
                AppLogService.Split($"Transcode failed: {result.ErrorMessage}", LogLevel.Error);
                return result;
            }

            string? ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                result.Success = false;
                result.ErrorMessage = "FFmpeg not found. Please ensure ffmpeg.exe is available.";
                AppLogService.Split("Transcode failed: FFmpeg not found", LogLevel.Error);
                return result;
            }

            AppLogService.Split($"Starting transcode: {Path.GetFileName(inputPath)} -> {targetFormat}");

            try
            {
                // 确保输出目录存在
                Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? string.Empty);

                // 如果输出文件已存在，先删除
                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                string arguments = BuildFFmpegArguments(inputPath, outputPath, targetFormat);

                using var process = new Process();
                process.StartInfo.FileName = ffmpegPath;
                process.StartInfo.Arguments = arguments;
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardError = true;
                process.StartInfo.RedirectStandardOutput = true;

                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                process.EnableRaisingEvents = true;
                process.Exited += (_, _) => tcs.TrySetResult(true);

                try
                {
                    process.Start();
                }
                catch (Exception ex)
                {
                    result.Success = false;
                    result.ErrorMessage = $"Failed to start FFmpeg: {ex.Message}";
                    AppLogService.Split($"Transcode failed: {result.ErrorMessage}", LogLevel.Error, ex);
                    return result;
                }

                using var registration = token.Register(() =>
                {
                    try
                    {
                        if (!process.HasExited)
                        {
                            process.Kill();
                        }
                    }
                    catch { }
                    tcs.TrySetCanceled();
                });

                // 异步读取错误输出（FFmpeg 进度信息会输出到 stderr）
                var errorReadTask = ReadFFmpegOutputAsync(process);

                // 等待进程完成
                await tcs.Task.ConfigureAwait(false);

                // 取消时直接返回
                if (token.IsCancellationRequested)
                {
                    result.Success = false;
                    result.ErrorMessage = "Transcode cancelled by user";
                    AppLogService.Split("Transcode cancelled", LogLevel.Warning);
                    return result;
                }

                stopwatch.Stop();
                result.Duration = stopwatch.Elapsed;

                // 检查进程退出码
                if (process.ExitCode == 0 && File.Exists(outputPath))
                {
                    result.Success = true;
                    result.OutputPath = outputPath;
                    AppLogService.Split($"Transcode completed: {Path.GetFileName(outputPath)} ({result.Duration.TotalSeconds:F1}s)", LogLevel.Info);
                }
                else
                {
                    string errorOutput = await errorReadTask.ConfigureAwait(false);
                    result.Success = false;
                    result.ErrorMessage = $"FFmpeg exited with code {process.ExitCode}. Output: {errorOutput}";
                    AppLogService.Split($"Transcode failed: {result.ErrorMessage}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.Duration = stopwatch.Elapsed;
                result.Success = false;
                result.ErrorMessage = ex.Message;
                AppLogService.Split($"Transcode error: {ex.Message}", LogLevel.Error, ex);
            }

            return result;
        }

        /// <summary>
        /// 构建 FFmpeg 命令行参数
        /// </summary>
        private static string BuildFFmpegArguments(string inputPath, string outputPath, VideoFormat targetFormat)
        {
            string? encoder = GetCurrentEncoder();
            int threadCount = GetThreadCount();

            // FFmpeg 参数说明:
            // -y: 覆盖输出文件
            // -i: 输入文件
            // -map 0:v: 保留第一个视频流
            // -map 0:a: 保留所有音频流
            // -map_metadata 0: 复制输入文件的所有全局元数据（如拍摄日期、GPS等）
            // -threads: 编码线程数
            // -c:v: 视频编码器
            // -c:a: 音频编码器
            // -movflags: MP4 容器选项
            // -preset: 编码速度 vs 压缩率 (veryslow = 最佳压缩比)
            // -crf: 质量 (0=无损, 18=视觉无损, 23=高质量)
            //
            // HDR 支持:
            // - 像素格式会自动从输入继承，保持 HDR 色彩空间 (如 yuv420p10le)
            // - 如果源视频包含 HEVC Main10 或其他 HDR 格式，会被正确处理

            string videoEncoder;
            string videoParams;

            if (!string.IsNullOrEmpty(encoder))
            {
                // 使用硬件编码器
                videoEncoder = encoder;
                videoParams = GetHardwareEncoderParams(encoder);
            }
            else
            {
                // 使用软件编码器
                videoEncoder = targetFormat == VideoFormat.MP4 ? "libx264" : "libx265";
                videoParams = targetFormat == VideoFormat.MP4
                    ? "-preset veryslow -crf 18"
                    : "-preset medium -crf 28";
            }

            return targetFormat switch
            {
                VideoFormat.MP4 => $"-y -i \"{inputPath}\" " +
                    $"-map 0:v -map 0:a " +
                    $"-map_metadata 0 " +
                    $"-threads {threadCount} " +
                    $"-c:v {videoEncoder} {videoParams} " +
                    $"-c:a aac -b:a 256k " +
                    $"-movflags +faststart " +
                    $"\"{outputPath}\"",

                VideoFormat.MOV => $"-y -i \"{inputPath}\" " +
                    $"-map 0:v -map 0:a " +
                    $"-map_metadata 0 " +
                    $"-threads {threadCount} " +
                    $"-c:v {videoEncoder} {videoParams} " +
                    $"-c:a aac -b:a 256k " +
                    $"-movflags +faststart " +
                    $"\"{outputPath}\"",

                _ => $"-y -i \"{inputPath}\" -c copy \"{outputPath}\""
            };
        }

        /// <summary>
        /// 获取硬件编码器的额外参数
        /// </summary>
        private static string GetHardwareEncoderParams(string encoder)
        {
            return encoder.ToLowerInvariant() switch
            {
                // NVIDIA NVENC - 高质量设置
                "h264_nvenc" => "-preset p7 -cq 18 -rc:v vbr",
                // NVIDIA NVENC HEVC
                "hevc_nvenc" => "-preset p7 -cq 28 -rc:v vbr",
                // Intel QSV
                "h264_qsv" => "-preset high -global_quality 28",
                // Intel QSV HEVC
                "hevc_qsv" => "-preset high -global_quality 30",
                // AMD AMF
                "h264_amf" => "-preset quality -quality 100",
                // AMD AMF HEVC
                "hevc_amf" => "-preset quality -quality 100",
                // 默认
                _ => "-crf 18"
            };
        }

        /// <summary>
        /// 异步读取 FFmpeg 输出
        /// </summary>
        private static async Task<string> ReadFFmpegOutputAsync(Process process)
        {
            try
            {
                return await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
            }
            catch
            {
                return string.Empty;
            }
        }

        /// <summary>
        /// 查找 FFmpeg 可执行文件
        /// </summary>
        public static string? FindFFmpeg()
        {
            string[] candidates =
            {
                // 1. 应用目录下的 Tools 文件夹
                Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg"),

                // 2. 应用根目录
                Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "ffmpeg"),

                // 3. Tools 目录下（不带子文件夹）
                Path.Combine(AppContext.BaseDirectory, "..", "Tools", "ffmpeg.exe"),

                // 4. PATH 环境变量中的 ffmpeg
                "ffmpeg"
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }

                    // 检查 PATH 中的可执行文件
                    if (candidate == "ffmpeg" || candidate == "ffprobe")
                    {
                        var pathEnv = Environment.GetEnvironmentVariable("PATH");
                        if (!string.IsNullOrEmpty(pathEnv))
                        {
                            foreach (var part in pathEnv.Split(Path.PathSeparator))
                            {
                                string fullPath = Path.Combine(part.Trim(), candidate + ".exe");
                                if (File.Exists(fullPath))
                                {
                                    return fullPath;
                                }
                            }
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        /// <summary>
        /// 检查 FFmpeg 是否可用
        /// </summary>
        public static bool IsFFmpegAvailable()
        {
            return !string.IsNullOrEmpty(FindFFmpeg());
        }

        /// <summary>
        /// 获取 FFmpeg 版本信息
        /// </summary>
        public static async Task<string?> GetFFmpegVersionAsync()
        {
            string? ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                return null;
            }

            try
            {
                using var process = new Process();
                process.StartInfo.FileName = ffmpegPath;
                process.StartInfo.Arguments = "-version";
                process.StartInfo.UseShellExecute = false;
                process.StartInfo.CreateNoWindow = true;
                process.StartInfo.RedirectStandardOutput = true;
                process.StartInfo.RedirectStandardError = true;

                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync();
                await process.WaitForExitAsync();

                // 返回第一行（版本信息）
                var lines = output.Split('\n');
                return lines.Length > 0 ? lines[0] : null;
            }
            catch
            {
                return null;
            }
        }
    }
}
