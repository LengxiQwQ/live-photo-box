using ImageMagick;
using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.Services
{
    // HEIC/HEIF → JPEG 转码服务
    // 支持两种解码器，可在设置页面中切换：
    // 0 — Magick.NET (ImageMagick + libheif)，默认
    // 1 — Windows BitmapDecoder（系统内置 HEIC 解码器）
    // 两种方案各有利弊，用户可根据实际效果选择。
    // 转换后均通过 ExifTool 复制元数据（排除 Orientation 以避免双重旋转）。
    public static class HeicConverterService
    {
        // 判断指定文件是否为 HEIC 或 HEIF 格式（仅检查扩展名，不读取文件头）。
        // path: 文件路径
        // è¿å: 是 HEIC/HEIF 文件返回 true
        public static bool IsHeicFile(string path)
        {
            return path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);
        }

        // 读取用户选择的解码器：0=Magick.NET, 1=Windows BitmapDecoder
        public static int DecoderIndex => AppSettingsService.GetValue("HeicDecoderIndex", 0);

        // ── 公开 API ──────────────────────────────────────

        public static Task<string> ConvertToJpegAsync(string heicPath, CancellationToken token = default)
        {
            if (!IsHeicFile(heicPath)) return Task.FromResult(heicPath);

            string jpegPath = Path.Combine(
                Path.GetDirectoryName(heicPath) ?? string.Empty,
                Path.GetFileNameWithoutExtension(heicPath) + ".jpg");

            return ConvertInternalAsync(heicPath, jpegPath, token);
        }

        public static Task<string> ConvertToJpegAsync(string heicPath, string outputDirectory, CancellationToken token = default)
        {
            if (!IsHeicFile(heicPath)) return Task.FromResult(heicPath);

            string baseName = Path.GetFileNameWithoutExtension(heicPath);
            string tempPath = Path.Combine(outputDirectory, baseName + "_heic.jpg");

            return ConvertInternalAsync(heicPath, tempPath, token);
        }

        // ── 调度 ──────────────────────────────────────────

        // 核心转换逻辑 — 根据用户选择的解码器方案分派到不同的实现。
        // 转换完成后通过 ExifTool 复制元数据，失败时清理临时产物。
        // heicPath: 源 HEIC 文件路径
        // outputPath: 目标 JPEG 文件路径
        // token: 取消令牌
        // è¿å: 转换后的 JPEG 文件路径
        private static async Task<string> ConvertInternalAsync(string heicPath, string outputPath, CancellationToken token)
        {
            // 一次读取解码器索引，避免多次调 AppSettingsService（IO 开销）
            int decoderIndex = DecoderIndex;
            var decoderName = decoderIndex == 1 ? "BitmapDecoder" : "Magick.NET";
            LogService.Merge($"Converting HEIC to JPEG ({decoderName}): {heicPath}");

            try
            {
                token.ThrowIfCancellationRequested();

                if (decoderIndex == 1)
                    // BitmapDecoder：WinRT I/O，天然异步
                    await ConvertWithBitmapDecoderAsync(heicPath, outputPath).ConfigureAwait(false);
                else
                    // Magick.NET：CPU 密集型，放线程池
                    await Task.Run(() => ConvertWithMagickNET(heicPath, outputPath), token).ConfigureAwait(false);

                await CopyTagsSafeAsync(heicPath, outputPath, token).ConfigureAwait(false);

                LogService.Merge($"HEIC conversion successful: {outputPath}");
                return outputPath;
            }
            catch (OperationCanceledException)
            {
                TryDelete(outputPath);
                throw;
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Merge($"HEIC conversion failed ({decoderName}): {ex.Message}", LogLevel.Error, ex);
                TryDelete(outputPath);
                throw NewHeicError(heicPath, ex.Message);
            }
        }

        // ── 方案 A：Magick.NET ─────────────────────────────

        // 使用 ImageMagick/libheif 解码 HEIC。
        // 优点：完全自包含，无需 Windows 商店扩展；瓦片网格自动拼接；
        // Display P3→sRGB 自动转换；EXIF 方向自动应用。
        private static void ConvertWithMagickNET(string heicPath, string outputPath)
        {
            using var image = new MagickImage(heicPath);

            // 自动应用 EXIF 方向并移除标签
            image.AutoOrient();

            // 强制 sRGB——Display P3 HEIC → JPEG 必须做，否则发白
            image.ColorSpace = ColorSpace.sRGB;

            image.Format = MagickFormat.Jpeg;
            image.Quality = 100;

            image.Write(outputPath);
        }

        // ── 方案 B：Windows BitmapDecoder ──────────────────

        // 使用 Windows 内置 BitmapDecoder 解码 HEIC。
        // 优点：系统原生解码，色彩还原最准确，HDR 色调映射由系统完成。
        // 缺点：依赖 Windows HEIC 扩展（Win10 需手动安装，Win11 内置）。
        private static async Task ConvertWithBitmapDecoderAsync(string heicPath, string outputPath)
        {
            StorageFile sourceFile = await StorageFile.GetFileFromPathAsync(heicPath);

            SoftwareBitmap? softwareBitmap = null;
            try
            {
                using var inputStream = await sourceFile.OpenAsync(FileAccessMode.Read);
                var decoder = await BitmapDecoder.CreateAsync(inputStream);

                // Bgra8 + Premultiplied：系统内部完成瓦片拼接、色彩空间转换、HDR 色调映射
                softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

                using var fileStream = new FileStream(outputPath, FileMode.Create, FileAccess.Write);
                using var randomAccessStream = fileStream.AsRandomAccessStream();

                var propertySet = new BitmapPropertySet();
                propertySet.Add("ImageQuality", new BitmapTypedValue(1.0f, PropertyType.Single));

                var encoder = await BitmapEncoder.CreateAsync(
                    BitmapEncoder.JpegEncoderId, randomAccessStream, propertySet);

                encoder.SetSoftwareBitmap(softwareBitmap);
                await encoder.FlushAsync();
            }
            finally
            {
                softwareBitmap?.Dispose();
            }
        }

        // ── ExifTool 元数据补充 ────────────────────────────

        private static async Task CopyTagsSafeAsync(string sourcePath, string targetPath, CancellationToken token)
        {
            try { await CopyTagsAsync(sourcePath, targetPath, token).ConfigureAwait(false); }
            catch (Exception ex)
            {
                LogService.Merge($"Copy metadata from HEIC failed: {ex.Message}", LogLevel.Warning, ex);
            }
        }

        // 复制所有标签但排除 Orientation。
        // AutoOrient / BitmapDecoder 已把方向应用到像素上，再复制 Orientation 会导致双重旋转。
        private static async Task CopyTagsAsync(string sourcePath, string targetPath, CancellationToken token)
        {
            // 使用 RunExifToolAsync（stdin 管道，UTF-8 编码），兼容所有语言文件名
            await LivePhotoRepairService.RunExifToolAsync(token,
                "-TagsFromFile", sourcePath,
                "-all:all",
                "-Orientation=",
                "-overwrite_original",
                "-quiet",
                targetPath);
        }

        // ── 工具方法 ──────────────────────────────────────

        private static InvalidOperationException NewHeicError(string heicPath, string detail)
        {
            return new InvalidOperationException(
                ResourceService.Format("Error_HeicConversionFailed",
                    Path.GetFileName(heicPath), detail));
        }

        private static void TryDelete(string path)
        {
            try { if (File.Exists(path)) File.Delete(path); } catch { }
        }
    }
}
