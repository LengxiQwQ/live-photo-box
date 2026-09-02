using ImageMagick;
using LivePhotoBox.Media;
using LivePhotoBox.Media.Models;
using LivePhotoBox.Media.Workspace;
using LivePhotoBox.Models;
using LivePhotoBox.Protocols.Cleaning;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 通用图片格式转换服务 — 基于 Magick.NET 实现任意图片格式之间的转换。
    /// 支持的输出格式：JPEG、PNG、WebP。
    /// </summary>
    public static class ImageFormatService
    {
        private static readonly Dictionary<string, MagickFormat> ExtensionToFormat = new(StringComparer.OrdinalIgnoreCase)
        {
            { ".jpg", MagickFormat.Jpeg },
            { ".jpeg", MagickFormat.Jpeg },
            { ".png", MagickFormat.Png },
            { ".webp", MagickFormat.WebP },
        };

        /// <summary>支持的导出图片格式</summary>
        public static readonly IReadOnlyList<string> SupportedExportExtensions = new[]
        {
            ".jpg", ".png", ".webp"
        };

        /// <summary>
        /// 将源图片转换为目标格式。
        /// </summary>
        /// <param name="sourcePath">源图片文件路径</param>
        /// <param name="targetPath">目标文件路径（扩展名决定输出格式）</param>
        /// <param name="quality">质量 1-100（仅对 JPEG/WebP/HEIC 等有损格式生效）</param>
        /// <param name="token">取消令牌</param>
        public static async Task ConvertImageAsync(
            string sourcePath, string targetPath,
            int quality = 80, CancellationToken token = default)
        {
            token.ThrowIfCancellationRequested();

            if (ProcessingBackendSettingsService.Load().Mode == ProcessingPipelineMode.Rebuilt)
            {
                await ProcessingPipelineRouter.RunRebuiltAsync(
                    "edit.image-export",
                    () => ConvertWithRebuiltNativeAsync(sourcePath, targetPath, quality, token));
                return;
            }

            string targetExt = Path.GetExtension(targetPath);

            // 源和目标格式相同 → 直接复制
            string sourceExt = Path.GetExtension(sourcePath);
            if (string.Equals(sourceExt, targetExt, StringComparison.OrdinalIgnoreCase))
            {
                await Task.Run(() => File.Copy(sourcePath, targetPath, overwrite: true), token);
                return;
            }

            // 所有格式 → Magick.NET
            if (!ExtensionToFormat.TryGetValue(targetExt, out var magickFormat))
                throw new NotSupportedException($"Unsupported output format: {targetExt}");

            LogService.Merge($"ImageFormat: {Path.GetFileName(sourcePath)} -> {Path.GetFileName(targetPath)} (q={quality})");

            await Task.Run(() => ConvertWithMagick(sourcePath, targetPath, magickFormat, quality), token);

            // JPEG 输出后复制 EXIF
            if (targetExt.Equals(".jpg", StringComparison.OrdinalIgnoreCase) ||
                targetExt.Equals(".jpeg", StringComparison.OrdinalIgnoreCase))
            {
                try { await CopyExifTagsAsync(sourcePath, targetPath, token); }
                catch (Exception ex)
                {
                    LogService.Merge($"ImageFormat: EXIF copy failed (non-fatal): {ex.Message}", LogLevel.Warning);
                }
            }
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
            NeutralMediaBundle bundle = await new NeutralMediaService().CreateNeutralBundleAsync(
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

            if (quality is < 1 or > 100)
                throw new ArgumentOutOfRangeException(nameof(quality), "Image quality must be between 1 and 100.");

            Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(targetPath))!);
            File.Copy(bundle.PrimaryImage.Path, targetPath, overwrite: true);
        }

        /// <summary>
        /// 判断目标格式是否需要转换（扩展名相同则不需要）。
        /// </summary>
        public static bool NeedsConversion(string sourcePath, string targetExtension)
        {
            string sourceExt = Path.GetExtension(sourcePath);
            return !string.Equals(sourceExt, targetExtension, StringComparison.OrdinalIgnoreCase);
        }

        // ── Magick.NET 转换 ────────────────────────────────

        private static void ConvertWithMagick(string sourcePath, string targetPath,
            MagickFormat format, int quality)
        {
            using var image = new MagickImage(sourcePath);

            image.AutoOrient();
            image.ColorSpace = ColorSpace.sRGB;
            image.Format = format;

            // 只有有损格式才设 Quality（PNG/BMP/TIFF 忽略此值）
            if (format is MagickFormat.Jpeg or MagickFormat.WebP)
                image.Quality = (uint)quality;

            image.Write(targetPath);
        }

        // ── EXIF 复制（仅对 JPEG 输出）──────────────────────

        private static async Task CopyExifTagsAsync(string sourcePath, string targetPath,
            CancellationToken token)
        {
            // 复用现有 exiftool 模式：复制原图全部标签，排除方向和缩略图
            await LivePhotoRepairService.RunExifToolAsync(token,
                "-TagsFromFile", sourcePath,
                "-all:all",
                "-Orientation=",
                "-ExifImageWidth=",
                "-ExifImageHeight=",
                "-ThumbnailImage=",
                "-overwrite_original",
                "-quiet",
                targetPath);

            // 删除实况照片私有协议标签
            await LivePhotoRepairService.RunExifToolAsync(token,
                "-xmp-GCamera:all=",
                "-xmp-OpCamera:all=",
                "-xmp-Container:all=",
                "-ContentIdentifier=",
                "-overwrite_original",
                "-quiet",
                targetPath);
        }
    }
}
