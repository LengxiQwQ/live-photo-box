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

        private static bool IsHeicFile(string path) =>
            path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);

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

            string rotation = root.TryGetProperty("Rotation", out var rProp) ? rProp.GetString() ?? "" : "";
            string orientation = root.TryGetProperty("Orientation", out var oProp) ? oProp.GetString() ?? "" : "";
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
        /// 2. 修复文件：jhead 自动旋转 + exiftool 剥离缩略图
        /// </summary>
        public static async Task<(bool Success, string Message)> RepairAsync(string sourcePath, string targetPath, RepairAnalysisResult analysis, CancellationToken token)
        {
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
