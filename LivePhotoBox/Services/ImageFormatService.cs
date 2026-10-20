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

            await ProcessingPipelineRouter.RunAsync(
                "edit.image-export",
                () => ConvertWithRebuiltNativeAsync(sourcePath, targetPath, quality, token));
        }

        private static async Task ConvertWithRebuiltNativeAsync(
            string sourcePath, string targetPath, int quality, CancellationToken token)
        {
            ImageContainer targetContainer = Path.GetExtension(targetPath).ToLowerInvariant() switch
            {
                ".jpg" or ".jpeg" => ImageContainer.Jpeg,
                ".heic" or ".heif" => ImageContainer.Heic,
                _ => throw new NotSupportedException(
                    "The Rebuilt Native image pipeline currently supports JPEG and HEIC output only.")
            };

            await using var workspace = new MediaWorkspace();
            NeutralMediaBundle bundle = await new NeutralMediaService(
                imageConverter: new RequestedQualityImageConverter(quality)).CreateNeutralBundleAsync(
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

            if (bundle.PrimaryImage == null || !File.Exists(bundle.PrimaryImage.Path))
                throw new IOException("The Rebuilt Native image pipeline produced no image output.");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
            await WindowsOutputArtifactPublisher.PublishCopyAsync(
                bundle.PrimaryImage, targetPath, overwriteExisting: true, token).ConfigureAwait(false);
        }

        // Export quality is a caller preference over the existing converter
        // contract. Keep Neutral's extraction, binding, policy and validation
        // flow intact, changing only the quality passed to its codec request.
        private sealed class RequestedQualityImageConverter(int quality) : IImageConverter
        {
            public Task<ImageConversionResult> ConvertAsync(ImageConversionRequest request,
                CancellationToken cancellationToken = default) =>
                new ImageConverter().ConvertAsync(request with { Quality = quality }, cancellationToken);
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
