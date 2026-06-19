using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.Services
{
    public static class ThumbnailService
    {
        private static readonly ConcurrentDictionary<string, ImageSource> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Task<ImageSource?>> _inflightLoads = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim _loadLimiter = new(4, 4);
        private static int _cacheVersion;

        public static ImageSource? GetCached(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            return _thumbnailCache.TryGetValue(imagePath, out var cached) ? cached : null;
        }

        public static Task<ImageSource?> LoadAsync(string imagePath, Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return Task.FromResult<ImageSource?>(null);

            dispatcher ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher == null) return Task.FromResult<ImageSource?>(null);

            if (_thumbnailCache.TryGetValue(imagePath, out var cached))
            {
                return Task.FromResult<ImageSource?>(cached);
            }

            int version = Volatile.Read(ref _cacheVersion);

            return _inflightLoads.GetOrAdd(imagePath, path => LoadCoreAsync(path, dispatcher, version));
        }

        public static void Preload(IEnumerable<string> imagePaths, Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
        {
            dispatcher ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher == null)
            {
                return;
            }

            foreach (var imagePath in imagePaths.Where(static path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _ = LoadAsync(imagePath, dispatcher);
            }
        }

        private static async Task<ImageSource?> LoadCoreAsync(string imagePath, Microsoft.UI.Dispatching.DispatcherQueue dispatcher, int version)
        {
            try
            {
                await _loadLimiter.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_thumbnailCache.TryGetValue(imagePath, out var cached))
                    {
                        return cached;
                    }

                    ImageSource? result = null;

                    if (HeicConverterService.IsHeicFile(imagePath))
                    {
                        result = await LoadHeicThumbnailAsync(imagePath, dispatcher, version);
                    }
                    else
                    {
                        StorageFile file = await StorageFile.GetFileFromPathAsync(imagePath);
                        using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.ListView, 80, ThumbnailOptions.UseCurrentScale);

                        if (thumbnail != null && thumbnail.Size > 0)
                        {
                            var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);

                            if (!dispatcher.TryEnqueue(async () =>
                            {
                                try
                                {
                                    var bitmap = new BitmapImage();
                                    await bitmap.SetSourceAsync(thumbnail);

                                    if (version == Volatile.Read(ref _cacheVersion))
                                    {
                                        _thumbnailCache[imagePath] = bitmap;
                                        tcs.TrySetResult(bitmap);
                                    }
                                    else
                                    {
                                        tcs.TrySetResult(null);
                                    }
                                }
                                catch
                                {
                                    tcs.TrySetResult(null);
                                }
                            }))
                            {
                                tcs.TrySetResult(null);
                            }

                            result = await tcs.Task.ConfigureAwait(false);
                        }
                    }

                    return result;
                }
                finally
                {
                    _loadLimiter.Release();
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                _inflightLoads.TryRemove(imagePath, out _);
            }
        }

        private static async Task<ImageSource?> LoadHeicThumbnailAsync(string imagePath, Microsoft.UI.Dispatching.DispatcherQueue dispatcher, int version)
        {
            try
            {
                string tempJpegPath = Path.Combine(
                    Path.GetTempPath(),
                    $"thumb_{Guid.NewGuid():N}.jpg"
                );

                try
                {
                    StorageFile sourceFile = await StorageFile.GetFileFromPathAsync(imagePath);
                    using var inputStream = await sourceFile.OpenAsync(FileAccessMode.Read);
                    var decoder = await BitmapDecoder.CreateAsync(inputStream);

                    uint originalWidth = decoder.PixelWidth;
                    uint originalHeight = decoder.PixelHeight;

                    double scale = Math.Min(80.0 / originalWidth, 80.0 / originalHeight);
                    uint targetWidth, targetHeight;

                    if (scale >= 1.0)
                    {
                        targetWidth = originalWidth;
                        targetHeight = originalHeight;
                    }
                    else
                    {
                        targetWidth = (uint)Math.Max(1, originalWidth * scale);
                        targetHeight = (uint)Math.Max(1, originalHeight * scale);
                    }

                    using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

                    using (var fileStream = new FileStream(tempJpegPath, FileMode.Create, FileAccess.Write))
                    using (var randomAccessStream = fileStream.AsRandomAccessStream())
                    {
                        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, randomAccessStream);
                        encoder.SetSoftwareBitmap(softwareBitmap);
                        if (targetWidth != originalWidth || targetHeight != originalHeight)
                        {
                            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                            encoder.BitmapTransform.ScaledWidth = targetWidth;
                            encoder.BitmapTransform.ScaledHeight = targetHeight;
                        }
                        await encoder.FlushAsync();
                    }

                    var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);

                    if (!dispatcher.TryEnqueue(() =>
                    {
                        try
                        {
                            var bitmapImage = new BitmapImage();
                            bitmapImage.DecodePixelWidth = (int)targetWidth;
                            bitmapImage.DecodePixelHeight = (int)targetHeight;

                            using var fileStream = new FileStream(tempJpegPath, FileMode.Open, FileAccess.Read);
                            bitmapImage.SetSource(fileStream.AsRandomAccessStream());

                            if (version == Volatile.Read(ref _cacheVersion))
                            {
                                _thumbnailCache[imagePath] = bitmapImage;
                                tcs.TrySetResult(bitmapImage);
                            }
                            else
                            {
                                tcs.TrySetResult(null);
                            }
                        }
                        catch (Exception ex)
                        {
                            LogService.Combo($"HEIC thumbnail load error: {ex.Message}", LogLevel.Warning, ex);
                            tcs.TrySetResult(null);
                        }
                    }))
                    {
                        tcs.TrySetResult(null);
                    }

                    return await tcs.Task.ConfigureAwait(false);
                }
                finally
                {
                    try { File.Delete(tempJpegPath); } catch { }
                }
            }
            catch (Exception ex)
            {
                LogService.Combo($"HEIC thumbnail decode error: {ex.Message}", LogLevel.Warning, ex);
                return null;
            }
        }

        public static void ClearCache()
        {
            _thumbnailCache.Clear();
            _inflightLoads.Clear();
            Interlocked.Increment(ref _cacheVersion);
        }

        //  ------  x:Bind property-getter support  ------

        /// <summary>
        /// For x:Bind property getter usage. Non-async, returns cached or triggers background load.
        /// </summary>
        public static ImageSource? TryGetOrLoad(
            ref ImageSource? thumbnail,
            ref bool isLoading,
            string? imagePath,
            Action<ImageSource?> assignThumbnail)
        {
            if (thumbnail == null && !isLoading && !string.IsNullOrWhiteSpace(imagePath))
            {
                isLoading = true;
                var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                var path = imagePath;

                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    await _loadLimiter.WaitAsync();

                    try
                    {
                        byte[]? imageData = null;
                        int width = 80;
                        int height = 80;

                        try
                        {
                            if (HeicConverterService.IsHeicFile(path))
                            {
                                (imageData, width, height) = await LoadHeicThumbnailDataAsync(path);
                            }
                            else
                            {
                                (imageData, width, height) = await LoadSystemThumbnailDataAsync(path);
                            }
                        }
                        catch
                        {
                        }

                        if (imageData != null && imageData.Length > 0 && dispatcher != null)
                        {
                            dispatcher.TryEnqueue(() =>
                            {
                                try
                                {
                                    var bitmapImage = new BitmapImage();
                                    var stream = new MemoryStream(imageData);
                                    bitmapImage.SetSource(stream.AsRandomAccessStream());
                                    assignThumbnail(bitmapImage);
                                }
                                catch
                                {
                                }
                            });
                        }
                    }
                    finally
                    {
                        _loadLimiter.Release();
                    }
                });
            }

            return thumbnail;
        }

        public static Visibility GetPlaceholderVisibility(ImageSource? thumbnail)
            => thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        private static async Task<(byte[] data, int width, int height)> LoadHeicThumbnailDataAsync(string imagePath)
        {
            var file = await StorageFile.GetFileFromPathAsync(imagePath);
            using var inputStream = await file.OpenAsync(FileAccessMode.Read);

            var decoder = await BitmapDecoder.CreateAsync(inputStream);
            uint w = decoder.PixelWidth;
            uint h = decoder.PixelHeight;

            uint targetSize = 80;
            double scale = Math.Min((double)targetSize / w, (double)targetSize / h);
            uint targetWidth = Math.Max(1, (uint)(w * scale));
            uint targetHeight = Math.Max(1, (uint)(h * scale));

            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            var outputStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream);
            encoder.SetSoftwareBitmap(softwareBitmap);
            encoder.BitmapTransform.ScaledWidth = targetWidth;
            encoder.BitmapTransform.ScaledHeight = targetHeight;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            await encoder.FlushAsync();

            outputStream.Seek(0);
            using var reader = new Windows.Storage.Streams.DataReader(outputStream);
            var buffer = new byte[outputStream.Size];
            await reader.LoadAsync((uint)outputStream.Size);
            reader.ReadBytes(buffer);

            softwareBitmap.Dispose();

            return (buffer, (int)targetWidth, (int)targetHeight);
        }

        private static async Task<(byte[] data, int width, int height)> LoadSystemThumbnailDataAsync(string imagePath)
        {
            var file = await StorageFile.GetFileFromPathAsync(imagePath);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.ListView, 80, ThumbnailOptions.UseCurrentScale);

            if (thumb != null && thumb.Size > 0)
            {
                var thumbCopy = new MemoryStream();
                await thumb.AsStream().CopyToAsync(thumbCopy);
                return (thumbCopy.ToArray(), 80, 80);
            }

            return (Array.Empty<byte>(), 0, 0);
        }
    }
}