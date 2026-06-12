using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Threading.Tasks;

namespace LivePhotoBox.Models
{
    public partial class MergeTask : ObservableObject
    {
        [ObservableProperty] private int _index;
        [ObservableProperty] private string _imageFileName = string.Empty;
        [ObservableProperty] private string _videoFileName = string.Empty;
        [ObservableProperty] private string _imageSize = string.Empty;
        [ObservableProperty] private string _videoSize = string.Empty;
        [ObservableProperty] private string _imagePath = string.Empty;
        [ObservableProperty] private string _videoPath = string.Empty;
        [ObservableProperty] private ProcessStatus _status = ProcessStatus.Pending;
        [ObservableProperty] private string _details = string.Empty;

        public bool HasErrorDetails => Status == ProcessStatus.Failed && !string.IsNullOrWhiteSpace(Details);

        public long TotalSizeBytes { get; set; }
        public string BaseName { get; set; } = string.Empty;

        public string DisplayImageName => TruncateFileName(ImageFileName);
        public string DisplayVideoName => TruncateFileName(VideoFileName);

        public Visibility ThumbnailPlaceholderVisibility => ThumbnailBindingService.GetPlaceholderVisibility(_thumbnail);

        private bool _isLoadingThumbnail;
        private ImageSource? _thumbnail;

        public ImageSource? Thumbnail
        {
            get => ThumbnailBindingService.TryGetOrLoad(ref _thumbnail, ref _isLoadingThumbnail, ImagePath, value => Thumbnail = value);
            set
            {
                if (SetProperty(ref _thumbnail, value))
                {
                    OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
                }
            }
        }

        partial void OnImagePathChanged(string value)
        {
            _isLoadingThumbnail = false;
            Thumbnail = null;
        }

        partial void OnStatusChanged(ProcessStatus value)
        {
            OnPropertyChanged(nameof(HasErrorDetails));
        }

        partial void OnDetailsChanged(string value)
        {
            OnPropertyChanged(nameof(HasErrorDetails));
        }

        public Task EnsureThumbnailAsync(Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
        {
            var trigger = Thumbnail;
            return Task.CompletedTask;
        }

        public void CancelThumbnailLoad()
        {
        }

        private string TruncateFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return fileName;
            string ext = Path.GetExtension(fileName);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            if (nameWithoutExt.Length <= 30) return fileName;
            return $"{nameWithoutExt.Substring(0, 22)}...{nameWithoutExt.Substring(nameWithoutExt.Length - 8)}{ext}";
        }
    }
}







