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

            // 如果新 key 有值，验证可用性
            if (!string.IsNullOrEmpty(encoder))
            {
                if (!IsEncoderAvailable(encoder))
                {
                    LogService.Split($"Saved encoder '{encoder}' for {codec} is not available in current FFmpeg, will re-detect", LogLevel.Warning);
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
                    LogService.Split($"Migrating legacy encoder '{legacyH264}' -> '{migratedHevc}' for HEVC", LogLevel.Info);
                    if (IsEncoderAvailable(migratedHevc))
                    {
                        AppSettingsService.SetValue(newKey, migratedHevc);
                        return migratedHevc;
                    }
                    else
                    {
                        LogService.Split($"Migrated encoder '{migratedHevc}' not available, will auto-detect", LogLevel.Warning);
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
                LogService.Split($"Remux failed: {result.ErrorMessage}", LogLevel.Error);
                return result;
            }

            string? ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                result.Success = false;
                result.ErrorMessage = "FFmpeg not found.";
                LogService.Split("Remux failed: FFmpeg not found", LogLevel.Error);
                return result;
            }

            LogService.Split($"Starting remux (container only, no re-encoding): {Path.GetFileName(inputPath)}");

            try
            {
                // 安全创建目录：防止空字符串导致 ArgumentException 崩溃
                string? outDir = Path.GetDirectoryName(outputPath);
                if (!string.IsNullOrWhiteSpace(outDir))
                {
                    Directory.CreateDirectory(outDir);
                }

                if (File.Exists(outputPath))
                {
                    File.Delete(outputPath);
                }

                string extension = Path.GetExtension(outputPath).ToLowerInvariant();
                string movflags = extension == ".mp4" ? "+faststart" : "";

                // Remux 参数说明:
                // -c copy: 无损拷贝
                // -map 0:V:0 -> 【神级参数】大写 V 表示提取第1个"真正的视频轨"，完美避开苹果的 128x96 缩略图轨和安卓的 MJPEG 封面轨
                // -map 0:a:0? -> 提取第1个音频轨（问号表示如果没有音频也不报错，防止静音视频闪退）
                // -map_metadata 0: 保留源文件时间、GPS等元数据
                string arguments = string.IsNullOrEmpty(movflags)
                    ? $"-y -i \"{inputPath}\" -c copy -map 0:V:0 -map 0:a:0? -map_metadata 0 \"{outputPath}\""
                    : $"-y -i \"{inputPath}\" -c copy -map 0:V:0 -map 0:a:0? -map_metadata 0 -movflags {movflags} \"{outputPath}\"";

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
                    LogService.Split($"Remux failed: {result.ErrorMessage}", LogLevel.Error, ex);
                    return result;
                }

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
                    LogService.Split($"Remux completed: {Path.GetFileName(outputPath)} ({result.Duration.TotalSeconds:F1}s)", LogLevel.Info);
                }
                else
                {
                    string errorOutput = string.Empty;
                    try { errorOutput = await errorReadTask.ConfigureAwait(false); } catch { }

                    result.Success = false;
                    result.ErrorMessage = $"FFmpeg exited with code {process.ExitCode}. Output: {errorOutput}";
                    LogService.Split($"Remux failed: {result.ErrorMessage}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.Duration = stopwatch.Elapsed;
                result.Success = false;
                result.ErrorMessage = ex.Message;
                LogService.Split($"Remux error: {ex.Message}", LogLevel.Error, ex);
            }

            return result;
        }

        /// <summary>
        /// 将视频转换为 MP4 格式 (H.264/AAC)
        /// </summary>
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
                LogService.Split($"Transcode failed: {result.ErrorMessage}", LogLevel.Error);
                return result;
            }

            string? ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                result.Success = false;
                result.ErrorMessage = "FFmpeg not found. Please ensure ffmpeg.exe is available.";
                LogService.Split("Transcode failed: FFmpeg not found", LogLevel.Error);
                return result;
            }

            LogService.Split($"Starting transcode: {Path.GetFileName(inputPath)} -> {targetFormat}");

            // 安全创建目录：防止空字符串导致 ArgumentException 崩溃
            string? outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outDir))
            {
                Directory.CreateDirectory(outDir);
            }

            if (File.Exists(outputPath))
            {
                File.Delete(outputPath);
            }

            string codec = targetFormat == VideoFormat.MP4 ? "h264" : "hevc";
            bool useHardwareEncoder = !string.IsNullOrEmpty(GetEncoderForCodec(codec));
            bool transcodeCompleted = false;
            string? lastError = null;

            while (!transcodeCompleted)
            {
                try
                {
                    string arguments = BuildFFmpegArguments(inputPath, outputPath, targetFormat, !useHardwareEncoder);

                    LogService.Split($"ffmpeg {arguments}", LogLevel.Debug);

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
                        LogService.Split($"Transcode failed: {result.ErrorMessage}", LogLevel.Error, ex);
                        return result;
                    }

                    using var registration = token.Register(() =>
                    {
                        try { if (!process.HasExited) process.Kill(); } catch { }
                        tcs.TrySetCanceled();
                    });

                    var errorReadTask = ReadFFmpegOutputAsync(process);

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
                            if (!process.HasExited) { process.Kill(); }
                            result.Success = false;
                            result.ErrorMessage = $"Transcode timeout (>5 minutes). FFmpeg output: {cancelError}";
                            LogService.Split($"Transcode timeout: {result.ErrorMessage}", LogLevel.Error);
                            return result;
                        }
                        if (!string.IsNullOrWhiteSpace(cancelError))
                        {
                            LogService.Split($"[FFmpeg stderr on cancel]: {cancelError}", LogLevel.Warning);
                        }
                        result.Success = false;
                        result.ErrorMessage = "Transcode cancelled by user";
                        LogService.Split("Transcode cancelled", LogLevel.Warning);
                        return result;
                    }

                    stopwatch.Stop();
                    result.Duration = stopwatch.Elapsed;

                    if (process.ExitCode == 0 && File.Exists(outputPath))
                    {
                        result.Success = true;
                        result.OutputPath = outputPath;
                        string mode = useHardwareEncoder ? "GPU" : "CPU";
                        LogService.Split($"Transcode completed ({mode}): {Path.GetFileName(outputPath)} ({result.Duration.TotalSeconds:F1}s)", LogLevel.Info);
                        transcodeCompleted = true;
                    }
                    else
                    {
                        string errorOutput = await errorReadTask.ConfigureAwait(false);
                        lastError = errorOutput;

                        if (useHardwareEncoder && ShouldFallbackToSoftware(errorOutput))
                        {
                            LogService.Split($"Hardware encoder failed, falling back to software encoding...", LogLevel.Warning);
                            useHardwareEncoder = false;
                            if (File.Exists(outputPath)) File.Delete(outputPath);
                            stopwatch.Restart();
                            continue;
                        }

                        result.Success = false;
                        result.ErrorMessage = $"FFmpeg exited with code {process.ExitCode}. Output: {errorOutput}";
                        LogService.Split($"Transcode failed: {result.ErrorMessage}", LogLevel.Error);
                        transcodeCompleted = true;
                    }
                }
                catch (Exception ex)
                {
                    stopwatch.Stop();
                    result.Duration = stopwatch.Elapsed;
                    result.Success = false;
                    result.ErrorMessage = ex.Message;
                    LogService.Split($"Transcode error: {ex.Message}", LogLevel.Error, ex);
                    transcodeCompleted = true;
                }
            }

            return result;
        }

        private static bool ShouldFallbackToSoftware(string errorOutput)
        {
            if (string.IsNullOrEmpty(errorOutput)) return false;

            string lowerError = errorOutput.ToLowerInvariant();
            string[] fallbackTriggers = new[]
            {
                "10 bit encode not supported", "10-bit encode not supported", "does not support", "unsupported pixel format", "unsupported format",
                "no capable devices found", "no device found", "failed to initialize", "device lost",
                "could not open encoder", "error opening encoder", "failed to open encoder", "encoder not found",
                "cuda_error", "cuda error", "nvenc encoder not found", "qsv encoder not found", "amf encoder not found",
                "unsupported codec", "invalid codec", "encoder error", "encoding error",
                "permission denied", "operation not permitted", "out of memory", "allocation failed", "driver not installed", "not supported",
                "failed to encode", "encoding failed", "encode failed"
            };

            foreach (var trigger in fallbackTriggers)
            {
                if (lowerError.Contains(trigger))
                {
                    LogService.Split($"Detected hardware encoder issue: '{trigger}', will fallback to software encoder", LogLevel.Warning);
                    return true;
                }
            }

            return false;
        }

        private static (string encoder, string encoderParams) GetEncoderForFormat(VideoFormat targetFormat, bool forceSoftware = false)
        {
            string codec = targetFormat == VideoFormat.MP4 ? "h264" : "hevc";

            if (forceSoftware)
            {
                string enc = codec == "h264" ? "libx264" : "libx265";
                // CRF 19 (H.264) / CRF 21 (HEVC)：输入≈输出码率的精准平衡点。
                // CRF 18 膨胀 ~60%，CRF 20 偏压缩 ~20%，CRF 19 恰好持平。
                string prms = codec == "h264" ? "-preset medium -crf 19" : "-preset medium -crf 21";
                LogService.Split($"Using software encoder (forced): {enc} for {targetFormat}");
                return (enc, prms);
            }

            string? savedEncoder = GetEncoderForCodec(codec);

            if (string.IsNullOrEmpty(savedEncoder))
            {
                string? detected = DetectHardwareEncoderForCodec(codec);
                if (!string.IsNullOrEmpty(detected))
                {
                    savedEncoder = detected;
                    LogService.Split($"No saved encoder for {codec}, detected from FFmpeg: {detected}", LogLevel.Debug);
                }
            }

            if (!string.IsNullOrEmpty(savedEncoder) && IsEncoderAvailable(savedEncoder))
            {
                string encoderParams = GetHardwareEncoderParams(savedEncoder, targetFormat);
                LogService.Split($"Using hardware encoder: {savedEncoder} for {targetFormat}");
                return (savedEncoder, encoderParams);
            }
            else if (!string.IsNullOrEmpty(savedEncoder))
            {
                LogService.Split($"Saved encoder '{savedEncoder}' not available for {targetFormat}, falling back to CPU", LogLevel.Warning);
            }

            string encName = codec == "h264" ? "libx264" : "libx265";
            string encParams = codec == "h264" ? "-preset medium -crf 19" : "-preset medium -crf 21";

            LogService.Split($"Using software encoder: {encName} for {targetFormat}");
            return (encName, encParams);
        }

        private static string? DetectHardwareEncoderForCodec(string codec)
        {
            try
            {
                string? ffmpegPath = FindFFmpeg();
                if (string.IsNullOrEmpty(ffmpegPath)) return null;

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
                    if (output.Contains(candidate, StringComparison.OrdinalIgnoreCase)) return candidate;
                }
            }
            catch (Exception ex)
            {
                LogService.Split($"DetectHardwareEncoderForCodec error: {ex.Message}", LogLevel.Warning);
            }
            return null;
        }

        private static string BuildVideoFilter(VideoFormat targetFormat, string encoder)
        {
            // 这里只用 setsar=1 锁定正方形像素比，不做任何 scale。
            // 分辨率保护由 decoder 侧的 -apply_cropping 0 处理（见 BuildFFmpegArguments），
            // 该参数关闭 HEVC conformance window 裁切，确保解码器输出原始完整帧。
            // 之前在此处放置 scale=trunc(iw/2)*2 无法阻止 decoder 层面的裁切，
            // 因为 scale 滤镜拿到的帧已经是裁切后的。
            return "-vf \"setsar=1\"";
        }

        private static string BuildFFmpegArguments(string inputPath, string outputPath, VideoFormat targetFormat, bool forceSoftwareEncoder = false)
        {
            var (videoEncoder, videoParams) = GetEncoderForFormat(targetFormat, forceSoftwareEncoder);
            int threadCount = GetThreadCount(videoEncoder);

            string pixelFormat = GetPixelFormatParams(videoEncoder, targetFormat);
            string videoFilter = BuildVideoFilter(targetFormat, videoEncoder);

            // -apply_cropping 0 -> 关闭 HEVC conformance window 自动裁切。
            //   手机（尤其 iPhone/三星）拍摄的 Live Photo 视频常以 HEVC 编码，
            //   编码分辨率会 pad 到 CTU 对齐（如 1920→1920, 1440→1472），
            //   再通过 metadata 让解码器裁回"显示尺寸"。FFmpeg 默认执行此裁切，
            //   导致 1920×1440 → 1744×1308 这类非预期的分辨率变化。
            //   设为 0 后解码器输出完整帧，-vf setsar=1 + -pix_fmt 再规范化输出。
            //
            // -map 0:v:0 -> 小写 v，标准视频流选择（temp 文件只有一条视频轨，无需大写 V）
            // -map 0:a:0? -> 提取单一主音频，若不存在则跳过
            // -c:a copy -> 直接复制原始音频流，完全保留原始音质
            //
            //  不加 -noautorotate：让 FFmpeg 自动将旋转矩阵应用到像素上。
            //  iPhone MOV 的旋转在 moov.trak.tkhd 中，-vf 触发 autorotate 滤镜
            //  物理旋转像素，确保输出始终正立，不依赖播放器解析旋转标签。
            return targetFormat switch
            {
                VideoFormat.MP4 => $"-apply_cropping 0 -y -i \"{inputPath}\" " +
                    $"-map 0:v:0 -map 0:a:0? " +
                    $"-map_metadata 0 " +
                    $"-threads {threadCount} " +
                    $"{videoFilter} " +
                    $"{pixelFormat} " +
                    $"-c:v {videoEncoder} {videoParams} " +
                    $"-c:a copy " +
                    $"-movflags +faststart " +
                    $"\"{outputPath}\"",

                VideoFormat.MOV => $"-apply_cropping 0 -y -i \"{inputPath}\" " +
                    $"-map 0:v:0 -map 0:a:0? " +
                    $"-map_metadata 0 " +
                    $"-threads {threadCount} " +
                    $"{videoFilter} " +
                    $"{pixelFormat} " +
                    $"-c:v {videoEncoder} {videoParams} -tag:v hvc1 " +
                    $"-c:a copy " +
                    $"-movflags +faststart " +
                    $"\"{outputPath}\"",

                _ => $"-apply_cropping 0 -y -i \"{inputPath}\" -c copy -map 0:v:0 -map 0:a:0? \"{outputPath}\""
            };
        }

        private static string GetPixelFormatParams(string encoder, VideoFormat targetFormat)
        {
            if (targetFormat == VideoFormat.MP4) return "-pix_fmt yuv420p";
            if (encoder.ToLowerInvariant().Contains("hevc") || encoder.ToLowerInvariant().Contains("h265")) return "";
            return "";
        }

        private static string GetHardwareEncoderParams(string encoder, VideoFormat targetFormat)
        {
            string lowerEncoder = encoder.ToLowerInvariant();

            // CRF 19 (H.264) / CRF 21 (HEVC)：输入≈输出码率的精准平衡点。
            // CRF 20 仍会让原本 1 万 kbps 的源被压到 8000+，CRF 19 刚好持平。
            if (lowerEncoder.StartsWith("h264"))
            {
                return lowerEncoder switch
                {
                    "h264_nvenc" => "-preset p5 -rc:v vbr_hq -cq:v 19 -b:v 0 -maxrate:v 30M -bufsize:v 60M -profile:v high",
                    "h264_qsv" => "-global_quality 19 -look_ahead 1",
                    "h264_amf" => "-preset quality -rc cqp -qp 19",
                    "h264_vaapi" => "-quality 85 -rc_mode 1",
                    _ => "-preset medium -crf 19"
                };
            }

            return lowerEncoder switch
            {
                "hevc_nvenc" => "-preset p5 -rc:v vbr_hq -cq:v 21 -b:v 0 -maxrate:v 25M -bufsize:v 50M -tune hq",
                "hevc_qsv" => "-global_quality 21 -look_ahead 1",
                "hevc_amf" => "-preset quality -rc cqp -qp 21",
                "hevc_vaapi" => "-quality 85 -rc_mode 1",
                _ => "-preset medium -crf 21"
            };
        }

        private static async Task<string> ReadFFmpegOutputAsync(Process process)
        {
            try { return await process.StandardError.ReadToEndAsync().ConfigureAwait(false); }
            catch { return string.Empty; }
        }

        /// <summary>
        /// 查找 FFmpeg 可执行文件
        /// </summary>
        public static string? FindFFmpeg()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg"),
                Path.Combine(AppContext.BaseDirectory, "ffmpeg.exe"),
                Path.Combine(AppContext.BaseDirectory, "ffmpeg"),
                Path.Combine(AppContext.BaseDirectory, "..", "Tools", "ffmpeg.exe"),
                "ffmpeg"
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate)) return candidate;

                    // 修复：局部 Try-Catch 处理 PATH 变量带来的隐患
                    if (candidate == "ffmpeg" || candidate == "ffprobe")
                    {
                        var pathEnv = Environment.GetEnvironmentVariable("PATH");
                        if (!string.IsNullOrEmpty(pathEnv))
                        {
                            foreach (var part in pathEnv.Split(Path.PathSeparator))
                            {
                                try
                                {
                                    string cleanPart = part.Trim(' ', '"'); // 净化非法字符和引号
                                    if (string.IsNullOrEmpty(cleanPart)) continue;

                                    string fullPath = Path.Combine(cleanPart, candidate + ".exe");
                                    if (File.Exists(fullPath)) return fullPath;
                                }
                                catch { /* 单个 PATH 解析失败不会中断整体寻找 */ }
                            }
                        }
                    }
                }
                catch { }
            }

            return null;
        }

        public static bool IsFFmpegAvailable()
        {
            return !string.IsNullOrEmpty(FindFFmpeg());
        }

        public static async Task<string?> GetFFmpegVersionAsync()
        {
            string? ffmpegPath = FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath)) return null;

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

                var lines = output.Split('\n');
                return lines.Length > 0 ? lines[0] : null;
            }
            catch { return null; }
        }
    }
}