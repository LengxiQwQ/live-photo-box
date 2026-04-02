using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoStudio.Services;
using Microsoft.UI.Xaml.Media;
using System.IO;
using System.Threading.Tasks;

namespace LivePhotoStudio.Models
{
    public enum ProcessStatus
    {
        Pending,
        Processing,
        Success,
        Failed
    }

    public partial class LivePhotoMergeTask : ObservableObject
    {
        [ObservableProperty] private int _index;
        [ObservableProperty] private string _imageFileName = string.Empty;
        [ObservableProperty] private string _videoFileName = string.Empty;
        [ObservableProperty] private string _imageSize = string.Empty;
        [ObservableProperty] private string _videoSize = string.Empty;
        [ObservableProperty] private string _imagePath = string.Empty;
        [ObservableProperty] private string _videoPath = string.Empty;
        [ObservableProperty] private ProcessStatus _status = ProcessStatus.Pending;
        [ObservableProperty] private string _details = "等待处理";

        public long TotalSizeBytes { get; set; }
        public string BaseName { get; set; } = string.Empty;
        public string DisplayImageName => TruncateFileName(ImageFileName);
        public string DisplayVideoName => TruncateFileName(VideoFileName);

        private bool _isLoadingThumbnail;
        private ImageSource? _thumbnail;

        public ImageSource? Thumbnail
        {
            get
            {
                if (_thumbnail == null && !_isLoadingThumbnail && !string.IsNullOrWhiteSpace(ImagePath))
                {
                    _isLoadingThumbnail = true;
                    _ = LoadThumbnailAsync();
                }

                return _thumbnail;
            }
            private set => SetProperty(ref _thumbnail, value);
        }

        private async Task LoadThumbnailAsync()
        {
            Thumbnail = await ThumbnailService.LoadAsync(ImagePath);
            _isLoadingThumbnail = false;
        }

        private static string TruncateFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return fileName;

            string extension = Path.GetExtension(fileName);
            string nameWithoutExtension = Path.GetFileNameWithoutExtension(fileName);
            if (nameWithoutExtension.Length <= 30) return fileName;

            string left = nameWithoutExtension[..22];
            string right = nameWithoutExtension[^8..];
            return $"{left}...{right}{extension}";
        }
    }
}