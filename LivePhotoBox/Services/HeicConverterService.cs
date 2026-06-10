using System;
using System.IO;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.Streams;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.Services
{
    public static class HeicConverterService
    {
        public static bool IsHeicFile(string path)
        {
            return path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase)
                || path.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);
        }

        public static async Task<string> ConvertToJpegAsync(string heicPath, CancellationToken token = default)
        {
            if (!IsHeicFile(heicPath))
            {
                return heicPath;
            }

            AppLogService.Combo($"Converting HEIC to JPEG: {heicPath}");

            try
            {
                string jpegPath = Path.Combine(
                    Path.GetDirectoryName(heicPath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(heicPath) + ".jpg"
                );

                if (File.Exists(jpegPath))
                {
                    AppLogService.Combo($"JPEG already exists, using existing file: {jpegPath}");
                    return jpegPath;
                }

                string tempJpegPath = Path.Combine(
                    Path.GetDirectoryName(heicPath) ?? string.Empty,
                    Path.GetFileNameWithoutExtension(heicPath) + "_temp.jpg"
                );

                StorageFile sourceFile = await StorageFile.GetFileFromPathAsync(heicPath);
                SoftwareBitmap? softwareBitmap = null;

                try
                {
                    using var inputStream = await sourceFile.OpenAsync(FileAccessMode.Read);
                    var decoder = await BitmapDecoder.CreateAsync(inputStream);
                    softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

                    using var fileStream = new FileStream(tempJpegPath, FileMode.Create, FileAccess.Write);
                    using var randomAccessStream = fileStream.AsRandomAccessStream();
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, randomAccessStream);
                    encoder.SetSoftwareBitmap(softwareBitmap);
                    await encoder.FlushAsync();
                }
                finally
                {
                    softwareBitmap?.Dispose();
                }

                if (File.Exists(tempJpegPath))
                {
                    if (File.Exists(jpegPath))
                    {
                        File.Delete(jpegPath);
                    }
                    File.Move(tempJpegPath, jpegPath);
                }

                AppLogService.Combo($"HEIC conversion successful: {jpegPath}");
                return jpegPath;
            }
            catch (Exception ex)
            {
                AppLogService.Combo($"HEIC conversion failed: {ex.Message}", LogLevel.Error, ex);
                throw new InvalidOperationException(
                    ResourceService.Format("Error_HeicConversionFailed", Path.GetFileName(heicPath), ex.Message), ex);
            }
        }

        public static async Task<string> ConvertToJpegAsync(string heicPath, string outputDirectory, CancellationToken token = default)
        {
            if (!IsHeicFile(heicPath))
            {
                return heicPath;
            }

            AppLogService.Combo($"Converting HEIC to JPEG: {heicPath}");

            try
            {
                string baseName = Path.GetFileNameWithoutExtension(heicPath);
                string jpegPath = Path.Combine(outputDirectory, baseName + ".jpg");

                if (File.Exists(jpegPath))
                {
                    AppLogService.Combo($"JPEG already exists in output, using existing file: {jpegPath}");
                    return jpegPath;
                }

                StorageFile sourceFile = await StorageFile.GetFileFromPathAsync(heicPath);
                SoftwareBitmap? softwareBitmap = null;

                try
                {
                    using var inputStream = await sourceFile.OpenAsync(FileAccessMode.Read);
                    var decoder = await BitmapDecoder.CreateAsync(inputStream);
                    softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

                    using var fileStream = new FileStream(jpegPath, FileMode.Create, FileAccess.Write);
                    using var randomAccessStream = fileStream.AsRandomAccessStream();
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, randomAccessStream);
                    encoder.SetSoftwareBitmap(softwareBitmap);
                    await encoder.FlushAsync();
                }
                finally
                {
                    softwareBitmap?.Dispose();
                }

                AppLogService.Combo($"HEIC conversion successful: {jpegPath}");
                return jpegPath;
            }
            catch (Exception ex)
            {
                AppLogService.Combo($"HEIC conversion failed: {ex.Message}", LogLevel.Error, ex);
                throw new InvalidOperationException(
                    ResourceService.Format("Error_HeicConversionFailed", Path.GetFileName(heicPath), ex.Message), ex);
            }
        }
    }
}
