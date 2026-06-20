using LivePhotoBox.Models;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    public static class LivePhotoRepairService
    {
        private static string? _exifToolPath;
        private static string ExifToolPath => _exifToolPath ??= ExternalToolLocator.FindExifTool() ?? Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe");

        private static string? _jheadPath;
        private static string JheadPath => _jheadPath ??= ExternalToolLocator.FindJhead() ?? Path.Combine(AppContext.BaseDirectory, "Tools", "jhead.exe");

        private static string? _jpegTranPath;
        private static string JpegTranPath => _jpegTranPath ??= Path.Combine(AppContext.BaseDirectory, "Tools", "jpegtran.exe");

        private static string? _ffmpegPath;
        private static string FFmpegPath => _ffmpegPath ??= ExternalToolLocator.FindFFmpeg() ?? Path.Combine(AppContext.BaseDirectory, "Tools", "ffmpeg.exe");


        private static bool IsHeicFile(string path) =>
            path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);

        private static bool IsVideoFile(string path) =>
            path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

        /// <summary>
        /// 统一的内部日志记录器
        /// </summary>
        private static void WriteDebugLog(string level, string source, string message, string details = "")
        {
            var logLevel = level switch
            {
                "ERROR" => LogLevel.Error,
                "WARN" => LogLevel.Warning,
                _ => LogLevel.Info
            };

            string msg = string.IsNullOrWhiteSpace(details) ? message : $"{message}\n{details.Trim()}";
            LogService.Repair(msg, logLevel);
        }

        /// <summary>
        /// 1. 扫描与诊断文件：仅用 exiftool 读取方向与缩略图状态。
        /// 可传入常驻 exiftool 进程避免重复启动开销。
        /// </summary>
        public static async Task<RepairAnalysisResult> AnalyzeFileAsync(string filePath, PersistentExifTool? persistentExifTool = null, CancellationToken token = default)
        {
            // Video files use a separate analysis path (ffprobe + exiftool Rotation)
            if (IsVideoFile(filePath))
                return await AnalyzeVideoAsync(filePath, persistentExifTool, token);

            bool isHeic = IsHeicFile(filePath);

            if (!File.Exists(ExifToolPath))
            {
                WriteDebugLog("ERROR", "Analyze", ResourceService.GetString("Log_MissingDependency"), $"File not found: {ExifToolPath}");
                return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = ResourceService.GetString("Error_ExifToolMissing") };
            }

            // jhead / jpegtran 只用于 JPEG 修复，HEIC 不需要
            if (!isHeic)
            {
                if (!File.Exists(JheadPath))
                {
                    WriteDebugLog("ERROR", "Analyze", ResourceService.GetString("Log_MissingDependency"), $"File not found: {JheadPath}");
                    return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = "jhead.exe not found" };
                }

                if (!File.Exists(JpegTranPath))
                {
                    WriteDebugLog("ERROR", "Analyze", ResourceService.GetString("Log_MissingDependency"), $"File not found: {JpegTranPath}");
                    return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = "jpegtran.exe not found (required by jhead)" };
                }
            }

            try
            {
                string output;
                string error = "";

                if (persistentExifTool != null)
                {
                    // ✅ 快速路径：使用常驻 exiftool 进程，无启动开销
                    // Rotation 用于 HEIC（QuickTime 标签）+ JPEG 兼容
                    output = await persistentExifTool.SendCommandAsync(token, "-j", "-Rotation", "-Orientation", "-ThumbnailImage", filePath);
                    error = persistentExifTool.FlushStderr();
                }
                else
                {
                    // 慢速路径：启动新的 exiftool 进程（兼容独立调用）
                    string tempDir = Path.GetTempPath();
                    string toolDir = Path.GetDirectoryName(ExifToolPath) ?? AppContext.BaseDirectory;

                    var psi = new ProcessStartInfo
                    {
                        FileName = ExifToolPath,
                        WorkingDirectory = toolDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8
                    };

                    psi.Environment["TEMP"] = tempDir;
                    psi.Environment["TMP"] = tempDir;
                    psi.Environment["PAR_GLOBAL_TMPDIR"] = tempDir;

                    psi.ArgumentList.Add("-j");
                    psi.ArgumentList.Add("-Rotation");
                    psi.ArgumentList.Add("-Orientation");
                    psi.ArgumentList.Add("-ThumbnailImage");
                    psi.ArgumentList.Add(filePath);

                    using var process = Process.Start(psi);
                    if (process == null)
                    {
                        WriteDebugLog("ERROR", "Analyze", ResourceService.GetString("Log_ExifToolStartFailed"), "Process.Start returned null.");
                        throw new Exception(ResourceService.GetString("Error_CannotStartExifTool"));
                    }

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    try
                    {
                        await process.WaitForExitAsync(token);
                    }
                    catch (OperationCanceledException)
                    {
                        process.Kill();
                        throw;
                    }
                    output = await outputTask;
                    error = await errorTask;
                }

                return ParseExifToolOutput(output, error, filePath);
            }
            catch (OperationCanceledException)
            {
                // 取消信号必须穿透，不吞
                throw;
            }
            catch (InvalidOperationException)
            {
                // exiftool 进程崩溃类异常（stdout 关闭、进程退出等），
                // 穿透出去让扫描停止，不要吞掉然后傻傻地逐个文件重试
                throw;
            }
            catch (Exception ex)
            {
                WriteDebugLog("ERROR", "Analyze", ResourceService.Format("Log_CSharpException", Path.GetFileName(filePath)), ex.ToString());
                // 把错误详情直接显示在结果中，用户不需要去翻日志
                string shortMsg = ex.Message;
                if (shortMsg.Length > 200) shortMsg = shortMsg[..200] + "…";
                return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = $"{ResourceService.GetString("Error_InternalCheckLog")}\n{ex.GetType().Name}: {shortMsg}" };
            }
        }

        /// <summary>
        /// 从方向标签字符串中提取旋转角度（0/90/180/270）
        /// </summary>
        private static int ParseAngleFromTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return 0;
            if (tag.Contains("270")) return 270;
            if (tag.Contains("180")) return 180;
            if (tag.Contains("90")) return 90;
            return 0;
        }

        /// <summary>
        /// 判断方向标签是否包含镜像/翻转标记（mirror / flip）
        /// </summary>
        private static bool TagHasMirror(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return false;
            return tag.Contains("Mirror", StringComparison.OrdinalIgnoreCase)
                || tag.Contains("Flip", StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>
        /// 根据 QuickTime:Rotation 值推导正确的 EXIF Orientation 值
        /// </summary>
        private static string GetOrientationForRotation(string rotation)
        {
            int angle = ParseAngleFromTag(rotation);
            return angle switch
            {
                90 => "Rotate 90 CW",
                180 => "Rotate 180",
                270 => "Rotate 270 CW",
                _ => "Horizontal (normal)"
            };
        }

        /// <summary>
        /// 安全地从 JsonElement 读取值（兼容 string 和 number 类型）。
        /// exiftool 对 MOV 视频的 Rotation 输出为数字（如 90），对 JPEG/HEIC 为字符串（如 "Rotate 90 CW"）。
        /// </summary>
        private static string GetJsonValueAsString(JsonElement element, string propertyName)
        {
            if (!element.TryGetProperty(propertyName, out var prop))
                return "";

            return prop.ValueKind switch
            {
                System.Text.Json.JsonValueKind.String => prop.GetString() ?? "",
                System.Text.Json.JsonValueKind.Number => prop.GetRawText(),
                _ => prop.ToString()
            };
        }

        /// <summary>
        /// 解析 exiftool 的 JSON 输出，生成 RepairAnalysisResult
        /// </summary>
        private static RepairAnalysisResult ParseExifToolOutput(string output, string error, string filePath)
        {
            if (string.IsNullOrWhiteSpace(output) || !output.TrimStart().StartsWith("["))
            {
                WriteDebugLog("ERROR", "Analyze", ResourceService.Format("Log_ExifToolParseFailed", Path.GetFileName(filePath)), $"StdError:\n{error}\n\nStdOutput:\n{output}");
                // 把 exiftool 的实际错误直接显示在结果中
                string errDetail = string.IsNullOrWhiteSpace(error) ? "stdout is empty or not JSON" : error.Trim();
                if (errDetail.Length > 300) errDetail = errDetail[..300] + "…";
                return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = $"{ResourceService.GetString("Error_CheckLog")}\n{errDetail}" };
            }

            using var doc = JsonDocument.Parse(output);
            var root = doc.RootElement[0];

            string rotation = GetJsonValueAsString(root, "Rotation");
            string orientation = GetJsonValueAsString(root, "Orientation");
            bool hasThumb = root.TryGetProperty("ThumbnailImage", out _);
            bool isHeic = IsHeicFile(filePath);

            var tags = new List<string>();
            bool needsOrientationFix = false;

            if (isHeic)
            {
                // ── HEIC 分析：Rotation 是 QuickTime 标签，用于告诉查看器如何旋转显示 ──
                // HEIC 像素数据无法无损旋转（无 jpegtran 等效工具），因此 Rotation 是
                // 正确的元数据，不是问题。只检测以下两种真正的问题：
                //   1. Orientation 含有镜像/翻转标记（mirror/flip）— 几乎总是误写入
                //   2. Orientation 的旋转角度与 Rotation 不一致 — 会导致显示冲突

                int rotAngle = ParseAngleFromTag(rotation);
                int orientAngle = ParseAngleFromTag(orientation);
                bool orientHasMirror = TagHasMirror(orientation);
                bool angleMismatch = rotAngle != orientAngle;

                needsOrientationFix = orientHasMirror || angleMismatch;

                if (orientHasMirror)
                    tags.Add($"[{ResourceService.GetString("Tag_OrientationMirror")}]");

                if (angleMismatch)
                    tags.Add($"[{ResourceService.GetString("Tag_OrientationAngleMismatch")}]");

                if (hasThumb)
                    tags.Add($"[{ResourceService.GetString("Tag_ExtraThumbnail")}]");
            }
            else
            {
                // ── JPEG 分析：维持原有逻辑，jhead -autorot 可以无损旋转像素 ──
                bool hasRotation = (!string.IsNullOrWhiteSpace(rotation)
                    && !rotation.Equals("Horizontal (normal)", StringComparison.OrdinalIgnoreCase)
                    && !rotation.Equals("0", StringComparison.Ordinal))
                    ||
                    (!string.IsNullOrWhiteSpace(orientation)
                    && !orientation.Equals("Horizontal (normal)", StringComparison.OrdinalIgnoreCase)
                    && !orientation.Equals("1", StringComparison.Ordinal));

                if (hasRotation)
                {
                    string rotSource = !string.IsNullOrWhiteSpace(rotation) ? rotation : orientation;
                    int angle = 0;
                    if (rotSource.Contains("90", StringComparison.OrdinalIgnoreCase)) angle = 90;
                    else if (rotSource.Contains("180", StringComparison.OrdinalIgnoreCase)) angle = 180;
                    else if (rotSource.Contains("270", StringComparison.OrdinalIgnoreCase)) angle = 270;
                    tags.Add($"[{ResourceService.Format("Tag_RotationLabel", angle)}]");
                }

                if (hasThumb)
                    tags.Add($"[{ResourceService.GetString("Tag_ExtraThumbnail")}]");
            }

            if (tags.Count == 0)
            {
                return new RepairAnalysisResult
                {
                    IssueType = RepairIssueType.Perfect,
                    IssueDescription = $"[{ResourceService.GetString("Status_Perfect")}]",
                    RotationAngle = 0,
                    HasThumbnail = false
                };
            }

            // HEIC: orientation 修复归类为 NeedsRebuild（元数据重建）
            // JPEG: 旋转修复归类为 NeedsRebuild
            bool needsRebuild = isHeic ? needsOrientationFix : tags.Any(t => t.Contains("°"));
            RepairIssueType type = needsRebuild ? RepairIssueType.NeedsRebuild : RepairIssueType.NeedsStrip;

            string lang = LanguageService.GetCurrentLanguageTag();
            string finalDescription;
            if (!string.IsNullOrWhiteSpace(lang) && lang.StartsWith("zh", StringComparison.OrdinalIgnoreCase))
            {
                var formattedLines = new List<string>();
                for (int i = 0; i < tags.Count; i += 2)
                {
                    var lineTags = tags.Skip(i).Take(2);
                    formattedLines.Add(string.Join(" ", lineTags));
                }
                finalDescription = string.Join("\n", formattedLines);
            }
            else
            {
                finalDescription = string.Join(" ", tags);
            }

            return new RepairAnalysisResult
            {
                IssueType = type,
                IssueDescription = finalDescription,
                RotationAngle = 0,
                HasThumbnail = hasThumb,
                HeicOriginalRotation = isHeic ? rotation : string.Empty
            };
        }

        /// <summary>
        /// Analyze video file (MOV/MP4) with exiftool. Reads Rotation, AvgBitrate, CompressorID.
        /// No ffprobe needed — exiftool provides all necessary metadata.
        /// </summary>
        private static async Task<RepairAnalysisResult> AnalyzeVideoAsync(
            string filePath, PersistentExifTool? persistentExifTool, CancellationToken token)
        {
            if (!File.Exists(ExifToolPath))
            {
                WriteDebugLog("ERROR", "AnalyzeVideo", ResourceService.GetString("Log_MissingDependency"), $"File not found: {ExifToolPath}");
                return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = ResourceService.GetString("Error_ExifToolMissing") };
            }

            try
            {
                string output;
                string error = "";

                // Read Rotation, dimensions, codec ID, and average bitrate — all in one exiftool call
                string[] exifArgs = { "-j", "-Rotation", "-ImageWidth", "-ImageHeight", "-AvgBitrate", "-CompressorID", filePath };

                if (persistentExifTool != null)
                {
                    output = await persistentExifTool.SendCommandAsync(token, exifArgs);
                    error = persistentExifTool.FlushStderr();
                }
                else
                {
                    string tempDir = Path.GetTempPath();
                    string toolDir = Path.GetDirectoryName(ExifToolPath) ?? AppContext.BaseDirectory;

                    var psi = new ProcessStartInfo
                    {
                        FileName = ExifToolPath,
                        WorkingDirectory = toolDir,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        StandardOutputEncoding = System.Text.Encoding.UTF8
                    };

                    psi.Environment["TEMP"] = tempDir;
                    psi.Environment["TMP"] = tempDir;
                    psi.Environment["PAR_GLOBAL_TMPDIR"] = tempDir;

                    foreach (var arg in exifArgs) psi.ArgumentList.Add(arg);

                    using var process = Process.Start(psi);
                    if (process == null)
                        return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = "Cannot start exiftool for video analysis" };

                    var outputTask = process.StandardOutput.ReadToEndAsync();
                    var errorTask = process.StandardError.ReadToEndAsync();
                    try { await process.WaitForExitAsync(token); }
                    catch (OperationCanceledException) { process.Kill(); throw; }
                    output = await outputTask;
                    error = await errorTask;
                }

                if (string.IsNullOrWhiteSpace(output) || !output.TrimStart().StartsWith("["))
                {
                    WriteDebugLog("ERROR", "AnalyzeVideo", $"Failed to parse exiftool output for {Path.GetFileName(filePath)}", $"Error: {error}");
                    return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = "Video metadata read failed" };
                }

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement[0];

                string rotation = GetJsonValueAsString(root, "Rotation");
                int angle = ParseAngleFromTag(rotation);
                string compressorId = GetJsonValueAsString(root, "CompressorID");
                long bitrateBps = ParseAvgBitrate(GetJsonValueAsString(root, "AvgBitrate")) ?? 0;

                if (angle == 0)
                {
                    return new RepairAnalysisResult
                    {
                        IssueType = RepairIssueType.Perfect,
                        IssueDescription = $"[{ResourceService.GetString("Status_Perfect")}]",
                        IsVideo = true,
                        VideoRotationAngle = 0,
                        VideoCodec = compressorId,
                        VideoBitrateBps = bitrateBps
                    };
                }

                string tag = ResourceService.Format("Tag_VideoRotation", angle);
                return new RepairAnalysisResult
                {
                    IssueType = RepairIssueType.NeedsRebuild,
                    IssueDescription = $"[{tag}]",
                    IsVideo = true,
                    VideoRotationAngle = angle,
                    VideoCodec = compressorId,
                    VideoBitrateBps = bitrateBps
                };
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                WriteDebugLog("ERROR", "AnalyzeVideo", $"Video analysis failed for {Path.GetFileName(filePath)}", ex.Message);
                return new RepairAnalysisResult
                {
                    IssueType = RepairIssueType.Error,
                    IssueDescription = $"{ResourceService.GetString("Error_InternalCheckLog")}\n{ex.GetType().Name}: {ex.Message}"
                };
            }
        }

        /// <summary>
        /// Parse exiftool AvgBitrate string (e.g. "12.2 Mbps") to bps (12200000).
        /// </summary>
        private static long? ParseAvgBitrate(string? avgBitrate)
        {
            if (string.IsNullOrWhiteSpace(avgBitrate)) return null;

            // Try "12.2 Mbps" format
            var match = System.Text.RegularExpressions.Regex.Match(avgBitrate, @"([\d.]+)\s*Mbps");
            if (match.Success && double.TryParse(match.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double mbps))
                return (long)(mbps * 1_000_000);

            // Try "10836062" (raw bps)
            if (long.TryParse(avgBitrate, out long rawBps))
                return rawBps;

            return null;
        }

        /// <summary>
        /// 2. 修复文件：jhead 自动旋转 + exiftool 剥离缩略图
        /// </summary>
        public static async Task<(bool Success, string Message)> RepairAsync(string sourcePath, string targetPath, RepairAnalysisResult analysis, CancellationToken token)
        {
            // Video repair uses FFmpeg re-encode with autorotate
            if (IsVideoFile(sourcePath))
                return await RepairVideoAsync(sourcePath, targetPath, analysis, token);

            bool isHeic = IsHeicFile(sourcePath);
            bool needsRotation = analysis.IssueType == RepairIssueType.NeedsRebuild;
            bool hasThumbnail = analysis.HasThumbnail;

            // 临时文件扩展名跟随原格式，避免 jhead 对 HEIC 文件报错
            string ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
            string tempWorkFile = Path.Combine(Path.GetTempPath(), $"lpb_repair_{Guid.NewGuid():N}{ext}");

            try
            {
                // 先复制到 %TEMP% 下的安全路径
                File.Copy(sourcePath, tempWorkFile, overwrite: true);

                token.ThrowIfCancellationRequested();

                if (isHeic)
                {
                    // ── HEIC 修复：仅修正 EXIF Orientation，保留 QuickTime:Rotation ──
                    // HEIC 像素数据无法无损旋转（无 jpegtran 等效工具），Rotation
                    // 标签是查看器正确显示照片的关键元数据，绝对不能清除。
                    // 只修复两类真问题：
                    //   1. Orientation 含镜像/翻转 → 用 Rotation 推导正确 Orientation
                    //   2. Orientation 角度与 Rotation 不一致 → 以 Rotation 为准
                    bool needsOrientFix = needsRotation; // analysis.IssueType == NeedsRebuild（HEIC 下即 Orientation 异常）
                    bool needsThumbStrip = hasThumbnail;

                    if (needsOrientFix || needsThumbStrip)
                    {
                        var exifArgs = new System.Collections.Generic.List<string>();
                        if (needsOrientFix)
                        {
                            // 根据 Rotation 推导正确的 Orientation，清除镜像/角度冲突
                            string targetOrientation = GetOrientationForRotation(
                                string.IsNullOrWhiteSpace(analysis.HeicOriginalRotation)
                                    ? "Horizontal (normal)"
                                    : analysis.HeicOriginalRotation);
                            exifArgs.Add($"-Orientation={targetOrientation}");
                            // 保持 Rotation 不变（不添加 -Rotation 参数）
                        }
                        if (needsThumbStrip)
                        {
                            exifArgs.Add("-ThumbnailImage=");
                            exifArgs.Add("-PreviewImage=");
                        }
                        exifArgs.Add("-overwrite_original");
                        exifArgs.Add(tempWorkFile);
                        await RunExifToolAsync(exifArgs.ToArray());
                    }
                }
                else
                {
                    // ── JPEG 修复：jhead 旋转 + exiftool 剥离缩略图 ──
                    if (needsRotation)
                    {
                        await RunJheadAsync("-autorot", tempWorkFile);
                        token.ThrowIfCancellationRequested();
                    }

                    if (hasThumbnail)
                    {
                        await RunExifToolAsync("-ThumbnailImage=", "-overwrite_original", tempWorkFile);
                    }
                }

                // 移动到目标路径
                if (File.Exists(targetPath)) File.Delete(targetPath);
                string? targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);
                File.Move(tempWorkFile, targetPath);

                WriteDebugLog("INFO", "Repair", ResourceService.Format("Log_RepairSuccess", Path.GetFileName(sourcePath)));
                return (true, ResourceService.GetString("Status_RepairSuccess"));
            }
            catch (OperationCanceledException)
            {
                WriteDebugLog("WARN", "Repair", ResourceService.Format("Log_RepairCancelled", Path.GetFileName(sourcePath)));
                throw;
            }
            catch (Exception ex)
            {
                WriteDebugLog("ERROR", "Repair", ResourceService.Format("Log_RepairFailed", Path.GetFileName(sourcePath)), ex.Message);
                return (false, ResourceService.Format("Task_Error", ex.Message));
            }
            finally
            {
                // 清理临时文件
                try { if (File.Exists(tempWorkFile)) File.Delete(tempWorkFile); } catch { }
            }
        }

        /// <summary>
        /// 3. 视频旋转修复：FFmpeg 编码 + autorotate，将旋转矩阵烘焙到像素中。
        /// 支持硬件加速（NVENC/QSV/AMF/VAAPI），失败自动回退 CPU 编码。
        /// 设置从"视频转码"面板读取（与拆分页面共享）。
        ///
        /// 安全机制：
        ///   1. 始终先写入临时文件，成功后再移动到目标路径。
        ///      防止硬件编码中途失败损坏源文件（原地修复时 sourcePath==targetPath）。
        ///   2. 硬件失败自动回退到软件编码，源文件始终保持完整。
        /// </summary>
        private static async Task<(bool Success, string Message)> RepairVideoAsync(
            string sourcePath, string targetPath, RepairAnalysisResult analysis, CancellationToken token)
        {
            if (!File.Exists(FFmpegPath))
            {
                WriteDebugLog("ERROR", "RepairVideo", "ffmpeg.exe not found", $"Expected at: {FFmpegPath}");
                return (false, ResourceService.GetString("Error_CannotStartExifTool") ?? "ffmpeg.exe not found");
            }

            string compId = analysis.VideoCodec ?? "";
            bool isHevc = compId.Contains("hvc", StringComparison.OrdinalIgnoreCase)
                       || compId.Contains("hev", StringComparison.OrdinalIgnoreCase);
            string codecKey = isHevc ? "hevc" : "h264";
            bool sourceIsMp4 = sourcePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase);

            string? targetDir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                Directory.CreateDirectory(targetDir);

            // 安全性：始终使用临时输出文件。
            // JPEG/HEIC 修复路径已经这样做了（先复制到 %TEMP%，再移动回来）。
            // 视频修复涉及重编码，硬件编码失败可能产生不完整文件；
            // 原地修复时 sourcePath==targetPath 会导致软件回退也读不到完整源文件。
            bool isInPlace = string.Equals(
                Path.GetFullPath(sourcePath),
                Path.GetFullPath(targetPath),
                StringComparison.OrdinalIgnoreCase);

            string tempOutput = Path.Combine(
                Path.GetTempPath(),
                $"lpb_vrepair_{Guid.NewGuid():N}{Path.GetExtension(targetPath)}");

            try
            {
                // Try hardware encoder first, fall back to software if it fails
                var (videoEncoder, videoParams) = GetRepairEncoder(codecKey);
                var (ok, errMsg) = await RunRepairFFmpegAsync(sourcePath, tempOutput, videoEncoder, videoParams, isHevc, codecKey, sourceIsMp4, token);

                bool isHardware = videoEncoder.Contains("nvenc") || videoEncoder.Contains("qsv")
                               || videoEncoder.Contains("amf") || videoEncoder.Contains("vaapi");

                if (!ok && isHardware)
                {
                    WriteDebugLog("WARN", "RepairVideo", $"Hardware encoder {videoEncoder} failed, falling back to software. HW error: {errMsg}");
                    // 清理硬件尝试可能残留的临时文件
                    try { if (File.Exists(tempOutput)) File.Delete(tempOutput); } catch { }
                    var (swEncoder, swParams) = GetSoftwareEncoder(codecKey);
                    (ok, errMsg) = await RunRepairFFmpegAsync(sourcePath, tempOutput, swEncoder, swParams, isHevc, codecKey, sourceIsMp4, token);
                }

                if (ok)
                {
                    // 成功：将临时文件移动到目标路径
                    if (File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                    }
                    File.Move(tempOutput, targetPath);
                    WriteDebugLog("INFO", "RepairVideo", $"Video repair succeeded: {Path.GetFileName(sourcePath)} (in-place={isInPlace})");
                    return (true, ResourceService.GetString("Status_RepairSuccess"));
                }
                else
                {
                    // Show the actual FFmpeg error to the user
                    string shortErr = errMsg.Length > 300 ? errMsg[^300..] : errMsg;
                    return (false, ResourceService.Format("Task_Error", $"FFmpeg: {shortErr.TrimEnd()}"));
                }
            }
            catch (OperationCanceledException)
            {
                WriteDebugLog("WARN", "RepairVideo", $"Video repair cancelled: {Path.GetFileName(sourcePath)}");
                throw;
            }
            catch (Exception ex)
            {
                WriteDebugLog("ERROR", "RepairVideo", $"Video repair failed: {Path.GetFileName(sourcePath)}", ex.Message);
                return (false, ResourceService.Format("Task_Error", ex.Message));
            }
            finally
            {
                // 清理临时文件
                try { if (File.Exists(tempOutput)) File.Delete(tempOutput); } catch { }
            }
        }

        /// <summary>
        /// Build FFmpeg arguments and run for video repair.
        /// Both hardware and software paths now align with the proven transcode path
        /// (VideoTranscodeService.BuildFFmpegArguments). Key alignment points:
        ///   -apply_cropping 0: HEVC decoder option, safe for both SW and HW decoder.
        ///     HW decoders (NVDEC) ignore it; SW decoder preserves full encoded frame.
        ///   -map 0:v:0: lowercase v, consistent with transcode path.
        ///   -threads: always specified (HW=1, SW=user configured).
        ///   -c:a aac: always re-encode audio (HW muxer can't copy PCM; safer than copy).
        ///   No forced -f: let FFmpeg auto-detect output format from extension.
        /// </summary>
        private static async Task<(bool success, string errorMessage)> RunRepairFFmpegAsync(
            string sourcePath, string targetPath,
            string videoEncoder, string videoParams,
            bool isHevc, string codecKey, bool sourceIsMp4,
            CancellationToken token)
        {
            bool isHardware = videoEncoder.Contains("nvenc") || videoEncoder.Contains("qsv")
                           || videoEncoder.Contains("amf") || videoEncoder.Contains("vaapi");

            var args = new List<string>
            {
                "-apply_cropping", "0",
                "-y",
                "-i", sourcePath,
                "-map", "0:v:0",
                "-map", "0:a:0?",
                "-map_metadata", "0",
                "-threads", GetRepairThreadCount(videoEncoder).ToString(),
                "-vf", "setsar=1",
                "-c:v", videoEncoder
            };

            // Encoder-specific params (CQP for HW, CRF+preset for SW)
            if (!string.IsNullOrWhiteSpace(videoParams))
            {
                foreach (var param in videoParams.Split(' ', StringSplitOptions.RemoveEmptyEntries))
                    args.Add(param);
            }

            // Pixel format: force yuv420p only for H.264 (non-HEVC), matching transcode path.
            // HEVC encoders auto-select the best format from source (e.g. p010le for 10-bit).
            if (!isHevc)
            {
                args.Add("-pix_fmt");
                args.Add("yuv420p");
            }

            // Input flags: +genpts regenerates missing timestamps (some H.264 MOV files
            // exported by third-party tools like AISI have broken/incomplete PTS).
            args.Add("-fflags");
            args.Add("+genpts");

            // Audio: always re-encode to AAC 192k.
            //   HW path: hardware muxer can't copy PCM audio.
            //   SW path: copy could fail if source has PCM in MP4 container.
            args.Add("-c:a");
            args.Add("aac");
            args.Add("-b:a");
            args.Add("192k");

            // Container flags: use -movflags +faststart (moov atom at front).
            // HEVC → tag hvc1 for Apple compatibility; H.264 → let FFmpeg auto-select (avc1).
            if (!sourceIsMp4 && isHevc)
            {
                args.Add("-tag:v");
                args.Add("hvc1");
            }
            args.Add("-movflags");
            args.Add("+faststart");

            args.Add(targetPath);

            string encType = isHardware ? "HW" : "SW";
            WriteDebugLog("INFO", "RepairVideo", $"FFmpeg ({encType}) [{videoEncoder}] {Path.GetFileName(sourcePath)}");
            return await RunFFmpegAsync(args, token);
        }

        /// <summary>
        /// Get encoder + params for repair (reads same settings as Split page).
        /// Tries hardware encoder first, falls back to software.
        /// Auto-derives HEVC encoder from H.264 when only h264 key is set (e.g. h264_nvenc → hevc_nvenc).
        /// </summary>
        private static (string encoder, string encoderParams) GetRepairEncoder(string codecKey)
        {
            string settingKey = $"SplitEncoder_{codecKey}";
            string? savedEncoder = AppSettingsService.GetValue<string?>(settingKey, null);

            // If no saved encoder for this codec, try to derive from the other codec's setting
            // (user may have only saved h264_nvenc but needs hevc_nvenc)
            if (string.IsNullOrEmpty(savedEncoder))
            {
                string otherKey = codecKey == "hevc" ? "SplitEncoder_h264" : "SplitEncoder_hevc";
                string? otherEncoder = AppSettingsService.GetValue<string?>(otherKey, null);
                if (!string.IsNullOrEmpty(otherEncoder))
                {
                    // Derive: h264_nvenc → hevc_nvenc, hevc_nvenc → h264_nvenc
                    string prefix = codecKey == "hevc" ? "h264_" : "hevc_";
                    string targetPrefix = codecKey == "hevc" ? "hevc_" : "h264_";
                    if (otherEncoder.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                    {
                        string derived = targetPrefix + otherEncoder.Substring(prefix.Length);
                        if (IsFFmpegEncoderAvailable(derived))
                        {
                            savedEncoder = derived;
                            // Save for future use so derivation isn't needed again
                            AppSettingsService.SetValue(settingKey, derived);
                            WriteDebugLog("INFO", "RepairVideo", $"Derived encoder: {otherEncoder} → {derived}");
                        }
                    }
                }
            }

            if (!string.IsNullOrEmpty(savedEncoder) && IsFFmpegEncoderAvailable(savedEncoder))
            {
                string hwParams = GetHardwareRepairParams(savedEncoder, codecKey);
                return (savedEncoder, hwParams);
            }

            return GetSoftwareEncoder(codecKey);
        }

        private static (string encoder, string encoderParams) GetSoftwareEncoder(string codecKey)
        {
            return codecKey == "hevc"
                ? ("libx265", "-preset medium -crf 14")
                : ("libx264", "-preset medium -crf 13");
        }

        private static string GetHardwareRepairParams(string encoder, string codecKey)
        {
            string lower = encoder.ToLowerInvariant();

            if (lower.StartsWith("h264"))
            {
                return lower switch
                {
                    "h264_nvenc" => "-preset p5 -rc:v vbr_hq -cq:v 19 -b:v 0 -maxrate:v 30M -bufsize:v 60M -profile:v high",
                    "h264_qsv" => "-global_quality 19 -look_ahead 1",
                    "h264_amf" => "-preset quality -rc cqp -qp 19",
                    "h264_vaapi" => "-quality 85 -rc_mode 1",
                    _ => "-preset medium -crf 13"
                };
            }

            return lower switch
            {
                "hevc_nvenc" => "-preset p5 -rc:v vbr_hq -cq:v 21 -b:v 0 -maxrate:v 25M -bufsize:v 50M -tune hq",
                "hevc_qsv" => "-global_quality 21 -look_ahead 1",
                "hevc_amf" => "-preset quality -rc cqp -qp 21",
                "hevc_vaapi" => "-quality 85 -rc_mode 1",
                _ => "-preset medium -crf 14"
            };
        }

        private static int GetRepairThreadCount(string? encoder)
        {
            int userThreads = AppSettingsService.GetValue("SplitThreadCount", Environment.ProcessorCount);

            if (!string.IsNullOrEmpty(encoder))
            {
                string enc = encoder.ToLowerInvariant();
                if (enc.Contains("nvenc") || enc.Contains("qsv") || enc.Contains("vaapi") || enc.Contains("amf"))
                    return Math.Min(userThreads, 1);
            }

            return userThreads;
        }

        private static bool IsFFmpegEncoderAvailable(string encoder)
        {
            try
            {
                if (!File.Exists(FFmpegPath)) return false;

                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = FFmpegPath,
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
            catch { return false; }
        }

        /// <summary>
        /// Run FFmpeg process with given arguments. Returns (success, errorMessage).
        /// On failure, errorMessage contains the last portion of FFmpeg stderr for diagnosis.
        /// </summary>
        private static async Task<(bool success, string errorMessage)> RunFFmpegAsync(List<string> args, CancellationToken token)
        {
            string tempDir = Path.GetTempPath();

            // Don't set WorkingDirectory — FFmpeg needs to find CUDA/NVENC DLLs via the
            // standard DLL search path. Setting it to ffmpeg.exe's directory can break
            // this on systems where ffmpeg is installed via winget (symlinked directory).
            var psi = new ProcessStartInfo
            {
                FileName = FFmpegPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            psi.Environment["TEMP"] = tempDir;
            psi.Environment["TMP"] = tempDir;

            foreach (var arg in args)
                psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null)
            {
                WriteDebugLog("ERROR", "FFmpeg", "FFmpeg process failed to start", $"Path: {FFmpegPath}");
                return (false, "FFmpeg process failed to start");
            }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            try { await process.WaitForExitAsync(token); }
            catch (OperationCanceledException) { process.Kill(); throw; }

            string error = await errorTask;

            if (process.ExitCode != 0)
            {
                // FFmpeg writes progress to stderr — keep the last part which has the actual error
                string errSummary = error;
                if (errSummary.Length > 600)
                    errSummary = "…" + errSummary[^600..];
                WriteDebugLog("ERROR", "FFmpeg", $"FFmpeg exited with code {process.ExitCode}", errSummary);
                return (false, errSummary.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                if (error.Contains("Error", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || error.Contains("Invalid", StringComparison.OrdinalIgnoreCase))
                {
                    WriteDebugLog("WARN", "FFmpeg", "FFmpeg completed with warnings/errors in stderr", error[..Math.Min(error.Length, 500)]);
                }
            }

            return (true, string.Empty);
        }

        /// <summary>
        /// 运行 jhead（jhead 需要 jpegtran 在同目录或 PATH 中，设置 WorkingDirectory 为 Tools 目录）
        /// </summary>
        private static async Task RunJheadAsync(params string[] args)
        {
            string toolDir = Path.GetDirectoryName(JheadPath) ?? AppContext.BaseDirectory;

            var psi = new ProcessStartInfo
            {
                FileName = JheadPath,
                WorkingDirectory = toolDir, // 确保 jhead 能找到同目录下的 jpegtran.exe
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null)
            {
                WriteDebugLog("ERROR", "Jhead", "jhead process failed to start", $"Path: {JheadPath}");
                throw new Exception("Cannot start jhead.exe");
            }

            // 并行读取 stdout/stderr 避免缓冲区死锁
            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string output = await outputTask;
            string error = await errorTask;

            if (process.ExitCode != 0)
            {
                WriteDebugLog("ERROR", "Jhead", $"jhead failed (ExitCode: {process.ExitCode})", $"Args: jhead {string.Join(" ", args)}\n\nOutput:\n{output}\n\nError:\n{error}");
                throw new Exception($"jhead: {error.TrimEnd()}".TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(error))
            {
                WriteDebugLog("WARN", "Jhead", "jhead warning", $"Args: jhead {string.Join(" ", args)}\n\nOutput:\n{error}");
            }
        }

        /// <summary>
        /// 运行 exiftool
        /// </summary>
        private static async Task RunExifToolAsync(params string[] args)
        {
            string tempDir = Path.GetTempPath();
            string toolDir = Path.GetDirectoryName(ExifToolPath) ?? AppContext.BaseDirectory;

            var psi = new ProcessStartInfo
            {
                FileName = ExifToolPath,
                WorkingDirectory = toolDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            psi.Environment["TEMP"] = tempDir;
            psi.Environment["TMP"] = tempDir;
            psi.Environment["PAR_GLOBAL_TMPDIR"] = tempDir;

            psi.ArgumentList.Add("-charset");
            psi.ArgumentList.Add("filename=utf8");
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null)
            {
                WriteDebugLog("ERROR", "ExifTool", ResourceService.GetString("Log_ExifToolStartFailed"), $"Path: {ExifToolPath}");
                throw new Exception(ResourceService.GetString("Error_CannotStartExifTool"));
            }

            // 并行读取 stderr 避免缓冲区死锁（exiftool 主要输出在 stderr）
            var errorTask = process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();
            string error = await errorTask;

            if (error.Contains("Error:", StringComparison.OrdinalIgnoreCase))
            {
                WriteDebugLog("ERROR", "ExifTool", ResourceService.GetString("Log_ExifToolFatalError"), $"Args:\nexiftool {string.Join(" ", args)}\n\nOutput:\n{error}");
                throw new Exception($"exiftool: {error.TrimEnd()}".TrimEnd());
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                WriteDebugLog("WARN", "ExifTool", ResourceService.GetString("Log_ExifToolWarning"), $"Args:\nexiftool {string.Join(" ", args)}\n\nOutput:\n{error}");
            }
        }
    }
}
