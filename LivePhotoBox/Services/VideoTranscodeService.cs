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
        /// 获取当前选择的硬件编码器（按 codec 格式独立取值）
        /// 旧版本只存一个 SplitHardwareEncoder（H.264），新版本按 codec 分别存 SplitEncoder_h264 / SplitEncoder_hevc。
        /// 第一次使用新格式（HEVC）时，如果没有保存值，会自动从旧值迁移：h264_xxx -> hevc_xxx
        /// </summary>
        private static string? GetEncoderForCodec(string codec)
        {
            string newKey = $"SplitEncoder_{codec}";
            string? encoder = AppSettingsService.GetValue<string?>(newKey, null);

            AppLogService.Split($"[DEBUG] GetEncoderForCodec({codec}): key='{newKey}', value='{encoder ?? "(null)"}'", LogLevel.Info);

            // 如果新 key 有值，验证可用性
            if (!string.IsNullOrEmpty(encoder))
            {
                if (!IsEncoderAvailable(encoder))
                {
                    AppLogService.Split($"Saved encoder '{encoder}' for {codec} is not available in current FFmpeg, will re-detect", LogLevel.Warning);
                    return null;
                }
                return encoder;
            }

            // 新 key 没有值：尝试从旧 SplitHardwareEncoder 迁移
            if (codec == "hevc")
            {
                string? legacyH264 = AppSettingsService.GetValue<string?>("SplitHardwareEncoder", null);
                if (!string.IsNullOrEmpty(legacyH264) && legacyH264.StartsWith("h264_", StringComparison.OrdinalIgnoreCase))
                {
                    // 迁移：h264_xxx -> hevc_xxx
                    string migratedHevc = "hevc" + legacyH264.Substring(4);
                    AppLogService.Split($"Migrating legacy encoder '{legacyH264}' -> '{migratedHevc}' for HEVC", LogLevel.Info);
                    if (IsEncoderAvailable(migratedHevc))
                    {
                        AppSettingsService.SetValue(newKey, migratedHevc);
                        return migratedHevc;
                    }
                    else
                    {
                        AppLogService.Split($"Migrated encoder '{migratedHevc}' not available, will auto-detect", LogLevel.Warning);
                        return null;
                    }
                }
            }

            return null;
        }

        /// <summary>
        /// 检查 FFmpeg 编码器是否可用
        /// </summary>
        private static bool IsEncoderAvailable(string encoder)
        {
            try
            {
                string? ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath))
                {
                    return false;
                }

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = "-hide_banner -encoders",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };

                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);

                return output.Contains(encoder, StringComparison.OrdinalIgnoreCase);
            }
            catch
            {
                return false;
            }
        }

        /// <summary>
        /// 获取当前线程数设置
        /// </summary>
        private static int GetThreadCount(string? encoder = null)
        {
            int userThreadCount = AppSettingsService.GetValue<int>("SplitThreadCount", Environment.ProcessorCount);

            // 如果使用硬件编码器（NVENC/QSV/AMF/VAAPI），限制线程数为 1
            // 硬件编码的瓶颈在 GPU 而非 CPU，过多线程反而增加线程切换开销
            if (!string.IsNullOrEmpty(encoder))
            {
                string enc = encoder.ToLowerInvariant();
                if (enc.Contains("nvenc") || enc.Contains("qsv") || enc.Contains("vaapi") || enc.Contains("amf"))
                {
                    return Math.Min(userThreadCount, 1);
                }
            }

            return userThreadCount;
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

            AppLogService.Split($"Starting remux (container only, no re-encoding): {Path.GetFileName(inputPath)}");

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
                    ? $"-y -i \"{inputPath}\" -c copy -map 0 -map_metadata 0 \"{outputPath}\""
                    : $"-y -i \"{inputPath}\" -c copy -map 0 -map_metadata 0 -movflags {movflags} \"{outputPath}\"";

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

                // 关键修复：必须异步消费 stdout/stderr，否则管道缓冲区填满后
                // FFmpeg 会阻塞写入导致整个进程卡死（CPU/GPU 都不动）
                var errorReadTask = process.StandardError.ReadToEndAsync();
                var outputReadTask = process.StandardOutput.ReadToEndAsync();

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
                    // 等待 stderr 异步读取任务完成，拿到完整错误输出
                    string errorOutput = string.Empty;
                    try
                    {
                        errorOutput = await errorReadTask.ConfigureAwait(false);
                    }
                    catch { }
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
        /// 通用视频转码方法（支持降级重试）
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

            // 确保输出目录存在
            Directory.CreateDirectory(Path.GetDirectoryName(outputPath) ?? string.Empty);

            // 如果输出文件已存在，先删除
            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            // 尝试硬件编码，失败则降级到软件编码
            // 按目标格式的 codec 独立取硬件编码器（H.264 和 HEVC 是独立的硬件编码器）
            string codec = targetFormat == VideoFormat.MP4 ? "h264" : "hevc";
            bool useHardwareEncoder = !string.IsNullOrEmpty(GetEncoderForCodec(codec));
            bool transcodeCompleted = false;
            string? lastError = null;

            while (!transcodeCompleted)
            {
                try
                {
                    string arguments = BuildFFmpegArguments(inputPath, outputPath, targetFormat, !useHardwareEncoder);

                    AppLogService.Split($"[FFmpeg args]: ffmpeg {arguments}", LogLevel.Info);

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

                    // 异步读取错误输出
                    var errorReadTask = ReadFFmpegOutputAsync(process);

                    // 等待进程完成，最多等待 5 分钟
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromMinutes(5));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(token, timeoutCts.Token);

                    try
                    {
                        await tcs.Task.WaitAsync(linkedCts.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException)
                    {
                        string cancelError = await errorReadTask.ConfigureAwait(false);
                        if (timeoutCts.Token.IsCancellationRequested)
                        {
                            // 超时被取消
                            if (!process.HasExited)
                            {
                                process.Kill();
                            }
                            result.Success = false;
                            result.ErrorMessage = $"Transcode timeout (>5 minutes). FFmpeg output: {cancelError}";
                            AppLogService.Split($"Transcode timeout: {result.ErrorMessage}", LogLevel.Error);
                            return result;
                        }
                        // 用户取消
                        if (!string.IsNullOrWhiteSpace(cancelError))
                        {
                            AppLogService.Split($"[FFmpeg stderr on cancel]: {cancelError}", LogLevel.Warning);
                        }
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
                        if (useHardwareEncoder)
                        {
                            AppLogService.Split($"Transcode completed (GPU): {Path.GetFileName(outputPath)} ({result.Duration.TotalSeconds:F1}s)", LogLevel.Info);
                        }
                        else
                        {
                            AppLogService.Split($"Transcode completed (CPU): {Path.GetFileName(outputPath)} ({result.Duration.TotalSeconds:F1}s)", LogLevel.Info);
                        }
                        transcodeCompleted = true;
                    }
                    else
                    {
                        string errorOutput = await errorReadTask.ConfigureAwait(false);
                        lastError = errorOutput;

                        // 检查是否是硬件编码器特有的错误，需要降级
                        if (useHardwareEncoder && ShouldFallbackToSoftware(errorOutput))
                        {
                            AppLogService.Split($"Hardware encoder failed, falling back to software encoding...", LogLevel.Warning);
                            useHardwareEncoder = false;

                            // 删除可能创建的不完整文件
                            if (File.Exists(outputPath))
                            {
                                File.Delete(outputPath);
                            }

                            // 重置计时器
                            stopwatch.Restart();
                            continue;
                        }

                        // 不是需要降级的错误，或者已经降级过一次了
                        result.Success = false;
                        result.ErrorMessage = $"FFmpeg exited with code {process.ExitCode}. Output: {errorOutput}";
                        AppLogService.Split($"Transcode failed: {result.ErrorMessage}", LogLevel.Error);
                        transcodeCompleted = true;
                    }
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    result.Duration = stopwatch.Elapsed;
                    result.Success = false;
                    result.ErrorMessage = ex.Message;
                    AppLogService.Split($"Transcode error: {ex.Message}", LogLevel.Error, ex);
                    transcodeCompleted = true;
                }
            }

            return result;
        }

        /// <summary>
        /// 检查是否应该降级到软件编码器
        /// </summary>
        private static bool ShouldFallbackToSoftware(string errorOutput)
        {
            if (string.IsNullOrEmpty(errorOutput))
                return false;

            string lowerError = errorOutput.ToLowerInvariant();

            // 检查常见的硬件编码器不支持的错误
            string[] fallbackTriggers = new[]
            {
                // 10-bit/色彩格式问题
                "10 bit encode not supported",
                "10-bit encode not supported",
                "does not support",
                "unsupported pixel format",
                "unsupported format",
                // 设备/硬件问题
                "no capable devices found",
                "no device found",
                "failed to initialize",
                "device lost",
                // 编码器打开/创建问题
                "could not open encoder",
                "error opening encoder",
                "failed to open encoder",
                "encoder not found",
                // CUDA/GPU 问题
                "cuda_error",
                "cuda error",
                "nvenc encoder not found",
                "qsv encoder not found",
                "amf encoder not found",
                // 编解码器问题
                "unsupported codec",
                "invalid codec",
                "encoder error",
                "encoding error",
                // 其他常见问题
                "permission denied",
                "operation not permitted",
                "out of memory",
                "allocation failed",
                "driver not installed",
                "not supported",
                // HEVC 硬件编码器 HDR 问题会在上面的一般性错误中被捕获
                // 不需要单独添加编码器名称
                // 一般性错误
                "failed to encode",
                "encoding failed",
                "encode failed"
            };

            foreach (var trigger in fallbackTriggers)
            {
                if (lowerError.Contains(trigger))
                {
                    AppLogService.Split($"Detected hardware encoder issue: '{trigger}', will fallback to software encoder", LogLevel.Warning);
                    return true;
                }
            }

            return false;
        }

        /// <summary>
        /// 根据当前硬件设置和目标格式获取对应的视频编码器
        /// MP4 -> H.264, MOV -> HEVC
        /// 重要：HEVC 和 H.264 的硬件编码器是独立检测的，不能用字符串"硬转换"。
        /// 原因：消费级 GPU（如 RTX 30 系列）的 HEVC 硬件编码往往受限
        /// （驱动未启用 / 缺少 Studio 驱动授权 / NVENC session 限制），
        /// 而 H.264 编码器却完全可用；反过来也存在这种情况。
        /// 因此 MOV 流程独立走一遍"在 FFmpeg encoders 列表里实际存在"的探测。
        /// </summary>
        /// <param name="targetFormat">目标格式</param>
        /// <param name="forceSoftware">强制使用软件编码器</param>
        private static (string encoder, string encoderParams) GetEncoderForFormat(VideoFormat targetFormat, bool forceSoftware = false)
        {
            string codec = targetFormat == VideoFormat.MP4 ? "h264" : "hevc";

            // 如果强制使用软件编码器，直接返回软件编码器
            if (forceSoftware)
            {
                string enc = codec == "h264" ? "libx264" : "libx265";
                // 业界最推荐的"保画质"软件预设：
                // -preset medium: 速度与压缩率甜点（slow 太慢 3 倍只多 5% 压缩；fast 太草率少 15% 压缩）
                // -crf 18 (H.264) / -crf 20 (HEVC): 视觉无损档
                string prms = codec == "h264"
                    ? "-preset medium -crf 18"
                    : "-preset medium -crf 20";
                AppLogService.Split($"Using software encoder (forced): {enc} for {targetFormat}");
                return (enc, prms);
            }

            // 根据目标格式独立选择编码器
            // MP4 走用户上次保存的 H.264 编码器（按 H.264 验证可用性）
            // MOV 走用户上次保存的 H.265 编码器（按 H.265 验证可用性）
            // 如果目标格式没有对应的保存值，则尝试用硬件 GPU 探测一个可用的对应格式编码器
            string? savedEncoder = GetEncoderForCodec(codec);

            if (string.IsNullOrEmpty(savedEncoder))
            {
                // 没有保存的该格式编码器，尝试从 GPU 硬件探测
                string? detected = DetectHardwareEncoderForCodec(codec);
                if (!string.IsNullOrEmpty(detected))
                {
                    savedEncoder = detected;
                    AppLogService.Split($"No saved encoder for {codec}, detected from FFmpeg: {detected}", LogLevel.Info);
                }
            }

            if (!string.IsNullOrEmpty(savedEncoder) && IsEncoderAvailable(savedEncoder))
            {
                string encoderParams = GetHardwareEncoderParams(savedEncoder, targetFormat);
                AppLogService.Split($"Using hardware encoder: {savedEncoder} for {targetFormat}");
                return (savedEncoder, encoderParams);
            }
            else if (!string.IsNullOrEmpty(savedEncoder))
            {
                AppLogService.Split($"Saved encoder '{savedEncoder}' not available for {targetFormat}, falling back to CPU", LogLevel.Warning);
            }

            // CPU 编码：使用软件编码器
            // 业界最推荐的"保画质"软件预设：
            // -preset medium: 速度与压缩率甜点
            // -crf 18 (H.264) / -crf 20 (HEVC): 视觉无损档
            // 源 15Mbps 时输出参考：libx264 crf 18 → ~12-15Mbps；libx265 crf 20 → ~5-8Mbps
            // HEVC 压缩效率高 40%，所以"看起来"码率低是正常的，画质视觉等价
            string encName = codec == "h264" ? "libx264" : "libx265";
            string encParams = codec == "h264"
                ? "-preset medium -crf 18"
                : "-preset medium -crf 20";

            AppLogService.Split($"Using software encoder: {encName} for {targetFormat}");
            return (encName, encParams);
        }

        /// <summary>
        /// 从 FFmpeg 编码器列表里探测当前 GPU 厂商的 H.264 / HEVC 硬件编码器
        /// </summary>
        private static string? DetectHardwareEncoderForCodec(string codec)
        {
            try
            {
                string? ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath)) return null;

                // 候选编码器优先级：NVENC > AMF > QSV > VAAPI
                string[] candidates = codec == "h264"
                    ? new[] { "h264_nvenc", "h264_amf", "h264_qsv", "h264_vaapi" }
                    : new[] { "hevc_nvenc", "hevc_amf", "hevc_qsv", "hevc_vaapi" };

                var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = "-hide_banner -encoders",
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    }
                };
                process.Start();
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);

                foreach (var candidate in candidates)
                {
                    if (output.Contains(candidate, StringComparison.OrdinalIgnoreCase))
                    {
                        return candidate;
                    }
                }
            }
            catch (Exception ex)
            {
                AppLogService.Split($"DetectHardwareEncoderForCodec error: {ex.Message}", LogLevel.Warning);
            }
            return null;
        }

        /// <summary>
        /// 构建 FFmpeg 命令行参数
        /// </summary>
        /// <param name="inputPath">输入路径</param>
        /// <param name="outputPath">输出路径</param>
        /// <param name="targetFormat">目标格式</param>
        /// <param name="forceSoftwareEncoder">强制使用软件编码器（忽略硬件设置）</param>
        private static string BuildFFmpegArguments(string inputPath, string outputPath, VideoFormat targetFormat, bool forceSoftwareEncoder = false)
        {
            var (videoEncoder, videoParams) = GetEncoderForFormat(targetFormat, forceSoftwareEncoder);
            // FFmpeg 参数说明:
            // -y: 覆盖输出文件
            // -i: 输入文件
            // -map 0:v:0: 只映射第一个视频流（主视频，不包含辅助图像流）
            // -map 0:a: 映射所有音频流
            // -map_metadata 0: 复制输入文件的所有全局元数据（如拍摄日期、GPS等）
            // -threads: 编码线程数
            // -c:v: 视频编码器
            // -c:a: 音频编码器
            // -pix_fmt: 像素格式 - H.264 不支持 10-bit，HEVC 支持
            // -movflags: MP4 容器选项
            // -preset: 编码速度 vs 压缩率 (veryslow = 最佳压缩比)
            // -crf: 质量 (0=无损, 18=视觉无损, 23=高质量)

            int threadCount = GetThreadCount(videoEncoder);
            AppLogService.Split($"FFmpeg args: encoder={videoEncoder}, params={videoParams}, threads={threadCount}", LogLevel.Info);

            // 构建像素格式参数
            // H.264 不支持 10-bit 输出，必须转换为 8-bit
            // HEVC 支持 10-bit，可以保持原样
            string pixelFormat = GetPixelFormatParams(videoEncoder, targetFormat);

            return targetFormat switch
            {
                VideoFormat.MP4 => $"-y -i \"{inputPath}\" " +
                    $"-map 0:v:0 -map 0:a " +
                    $"-map_metadata 0 " +
                    $"-threads {threadCount} " +
                    $"{pixelFormat} " +
                    $"-c:v {videoEncoder} {videoParams} " +
                    $"-c:a aac -b:a 320k " +
                    $"-movflags +faststart " +
                    $"\"{outputPath}\"",

                VideoFormat.MOV => $"-y -i \"{inputPath}\" " +
                    $"-map 0:v:0 -map 0:a " +
                    $"-map_metadata 0 " +
                    $"-threads {threadCount} " +
                    $"{pixelFormat} " +
                    $"-c:v {videoEncoder} {videoParams} " +
                    $"-c:a aac -b:a 320k " +
                    $"-movflags +faststart " +
                    $"\"{outputPath}\"",

                _ => $"-y -i \"{inputPath}\" -c copy \"{outputPath}\""
            };
        }

        /// <summary>
        /// 获取像素格式参数
        /// H.264 不支持 10-bit，必须转为 8-bit
        /// HEVC 支持 10-bit，可以保持原样
        /// </summary>
        private static string GetPixelFormatParams(string encoder, VideoFormat targetFormat)
        {
            // 如果目标是 MP4（H.264），必须使用 8-bit
            if (targetFormat == VideoFormat.MP4)
            {
                return "-pix_fmt yuv420p";
            }

            // HEVC 编码器可以保持原像素格式
            if (encoder.ToLowerInvariant().Contains("hevc") || encoder.ToLowerInvariant().Contains("h265"))
            {
                return ""; // 让 FFmpeg 保持原样
            }

            return "";
        }

        /// <summary>
        /// 获取硬件编码器的额外参数
        /// ====================================================================
        /// 保画质优先的业界最推荐预设（按 codec / 厂商分别调优）
        /// ====================================================================
        /// 关键原则：
        /// 1. 必须显式指定质量目标（CQ / global_quality / CRF），不能裸用 -rc:v vbr，
        ///    否则 NVENC HEVC 默认会按 ~5Mbps 跑，导致画质暴跌、文件变小。
        /// 2. 软编用 -preset medium/slow（不用 fast），同 CRF 下慢 preset 不改变画质，
        ///    但能省 10-20% 码率——但用户能感知的是"画质"，所以牺牲点速度换更好压缩。
        /// 3. 硬编 NVENC 用 p5（p4 太草率，p5 是甜点，p7 太慢），p5 vs p4 同画质但更稳。
        /// 4. CRF 18 (H.264) / CRF 20 (HEVC) 是视觉无损档。源 15Mbps 时：
        ///    - libx264 crf 18 → ~12-15Mbps（接近源）
        ///    - libx265 crf 20 → ~5-8Mbps（HEVC 压缩效率高 40%，**视觉等价**）
        /// 5. 音频 320k AAC（接近 Apple Music / Spotify 高质量档；256k 是 AAC 透明上限）
        /// ====================================================================
        /// </summary>
        private static string GetHardwareEncoderParams(string encoder, VideoFormat targetFormat)
        {
            string lowerEncoder = encoder.ToLowerInvariant();

            // H.264 编码器
            if (lowerEncoder.StartsWith("h264"))
            {
                return lowerEncoder switch
                {
                    // NVIDIA NVENC H.264
                    // -preset p5: 平衡（p4 偏快、p6 偏慢；p5 是画质/速度甜点）
                    // -rc:v vbr_hq: 高质量 VBR
                    // -cq:v 18: 视觉无损
                    // -b:v 0: 让码率由 CQ 决定
                    // -maxrate:v 50M / -bufsize:v 100M: 留足峰值空间，不卡高码率场景
                    "h264_nvenc" => "-preset p5 -rc:v vbr_hq -cq:v 18 -b:v 0 -maxrate:v 50M -bufsize:v 100M -profile:v high",
                    // Intel QSV H.264 - global_quality 18 对应 NVENC CQ 18
                    "h264_qsv" => "-global_quality 18 -look_ahead 1",
                    // AMD AMF H.264
                    "h264_amf" => "-preset quality -rc cqp -qp 18",
                    // VAAPI H.264
                    "h264_vaapi" => "-quality 100 -rc_mode 1",
                    // 其他 H.264 编码器（软件 libx264 兜底）
                    _ => "-preset medium -crf 18"
                };
            }

            // HEVC 编码器
            return lowerEncoder switch
            {
                // NVIDIA NVENC HEVC
                // -preset p5: 画质/速度甜点
                // -rc:v vbr_hq: 高质量 VBR
                // -cq:v 20: 视觉无损（HEVC 比 H.264 高 2 个 CRF 单位同画质）
                // -b:v 0: 让码率由 CQ 决定（不强制目标码率）
                // -maxrate:v 40M: 峰值上限（HEVC 峰值比 H.264 低）
                // -tune hq: 启用高质量调优
                "hevc_nvenc" => "-preset p5 -rc:v vbr_hq -cq:v 20 -b:v 0 -maxrate:v 40M -bufsize:v 80M -tune hq",
                // Intel QSV HEVC - global_quality 20 = 视觉无损
                "hevc_qsv" => "-global_quality 20 -look_ahead 1",
                // AMD AMF HEVC
                "hevc_amf" => "-preset quality -rc cqp -qp 20",
                // VAAPI HEVC
                "hevc_vaapi" => "-quality 100 -rc_mode 1",
                // 软件 libx265 兜底
                _ => "-preset medium -crf 20"
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
