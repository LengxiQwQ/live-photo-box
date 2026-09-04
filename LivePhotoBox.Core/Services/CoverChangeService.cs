using LivePhotoBox.Models;
using LivePhotoBox.Media.Inspection;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 更换封面帧操作的请求参数。
    /// </summary>
    public sealed class CoverChangeRequest
    {
        public required string ImagePath { get; init; }
        public string? VideoPath { get; init; }
        public required LivePhotoType LivePhotoType { get; init; }
        public required LivePhotoProtocolType Protocol { get; init; }
        public required long TimestampUs { get; init; }
        public int? FrameIndex { get; init; }
        public required string OutputImagePath { get; init; }
        public string? OutputVideoPath { get; init; }
    }

    /// <summary>
    /// 更换封面帧操作的结果。
    /// </summary>
    public sealed class CoverChangeResult
    {
        public required string OutputImagePath { get; init; }
        public string? OutputVideoPath { get; init; }
    }

    /// <summary>
    /// 实况照片封面更换服务。
    /// 在当前 Rebuilt 架构中，Cover 重构处于冻结状态（保留 API 结构，移除外部工具）。
    /// </summary>
    public static class CoverChangeService
    {
        /// <summary>
        /// 按协议更换封面帧并写出目标文件。
        /// 在当前 Rebuilt 架构中未开放，直接抛出未支持异常。
        /// </summary>
        public static Task<CoverChangeResult> ChangeCoverAsync(
            CoverChangeRequest request,
            CancellationToken token)
        {
            return ProcessingPipelineRouter.RunAsync<CoverChangeResult>("cover", () =>
                throw new RebuiltPipelineNotReadyException("cover"));
        }

        /// <summary>
        /// 为预览/帧序号换算临时提取实况照片内嵌视频。
        /// 调用方负责创建并清理 workDir。
        /// </summary>
        public static async Task<string?> ExtractEmbeddedVideoForPreviewAsync(
            string imagePath,
            LivePhotoProtocolType protocol,
            string workDir,
            CancellationToken token)
        {
            token.ThrowIfCancellationRequested();

            if (protocol == LivePhotoProtocolType.Huawei)
                return await ExtractHuaweiVideoAsync(imagePath, workDir, token).ConfigureAwait(false);

            if (protocol is LivePhotoProtocolType.Samsung or LivePhotoProtocolType.Fusion)
                return await ExtractSamsungVideoAsync(imagePath, workDir, token).ConfigureAwait(false);

            if (protocol == LivePhotoProtocolType.GoogleV2 && HeicConverterService.IsHeicFile(imagePath))
                return await ExtractHeicMpvdVideoAsync(imagePath, workDir, token).ConfigureAwait(false);

            return await ExtractXmpVideoAsync(imagePath, protocol, workDir, token).ConfigureAwait(false);
        }

        private static Task<string> ExtractHuaweiVideoAsync(
            string imagePath,
            string workDir,
            CancellationToken token)
        {
            var range = LivePhotoSplitService.GetHuaweiEmbeddedVideoRange(imagePath);
            if (range == null)
                throw new InvalidDataException("Cannot locate embedded MP4 in Huawei file.");

            string targetPath = Path.Combine(workDir, "video.mp4");
            CopyByteRange(imagePath, targetPath, range.Value.videoStart, range.Value.videoLength);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(targetPath);
        }

        private static Task<string> ExtractSamsungVideoAsync(
            string imagePath,
            string workDir,
            CancellationToken token)
        {
            string targetPath = Path.Combine(workDir, "video.mp4");

            if (HeicConverterService.IsHeicFile(imagePath))
            {
                long videoStart = LivePhotoMergeService.GetMpvdVideoStart(imagePath);
                long videoLength = LivePhotoMergeService.GetMpvdVideoLength(imagePath);
                if (videoStart <= 0 || videoLength <= 0)
                    throw new InvalidDataException("Cannot locate mpvd box / embedded video in Samsung HEIC file.");

                CopyByteRange(imagePath, targetPath, videoStart, videoLength);
                token.ThrowIfCancellationRequested();
                return Task.FromResult(targetPath);
            }

            var range = LivePhotoSplitService.FindSamsungJpegVideoRange(imagePath);
            if (range == null)
                throw new InvalidDataException("Cannot locate Samsung MotionPhoto_Data video.");

            long fileSize = new FileInfo(imagePath).Length;
            long start = range.Value.videoStart;
            long length = fileSize - start;
            if (length <= 0)
                throw new InvalidDataException("Invalid Samsung embedded video range.");

            CopyByteRange(imagePath, targetPath, start, length);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(targetPath);
        }

        private static Task<string> ExtractHeicMpvdVideoAsync(
            string imagePath,
            string workDir,
            CancellationToken token)
        {
            long videoStart = LivePhotoMergeService.GetMpvdVideoStart(imagePath);
            long videoLength = LivePhotoMergeService.GetMpvdVideoLength(imagePath);
            if (videoStart <= 0 || videoLength <= 0)
                throw new InvalidDataException("Cannot locate mpvd box / embedded video in HEIC file.");

            string targetPath = Path.Combine(workDir, "video.mp4");
            CopyByteRange(imagePath, targetPath, videoStart, videoLength);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(targetPath);
        }

        private static Task<string> ExtractXmpVideoAsync(
            string imagePath,
            LivePhotoProtocolType protocol,
            string workDir,
            CancellationToken token)
        {
            string xmpText = LivePhotoSplitService.ReadMetadataTextSync(imagePath);
            long appendedVideoLength = LivePhotoSplitService.GetAppendedVideoLength(xmpText);
            if (appendedVideoLength <= 0)
                throw new InvalidDataException("Cannot determine embedded video length from XMP metadata.");

            long videoLength = appendedVideoLength;

            if (protocol == LivePhotoProtocolType.OPPO)
            {
                long pureLength = LivePhotoSplitService.GetOppoPureVideoLength(xmpText);
                if (pureLength > 0 && pureLength <= videoLength)
                    videoLength = pureLength;
            }

            long fileSize = new FileInfo(imagePath).Length;
            long videoOffset = fileSize - appendedVideoLength;
            if (videoOffset < 0 || videoLength <= 0)
                throw new InvalidDataException("Invalid embedded video range in XMP live photo.");

            string targetPath = Path.Combine(workDir, "video.mp4");
            CopyByteRange(imagePath, targetPath, videoOffset, videoLength);
            token.ThrowIfCancellationRequested();
            return Task.FromResult(targetPath);
        }

        private static void CopyByteRange(string sourcePath, string destPath, long start, long length)
        {
            using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None);
            src.Seek(start, SeekOrigin.Begin);

            var buffer = new byte[81920];
            long remaining = length;
            while (remaining > 0)
            {
                int read = src.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0)
                    break;
                dst.Write(buffer, 0, read);
                remaining -= read;
            }
        }

        internal static async Task<string?> ReadContentIdentifierAsync(string filePath, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return null;

            try
            {
                var inspector = new SourceInspector();
                var facts = await inspector.InspectAsync(filePath, null, token).ConfigureAwait(false);
                return facts.PairingIdentifier;
            }
            catch
            {
                return null;
            }
        }
    }
}
