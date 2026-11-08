using LivePhotoBox.Media;
using LivePhotoBox.Media.Image;
using LivePhotoBox.Interop;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Models;
using LivePhotoBox.Protocols.Cleaning;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 通用图片格式转换服务，基于 Rebuilt Native codec backend 与 NeutralMediaService。
    /// 当前支持 JPEG、HEIC；PNG/WebP 不属于产品支持的输入或输出格式。
    /// </summary>
    public static class ImageFormatService
    {
        /// <summary>支持的导出图片格式</summary>
        public static IReadOnlyList<string> SupportedExportExtensions { get; } = new[] { ".jpg", ".heic" };

        /// <summary>
        /// 将源图片转换为目标格式。
        /// </summary>
        /// <param name="sourcePath">源图片文件路径</param>
        /// <param name="targetPath">目标文件路径（扩展名决定输出格式）</param>
        /// <param name="quality">质量 1-100（仅对 JPEG/HEIC 等有损格式生效）</param>
        /// <param name="token">取消令牌</param>
        public static async Task ConvertImageAsync(
            string sourcePath, string targetPath,
            int quality = 80, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();
            if (quality is < 1 or > 100)
                throw new ArgumentOutOfRangeException(nameof(quality), "Image quality must be between 1 and 100.");

            using var scope = WorkspaceCleanupScope.Begin();
            Exception? failure = null;
            try
            {
                await ProcessingPipelineRouter.RunAsync(
                    "edit.image-export",
                    async () =>
                    {
                        failure = await TryConvertImageAsync(sourcePath, targetPath, quality, token).ConfigureAwait(false);
                    });
            }
            catch (Exception ex) { failure = ex; }
            var cleanupFailures = scope.Failures;
            scope.Dispose();
            if (cleanupFailures.Count > 0)
                throw new IOException("Image conversion cleanup was limited. Output may exist at: " + targetPath +
                    Environment.NewLine + string.Join(Environment.NewLine, cleanupFailures), failure);
            if (failure != null)
                System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(failure).Throw();
        }

        internal static async Task<Exception?> TryConvertImageAsync(
            string sourcePath, string targetPath, int quality, CancellationToken token)
        {
            if (token.IsCancellationRequested) return new OperationCanceledException(token);
            if (quality is < 1 or > 100) return new ArgumentOutOfRangeException(nameof(quality));
            ImageContainer targetContainer = Path.GetExtension(targetPath).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => ImageContainer.Jpeg,
                ".heic" or ".heif" => ImageContainer.Heic,
                _ => ImageContainer.Unknown
            };
            if (targetContainer == ImageContainer.Unknown)
                return new NotSupportedException("The Rebuilt Native image pipeline currently supports JPEG and HEIC output only.");

            var workspaceAttempt = MediaWorkspace.TryCreate(token);
            if (workspaceAttempt.Failure is { } workspaceFailure) return workspaceFailure;
            await using var workspace = workspaceAttempt.Workspace!;
            var preparation = await new NeutralMediaService(
                imageConverter: new RequestedQualityImageConverter(quality)).TryCreateNeutralBundleAsync(
                sourcePath,
                secondaryPath: null,
                workspace,
                new MediaFormatRequirement
                {
                    ImageContainer = targetContainer,
                    VideoContainer = VideoContainer.Unknown,
                    VideoCodec = VideoCodec.Copy
                },
                PreservationPolicy.AllowDiscard,
                token).ConfigureAwait(false);
            if (preparation.Failure is { } preparationFailure) return preparationFailure;
            NeutralMediaBundle bundle = preparation.Bundle!;

            if (bundle.PrimaryImage == null || !File.Exists(bundle.PrimaryImage.Path))
                return new IOException("The Rebuilt Native image pipeline produced no image output.");

            if (OutputDirectoryAttempt.Ensure(Path.GetDirectoryName(Path.GetFullPath(targetPath))!) is { } directoryFailure) return directoryFailure;
            var publication = await WindowsOutputArtifactPublisher.TryPublishCopyAsync(
                bundle.PrimaryImage, targetPath, overwriteExisting: true, token).ConfigureAwait(false);
            return publication.Failure;
        }

        // Export quality is a caller preference over the existing converter
        // contract. Keep Neutral's extraction, binding, policy and validation
        // flow intact, changing only the quality passed to its codec request.
        private sealed class RequestedQualityImageConverter(int quality) : IImageConverter
        {
            public Task<ImageConversionResult> ConvertAsync(ImageConversionRequest request,
                CancellationToken cancellationToken = default) =>
                new ImageConverter().TryConvertAsync(request with { Quality = quality }, cancellationToken);
        }

        /// <summary>
        /// 判断目标格式是否需要转换（扩展名相同则不需要）。
        /// </summary>
        public static bool NeedsConversion(string sourcePath, string targetExtension)
        {
            string sourceExt = Path.GetExtension(sourcePath);
            return !string.Equals(sourceExt, targetExtension, StringComparison.OrdinalIgnoreCase);
        }
    }
}
