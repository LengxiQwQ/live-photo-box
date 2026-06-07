using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    public static class ThumbnailBindingService
    {
        public static ImageSource? TryGetOrLoad(
            ref ImageSource? thumbnail,
            ref bool isLoadingThumbnail,
            string? imagePath,
            System.Action<ImageSource?> assignThumbnail)
        {
            if (thumbnail == null && !isLoadingThumbnail && !string.IsNullOrWhiteSpace(imagePath))
            {
                isLoadingThumbnail = true;
                var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

                _ = Task.Run(async () =>
                {
                    try
                    {
                        var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(imagePath);
                        var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.ListView, 80, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);

                        if (thumb != null && dispatcher != null)
                        {
                            dispatcher.TryEnqueue(async () =>
                            {
                                try
                                {
                                    var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                                    await bitmap.SetSourceAsync(thumb);
                                    assignThumbnail(bitmap);
                                }
                                catch
                                {
                                }
                            });
                        }
                    }
                    catch
                    {
                    }
                });
            }

            return thumbnail;
        }

        public static Visibility GetPlaceholderVisibility(ImageSource? thumbnail)
            => thumbnail == null ? Visibility.Visible : Visibility.Collapsed;
    }
}
