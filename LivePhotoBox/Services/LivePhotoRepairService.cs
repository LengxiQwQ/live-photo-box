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
    public enum RepairIssueType
    {
        Perfect,       // 状况C：原生竖向且没有缩图（完美跳过）
        NeedsStrip,    // 状况B：底层正的，藏了缩略图（需要瘦身）
        NeedsRebuild,  // 状况A：底层歪了（需要重构并剥离）
        Error          // 读取出错
    }

    public class RepairAnalysisResult
    {
        public RepairIssueType IssueType { get; set; }
        public string IssueDescription { get; set; } = string.Empty;
        public int RotationAngle { get; set; } = 0;
        public bool NeedsRepair => IssueType == RepairIssueType.NeedsStrip || IssueType == RepairIssueType.NeedsRebuild;
    }

    public static class LivePhotoRepairService
    {
        private static readonly string ExifToolPath = Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe");
        private static readonly string JpegTranPath = Path.Combine(AppContext.BaseDirectory, "Tools", "jpegtran.exe");

        /// <summary>
        /// 1. 扫描与诊断文件
        /// </summary>
        public static async Task<RepairAnalysisResult> AnalyzeFileAsync(string filePath)
        {
            if (!File.Exists(ExifToolPath))
                return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = ResourceService.GetString("Error_ExifToolMissing") };
            if (!File.Exists(JpegTranPath))
                return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = ResourceService.GetString("Error_JpegTranMissing") };

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
                if (process == null) throw new Exception(ResourceService.GetString("Error_CannotStartExifTool"));

                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (string.IsNullOrWhiteSpace(output) || !output.TrimStart().StartsWith("["))
                {
                    return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = ResourceService.Format("Error_ExifToolError", error.Trim()) };
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

                    // 根据语言决定格式：中文环境每两个标签换行，其他语言一行显示所有标签
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
                return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = ResourceService.Format("Error_CSharpException", ex.Message) };
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
                if (analysis.IssueType == RepairIssueType.NeedsRebuild)
                {
                    await RunJpegTranAsync("-copy", "none", "-rotate", analysis.RotationAngle.ToString(), "-outfile", tempJpg, sourcePath);
                    token.ThrowIfCancellationRequested();
                    await RunExifToolAsync("-m", "-tagsfromfile", sourcePath, "-all:all", "-ThumbnailImage=", "-Orientation=", "-overwrite_original", tempJpg);
                }
                else if (analysis.IssueType == RepairIssueType.NeedsStrip)
                {
                    await RunJpegTranAsync("-copy", "none", "-outfile", tempJpg, sourcePath);
                    token.ThrowIfCancellationRequested();
                    await RunExifToolAsync("-m", "-tagsfromfile", sourcePath, "-all:all", "-ThumbnailImage=", "-overwrite_original", tempJpg);
                }

                token.ThrowIfCancellationRequested();

                if (File.Exists(targetPath)) File.Delete(targetPath);
                File.Move(tempJpg, targetPath);

                return (true, ResourceService.GetString("Status_RepairSuccess"));
            }
            catch (OperationCanceledException)
            {
                return (false, ResourceService.GetString("Status_Cancelled"));
            }
            catch (Exception ex)
            {
                return (false, ResourceService.Format("Status_RepairFailed", ex.Message));
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
            if (process == null) throw new Exception(ResourceService.GetString("Error_CannotStartJpegTran"));

            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0) throw new Exception(error);
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
            if (process == null) throw new Exception(ResourceService.GetString("Error_CannotStartExifTool"));

            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (error.Contains("Error:", StringComparison.OrdinalIgnoreCase)) throw new Exception(error);
        }
    }
}