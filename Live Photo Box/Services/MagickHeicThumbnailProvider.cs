using ImageMagick;
using System;
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
            var data = await Task.Run(() =>
            {
                using var image = new MagickImage(imagePath);
                image.AutoOrient();
                image.Strip();
                image.Sample(targetSize, targetSize);
                image.Format = MagickFormat.Jpeg;
                return image.ToByteArray();
            });
            return (data, (int)targetSize, (int)targetSize);
        }
    }
}
