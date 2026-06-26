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
// 实况照片修复服务。
// 功能包括：
// 1. 诊断（AnalyzeFileAsync）：通过 exiftool 扫描文件的方向标记、缩略图、ContentIdentifier 等
// 2. 修复（RepairAsync）：修正 EXIF Orientation / 旋转角度（JPEG 用 jhead，HEIC 用 exiftool）、剥离多余缩略图
// 3. 视频修复（RepairVideoAsync）：FFmpeg 重编码 + auto-rotate，支持硬件加速与软件回退
// 4. 标记（TryWriteLivePhotoBoxMarkerAsync）：在 XMP dc:subject 写入操作记录
// 依赖的外部工具：exiftool, jhead, jpegtran, ffmpeg。
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

        // 统一的内部日志记录器
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

        // 1. 扫描与诊断文件：仅用 exiftool 读取方向与缩略图状态。
        // 可传入常驻 exiftool 进程避免重复启动开销。
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
                    // ContentIdentifier 用于实况照片匹配
                    // DateTimeOriginal + CreateDate 用于元数据匹配
                    output = await persistentExifTool.SendCommandAsync(token, "-j", "-Rotation", "-Orientation", "-ThumbnailImage", "-ContentIdentifier", "-DateTimeOriginal", "-CreateDate", "-OffsetTimeOriginal", "-OffsetTimeDigitized", filePath);
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
                    psi.ArgumentList.Add("-ContentIdentifier");
                    psi.ArgumentList.Add("-DateTimeOriginal");
                    psi.ArgumentList.Add("-CreateDate");
                    psi.ArgumentList.Add("-OffsetTimeOriginal");
                    psi.ArgumentList.Add("-OffsetTimeDigitized");
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
            catch (InvalidOperationException ex)
            {
                // exiftool 进程崩溃：PersistentExifTool 已自动重启，
                // 无需停止扫描——返回 Error 让当前文件失败，下一个文件继续。
                WriteDebugLog("ERROR", "Analyze",
                    $"ExifTool crashed on {Path.GetFileName(filePath)}, scan will continue",
                    ex.Message);
                return new RepairAnalysisResult
                {
                    IssueType = RepairIssueType.Error,
                    IssueDescription = $"ExifTool 进程异常退出，已自动重启\n{ex.Message}"
                };
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

        // 从方向标签字符串中提取旋转角度（0/90/180/270）
        private static int ParseAngleFromTag(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return 0;
            if (tag.Contains("270")) return 270;
            if (tag.Contains("180")) return 180;
            if (tag.Contains("90")) return 90;
            return 0;
        }

        // 判断方向标签是否包含镜像/翻转标记（mirror / flip）
        private static bool TagHasMirror(string? tag)
        {
            if (string.IsNullOrWhiteSpace(tag)) return false;
            return tag.Contains("Mirror", StringComparison.OrdinalIgnoreCase)
                || tag.Contains("Flip", StringComparison.OrdinalIgnoreCase);
        }

        // 根据 QuickTime:Rotation 值推导正确的 EXIF Orientation 值
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

        // 安全地从 JsonElement 读取值（兼容 string 和 number 类型）。
        // exiftool 对 MOV 视频的 Rotation 输出为数字（如 90），对 JPEG/HEIC 为字符串（如 "Rotate 90 CW"）。
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

        // 解析 exiftool 的 JSON 输出，生成 RepairAnalysisResult
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
            string contentIdentifier = GetJsonValueAsString(root, "ContentIdentifier");
            string dateTimeOriginal = GetJsonValueAsString(root, "DateTimeOriginal");
            string createDate = GetJsonValueAsString(root, "CreateDate");
            string offsetTimeOriginal = GetJsonValueAsString(root, "OffsetTimeOriginal");
            if (string.IsNullOrWhiteSpace(offsetTimeOriginal))
                offsetTimeOriginal = GetJsonValueAsString(root, "OffsetTimeDigitized");
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
                    HasThumbnail = false,
                    ContentIdentifier = contentIdentifier,
                    DateTimeOriginal = dateTimeOriginal,
                    CreateDate = createDate,
                    OffsetTimeOriginal = offsetTimeOriginal
                };
            }

            // HEIC: orientation 修复归类为 NeedsRebuild（元数据重建）
            // JPEG: 旋转修复归类为 NeedsRebuild
            bool needsRebuild = isHeic ? needsOrientationFix : tags.Any(t => t.Contains("°"));
            RepairIssueType type = needsRebuild ? RepairIssueType.NeedsRebuild : RepairIssueType.NeedsStrip;

            string finalDescription = string.Join("\n", tags);

            return new RepairAnalysisResult
            {
                IssueType = type,
                IssueDescription = finalDescription,
                RotationAngle = 0,
                HasThumbnail = hasThumb,
                HeicOriginalRotation = isHeic ? rotation : string.Empty,
                ContentIdentifier = contentIdentifier,
                DateTimeOriginal = dateTimeOriginal,
                CreateDate = createDate,
                OffsetTimeOriginal = offsetTimeOriginal
            };
        }

        // Analyze video file (MOV/MP4) with exiftool. Reads Rotation, AvgBitrate, CompressorID.
        // No ffprobe needed — exiftool provides all necessary metadata.
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

                // Read Rotation, dimensions, codec ID, average bitrate, ContentIdentifier, capture dates, and track-level MatrixStructure
                // — all in one exiftool call. MatrixStructure 通过常驻 exiftool 直接读取，无需 -v2 额外进程。
                string[] exifArgs = { "-j", "-Rotation", "-ImageWidth", "-ImageHeight", "-AvgBitrate", "-CompressorID", "-MediaDuration", "-ContentIdentifier", "-DateTimeOriginal", "-CreateDate", "-MatrixStructure", filePath };

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
                double duration = ParseMediaDuration(GetJsonValueAsString(root, "MediaDuration"));
                string contentId = GetJsonValueAsString(root, "ContentIdentifier");
                string dateTimeOriginal = GetJsonValueAsString(root, "DateTimeOriginal");
                string createDate = GetJsonValueAsString(root, "CreateDate");
                // MatrixStructure 标签：直接从常驻 exiftool 的 JSON 输出中读取轨道级变换矩阵，
                // 无需启动 -v2 额外进程。仅在标签不可用时回退到 GetVideoTrackMatrixAsync。
                string matrixStructure = GetJsonValueAsString(root, "MatrixStructure");

                if (angle == 0)
                {
                    // Rotation 标签为 0 但视频仍可能有轨道级翻转矩阵。
                    // 实测：前摄左旋转 (iOS 26.5) → Rotation=0, 矩阵 [1 0; 0 -1] = flip_vertical。
                    // 注意：这里只打标记 (NeedsRebuild + VideoTrackTransform)，
                    // BuildVideoTransformFilter 决定不对 flip 类型应用滤镜，
                    // 因为 Rotation=0 意味着像素方向是正确的，矩阵只是播放器合成元数据。
                    // 详见 BuildVideoTransformFilter 头部的长注释。

                    string trackMatrix;
                    if (!string.IsNullOrEmpty(matrixStructure))
                    {
                        // 快速路径：MatrixStructure 标签在常驻 exiftool 的同一命令中已获取，
                        // 无需启动独立 exiftool -v2 进程（省去每次 ~200-400ms 的 Perl 启动开销）
                        trackMatrix = matrixStructure;
                    }
                    else
                    {
                        // 回退路径：MatrixStructure 标签不可用（旧版 exiftool 或特殊情况），
                        // 仍通过 -v2 独立进程获取
                        trackMatrix = await GetVideoTrackMatrixAsync(filePath, persistentExifTool, token);
                    }

                    var (transform, matrixAngle) = ParseQuickTimeMatrix(trackMatrix);

                    if (!string.IsNullOrEmpty(transform))
                    {
                        string transformTag = transform switch
                        {
                            "flip_vertical" => ResourceService.GetString("Tag_FlipVertical"),
                            "flip_horizontal" => ResourceService.GetString("Tag_FlipHorizontal"),
                            _ => transform
                        };
                        WriteDebugLog("INFO", "AnalyzeVideo", $"{Path.GetFileName(filePath)}: Rotation=0 but track matrix detected — {trackMatrix} → {transform}");
                        return new RepairAnalysisResult
                        {
                            IssueType = RepairIssueType.NeedsRebuild,
                            IssueDescription = $"[{transformTag}]",
                            IsVideo = true,
                            VideoRotationAngle = matrixAngle,
                            VideoTrackTransform = transform,
                            VideoCodec = compressorId,
                            VideoBitrateBps = bitrateBps,
                            VideoDurationSeconds = duration,
                            ContentIdentifier = contentId,
                            DateTimeOriginal = dateTimeOriginal,
                            CreateDate = createDate
                        };
                    }

                    return new RepairAnalysisResult
                    {
                        IssueType = RepairIssueType.Perfect,
                        IssueDescription = $"[{ResourceService.GetString("Status_Perfect")}]",
                        IsVideo = true,
                        VideoRotationAngle = 0,
                        VideoCodec = compressorId,
                        VideoBitrateBps = bitrateBps,
                        VideoDurationSeconds = duration,
                        ContentIdentifier = contentId,
                        DateTimeOriginal = dateTimeOriginal,
                        CreateDate = createDate
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
                    VideoBitrateBps = bitrateBps,
                    VideoDurationSeconds = duration,
                    ContentIdentifier = contentId,
                    DateTimeOriginal = dateTimeOriginal,
                    CreateDate = createDate
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

        // 获取视频轨道级别的变换矩阵（TrackHeader MatrixStructure）。
        // 前摄自拍视频用翻转矩阵编码镜像效果，exiftool 的 Rotation 复合标签不检测这种变换。
        // 输出: "1 0 0 0 -1 0 0 1440 16384" (9个空格分隔的数值，最后一个16384=1.0 fixed-point)
        private static async Task<string> GetVideoTrackMatrixAsync(
            string filePath, PersistentExifTool? persistentExifTool, CancellationToken token)
        {
            try
            {
                // 使用新进程获取 verbose 输出（持久化 exiftool 不支持 -v2）
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
                psi.ArgumentList.Add("-v2");
                // 只请求 MatrixStructure，减少处理开销
                psi.ArgumentList.Add("-MatrixStructure");
                psi.ArgumentList.Add(filePath);

                using var process = Process.Start(psi);
                if (process == null) return string.Empty;

                var outputTask = process.StandardOutput.ReadToEndAsync();
                var errorTask = process.StandardError.ReadToEndAsync();
                try { await process.WaitForExitAsync(token).ConfigureAwait(false); }
                catch (OperationCanceledException) { process.Kill(); throw; }
                await Task.WhenAll(outputTask, errorTask);

                // 通过 stdout 解析轨道级矩阵
                string matrix = ParseTrackMatrixFromVerbose(outputTask.Result);
                WriteDebugLog("INFO", "GetTrackMatrix", $"{Path.GetFileName(filePath)}: {matrix}");
                return matrix;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                WriteDebugLog("WARN", "GetTrackMatrix", $"Failed for {Path.GetFileName(filePath)}: {ex.Message}");
                return string.Empty;
            }
        }

        // 从 exiftool -v2 的 stdout 中提取第一个轨道级 MatrixStructure。
        // 输入格式：
        //   ...
        //   | | TrackID = 1
        //   | | | MatrixStructure = 1 0 0 0 -1 0 0 1440 16384
        //   ...
        private static string ParseTrackMatrixFromVerbose(string verboseOutput)
        {
            if (string.IsNullOrWhiteSpace(verboseOutput)) return string.Empty;

            var lines = verboseOutput.Split('\n', StringSplitOptions.RemoveEmptyEntries);
            bool foundTrack1 = false;
            foreach (var line in lines)
            {
                if (line.Contains("TrackID = 1"))
                {
                    foundTrack1 = true;
                    continue;
                }
                if (foundTrack1 && line.Contains("MatrixStructure ="))
                {
                    int idx = line.IndexOf("= ", StringComparison.Ordinal);
                    if (idx >= 0)
                        return line.Substring(idx + 2).Trim();
                    return string.Empty;
                }
            }
            return string.Empty;
        }

        // ═══════════════════════════════════════════════════════════════════
        // BuildVideoTransformFilter — 根据诊断结果构建 ffmpeg 视频变换滤镜链
        // ═══════════════════════════════════════════════════════════════════
        //
        // 背景：iPhone 实况照片的视频部分（.MOV）在 QuickTime 容器里有两个独立的
        // 方向信息源，二者职责不同：
        //
        //   A. Composite Rotation 标签 — exiftool 从多个原始标签综合计算出的旋转角
        //      （0/90/180/270）。这是"权威答案"，ffmpeg 默认 autorotate 自动应用。
        //
        //   B. 轨道矩阵 (Track Matrix) — QuickTime tkhd 里的 3×3 变换矩阵，
        //      描述轨道像素 → 显示画面的映射。包含了旋转 + 翻转（前摄自拍镜像）。
        //
        // ═══════════════════════════════════════════════════════════════════
        // 实测数据（iOS 26.5 前摄实况照片 4 方向，2026-06-26）：
        // ═══════════════════════════════════════════════════════════════════
        //
        //   方向       Rotation    轨道矩阵          实际含义
        //   ────────  ──────────   ────────────────  ─────────────────
        //   正拍       90°         [0  1; 1  0]      90°旋转矩阵
        //   倒着拍     270°        [0 -1; -1 0]      270°旋转矩阵
        //   右旋转     180°        [-1 0; 0  1]      水平翻转矩阵
        //   左旋转      0°         [1  0; 0 -1]      垂直翻转矩阵 ← 问题来源
        //
        //   核心发现：
        //   - 前 3 个 Rotation ≠ 0 → ffmpeg autorotate 一步到位 ✅
        //   - 左旋转 Rotation = 0 但轨道矩阵是 [1 0; 0 -1] (垂直翻转)
        //     → 旧代码只看 Rotation 标签 → 误判为"完美无缺" ❌
        //     → 新代码追加轨道矩阵检测 → 发现 flip_vertical ✅
        //
        //   关键结论：
        //   Rotation 标签已经是综合了翻转和旋转的"最终答案"。
        //   Rotation = 0 意味着"这个视频方向没问题，不需要任何修正"。
        //   轨道矩阵里的翻转此时只是播放器合成元数据（QuickTime composition
        //   hint），不是像素缺陷。
        //   如果强行给 Rotation=0 的视频加 vflip/hflip 滤镜，反而会把正确的
        //   像素搞坏。因此 flip 类型的矩阵不产生任何滤镜。
        //
        // ═══════════════════════════════════════════════════════════════════
        private static string BuildVideoTransformFilter(RepairAnalysisResult analysis)
        {
            var filters = new System.Collections.Generic.List<string>();

            // 轨道矩阵检测到的变换。注意：
            // - flip_vertical / flip_horizontal → 不应用滤镜（原因见上方长注释）
            // - rotate_* 只在 Rotation=0 时才会走进来（正常情况下 Rotation ≠ 0
            //   时不会检查轨道矩阵），属于异常保护
            switch (analysis.VideoTrackTransform)
            {
                case "flip_vertical":
                case "flip_horizontal":
                    // 矩阵是播放器合成指令，像素本身方向正确 → 不做任何修正
                    break;
                case "rotate_90":       filters.Add("transpose=2"); break;   // 90° CCW
                case "rotate_180":      filters.Add("hflip"); filters.Add("vflip"); break;
                case "rotate_270":      filters.Add("transpose=1"); break;   // 90° CW
            }

            return string.Join(",", filters);
        }

        // ═══════════════════════════════════════════════════════════════════
        // ParseQuickTimeMatrix — 解析轨道 tkhd 的 3×3 变换矩阵
        // ═══════════════════════════════════════════════════════════════════
        //
        // 矩阵格式：exiftool -v2 输出的 9 个空格分隔整数
        //   "a b u  c d v  x y w"
        //
        // 显示变换公式（QuickTime 规范）：
        //   x_display = (a * x_track + c * y_track + x) / w
        //   y_display = (b * x_track + d * y_track + y) / w
        //
        // 值域说明：
        //   a/b/c/d 可能是 16.16 定点数（16384 = 1.0, 65536 = 1.0）
        //   也可能是已归一化的整数（1 = 1.0）
        //   两种都兼容
        //
        // ═══════════════════════════════════════════════════════════════════
        // 实测覆盖的矩阵模式（前摄实况照片 4 方向 + 已知变体）：
        // ═══════════════════════════════════════════════════════════════════
        //
        //   矩阵              a   b   d   e   返回 transform    对应 Rotation
        //   ────────────────  ──  ──  ──  ──  ──────────────  ────────────
        //   [1  0; 0  1]     1   0   0   1   (空 = 无变换)    — (identity)
        //   [1  0; 0 -1]     1   0   0  -1   flip_vertical    0  (左旋转)
        //   [-1 0; 0  1]    -1   0   0   1   flip_horizontal  180 (右旋转)
        //   [0  1; -1 0]     0   1  -1   0   rotate_90        90 (正拍)
        //   [0  1; 1  0]     0   1   1   0   rotate_90        90 (variant)
        //   [-1 0; 0 -1]    -1   0   0  -1   rotate_180       180
        //   [0 -1; 1  0]     0  -1   1   0   rotate_270       270 (倒着拍)
        //   [0 -1; -1 0]     0  -1  -1   0   rotate_270       270 (variant)
        //
        // ═══════════════════════════════════════════════════════════════════
        private static (string transform, int angle) ParseQuickTimeMatrix(string matrixStr)
        {
            if (string.IsNullOrWhiteSpace(matrixStr)) return (string.Empty, 0);

            var parts = matrixStr.Split(' ', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 9) return (string.Empty, 0);

            if (!int.TryParse(parts[0], out int a)) return (string.Empty, 0);
            if (!int.TryParse(parts[1], out int b)) return (string.Empty, 0);
            if (!int.TryParse(parts[3], out int d)) return (string.Empty, 0);
            if (!int.TryParse(parts[4], out int e)) return (string.Empty, 0);

            // identity: a=±1, b=0, d=0, e=±1 (兼容 16.16 定点数: 16384/65536 = 1.0)
            bool isIdentity = (a == 1 || a == 16384 || a == 65536) && b == 0
                           && d == 0 && (e == 1 || e == 16384 || e == 65536);
            if (isIdentity) return (string.Empty, 0);

            // flip_vertical  [1 0; 0 -1] — 前摄左旋转 (Rotation=0)
            if ((a == 1 || a == 16384) && b == 0 && d == 0 && (e == -1 || e == -16384))
                return ("flip_vertical", 0);

            // flip_horizontal [-1 0; 0 1] — 前摄右旋转 (Rotation=180)
            if ((a == -1 || a == -16384) && b == 0 && d == 0 && (e == 1 || e == 16384))
                return ("flip_horizontal", 0);

            // rotate_90  [0 1; -1 0] — 正拍 (Rotation=90)
            if (a == 0 && b == 1 && d == -1 && e == 0)
                return ("rotate_90", 90);
            // rotate_90 variant [0 1; 1 0]
            if (a == 0 && b == 1 && d == 1 && e == 0)
                return ("rotate_90", 90);

            // rotate_270  [0 -1; 1 0] — 倒着拍 (Rotation=270)
            if (a == 0 && b == -1 && d == 1 && e == 0)
                return ("rotate_270", 270);
            // rotate_270 variant [0 -1; -1 0] — 倒着拍变体
            if (a == 0 && b == -1 && d == -1 && e == 0)
                return ("rotate_270", 270);

            // rotate_180  [-1 0; 0 -1]
            if ((a == -1 || a == -16384) && b == 0 && d == 0 && (e == -1 || e == -16384))
                return ("rotate_180", 180);

            return (string.Empty, 0);
        }

        // Parse exiftool AvgBitrate string (e.g. "12.2 Mbps") to bps (12200000).
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

        // Parse exiftool MediaDuration PrintConv string to seconds.
        // Handles three formats:
        // "2.35 s"   — sub-minute (seconds with unit)
        // "0:01:05"  — ≥1 minute (HH:MM:SS)
        // "2.35"     — raw numeric (when -n flag is used)
        private static double ParseMediaDuration(string? mediaDuration)
        {
            if (string.IsNullOrWhiteSpace(mediaDuration)) return 0;

            // Timecode format: "HH:MM:SS" or "MM:SS" (used for ≥60s videos)
            var tcMatch = System.Text.RegularExpressions.Regex.Match(mediaDuration,
                @"^(?:(\d+):)?(\d+):(\d+(?:\.\d+)?)$");
            if (tcMatch.Success)
            {
                int hours = tcMatch.Groups[1].Success ? int.Parse(tcMatch.Groups[1].Value) : 0;
                int minutes = int.Parse(tcMatch.Groups[2].Value);
                double secs = double.Parse(tcMatch.Groups[3].Value,
                    System.Globalization.CultureInfo.InvariantCulture);
                return hours * 3600 + minutes * 60 + secs;
            }

            // "2.35 s" format (PrintConv for sub-minute durations)
            var sMatch = System.Text.RegularExpressions.Regex.Match(mediaDuration, @"^([\d.]+)\s*s");
            if (sMatch.Success && double.TryParse(sMatch.Groups[1].Value,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double seconds))
                return seconds;

            // Raw numeric format (e.g. 2.35)
            if (double.TryParse(mediaDuration,
                System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out double raw))
                return raw;

            return 0;
        }

        // 2. 修复文件：jhead 自动旋转 + exiftool 剥离缩略图
        public static async Task<(bool Success, string Message)> RepairAsync(string sourcePath, string targetPath, RepairAnalysisResult analysis, CancellationToken token)
        {
            // Video repair uses FFmpeg re-encode with autorotate
            if (IsVideoFile(sourcePath))
                return await RepairVideoAsync(sourcePath, targetPath, analysis, token);

            bool isHeic = IsHeicFile(sourcePath);
            bool needsRotation = analysis.IssueType == RepairIssueType.NeedsRebuild;
            bool hasThumbnail = analysis.HasThumbnail;

            // 临时文件放在系统 %TEMP% 下独立子文件夹（GUID 命名），避免中文路径编码问题，
            // 且每个修复操作互不干扰——并发修复时不会因共享 Temp 目录导致文件被意外删除。
            string ext = Path.GetExtension(sourcePath);
            if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
            string imgRepairTempDir = Path.Combine(Path.GetTempPath(), $"lpb_repair_{Guid.NewGuid():N}");
            Directory.CreateDirectory(imgRepairTempDir);
            string tempWorkFile = Path.Combine(imgRepairTempDir, $"repair_{Guid.NewGuid():N}{ext}");

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
                        // 用 -o 输出到新文件，避免 -overwrite_original 的内部备份机制出错
                        string cleanedHeicFile = Path.Combine(imgRepairTempDir, $"cleaned_{Guid.NewGuid():N}{ext}");
                        exifArgs.Add("-o");
                        exifArgs.Add(cleanedHeicFile);
                        exifArgs.Add(tempWorkFile);
                        await RunExifToolAsync(exifArgs.ToArray());
                        // 替换原临时文件
                        File.Delete(tempWorkFile);
                        tempWorkFile = cleanedHeicFile;
                    }
                }
                else
                {
                    // ── JPEG 修复：jhead 旋转 + exiftool 剥离缩略图 ──
                    if (needsRotation)
                    {
                        await RunJheadWithRetryAsync("-autorot", tempWorkFile, token);
                        token.ThrowIfCancellationRequested();
                    }

                    if (hasThumbnail)
                    {
                        // 用 -o 输出到新文件，避免 -overwrite_original 在某些文件上
                        // 内部创建备份时出错（报 "File not found" 假象）。
                        string cleanedFile = Path.Combine(imgRepairTempDir, $"cleaned_{Guid.NewGuid():N}{ext}");
                        await RunExifToolAsync("-ThumbnailImage=", "-o", cleanedFile, tempWorkFile);
                        // 替换原临时文件
                        File.Delete(tempWorkFile);
                        tempWorkFile = cleanedFile;
                    }
                }

                // 移动到目标路径
                if (File.Exists(targetPath)) File.Delete(targetPath);
                string? targetDir = Path.GetDirectoryName(targetPath);
                if (!string.IsNullOrEmpty(targetDir) && !Directory.Exists(targetDir))
                    Directory.CreateDirectory(targetDir);
                File.Move(tempWorkFile, targetPath);

                // 打上 LivePhotoBox 修复标记（记录实际修复了哪些内容）
                var fixes = new System.Collections.Generic.List<string>();
                if (needsRotation) fixes.Add("Rotation");
                if (hasThumbnail) fixes.Add("Thumbnail");
                await TryWriteLivePhotoBoxMarkerAsync(targetPath, "Repair",
                    fixes.Count > 0 ? $"Fix={string.Join("+", fixes)}" : "", token);

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
                // 清理临时文件及 Temp 目录
                try { if (File.Exists(tempWorkFile)) File.Delete(tempWorkFile); } catch { }
                try { if (Directory.Exists(imgRepairTempDir)) Directory.Delete(imgRepairTempDir, recursive: true); } catch { }
            }
        }

        // 3. 视频旋转修复：FFmpeg 编码 + autorotate，将旋转矩阵烘焙到像素中。
        // 支持硬件加速（NVENC/QSV/AMF/VAAPI），失败自动回退 CPU 编码。
        // 设置从"视频转码"面板读取（与拆分页面共享）。
        // 安全机制：
        // 1. 始终先写入临时文件，成功后再移动到目标路径。
        // 防止硬件编码中途失败损坏源文件（原地修复时 sourcePath==targetPath）。
        // 2. 硬件失败自动回退到软件编码，源文件始终保持完整。
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

            // 临时文件放在系统 %TEMP% 下独立子文件夹（GUID 命名），避免中文路径编码问题，
            // 且每个修复操作互不干扰——并发修复时不会因共享 Temp 目录导致文件被意外删除。
            string videoTempDir = Path.Combine(Path.GetTempPath(), $"lpb_repair_{Guid.NewGuid():N}");
            Directory.CreateDirectory(videoTempDir);
            string tempOutput = Path.Combine(videoTempDir, $"repair_{Guid.NewGuid():N}{Path.GetExtension(targetPath)}");

            try
            {
                // Try hardware encoder first, fall back to software if it fails
                string? savedEncoder = EncoderHelper.GetSavedEncoder(codecKey);
                if (string.IsNullOrEmpty(savedEncoder))
                {
                    // Try derive from the other codec's saved encoder
                    string otherCodec = codecKey == "hevc" ? "h264" : "hevc";
                    string? other = EncoderHelper.GetSavedEncoder(otherCodec);
                    if (!string.IsNullOrEmpty(other))
                    {
                        string? derived = EncoderHelper.DeriveCrossCodecEncoder(other);
                        if (!string.IsNullOrEmpty(derived) && EncoderHelper.IsEncoderAvailable(derived))
                        {
                            savedEncoder = derived;
                            AppSettingsService.SetValue($"SplitEncoder_{codecKey}", derived);
                            WriteDebugLog("INFO", "RepairVideo", $"Derived encoder: {other} → {derived}");
                        }
                    }
                }

                string videoEncoder;
                string videoParams;
                if (!string.IsNullOrEmpty(savedEncoder) && EncoderHelper.IsEncoderAvailable(savedEncoder))
                {
                    videoEncoder = savedEncoder;
                    videoParams = EncoderHelper.GetHardwareEncoderParams(savedEncoder, (13, 14));
                }
                else
                {
                    var sw = EncoderHelper.GetSoftwareEncoder(codecKey, codecKey == "hevc" ? 14 : 13);
                    videoEncoder = sw.encoder;
                    videoParams = sw.encoderParams;
                }

                // 构建视频变换滤镜链。
                // QuickTime 轨道矩阵描述的是"编码像素 → 显示画面"的映射。
                // 前摄自拍中: flip_vertical 代表自拍镜像（像素上下倒置存储, 播放器翻转后显示）。
                // 修复时需要把像素翻转回来, 这样输出文件无需矩阵就能正确显示。
                string extraVideoFilter = BuildVideoTransformFilter(analysis);

                var (ok, errMsg) = await RunRepairFFmpegAsync(sourcePath, tempOutput, videoEncoder, videoParams, isHevc, codecKey, sourceIsMp4, token, extraVideoFilter);

                bool isHardware = EncoderHelper.IsHardwareEncoder(videoEncoder);

                if (!ok && isHardware)
                {
                    WriteDebugLog("WARN", "RepairVideo", $"Hardware encoder {videoEncoder} failed, falling back to software. HW error: {errMsg}");
                    // 清理硬件尝试可能残留的临时文件
                    try { if (File.Exists(tempOutput)) File.Delete(tempOutput); } catch { }
                    var (swEncoder, swParams) = EncoderHelper.GetSoftwareEncoder(codecKey, codecKey == "hevc" ? 14 : 13);
                    (ok, errMsg) = await RunRepairFFmpegAsync(sourcePath, tempOutput, swEncoder, swParams, isHevc, codecKey, sourceIsMp4, token, extraVideoFilter);
                }

                if (ok)
                {
                    // 成功：将临时文件移动到目标路径
                    if (File.Exists(targetPath))
                    {
                        File.Delete(targetPath);
                    }
                    File.Move(tempOutput, targetPath);
                    await TryWriteLivePhotoBoxMarkerAsync(
                        targetPath, "Repair", "Fix=Rotation", token);
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
                // 清理临时文件及 Temp 目录
                try { if (File.Exists(tempOutput)) File.Delete(tempOutput); } catch { }
                try { if (Directory.Exists(videoTempDir)) Directory.Delete(videoTempDir, recursive: true); } catch { }
            }
        }

        // Build FFmpeg arguments and run for video repair.
        // Both hardware and software paths now align with the proven transcode path
        // (VideoTranscodeService.BuildFFmpegArguments). Key alignment points:
        // -apply_cropping 0: HEVC decoder option, safe for both SW and HW decoder.
        // HW decoders (NVDEC) ignore it; SW decoder preserves full encoded frame.
        // -map 0:v:0: lowercase v, consistent with transcode path.
        // -threads: always specified (HW=1, SW=user configured).
        // -c:a aac: always re-encode audio (HW muxer can't copy PCM; safer than copy).
        // No forced -f: let FFmpeg auto-detect output format from extension.
        private static async Task<(bool success, string errorMessage)> RunRepairFFmpegAsync(
            string sourcePath, string targetPath,
            string videoEncoder, string videoParams,
            bool isHevc, string codecKey, bool sourceIsMp4,
            CancellationToken token,
            string extraVideoFilter = "")
        {
            bool isHardware = videoEncoder.Contains("nvenc") || videoEncoder.Contains("qsv")
                           || videoEncoder.Contains("amf") || videoEncoder.Contains("vaapi");

            // 构建视频滤镜：先应用翻转/旋转变换（如果有），再设置 SAR
            string videoFilter = string.IsNullOrEmpty(extraVideoFilter)
                ? "setsar=1"
                : $"{extraVideoFilter},setsar=1";

            var args = new List<string>
            {
                "-apply_cropping", "0",
                "-y",
                "-i", sourcePath,
                "-map", "0:v:0",
                "-map", "0:a:0?",
                "-map_metadata", "0",
                "-threads", EncoderHelper.GetThreadCount(videoEncoder, maxSoftwareThreads: 6).ToString(),
                "-vf", videoFilter,
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

        // GetRepairEncoder → inline EncoderHelper calls in RepairVideoAsync
        // GetSoftwareEncoder → EncoderHelper.GetSoftwareEncoder
        // GetHardwareRepairParams → EncoderHelper.GetHardwareEncoderParams
        // GetRepairThreadCount → EncoderHelper.GetThreadCount
        // IsFFmpegEncoderAvailable → EncoderHelper.IsEncoderAvailable

        // Run FFmpeg process with given arguments. Returns (success, errorMessage).
        // On failure, errorMessage contains the last portion of FFmpeg stderr for diagnosis.
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

            // 将 ffmpeg 进程优先级设为低于标准，避免大量并发编码时
            // ffmpeg 与 UI 线程争抢 CPU 时间片导致系统操作卡顿
            try { process.PriorityClass = ProcessPriorityClass.BelowNormal; } catch { }

            var outputTask = process.StandardOutput.ReadToEndAsync();
            var errorTask = process.StandardError.ReadToEndAsync();

            try { await process.WaitForExitAsync(token); }
            catch (OperationCanceledException) { process.Kill(); throw; }

            string stdout = await outputTask;
            string stderr = await errorTask;

            if (process.ExitCode != 0)
            {
                // 组装完整错误信息：exit code + stderr + stdout（某些错误走 stdout）
                var parts = new System.Collections.Generic.List<string>();
                if (!string.IsNullOrWhiteSpace(stderr))
                    parts.Add(stderr.Trim());
                if (!string.IsNullOrWhiteSpace(stdout))
                    parts.Add($"[stdout] {stdout.Trim()}");

                string errSummary = parts.Count > 0
                    ? string.Join("\n", parts)
                    : $"(无错误输出 — FFmpeg 进程退出码 {process.ExitCode}，但 stdout/stderr 均为空)";

                if (errSummary.Length > 600)
                    errSummary = "…" + errSummary[^600..];

                WriteDebugLog("ERROR", "FFmpeg", $"FFmpeg exited with code {process.ExitCode}", errSummary);
                return (false, errSummary.TrimEnd());
            }

            if (!string.IsNullOrWhiteSpace(stderr))
            {
                if (stderr.Contains("Error", StringComparison.OrdinalIgnoreCase)
                    || stderr.Contains("failed", StringComparison.OrdinalIgnoreCase)
                    || stderr.Contains("Invalid", StringComparison.OrdinalIgnoreCase))
                {
                    WriteDebugLog("WARN", "FFmpeg", "FFmpeg completed with warnings/errors in stderr", stderr[..Math.Min(stderr.Length, 500)]);
                }
            }

            return (true, string.Empty);
        }

        // 带重试的 jhead 调用：处理 Windows Defender 等安全软件在文件创建后短暂锁定的偶发问题。
        // "Could not open file for write" 时最多重试 3 次，每次间隔 200ms。
        private static async Task RunJheadWithRetryAsync(string arg1, string arg2, CancellationToken token, int maxRetries = 3)
        {
            for (int attempt = 0; attempt < maxRetries; attempt++)
            {
                try
                {
                    await RunJheadAsync(arg1, arg2);
                    return; // 成功
                }
                catch (Exception ex) when (ex.Message.Contains("Could not open file", StringComparison.OrdinalIgnoreCase))
                {
                    if (attempt == maxRetries - 1) throw; // 最后一次仍失败，抛出
                    await Task.Delay(200, token);
                }
            }
        }

        // 运行 jhead（jhead 需要 jpegtran 在同目录或 PATH 中，设置 WorkingDirectory 为 Tools 目录）
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

        // 运行 exiftool（一次性模式）— 无取消令牌的便捷重载。
        public static Task RunExifToolAsync(params string[] args)
            => RunExifToolAsync(CancellationToken.None, args);

        // 运行 exiftool（一次性模式）。
        // 通过 stdin 管道传递参数（UTF-8 编码），而非命令行参数，
        // 彻底避开 Windows GetCommandLineA 的 ANSI 编码问题，
        // 任何语言（中日韩阿…）的文件名都能正确处理。
        public static async Task RunExifToolAsync(CancellationToken token, params string[] args)
        {
            string tempDir = Path.GetTempPath();
            string toolDir = Path.GetDirectoryName(ExifToolPath) ?? AppContext.BaseDirectory;

            var psi = new ProcessStartInfo
            {
                FileName = ExifToolPath,
                WorkingDirectory = toolDir,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                RedirectStandardInput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8,
                StandardInputEncoding = new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false)
            };

            psi.Environment["TEMP"] = tempDir;
            psi.Environment["TMP"] = tempDir;
            psi.Environment["PAR_GLOBAL_TMPDIR"] = tempDir;

            // 走 stdin 管道，-@ - 表示从标准输入读取参数
            // -charset filename=utf8 在此路径下是正确的：.NET StreamWriter
            // 以 UTF-8 写入 stdin，exiftool 也以 UTF-8 解析，编码一致。
            psi.ArgumentList.Add("-charset");
            psi.ArgumentList.Add("filename=utf8");
            psi.ArgumentList.Add("-@");
            psi.ArgumentList.Add("-");

            using var process = Process.Start(psi);
            if (process == null)
            {
                WriteDebugLog("ERROR", "ExifTool", ResourceService.GetString("Log_ExifToolStartFailed"), $"Path: {ExifToolPath}");
                throw new Exception(ResourceService.GetString("Error_CannotStartExifTool"));
            }

            // 取消时杀掉进程
            using var ctr = token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
            });

            try
            {
                // 通过 stdin 写入参数（UTF-8），一行一个，最后 -execute 触发执行
                foreach (var arg in args)
                    await process.StandardInput.WriteLineAsync(arg.AsMemory(), token);
                await process.StandardInput.WriteLineAsync("-execute".AsMemory(), token);
                process.StandardInput.Close();

                // 读取 stdout 直到 {ready}（-@ - 模式下 exiftool 输出此标记）
                var sb = new System.Text.StringBuilder();
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    string? line = await process.StandardOutput.ReadLineAsync(token);
                    if (line == null) break;
                    if (line.TrimEnd() == "{ready}") break;
                    if (sb.Length > 0) sb.Append('\n');
                    sb.Append(line);
                }

                token.ThrowIfCancellationRequested();

                // 同时消费 stderr
                string error = await process.StandardError.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);

                if (error.Contains("Error:", StringComparison.OrdinalIgnoreCase))
                {
                    WriteDebugLog("ERROR", "ExifTool", ResourceService.GetString("Log_ExifToolFatalError"),
                        $"Args (via stdin):\n{string.Join("\n", args)}\n\nStderr:\n{error}");
                    throw new Exception($"exiftool: {error.TrimEnd()}".TrimEnd());
                }
                else if (!string.IsNullOrWhiteSpace(error))
                {
                    WriteDebugLog("WARN", "ExifTool", ResourceService.GetString("Log_ExifToolWarning"),
                        $"Args (via stdin):\n{string.Join("\n", args)}\n\nStderr:\n{error}");
                }
            }
            catch (Exception)
            {
                try { process.Kill(); } catch { }
                throw;
            }
        }

        // Append a LivePhotoBox tracking entry to the XMP <c>dc:subject</c> array.
        // Two detail levels, controlled by the "详细操作记录" toggle:
        // - Toggle OFF (default): writes a lightweight marker —
        // <c>LivePhotoBox:{action}@@v{version}@</c>
        // (action + version only, no timestamp or fix details).
        // - Toggle ON: writes the full chronological entry —
        // <c>LivePhotoBox:{action}@{timestamp}@v{version}@{details}</c>
        // The lightweight marker is always written so every Split/Repair file
        // can be identified as processed by LivePhotoBox, matching Merge's
        // always-on XMP namespace attributes.
        // Best-effort — failures are silently swallowed so the caller never breaks.
        // filePath: JPEG, HEIC, MP4, or MOV path.
        // action: Operation name: "Split" or "Repair".
        // <param name="details">Action-specific key=value pairs, e.g. "Fix=Rotation+Thumbnail".
        // Only written when detailed history is enabled.</param>
        public static async Task TryWriteLivePhotoBoxMarkerAsync(
            string filePath, string action, string details, CancellationToken token)
        {
            if (string.IsNullOrEmpty(ExternalToolLocator.FindExifTool()))
                return;

            bool detailed = AppSettingsService.GetValue("IsDetailedHistoryEnabled", false);

            try
            {
                string version = App.AppVersion;

                // 轻量标记：仅 action + version；详细信息：含时间戳 + 修复内容
                string entry = detailed
                    ? $"LivePhotoBox:{action}@{DateTimeOffset.Now:yyyy-MM-ddTHH:mm:sszzz}@v{version}@{details}"
                    : $"LivePhotoBox:{action}@@v{version}@";

                await RunExifToolAsync(token,
                    "-overwrite_original",
                    $"-XMP-dc:Subject+={entry}",
                    filePath);
            }
            catch
            {
                // Best-effort — marker is non-essential metadata.
            }
        }

    }
}
