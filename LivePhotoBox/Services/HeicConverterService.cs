using System;
using System.Diagnostics;
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
                    var propertySet = new BitmapPropertySet();
                    propertySet.Add("ImageQuality", new BitmapTypedValue(1.0f, PropertyType.Single));
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, randomAccessStream, propertySet);
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

                try
                {
                    await CopyTagsAsync(heicPath, jpegPath, token);
                }
                catch (Exception ex)
                {
                    AppLogService.Combo($"Copy metadata from HEIC failed: {ex.Message}", LogLevel.Warning, ex);
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
                    var propertySet = new BitmapPropertySet();
                    propertySet.Add("ImageQuality", new BitmapTypedValue(1.0f, PropertyType.Single));
                    var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, randomAccessStream, propertySet);
                    encoder.SetSoftwareBitmap(softwareBitmap);
                    await encoder.FlushAsync();
                }
                finally
                {
                    softwareBitmap?.Dispose();
                }

                try
                {
                    await CopyTagsAsync(heicPath, jpegPath, token);
                }
                catch (Exception ex)
                {
                    AppLogService.Combo($"Copy metadata from HEIC failed: {ex.Message}", LogLevel.Warning, ex);
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

        private static async Task CopyTagsAsync(string sourcePath, string targetPath, CancellationToken token)
        {
            string? toolPath = FindExifTool();
            if (string.IsNullOrEmpty(toolPath))
            {
                return;
            }

            string arguments = $"-TagsFromFile \"{sourcePath}\" -all:all \"{targetPath}\" -overwrite_original -quiet";

            using var process = new Process();
            process.StartInfo.FileName = toolPath;
            process.StartInfo.Arguments = arguments;
            process.StartInfo.UseShellExecute = false;
            process.StartInfo.CreateNoWindow = true;
            process.StartInfo.RedirectStandardError = true;
            process.StartInfo.RedirectStandardOutput = true;

            var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            process.EnableRaisingEvents = true;
            process.Exited += (_, _) => tcs.TrySetResult(true);

            try
            {
                process.Start();
            }
            catch
            {
                return;
            }

            using var registration = token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                    {
                        process.Kill();
                    }
                }
                catch
                {
                }
                tcs.TrySetCanceled();
            });

            await tcs.Task.ConfigureAwait(false);

            if (process.ExitCode != 0)
            {
                string error = await process.StandardError.ReadToEndAsync().ConfigureAwait(false);
                throw new InvalidOperationException($"exiftool exited with code {process.ExitCode}: {error}");
            }
        }

        private static string? FindExifTool()
        {
            string[] candidates =
            {
                Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe"),
                Path.Combine(AppContext.BaseDirectory, "exiftool.exe"),
                "exiftool"
            };

            foreach (var candidate in candidates)
            {
                try
                {
                    if (File.Exists(candidate))
                    {
                        return candidate;
                    }
                }
                catch
                {
                }
            }

            string? pathEnv = Environment.GetEnvironmentVariable("PATH");
            if (!string.IsNullOrEmpty(pathEnv))
            {
                foreach (var part in pathEnv.Split(Path.PathSeparator))
                {
                    try
                    {
                        string candidate = Path.Combine(part.Trim(), "exiftool.exe");
                        if (File.Exists(candidate))
                        {
                            return candidate;
                        }
                    }
                    catch
                    {
                    }
                }
            }

            return null;
        }
    }
}
