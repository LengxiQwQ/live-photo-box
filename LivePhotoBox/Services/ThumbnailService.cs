using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace LivePhotoBox.Services
{
    public static class ThumbnailService
    {
        public static async Task<ImageSource?> LoadAsync(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath))
            {
                return null;
            }

            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(imagePath);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.ListView, 64, ThumbnailOptions.UseCurrentScale);
                if (thumbnail == null)
                {
                    return null;
                }

                var bitmap = new BitmapImage();
                await bitmap.SetSourceAsync(thumbnail);
                return bitmap;
            }
            catch
            {
                return null;
            }
        }
    }
}
