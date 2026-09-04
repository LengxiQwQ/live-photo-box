using LivePhotoBox.Models;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 实况照片修复服务。
    /// 在当前 Rebuilt 架构中，Repair 重构被明确冻结，外部工具已完全移除。
    /// 调用诊断与修复均安全返回未支持状态，不执行破坏性操作，不启动外部进程。
    /// </summary>
    public static class LivePhotoRepairService
    {
        /// <summary>
        /// 扫描与诊断文件。
        /// </summary>
        public static Task<RepairAnalysisResult> AnalyzeFileAsync(
            string filePath,
            CancellationToken token = default)
        {
            return Task.FromResult(new RepairAnalysisResult
            {
                IssueType = RepairIssueType.Error,
                IssueDescription = "Repair analysis is not supported in the Rebuilt Native engine (external tools removed)."
            });
        }

        /// <summary>
        /// 修复文件。
        /// </summary>
        public static Task<(bool Success, string Message)> RepairAsync(
            string sourcePath,
            string targetPath,
            RepairAnalysisResult analysis,
            CancellationToken token = default,
            RepairOptions? options = null)
        {
            return ProcessingPipelineRouter.RunAsync("repair", () =>
                Task.FromResult((false, "Repair is not supported in the Rebuilt Native engine (external tools removed).")));
        }

        /// <summary>
        /// 写入实况照片标记。
        /// </summary>
        public static Task TryWriteLivePhotoBoxMarkerAsync(
            string targetPath,
            string operation,
            CancellationToken token = default)
        {
            return Task.CompletedTask;
        }

        /// <summary>
        /// 运行 exiftool（已废弃）。
        /// </summary>
        public static Task RunExifToolAsync(params string[] args)
            => throw new NotSupportedException("ExifTool has been removed from LivePhotoBox.");

        /// <summary>
        /// 运行 exiftool（已废弃）。
        /// </summary>
        public static Task RunExifToolAsync(CancellationToken token, params string[] args)
            => throw new NotSupportedException("ExifTool has been removed from LivePhotoBox.");
    }
}
