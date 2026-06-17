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
        private static readonly string ExifToolPath = Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe");
        private static readonly string JpegTranPath = Path.Combine(AppContext.BaseDirectory, "Tools", "jpegtran.exe");

        /// <summary>
        /// 统一的内部日志记录器，将所有第三方工具报错、异常输出到主日志
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
        /// 1. 扫描与诊断文件
        /// </summary>
        public static async Task<RepairAnalysisResult> AnalyzeFileAsync(string filePath)
        {
            if (!File.Exists(ExifToolPath))
            {
                WriteDebugLog("ERROR", "Analyze", ResourceService.GetString("Log_MissingDependency"), $"File not found: {ExifToolPath}");
                return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = ResourceService.GetString("Error_ExifToolMissing") };
            }

            if (!File.Exists(JpegTranPath))
            {
                WriteDebugLog("ERROR", "Analyze", ResourceService.GetString("Log_MissingDependency"), $"File not found: {JpegTranPath}");
                return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = ResourceService.GetString("Error_JpegTranMissing") };
            }

            try
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

                psi.ArgumentList.Add("-j");
                psi.ArgumentList.Add("-ImageWidth");
                psi.ArgumentList.Add("-ImageHeight");
                psi.ArgumentList.Add("-Orientation");
                psi.ArgumentList.Add("-ThumbnailImage");
                psi.ArgumentList.Add(filePath);

                using var process = Process.Start(psi);
                if (process == null)
                {
                    WriteDebugLog("ERROR", "Analyze", ResourceService.GetString("Log_ExifToolStartFailed"), "Process.Start returned null.");
                    throw new Exception(ResourceService.GetString("Error_CannotStartExifTool"));
                }

                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (string.IsNullOrWhiteSpace(output) || !output.TrimStart().StartsWith("["))
                {
                    WriteDebugLog("ERROR", "Analyze", ResourceService.Format("Log_ExifToolParseFailed", Path.GetFileName(filePath)), $"StdError:\n{error}\n\nStdOutput:\n{output}");
                    return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = ResourceService.GetString("Error_CheckLog") };
                }

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement[0];

                int w = 0, h = 0;
                if (root.TryGetProperty("ImageWidth", out var wProp)) int.TryParse(wProp.ToString(), out w);
                if (root.TryGetProperty("ImageHeight", out var hProp)) int.TryParse(hProp.ToString(), out h);

                string orientation = root.TryGetProperty("Orientation", out var oProp) ? oProp.GetString() ?? "" : "";
                bool hasThumb = root.TryGetProperty("ThumbnailImage", out _);

                int angle = 0;
                if (orientation.Contains("90", StringComparison.OrdinalIgnoreCase)) angle = 90;
                else if (orientation.Contains("180", StringComparison.OrdinalIgnoreCase)) angle = 180;
                else if (orientation.Contains("270", StringComparison.OrdinalIgnoreCase)) angle = 270;

                var tags = new List<string>();

                if (w > h)
                {
                    tags.Add($"[{ResourceService.GetString("Tag_HorizontalStretch")}]");
                    if (angle > 0)
                        tags.Add($"[{ResourceService.Format("Tag_RotationLabel", angle)}]");
                    else
                        tags.Add($"[{ResourceService.GetString("Tag_MissingRotationLabel")}]");
                }
                else if (w < h && angle > 0)
                {
                    tags.Add($"[{ResourceService.GetString("Tag_VerticalStretch")}]");
                    tags.Add($"[{ResourceService.Format("Tag_RotationLabel", angle)}]");
                }

                if (hasThumb)
                {
                    tags.Add($"[{ResourceService.GetString("Tag_ExtraThumbnail")}]");
                }

                if (tags.Count == 0)
                {
                    return new RepairAnalysisResult
                    {
                        IssueType = RepairIssueType.Perfect,
                        IssueDescription = $"[{ResourceService.GetString("Status_Perfect")}]",
                        RotationAngle = 0
                    };
                }
                else
                {
                    bool needsRebuild = (w > h) || angle > 0;
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
                        RotationAngle = angle
                    };
                }
            }
            catch (Exception ex)
            {
                WriteDebugLog("ERROR", "Analyze", ResourceService.Format("Log_CSharpException", Path.GetFileName(filePath)), ex.ToString());
                return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = ResourceService.GetString("Error_InternalCheckLog") };
            }
        }

        /// <summary>
        /// 2. 无损修复文件
        /// </summary>
        public static async Task<(bool Success, string Message)> RepairAsync(string sourcePath, string targetPath, RepairAnalysisResult analysis, CancellationToken token)
        {
            string tempJpg = targetPath + ".tmp_repair";

            try
            {
                if (analysis.IssueType == RepairIssueType.NeedsRebuild || analysis.IssueType == RepairIssueType.NeedsStrip)
                {
                    var jpegtranArgs = new List<string> { "-copy", "none" };
                    if (analysis.RotationAngle > 0)
                    {
                        jpegtranArgs.Add("-rotate");
                        jpegtranArgs.Add(analysis.RotationAngle.ToString());
                    }
                    jpegtranArgs.Add("-outfile");
                    jpegtranArgs.Add(tempJpg);
                    jpegtranArgs.Add(sourcePath);

                    await RunJpegTranAsync(jpegtranArgs.ToArray());
                    token.ThrowIfCancellationRequested();

                    var exifArgs = new List<string> { "-m", "-tagsfromfile", sourcePath, "-all:all", "-ThumbnailImage=" };
                    if (analysis.IssueType == RepairIssueType.NeedsRebuild)
                    {
                        exifArgs.Add("-Orientation=");
                    }
                    exifArgs.Add("-overwrite_original");
                    exifArgs.Add(tempJpg);

                    await RunExifToolAsync(exifArgs.ToArray());
                }

                token.ThrowIfCancellationRequested();

                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(tempJpg, targetPath);

                WriteDebugLog("INFO", "Repair", ResourceService.Format("Log_RepairSuccess", Path.GetFileName(sourcePath)));
                return (true, ResourceService.GetString("Status_RepairSuccess"));
            }
            catch (OperationCanceledException)
            {
                WriteDebugLog("WARN", "Repair", ResourceService.Format("Log_RepairCancelled", Path.GetFileName(sourcePath)));
                return (false, ResourceService.GetString("Status_Cancelled"));
            }
            catch (Exception ex)
            {
                WriteDebugLog("ERROR", "Repair", ResourceService.Format("Log_RepairFailed", Path.GetFileName(sourcePath)), ex.Message);
                return (false, ResourceService.GetString("Error_RepairFailedCheckLog"));
            }
            finally
            {
                if (File.Exists(tempJpg)) File.Delete(tempJpg);
                if (File.Exists(tempJpg + "_original")) File.Delete(tempJpg + "_original");
            }
        }

        private static async Task RunJpegTranAsync(params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = JpegTranPath,
                WorkingDirectory = Path.GetDirectoryName(JpegTranPath) ?? AppContext.BaseDirectory,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null)
            {
                WriteDebugLog("ERROR", "JpegTran", ResourceService.GetString("Log_JpegTranStartFailed"), $"Path: {JpegTranPath}");
                throw new Exception(ResourceService.GetString("Error_CannotStartJpegTran"));
            }

            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0)
            {
                WriteDebugLog("ERROR", "JpegTran", ResourceService.Format("Log_JpegTranExecuteFailed", process.ExitCode), $"Args:\njpegtran {string.Join(" ", args)}\n\nOutput:\n{error}");
                throw new Exception($"JpegTran failed (ExitCode: {process.ExitCode})");
            }
        }

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

            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (error.Contains("Error:", StringComparison.OrdinalIgnoreCase))
            {
                WriteDebugLog("ERROR", "ExifTool", ResourceService.GetString("Log_ExifToolFatalError"), $"Args:\nexiftool {string.Join(" ", args)}\n\nOutput:\n{error}");
                throw new Exception("ExifTool process encountered a fatal error.");
            }
            else if (!string.IsNullOrWhiteSpace(error))
            {
                WriteDebugLog("WARN", "ExifTool", ResourceService.GetString("Log_ExifToolWarning"), $"Args:\nexiftool {string.Join(" ", args)}\n\nOutput:\n{error}");
            }
        }
    }
}