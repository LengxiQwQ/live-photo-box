using LivePhotoBox.Models;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Models;
using System;
using System.IO;
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
        public static async Task<RepairAnalysisResult> AnalyzeFileAsync(
            string filePath,
            CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(filePath))
                throw new ArgumentException("A source path is required.", nameof(filePath));

            // Repair diagnosis is deliberately a thin orchestration mapping.  The
            // Native SourceInspector owns all media/protocol recognition; Repair
            // must not recover facts from a managed DTO or an external parser.
            SourceMediaFacts facts = await new SourceInspector()
                .InspectAsync(filePath, null, token)
                .ConfigureAwait(false);

            bool isVideo = facts.MotionVideo is { IsPresent: true }
                || string.Equals(Path.GetExtension(filePath), ".mov", StringComparison.OrdinalIgnoreCase)
                || string.Equals(Path.GetExtension(filePath), ".mp4", StringComparison.OrdinalIgnoreCase);

            var result = new RepairAnalysisResult
            {
                IssueType = RepairIssueType.Perfect,
                IssueDescription = facts.Protocol == SourceProtocol.NonLive
                    ? "Native inspection confirmed a non-live media source."
                    : $"Native inspection confirmed {facts.Protocol}.",
                IsVideo = isVideo,
                ContentIdentifier = facts.PairingIdentifier ?? string.Empty
            };

            if (facts.MotionVideo is { } video)
            {
                result.VideoDurationSeconds = video.DurationSeconds;
                result.VideoRotationAngle = video.RotationDegrees;
                result.VideoCodec = video.Codec.ToString();
            }

            return result;
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
            return ProcessingPipelineRouter.RunAsync<(bool Success, string Message)>("repair", () =>
                throw new RebuiltPipelineNotReadyException("repair"));
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
