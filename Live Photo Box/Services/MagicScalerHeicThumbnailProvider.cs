using ImageMagick;
using PhotoSauce.MagicScaler;
using System;
using System.IO;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 基于 PhotoSauce MagicScaler 的 HEIC 缩略图解码器（实验性方案）。
    /// 快路径：先用 Magick.NET Ping() 提取内嵌 EXIF 缩略图（~1ms）。
    /// 慢路径：MagicScaler WIC 管线解码 → 缩放到目标尺寸 → JPEG 输出。
    /// HEIC 通过 PhotoSauce.NativeCodecs.Libheif 插件注册为 WIC 编解码器。
    /// </summary>
    public sealed class MagicScalerHeicThumbnailProvider : IThumbnailProvider
    {
        public string Name => "MagicScaler (PhotoSauce)";

        /// <summary>内嵌缩略图低于此尺寸时走完整解码</summary>
        private const uint MinEmbeddedThumbSize = 80;

        public async Task<(byte[] data, int width, int height)> LoadHeicThumbnailAsync(
            string imagePath, uint targetSize)
        {
            // ── 快路径：提取 HEIC 内嵌 EXIF 缩略图 ──
            // 绝大多数 iPhone 拍摄的 HEIC 都内嵌 JPEG 预览图（通常 ≥320px）
            // Ping() 只读元数据不解码像素，耗时 ~0.1ms
            try
            {
                using var pingImage = new MagickImage();
                pingImage.Ping(imagePath);
                var exif = pingImage.GetExifProfile();
                using var embeddedThumb = exif?.CreateThumbnail();
                if (embeddedThumb != null && embeddedThumb.Width >= MinEmbeddedThumbSize)
                {
                    // 内嵌缩略图可能比目标大，缩放到目标尺寸
                    embeddedThumb.Format = MagickFormat.Jpeg;
                    embeddedThumb.AutoOrient();
                    embeddedThumb.Strip();

                    if (embeddedThumb.Width > targetSize || embeddedThumb.Height > targetSize)
                        embeddedThumb.Sample(targetSize, targetSize);

                    var fastData = embeddedThumb.ToByteArray();
                    return (fastData, (int)targetSize, (int)targetSize);
                }
            }
            catch
            {
                // Ping 失败（文件损坏等）→ 回退到完整解码
            }

            // ── 慢路径：MagicScaler 完整解码 ──
            var data = await Task.Run(() =>
            {
                var settings = new ProcessImageSettings
                {
                    Width = (int)targetSize,
                    Height = (int)targetSize,
                    ResizeMode = CropScaleMode.Max,
                };

                using var outputStream = new MemoryStream();
                MagicImageProcessor.ProcessImage(imagePath, outputStream, settings);
                return outputStream.ToArray();
            });

            return (data, (int)targetSize, (int)targetSize);
        }
    }
}
