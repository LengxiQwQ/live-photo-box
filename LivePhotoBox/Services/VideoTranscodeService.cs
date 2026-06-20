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

        // 编码器可用性 / 参数 / 线程数 → 全部委托给 EncoderHelper（项目唯一入口）。

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

            string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
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
                // -c:v copy: 无损拷贝视频轨
                // -map 0:V:0 -> 【神级参数】大写 V 表示提取第1个"真正的视频轨"，完美避开苹果的 128x96 缩略图轨和安卓的 MJPEG 封面轨
                // -map 0:a:0? -> 提取第1个音频轨（问号表示如果没有音频也不报错，防止静音视频闪退）
                // -c:a aac -b:a 192k: 音频重编码为 AAC。
                //   原视频音轨可能是 PCM（iPhone 实况照片常见），MP4 容器不兼容 PCM，
                //   直接 -c copy 会导致无声。AAC 192kbps 人耳无法感知损失。
                // -map_metadata 0: 保留源文件时间、GPS等元数据
                string arguments = string.IsNullOrEmpty(movflags)
                    ? $"-y -i \"{inputPath}\" -c:v copy -map 0:V:0 -map 0:a:0? -c:a aac -b:a 192k -map_metadata 0 \"{outputPath}\""
                    : $"-y -i \"{inputPath}\" -c:v copy -map 0:V:0 -map 0:a:0? -c:a aac -b:a 192k -map_metadata 0 -movflags {movflags} \"{outputPath}\"";

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

            string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
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
            bool useHardwareEncoder = !string.IsNullOrEmpty(EncoderHelper.GetSavedEncoder(codec));
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
                var sw = EncoderHelper.GetSoftwareEncoder(codec, codec == "h264" ? 19 : 21);
                LogService.Split($"Using software encoder (forced): {sw.encoder} for {targetFormat}");
                return sw;
            }

            string? savedEncoder = EncoderHelper.GetSavedEncoder(codec);

            if (string.IsNullOrEmpty(savedEncoder))
            {
                string? detected = EncoderHelper.DetectHardwareEncoderForCodec(codec);
                if (!string.IsNullOrEmpty(detected))
                {
                    savedEncoder = detected;
                    LogService.Split($"No saved encoder for {codec}, detected from FFmpeg: {detected}", LogLevel.Debug);
                }
            }

            if (!string.IsNullOrEmpty(savedEncoder) && EncoderHelper.IsEncoderAvailable(savedEncoder))
            {
                string encoderParams = EncoderHelper.GetHardwareEncoderParams(savedEncoder, (19, 21));
                LogService.Split($"Using hardware encoder: {savedEncoder} for {targetFormat}");
                return (savedEncoder, encoderParams);
            }
            else if (!string.IsNullOrEmpty(savedEncoder))
            {
                LogService.Split($"Saved encoder '{savedEncoder}' not available for {targetFormat}, falling back to CPU", LogLevel.Warning);
            }

            var swFallback = EncoderHelper.GetSoftwareEncoder(codec, codec == "h264" ? 19 : 21);
            LogService.Split($"Using software encoder: {swFallback.encoder} for {targetFormat}");
            return swFallback;
        }

        // DetectHardwareEncoderForCodec → EncoderHelper.DetectHardwareEncoderForCodec
        // GetHardwareEncoderParams → EncoderHelper.GetHardwareEncoderParams

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
            int threadCount = EncoderHelper.GetThreadCount(videoEncoder, maxSoftwareThreads: null);

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
            // -c:a aac -b:a 192k -> 音频重编码为 AAC，确保 MP4/MOV 兼容性。
            //   iPhone 实况照片提取出的视频音轨可能是 PCM，MP4 容器不兼容，
            //   直接 -c:a copy 会导致无声。AAC 192kbps 透明级音质。
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
                    $"-c:a aac -b:a 192k " +
                    $"-movflags +faststart " +
                    $"\"{outputPath}\"",

                VideoFormat.MOV => $"-apply_cropping 0 -y -i \"{inputPath}\" " +
                    $"-map 0:v:0 -map 0:a:0? " +
                    $"-map_metadata 0 " +
                    $"-threads {threadCount} " +
                    $"{videoFilter} " +
                    $"{pixelFormat} " +
                    $"-c:v {videoEncoder} {videoParams} -tag:v hvc1 " +
                    $"-c:a aac -b:a 192k " +
                    $"-movflags +faststart " +
                    $"\"{outputPath}\"",

                _ => $"-apply_cropping 0 -y -i \"{inputPath}\" -c:v copy -map 0:v:0 -map 0:a:0? -c:a aac -b:a 192k \"{outputPath}\""
            };
        }

        private static string GetPixelFormatParams(string encoder, VideoFormat targetFormat)
        {
            if (targetFormat == VideoFormat.MP4) return "-pix_fmt yuv420p";
            if (encoder.ToLowerInvariant().Contains("hevc") || encoder.ToLowerInvariant().Contains("h265")) return "";
            return "";
        }

        // GetHardwareEncoderParams → EncoderHelper.GetHardwareEncoderParams

        private static async Task<string> ReadFFmpegOutputAsync(Process process)
        {
            try { return await process.StandardError.ReadToEndAsync().ConfigureAwait(false); }
            catch { return string.Empty; }
        }

        // FindFFmpeg / IsFFmpegAvailable → migrated to ExternalToolLocator

        public static async Task<string?> GetFFmpegVersionAsync()
        {
            string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
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