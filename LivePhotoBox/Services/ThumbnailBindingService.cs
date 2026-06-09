using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace LivePhotoBox.Services
{
    public static class ThumbnailBindingService
    {
        private static readonly SemaphoreSlim _loadThrottle = new(8);

        public static ImageSource? TryGetOrLoad(
            ref ImageSource? thumbnail,
            ref bool isLoadingThumbnail,
            string? imagePath,
            System.Action<ImageSource?> assignThumbnail)
        {
            if (thumbnail == null && !isLoadingThumbnail && !string.IsNullOrWhiteSpace(imagePath))
            {
                isLoadingThumbnail = true;
                var dispatcher = DispatcherQueue.GetForCurrentThread();
                var path = imagePath;

                _ = Task.Run(async () =>
                {
                    await _loadThrottle.WaitAsync();

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
                        _loadThrottle.Release();
                    }
                });
            }

            return thumbnail;
        }

        private static async Task<(byte[] data, int width, int height)> LoadHeicThumbnailDataAsync(string imagePath)
        {
            var file = await StorageFile.GetFileFromPathAsync(imagePath);
            using var inputStream = await file.OpenAsync(FileAccessMode.Read);

            var decoder = await BitmapDecoder.CreateAsync(inputStream);
            uint width = decoder.PixelWidth;
            uint height = decoder.PixelHeight;

            uint targetSize = 80;
            double scale = Math.Min((double)targetSize / width, (double)targetSize / height);
            uint targetWidth = Math.Max(1, (uint)(width * scale));
            uint targetHeight = Math.Max(1, (uint)(height * scale));

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

        public static Visibility GetPlaceholderVisibility(ImageSource? thumbnail)
            => thumbnail == null ? Visibility.Visible : Visibility.Collapsed;
    }
}
