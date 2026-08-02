using System;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.Services
{
    // 视频转码服务 - 使用 FFmpeg 进行视频格式转换
    // 支持硬件加速 (NVENC/QSV/AMF) 和多线程处理
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

        // 获取当前选择的硬件编码器（按 codec 格式独立取值）
        // 旧版本只存一个 SplitHardwareEncoder（H.264），新版本按 codec 分别存 SplitEncoder_h264 / SplitEncoder_hevc。
        // 第一次使用新格式（HEVC）时，如果没有保存值，会自动从旧值迁移：h264_xxx -> hevc_xxx
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

        // 检查 FFmpeg 编码器是否可用
        private static bool IsEncoderAvailable(string encoder)
        {
            try
            {
                string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
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

        // 获取当前线程数设置
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

        // 快速容器转换（Remux）- 无损转换视频容器格式，完整保留 HDR 和所有元数据
        // inputPath: 输入视频路径
        // outputPath: 输出视频路径
        // token: 取消令牌
        // è¿å: 转换结果
        public static async Task<TranscodeResult> RemuxAsync(
            string inputPath,
            string outputPath,
            CancellationToken token = default,
            bool useFaststart = true)
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
                // Mate 60/Mate 80 实拍文件 moov 均在头部（紧跟 ftyp），华为相册期望此布局。
                // Huawei mode: set mp42 brand + ©too metadata.
                string movflags = extension == ".mp4" ? "+faststart" : "";
                string brandMeta = (!useFaststart && extension == ".mp4")
                    ? " -brand mp42 -metadata too=\"Openharmony6.1\"" : "";

                // Remux 参数说明:
                // -c copy: 无损拷贝
                // -map 0:V:0 -> 【神级参数】大写 V 表示提取第1个"真正的视频轨"，完美避开Apple的 128x96 缩略图轨和安卓的 MJPEG 封面轨
                // -map 0:a:0? -> 提取第1个音频轨（问号表示如果没有音频也不报错，防止静音视频闪退）
                // -map_metadata 0: 保留源文件时间、GPS等元数据
                string arguments;
                if (!string.IsNullOrEmpty(movflags))
                    arguments = $"-y -i \"{inputPath}\" -c copy -map 0:V:0 -map 0:a:0? -map_metadata 0 -movflags {movflags} \"{outputPath}\"";
                else if (!string.IsNullOrEmpty(brandMeta))
                    arguments = $"-y -i \"{inputPath}\" -c copy -map 0:V:0 -map 0:a:0? -map_metadata 0{brandMeta} \"{outputPath}\"";
                else
                    arguments = $"-y -i \"{inputPath}\" -c copy -map 0:V:0 -map 0:a:0? -map_metadata 0 \"{outputPath}\"";

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

        // 将视频转换为 MP4 格式 (H.264/AAC)
        public static async Task<TranscodeResult> TranscodeToMp4Async(
            string inputPath,
            string outputPath,
            CancellationToken token = default,
            bool useFaststart = true)
        {
            return await TranscodeAsync(inputPath, outputPath, VideoFormat.MP4, token, useFaststart);
        }

        // 将视频转换为 MOV 格式 (H.264/AAC)
        public static async Task<TranscodeResult> TranscodeToMovAsync(
            string inputPath,
            string outputPath,
            CancellationToken token = default)
        {
            return await TranscodeAsync(inputPath, outputPath, VideoFormat.MOV, token);
        }

        // ══════════════════════════════════════════════════════════════
        //  GIF 动图导出（palettegen/paletteuse 双 pass 管线）
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 将视频转码为 GIF 动图，使用 palettegen/paletteuse 滤镜生成最优调色板。
        /// </summary>
        /// <param name="inputPath">输入视频路径</param>
        /// <param name="outputPath">输出 GIF 路径</param>
        /// <param name="fps">输出帧率（1-30）</param>
        /// <param name="width">输出宽度（像素），传 0 表示保持原始宽度</param>
        /// <param name="height">输出高度（像素），传 0 表示保持原始高度</param>
        /// <param name="loopCount">循环次数（0=无限循环）</param>
        /// <param name="token">取消令牌</param>
        /// <returns>转码结果</returns>
        public static async Task<TranscodeResult> TranscodeToGifAsync(
            string inputPath,
            string outputPath,
            int fps,
            int width,
            int height,
            int loopCount,
            CancellationToken token = default)
        {
            var result = new TranscodeResult();
            var stopwatch = Stopwatch.StartNew();

            if (!File.Exists(inputPath))
            {
                result.Success = false;
                result.ErrorMessage = $"Input file not found: {inputPath}";
                LogService.Split($"GIF transcode failed: {result.ErrorMessage}", LogLevel.Error);
                return result;
            }

            string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath))
            {
                result.Success = false;
                result.ErrorMessage = "FFmpeg not found. Please ensure ffmpeg.exe is available.";
                LogService.Split("GIF transcode failed: FFmpeg not found", LogLevel.Error);
                return result;
            }

            LogService.Split(
                $"Starting GIF transcode: {Path.GetFileName(inputPath)} -> {fps}fps {width}x{height}",
                LogLevel.Info);

            // 安全创建输出目录
            string? outDir = Path.GetDirectoryName(outputPath);
            if (!string.IsNullOrWhiteSpace(outDir))
                Directory.CreateDirectory(outDir);

            if (File.Exists(outputPath))
                File.Delete(outputPath);

            // ── 构建 filtergraph ────────────────────────────────────
            // fps → scale(可选) → split → palettegen + paletteuse
            var filters = new System.Collections.Generic.List<string> { $"fps={fps}" };
            if (width > 0 && height > 0)
                filters.Add($"scale={width}:{height}:flags=lanczos");
            filters.Add("split[a][b]");
            filters.Add("[a]palettegen=max_colors=256:stats_mode=diff[p]");
            filters.Add("[b][p]paletteuse=dither=bayer:bayer_scale=5");

            string filterGraph = string.Join(",", filters);

            // -loop: 0=无限, -1=不循环, N=循环 N 次
            string arguments =
                $"-y -i \"{inputPath}\" " +
                $"-vf \"{filterGraph}\" " +
                $"-loop {loopCount} " +
                $"\"{outputPath}\"";

            LogService.Split($"ffmpeg {arguments}", LogLevel.Debug);

            try
            {
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
                    LogService.Split($"GIF transcode failed: {result.ErrorMessage}", LogLevel.Error, ex);
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
                        result.ErrorMessage = $"GIF transcode timeout (>5 minutes). FFmpeg output: {cancelError}";
                        LogService.Split($"GIF transcode timeout: {result.ErrorMessage}", LogLevel.Error);
                        return result;
                    }
                    if (!string.IsNullOrWhiteSpace(cancelError))
                    {
                        LogService.Split($"[FFmpeg stderr on cancel]: {cancelError}", LogLevel.Warning);
                    }
                    result.Success = false;
                    result.ErrorMessage = "GIF transcode cancelled by user";
                    LogService.Split("GIF transcode cancelled", LogLevel.Warning);
                    return result;
                }

                stopwatch.Stop();
                result.Duration = stopwatch.Elapsed;

                if (process.ExitCode == 0 && File.Exists(outputPath))
                {
                    result.Success = true;
                    result.OutputPath = outputPath;
                    LogService.Split(
                        $"GIF transcode completed: {Path.GetFileName(outputPath)} ({result.Duration.TotalSeconds:F1}s)",
                        LogLevel.Info);
                }
                else
                {
                    string errorOutput = await errorReadTask.ConfigureAwait(false);
                    result.Success = false;
                    result.ErrorMessage = $"FFmpeg exited with code {process.ExitCode}. Output: {errorOutput}";
                    LogService.Split($"GIF transcode failed: {result.ErrorMessage}", LogLevel.Error);
                }
            }
            catch (Exception ex)
            {
                stopwatch.Stop();
                result.Duration = stopwatch.Elapsed;
                result.Success = false;
                result.ErrorMessage = ex.Message;
                LogService.Split($"GIF transcode error: {ex.Message}", LogLevel.Error, ex);
            }

            return result;
        }

        // Ensure the source video is in MP4 format, transcoding if necessary.
        // If already MP4, returns the original path with zero overhead.
        // If MOV (or other), transcodes to a temp file in <paramref name="workDir"/>.
        // è¿å: (pathToUse, wasTranscoded):pathToUse — original or temp file path;wasTranscoded — true when a temp file was created (caller must clean up).
        // <param name="forceMp4">When true, always transcode to MP4. When false, only transcode if
        // the container is not MP4-compatible (e.g. AVI, MKV). MOV files are left as-is.</param>
        // <param name="useFaststart">When true (default), use +faststart to move moov to beginning
        // for streaming. When false (Huawei protocol), keep moov at end and set mp42 brand + ©too.</param>
        public static async Task<(string Path, bool WasTranscoded)> EnsureMp4Async(
            string inputPath,
            string workDir,
            CancellationToken token = default,
            bool forceMp4 = true,
            bool useFaststart = true)
        {
            // Already MP4? No-op.
            if (DetectContainerFormat(inputPath) == "mp4")
                return (inputPath, false);

            // When forceMp4 is disabled, only transcode incompatible formats (AVI, MKV, etc.).
            // MOV files are compatible with Live Photo protocols — skip transcode.
            if (!forceMp4 && DetectContainerFormat(inputPath) == "mov")
            {
                LogService.Merge(
                    $"Skipping MP4 conversion (forceMp4=off): '{Path.GetFileName(inputPath)}'",
                    LogLevel.Debug);
                return (inputPath, false);
            }

            LogService.Merge(
                $"Auto-transcoding to MP4: '{Path.GetFileName(inputPath)}'",
                LogLevel.Debug);

            string tempPath = Path.Combine(
                workDir,
                $"{Path.GetFileNameWithoutExtension(inputPath)}_merge_trans.mp4");

            if (File.Exists(tempPath))
                try { File.Delete(tempPath); } catch { }

            var result = await TranscodeToMp4Async(inputPath, tempPath, token, useFaststart);

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

        // ── container detection ───────────────────────────────────────

        // Detect video container from ftyp box, falling back to extension.
        private static string DetectContainerFormat(string path)
        {
            if (!File.Exists(path))
                return "unknown";

            string ext = Path.GetExtension(path).ToLowerInvariant();
            try
            {
                using var fs = new FileStream(
                    path, FileMode.Open, FileAccess.Read, FileShare.Read,
                    bufferSize: 64, options: FileOptions.SequentialScan);

                byte[] header = new byte[16];
                if (fs.Read(header, 0, header.Length) < 12)
                    return ExtToFormat(ext);

                string boxType = System.Text.Encoding.ASCII.GetString(header, 4, 4);
                if (boxType != "ftyp")
                    return ExtToFormat(ext);

                string majorBrand = System.Text.Encoding.ASCII.GetString(header, 8, 4);
                return majorBrand switch
                {
                    "qt  " => "mov",
                    "mp41" => "mp4",
                    "mp42" => "mp4",
                    "isom" => "mp4",
                    "avc1" => "mp4",
                    "iso2" => "mp4",
                    "mmp4" => "mp4",
                    "MSNV" => "mp4",
                    _ => ExtToFormat(ext),
                };
            }
            catch
            {
                return ExtToFormat(ext);
            }
        }

        private static string ExtToFormat(string ext) => ext switch
        {
            ".mp4" => "mp4",
            ".mov" => "mov",
            ".m4v" => "mp4",
            _      => "unknown",
        };

        // 通用视频转码方法（支持降级重试）
        private static async Task<TranscodeResult> TranscodeAsync(
            string inputPath,
            string outputPath,
            VideoFormat targetFormat,
            CancellationToken token,
            bool useFaststart = true)
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
            bool useHardwareEncoder = !string.IsNullOrEmpty(GetEncoderForCodec(codec));
            bool transcodeCompleted = false;
            string? lastError = null;

            while (!transcodeCompleted)
            {
                try
                {
                    string arguments = BuildFFmpegArguments(inputPath, outputPath, targetFormat, !useHardwareEncoder, useFaststart);

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

        // 为指定目标格式获取编码器（硬件优先，支持软件回退）。
        // 自动从设置读取保存的硬件编码器，或检测 FFmpeg 可用编码器。
        // targetFormat: 目标视频格式。
        // forceSoftware: 是否强制使用软件编码器。
        // è¿å: (编码器名, 编码参数)。
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

        // 通过枚举 FFmpeg 编码器列表检测可用的硬件编码器。
        // 按优先级顺序：NVENC > AMF > QSV > VAAPI。
        // codec: 编码标准（"h264" 或 "hevc"）。
        // è¿å: 检测到的硬件编码器名，如 "h264_nvenc"，若无则返回 null。
        private static string? DetectHardwareEncoderForCodec(string codec)
        {
            try
            {
                string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
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

        // 构建 FFmpeg 视频滤镜字符串。
        // 包含 setsar=1（设置 SAR 为 1:1）以及可选的 HEVC Conformance Window 黑边裁切。
        private static string BuildVideoFilter(VideoFormat targetFormat, string encoder, string inputPath)
        {
            string sarFilter = "setsar=1";
            var cropFilter = CropFilterForConformanceWindow(inputPath);
            if (string.IsNullOrEmpty(cropFilter))
                return $"-vf \"{sarFilter}\"";
            return $"-vf \"{cropFilter},{sarFilter}\"";
        }

        // ╔══════════════════════════════════════════════════════════════════════════╗
        // ║  HEVC Conformance Window 黑边修复 — 完整设计文档                        ║
        // ╠══════════════════════════════════════════════════════════════════════════╣
        // ║                                                                          ║
        // ║  【问题背景】                                                            ║
        // ║  iPhone 拍摄的 Live Photo 视频（MOV 容器 + HEVC 编码）有两层尺寸信息：   ║
        // ║                                                                          ║
        // ║  1. HEVC 码流 SPS 的 conformance window                                 ║
        // ║     coded=1440×1088 → display=1440×1080（CTU 64×64 对齐垫了 8 像素）    ║
        // ║     → 播放器只认这个，自动裁掉垫像素，所以播放器看着没黑边              ║
        // ║                                                                          ║
        // ║  2. MOV 容器的 clap (Clean Aperture) 原子                               ║
        // ║     exiftool 可读: CleanApertureDimensions = 1308×980                   ║
        // ║     → 这是旧版 iPhone (iOS ≤12/13) 写入的"干净显示区"                   ║
        // ║     → 数值不准确，可能是电子防抖或 ISP 处理的残余参数                   ║
        // ║     → 播放器不理它，但 FFmpeg 认它！                                    ║
        // ║                                                                          ║
        // ║  【FFmpeg 的问题】                                                       ║
        // ║  -apply_cropping 1 (默认): SPS window + clap 两层都裁                  ║
        // ║    1440×1088 → 1308×980 → 旋转 90° → 980×1308 ❌ 严重过裁！            ║
        // ║  -apply_cropping 0: 两层都不裁                                          ║
        // ║    1440×1088 → 旋转 90° → 1088×1440 ⚠️ 留了 8px CTU 垫像素 = 黑边      ║
        // ║  FFmpeg 没有"只关 clap 不关 SPS window"的开关                          ║
        // ║                                                                          ║
        // ║  【我们的方案】                                                          ║
        // ║  1. -apply_cropping 0: 全关 FFmpeg 的自动裁切                          ║
        // ║  2. exiftool 读 ImageWidth/ImageHeight（= QuickTime tkhd 尺寸           ║
        // ║     = Production Aperture = 播放器显示尺寸，不读 clap）                  ║
        // ║  3. 根据 Rotation 算出 autorotate 后的期望尺寸                          ║
        // ║  4. crop 滤镜裁到该尺寸（HEVC CTU 垫像素自然被去除）                     ║
        // ║                                                                          ║
        // ║  【与播放器行为对齐】                                                    ║
        // ║  播放器 = 认 SPS conformance window + 不理 clap                        ║
        // ║  我们   = 认 tkhd 显示尺寸 + 不理 clap + 有 64px 安全锁               ║
        // ║  两者结果一致                                                            ║
        // ║                                                                          ║
        // ║  【安全防护（宁可不裁，绝不误裁）】                                      ║
        // ║  a. exiftool 读不到 → 跳过                                              ║
        // ║  b. 尺寸 < 320 或 > 8192 → 脏数据 → 跳过                               ║
        // ║  c. Rotation 为 null → 无法判断方向 → 跳过                             ║
        // ║  d. Rotation 非 0/90/180/270 标准值 → 异常数据 → 跳过                  ║
        // ║  e. crop 滤镜内置 64px 硬上限                                           ║
        // ║     if(gte(iw-target,64), iw, target)                                   ║
        // ║     实际帧比期望宽超过 64px → metadata 不可靠 → 不裁，保留原帧          ║
        // ║     HEVC CTU=64×64，单边垫量 ≤ 64，所以 64 是安全天花板                ║
        // ║     如果差超过 64，说明 exiftool 读到的尺寸不属于这个视频              ║
        // ║     （如 2K/4K 视频被错误打了 1080p 标签）                              ║
        // ║  f. 整个方法包在 try-catch 里，任何异常 → 跳过                         ║
        // ║                                                                          ║
        // ║  【依赖】                                                                ║
        // ║  exiftool（项目 Tools/ 目录已有）+ FFmpeg（项目 Tools/ 目录已有）       ║
        // ║  不额外引入任何工具                                                     ║
        // ╚══════════════════════════════════════════════════════════════════════════╝
        private static string? CropFilterForConformanceWindow(string inputPath)
        {
            try
            {
                string? exifToolPath = ExternalToolLocator.FindExifTool();
                if (string.IsNullOrEmpty(exifToolPath) || !File.Exists(inputPath))
                    return null;

                // -s -s -S → 只输出值，三个标签各占一行
                int? dispW = null, dispH = null, rotation = null;
                var psi = new ProcessStartInfo
                {
                    FileName = exifToolPath,
                    Arguments = $"-ImageWidth -ImageHeight -Rotation -CompressorID -s -s -S \"{inputPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using (var process = Process.Start(psi))
                {
                    if (process == null) return null;
                    string output = process.StandardOutput.ReadToEnd();
                    process.WaitForExit(5000);
                    // 输出: "1440\n1080\n90\n"（ImageWidth / ImageHeight / Rotation）
                    var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                    if (lines.Length >= 2)
                    {
                        int.TryParse(lines[0].Trim(), out int w); dispW = w > 0 ? w : null;
                        int.TryParse(lines[1].Trim(), out int h); dispH = h > 0 ? h : null;
                    }
                    if (lines.Length >= 3)
                    {
                        int.TryParse(lines[2].Trim(), out int r); rotation = r;
                    }
                }

                if (dispW == null || dispH == null) return null;

                // 防护：尺寸必须在合理范围内（320~8192），否则是 exiftool 读到了脏数据
                if (dispW.Value < 320 || dispW.Value > 8192 || dispH.Value < 320 || dispH.Value > 8192)
                {
                    LogService.Split($"Crop skipped: unreasonable dimensions {dispW}x{dispH}", LogLevel.Warning);
                    return null;
                }

                // autorotate 后 frame 尺寸变换：90°/270° 时 w↔h 互换
                bool isRotated = rotation != null && (Math.Abs(rotation.Value) == 90 || Math.Abs(rotation.Value) == 270);
                int expectedW = isRotated ? dispH.Value : dispW.Value;
                int expectedH = isRotated ? dispW.Value : dispH.Value;

                if (expectedW <= 0 || expectedH <= 0) return null;

                // 防护：宽高比变化不能超过 2%（防误裁）
                // 正常情况下 coded ≈ display（差几个 CTU 垫像素），如果 display
                // 尺寸本身就是错的，不做 crop 比做错 crop 好。
                // 这里检查 display 和 coded 的差异，由 -apply_cropping 0 保留全帧，
                // 如果 display size 和 coded size 差距 > 5%，视为数据不可靠，跳过 crop。
                // 注：此时我们还不知道 coded size（exiftool 读不到 HEVC coded 尺寸），
                // 但 crop 目标即 display，若 coded 远大于 display 会把内容裁烂。
                // 大部分视频 coded-display 差 < 1%，所以最大允许 2% 差异。
                // 但这个检查需要 coded 尺寸…所以退而求其次：仅当 exiftool 读到
                // rotation 且值合法时才做 crop，否则宁可留 padding 也不错裁。
                if (rotation == null)
                {
                    // 无旋转信息 → 无法可靠判断方向 → 跳过
                    LogService.Split($"Crop skipped: no rotation metadata", LogLevel.Debug);
                    return null;
                }
                if (rotation.Value != 0 && rotation.Value != 90 && rotation.Value != 180 && rotation.Value != 270 && rotation.Value != -90 && rotation.Value != -180 && rotation.Value != -270)
                {
                    LogService.Split($"Crop skipped: unexpected rotation {rotation.Value}°", LogLevel.Warning);
                    return null;
                }

                LogService.Split(
                    $"exiftool crop: display={dispW.Value}x{dispH.Value} rot={rotation}° → crop={expectedW}:{expectedH} (max 64px trim)",
                    LogLevel.Debug);

                // 最终防线：crop 最多裁 64 像素（HEVC CTU 64×64，单边垫量 ≤ 64）。
                // 如果实际帧比目标宽/高超过 64px，说明 exiftool 数据不可靠
                // （可能是 2K/4K 视频被错误打标），此时不裁，保留原始帧。
                // FFmpeg 表达式：iw-目标>64 则用 iw，否则用目标。
                return $"crop='if(gte(iw-{expectedW},64),iw,{expectedW})':'if(gte(ih-{expectedH},64),ih,{expectedH})':0:0";
            }
            catch (Exception ex)
            {
                LogService.Split($"CropFilterForConformanceWindow error: {ex.Message}", LogLevel.Warning);
                return null;
            }
        }

        // ── Adaptive audio detection ───────────────────────────────────

        // Detect source audio format and channel count via exiftool.
        // Returns (format, channels) — format is exiftool's AudioFormat
        // (e.g. "lpcm", "mp4a", "MPEG AAC Audio"), channels is integer count.
        // Returns (null, 0) when detection fails.
        private static (string? format, int channels) DetectAudioInfo(string inputPath)
        {
            try
            {
                string? exifToolPath = ExternalToolLocator.FindExifTool();
                if (string.IsNullOrEmpty(exifToolPath))
                    return (null, 0);

                var psi = new ProcessStartInfo
                {
                    FileName = exifToolPath,
                    Arguments = $"-AudioFormat -AudioChannels -s -s -S \"{inputPath}\"",
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true
                };
                using var process = Process.Start(psi);
                if (process == null) return (null, 0);
                string output = process.StandardOutput.ReadToEnd();
                process.WaitForExit(5000);

                var lines = output.Split('\n', StringSplitOptions.RemoveEmptyEntries);
                string? format = null;
                int channels = 0;

                if (lines.Length >= 1)
                {
                    format = lines[0].Trim();
                    if (string.IsNullOrWhiteSpace(format)) format = null;
                }
                if (lines.Length >= 2)
                {
                    int.TryParse(lines[1].Trim(), out channels);
                }

                return (format, channels);
            }
            catch
            {
                return (null, 0);
            }
        }

        // Build adaptive audio arguments for MP4 transcoding.
        // - Already compressed (AAC/MP3/MP4A): -c:a copy — avoids generation loss
        // - PCM (lpcm/raw/twos/sowt): re-encode to AAC with channel-based bitrate
        // · Mono (most live photos, voice memos): 256k — near-transparent
        // · Stereo (music, ambient): 320k — AAC maximum, transparent
        // · Unknown channels: 256k — safe middle ground
        // - Detection failure: AAC 256k — conservative fallback
        private static string BuildAudioArgsForMp4(string inputPath)
        {
            var (format, channels) = DetectAudioInfo(inputPath);

            // Already-compressed formats MP4 supports natively → copy
            if (format != null)
            {
                string fmt = format.ToLowerInvariant().Trim();
                bool isCompressed = fmt is "mp4a" or "aac" or "mpeg aac audio"
                    or "mp3" or "mpeg audio" or "mpa" or "mp2";
                if (isCompressed)
                {
                    LogService.Split(
                        $"Audio: source is {format} → -c:a copy (no generation loss)",
                        LogLevel.Debug);
                    return "-c:a copy";
                }
            }

            // PCM (lpcm, pcm, twos, sowt, raw, in24, etc.) or unknown → encode to AAC
            int targetBitrate = channels switch
            {
                1 => 256,
                >= 2 => 320,
                _ => 256
            };

            string reason = format ?? "unknown format";
            LogService.Split(
                $"Audio: {reason}, {channels}ch → AAC {targetBitrate}k",
                LogLevel.Debug);

            return $"-c:a aac -b:a {targetBitrate}k";
        }

        private static string BuildFFmpegArguments(string inputPath, string outputPath,
            VideoFormat targetFormat, bool forceSoftwareEncoder = false, bool useFaststart = true)
        {
            var (videoEncoder, videoParams) = GetEncoderForFormat(targetFormat, forceSoftwareEncoder);
            int threadCount = GetThreadCount(videoEncoder);

            string pixelFormat = GetPixelFormatParams(videoEncoder, targetFormat);
            string videoFilter = BuildVideoFilter(targetFormat, videoEncoder, inputPath);

            string audioArgs = BuildAudioArgsForMp4(inputPath);

            // +faststart: moov at beginning (matches Mate 60/80 real files).
            // Huawei protocol (useFaststart=false): additionally set mp42 brand + ©too.
            // NON-Huawei: just +faststart, brandAndMeta is empty.
            string brandAndMeta = useFaststart
                ? ""
                : " -brand mp42 -metadata too=\"Openharmony6.1\"";

            return targetFormat switch
            {
                VideoFormat.MP4 => $"-apply_cropping 0 -y -i \"{inputPath}\" " +
                    $"-map 0:V:0 -map 0:a:0? " +
                    $"-map_metadata 0 " +
                    $"-threads {threadCount} " +
                    $"{videoFilter} " +
                    $"{pixelFormat} " +
                    $"-c:v {videoEncoder} {videoParams} " +
                    $"{audioArgs} " +
                    $"-movflags +faststart{brandAndMeta} " +
                    $"\"{outputPath}\"",

                VideoFormat.MOV => $"-apply_cropping 0 -y -i \"{inputPath}\" " +
                    $"-map 0:V:0 -map 0:a:0? " +
                    $"-map_metadata 0 " +
                    $"-threads {threadCount} " +
                    $"{videoFilter} " +
                    $"{pixelFormat} " +
                    $"-c:v {videoEncoder} {videoParams} -tag:v hvc1 " +
                    $"-c:a copy " +
                    $"-movflags +faststart{brandAndMeta} " +
                    $"\"{outputPath}\"",

                _ => $"-y -i \"{inputPath}\" -c copy -map 0:V:0 -map 0:a:0? \"{outputPath}\""
            };
        }

        // 获取像素格式参数。MP4 始终强制 yuv420p（保证兼容性），
        // HEVC/H.265 编码器自动优选，不强制。
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

        // 读取 FFmpeg 进程的 stderr 输出（异步）。
        private static async Task<string> ReadFFmpegOutputAsync(Process process)
        {
            try { return await process.StandardError.ReadToEndAsync().ConfigureAwait(false); }
            catch { return string.Empty; }
        }

    }
}