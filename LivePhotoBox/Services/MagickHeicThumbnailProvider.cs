using ImageMagick;
using LivePhotoBox.Media.Extraction;
using LivePhotoBox.Media.Inspection;
using LivePhotoBox.Media.Workspace;
using System;
using System.IO;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 基于 Magick.NET (ImageMagick) 的 HEIC 缩略图解码器。
    /// 快路径：Ping() 提取内嵌 EXIF 缩略图（~1ms）。
    /// 慢路径：Sample() 点采样 + AutoOrient + Strip → JPEG。
    /// 构造时限制 OpenMP 为单线程，让外层 Scheduler 控制并发。
    /// </summary>
    public sealed class MagickHeicThumbnailProvider : IThumbnailProvider
    {
        public string Name => "Magick.NET";

        private const uint MinEmbeddedThumbSize = 80;

        public MagickHeicThumbnailProvider()
        {
            try { Environment.SetEnvironmentVariable("OMP_NUM_THREADS", "1"); }
            catch { }
        }

        public async Task<(byte[] data, int width, int height)> LoadHeicThumbnailAsync(
            string imagePath, uint targetSize)
        {
            // ── 快路径：内嵌 EXIF 缩略图 ──
            try
            {
                using var pingImage = new MagickImage();
                pingImage.Ping(imagePath);
                var exif = pingImage.GetExifProfile();
                using var embeddedThumb = exif?.CreateThumbnail();
                if (embeddedThumb != null && embeddedThumb.Width >= MinEmbeddedThumbSize)
                {
                    embeddedThumb.AutoOrient();
                    embeddedThumb.Strip();
                    embeddedThumb.Format = MagickFormat.Jpeg;

                    if (embeddedThumb.Width > targetSize || embeddedThumb.Height > targetSize)
                        embeddedThumb.Sample(targetSize, targetSize);

                    return (embeddedThumb.ToByteArray(), (int)targetSize, (int)targetSize);
                }
            }
            catch { /* Ping 失败 → 回退到完整解码 */ }

            // ── 慢路径：完整解码 + Sample ──
            var data = await Task.Run(async () =>
            {
                try
                {
                    using var image = new MagickImage(imagePath);
                    image.AutoOrient();
                    image.Strip();
                    image.Sample(targetSize, targetSize);
                    image.Format = MagickFormat.Jpeg;
                    return image.ToByteArray();
                }
                catch (Exception)
                {
                    return await LoadInspectedPrimaryThumbnailAsync(imagePath, targetSize).ConfigureAwait(false);
                }
            });
            return (data, (int)targetSize, (int)targetSize);
        }

        internal static async Task<byte[]> LoadInspectedPrimaryThumbnailAsync(string imagePath, uint targetSize)
        {
            // Decode a transaction-owned primary image extracted under
            // Inspector authority. Never guess/truncate a HEIF box range.
            using var inspected = await new SourceInspector().InspectWithPlanAsync(imagePath).ConfigureAwait(false);
            using var workspace = new MediaWorkspace();
            var extracted = await new SourceExtractor().ExtractAsync(
                inspected.ExtractionPlan, imagePath, null, workspace).ConfigureAwait(false);
            using var cleanupPlan = extracted.CleanupPlan;
            using var image = new MagickImage(extracted.PrimaryImage.Path);
            image.AutoOrient();
            image.Strip();
            image.Sample(targetSize, targetSize);
            image.Format = MagickFormat.Jpeg;
            return image.ToByteArray();
        }
    }
}
