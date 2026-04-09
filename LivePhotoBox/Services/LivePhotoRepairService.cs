using System;
using System.Diagnostics;
using System.IO;
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
        private static readonly string ExifToolPath = Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool", "exiftool.exe");
        private static readonly string JpegTranPath = Path.Combine(AppContext.BaseDirectory, "Tools", "jpegtran", "jpegtran.exe");

        /// <summary>
        /// 1. 扫描与诊断文件
        /// </summary>
        public static async Task<RepairAnalysisResult> AnalyzeFileAsync(string filePath)
        {
            // 防御1：检查工具是否真的被复制到了运行目录
            if (!File.Exists(ExifToolPath))
                return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = "找不到: Tools\\exiftool\\exiftool.exe" };
            if (!File.Exists(JpegTranPath))
                return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = "找不到: Tools\\jpegtran\\jpegtran.exe" };

            try
            {
                // 防御2：使用 ArgumentList 彻底杜绝中文路径和空格带来的解析灾难
                var psi = new ProcessStartInfo
                {
                    FileName = ExifToolPath,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    StandardOutputEncoding = System.Text.Encoding.UTF8
                };

                psi.ArgumentList.Add("-charset");
                psi.ArgumentList.Add("filename=utf8");
                psi.ArgumentList.Add("-j");
                psi.ArgumentList.Add("-ImageWidth");
                psi.ArgumentList.Add("-ImageHeight");
                psi.ArgumentList.Add("-Orientation");
                psi.ArgumentList.Add("-ThumbnailImage");
                psi.ArgumentList.Add(filePath); // 直接传入绝对路径，不需要加引号

                using var process = Process.Start(psi);
                if (process == null) throw new Exception("无法启动 exiftool 进程");

                // 防御3：异步读取流，防止缓冲区塞满导致死锁
                string output = await process.StandardOutput.ReadToEndAsync();
                string error = await process.StandardError.ReadToEndAsync();
                await process.WaitForExitAsync();

                if (string.IsNullOrWhiteSpace(output) || !output.TrimStart().StartsWith("["))
                {
                    // 如果 ExifTool 返回的不是 JSON，直接把原生报错显示在界面上！
                    return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = $"ExifTool报错: {error.Trim()}" };
                }

                using var doc = JsonDocument.Parse(output);
                var root = doc.RootElement[0];

                // 防御4：极其宽容的 JSON 解析 (兼容文本和数字类型)
                int w = 0, h = 0;
                if (root.TryGetProperty("ImageWidth", out var wProp)) int.TryParse(wProp.ToString(), out w);
                if (root.TryGetProperty("ImageHeight", out var hProp)) int.TryParse(hProp.ToString(), out h);

                string orientation = root.TryGetProperty("Orientation", out var oProp) ? oProp.GetString() ?? "" : "";
                bool hasThumb = root.TryGetProperty("ThumbnailImage", out _);

                int angle = 0;
                if (orientation.Contains("Rotate 90 CW", StringComparison.OrdinalIgnoreCase)) angle = 90;
                else if (orientation.Contains("Rotate 180", StringComparison.OrdinalIgnoreCase)) angle = 180;
                else if (orientation.Contains("Rotate 270 CW", StringComparison.OrdinalIgnoreCase)) angle = 270;

                // 核心诊断逻辑
                if (w > h && angle > 0)
                {
                    return new RepairAnalysisResult { IssueType = RepairIssueType.NeedsRebuild, IssueDescription = "底层歪斜，需重构并剥离", RotationAngle = angle };
                }
                else if (hasThumb)
                {
                    return new RepairAnalysisResult { IssueType = RepairIssueType.NeedsStrip, IssueDescription = "包含多余缩略图，需瘦身清理" };
                }
                else
                {
                    return new RepairAnalysisResult { IssueType = RepairIssueType.Perfect, IssueDescription = "状态完美，无需修复" };
                }
            }
            catch (Exception ex)
            {
                return new RepairAnalysisResult { IssueType = RepairIssueType.Error, IssueDescription = $"C#内部异常: {ex.Message}" };
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

                return (true, "修复成功");
            }
            catch (OperationCanceledException)
            {
                return (false, "已取消");
            }
            catch (Exception ex)
            {
                return (false, $"修复失败: {ex.Message}");
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
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true
            };
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null) throw new Exception("无法启动 jpegtran");

            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            if (process.ExitCode != 0) throw new Exception(error);
        }

        private static async Task RunExifToolAsync(params string[] args)
        {
            var psi = new ProcessStartInfo
            {
                FileName = ExifToolPath,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
                StandardOutputEncoding = System.Text.Encoding.UTF8
            };

            psi.ArgumentList.Add("-charset");
            psi.ArgumentList.Add("filename=utf8");
            foreach (var arg in args) psi.ArgumentList.Add(arg);

            using var process = Process.Start(psi);
            if (process == null) throw new Exception("无法启动 exiftool");

            string error = await process.StandardError.ReadToEndAsync();
            await process.WaitForExitAsync();

            // Exiftool 有时警告也抛出 ExitCode 1，所以我们只抓取含有 "Error:" 的硬报错
            if (error.Contains("Error:", StringComparison.OrdinalIgnoreCase)) throw new Exception(error);
        }
    }
}