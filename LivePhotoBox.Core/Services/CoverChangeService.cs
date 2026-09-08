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
            CancellationToken token,
            ISourceInspector? sourceInspector = null)
        {
            token.ThrowIfCancellationRequested();
            // The protocol argument is UI context only. Range/container authority
            // always comes from the Native-backed inspector, so a stale protocol
            // label can never select a managed parser or a guessed tail range.
            _ = protocol;
            var facts = await (sourceInspector ?? new SourceInspector())
                .InspectAsync(imagePath, null, token).ConfigureAwait(false);
            var video = facts.MotionVideo;
            if (facts.Protocol is SourceProtocol.NonLive or SourceProtocol.Unknown ||
                video is not { IsPresent: true } || video.SourceIndex != 0)
                return null;
            if (video.ByteOffset < 0 || video.ByteLength <= 0)
                throw new InvalidDataException("Native source inspection returned an invalid embedded video range.");
            if (video.Container is not (VideoContainer.Mp4 or VideoContainer.Mov))
                throw new InvalidDataException("Native source inspection returned an unsupported embedded video container.");

            string extension = video.Container == VideoContainer.Mov ? ".mov" : ".mp4";
            string targetPath = Path.Combine(workDir, "video" + extension);
            await CopyByteRangeAsync(imagePath, targetPath, video.ByteOffset, video.ByteLength, token).ConfigureAwait(false);
            return targetPath;
        }

        private static async Task CopyByteRangeAsync(
            string sourcePath,
            string destPath,
            long start,
            long length,
            CancellationToken token)
        {
            if (start < 0 || length <= 0)
                throw new InvalidDataException("Invalid embedded video range.");

            await using var src = new FileStream(sourcePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite,
                bufferSize: 81920, useAsync: true);
            if (start > src.Length || length > src.Length - start)
                throw new InvalidDataException("Embedded video range is outside the source file.");

            await using var dst = new FileStream(destPath, FileMode.Create, FileAccess.Write, FileShare.None,
                bufferSize: 81920, useAsync: true);
            src.Seek(start, SeekOrigin.Begin);

            var buffer = new byte[81920];
            long remaining = length;
            while (remaining > 0)
            {
                token.ThrowIfCancellationRequested();
                int read = await src.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, remaining)), token).ConfigureAwait(false);
                if (read == 0)
                    throw new EndOfStreamException("Source changed while extracting the inspected video range.");
                await dst.WriteAsync(buffer.AsMemory(0, read), token).ConfigureAwait(false);
                remaining -= read;
            }
        }

    }
}
