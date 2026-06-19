using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoBox.Helpers;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Threading.Tasks;

namespace LivePhotoBox.Models
{
    public partial class ComboTask : ObservableObject
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

        public string DisplayImageName => FileNameFormatter.Truncate(ImageFileName);
        public string DisplayVideoName => FileNameFormatter.Truncate(VideoFileName);

        public Visibility ThumbnailPlaceholderVisibility => ThumbnailService.GetPlaceholderVisibility(_thumbnail);

        private bool _isLoadingThumbnail;
        private ImageSource? _thumbnail;

        public ImageSource? Thumbnail
        {
            get => ThumbnailService.TryGetOrLoad(ref _thumbnail, ref _isLoadingThumbnail, ImagePath, value => Thumbnail = value);
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

    }
}
