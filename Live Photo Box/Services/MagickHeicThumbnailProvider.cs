using ImageMagick;
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
            var data = await Task.Run(() =>
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
                    // Magick.NET 标准解码失败 → 文件中有额外数据（嵌入 MP4 / mpvd box）
                    // 华为: [HEIC] + [嵌入 MP4] + [LIVE_ 尾]
                    // Google V2: [HEIC] + [mpvd box]
                    // 切出纯 HEIC 图像部分再解码

                    // 先试华为格式（LIVE_ 尾标 + 嵌入 MP4）
                    var range = LivePhotoSplitService.GetHuaweiEmbeddedVideoRange(imagePath);
                    long heicEnd = 0;

                    if (range != null)
                    {
                        heicEnd = range.Value.videoStart;
                    }
                    else
                    {
                        // 不是华为 → 检查 Google V2 HEIC (mpvd box)
                        long mpvdLen = LivePhotoMergeService.GetMpvdVideoLength(imagePath);
                        if (mpvdLen > 0)
                        {
                            // mpvd box 起始位置 = 文件末尾 - mpvd box 大小
                            // mpvd box size = 8 (header) + videoLength
                            // 简化：用 GetMpvdVideoStart 定位 mpvd payload 起始，
                            // HEIC end = mpvd box 起始 - 8 (box size + fourcc)
                            long payloadStart = LivePhotoMergeService.GetMpvdVideoStart(imagePath);
                            if (payloadStart > 8)
                                heicEnd = payloadStart - 8; // mpvd box 从 box size 字段开始
                        }
                    }

                    if (heicEnd <= 0 || heicEnd > new FileInfo(imagePath).Length)
                        throw; // 无法定位纯 HEIC 部分，原样抛出

                    byte[] heicBytes = new byte[heicEnd];
                    using (var fs = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
                        fs.ReadExactly(heicBytes, 0, (int)heicEnd);

                    using var image = new MagickImage(heicBytes);
                    image.AutoOrient();
                    image.Strip();
                    image.Sample(targetSize, targetSize);
                    image.Format = MagickFormat.Jpeg;
                    return image.ToByteArray();
                }
            });
            return (data, (int)targetSize, (int)targetSize);
        }
    }
}
