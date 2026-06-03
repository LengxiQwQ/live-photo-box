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
        #region Observable Properties

        [ObservableProperty] private int _index;
        [ObservableProperty] private string _imageFileName = string.Empty;
        [ObservableProperty] private string _videoFileName = string.Empty;
        [ObservableProperty] private string _imageSize = string.Empty;
        [ObservableProperty] private string _videoSize = string.Empty;
        [ObservableProperty] private string _imagePath = string.Empty;
        [ObservableProperty] private string _videoPath = string.Empty;
        [ObservableProperty] private ProcessStatus _status = ProcessStatus.Pending;
        [ObservableProperty] private string _details = string.Empty;

        #endregion

        #region Data Properties

        public long TotalSizeBytes { get; set; }
        public string BaseName { get; set; } = string.Empty;

        #endregion

        #region Thumbnail

        private bool _isLoadingThumbnail = false;
        private ImageSource? _thumbnail;

        public ImageSource? Thumbnail
        {
            get
            {
                if (_thumbnail != null) return _thumbnail;
                if (string.IsNullOrWhiteSpace(ImagePath)) return null;
                if (ThumbnailService.GetCached(ImagePath) is { } cachedThumbnail)
                {
                    _thumbnail = cachedThumbnail;
                    return _thumbnail;
                }
                return _thumbnail;
            }
            set
            {
                if (SetProperty(ref _thumbnail, value))
                    OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
            }
        }

        public Visibility ThumbnailPlaceholderVisibility => Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        partial void OnImagePathChanged(string value)
        {
            _isLoadingThumbnail = false;
            Thumbnail = ThumbnailService.GetCached(value);
            OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
        }

        public async Task EnsureThumbnailAsync(Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
        {
            if (_thumbnail != null || _isLoadingThumbnail || string.IsNullOrWhiteSpace(ImagePath)) return;

            if (ThumbnailService.GetCached(ImagePath) is { } cachedThumbnail)
            {
                Thumbnail = cachedThumbnail;
                return;
            }

            _isLoadingThumbnail = true;
            try
            {
                dispatcher ??= App.MainWindow?.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                Thumbnail = await ThumbnailService.LoadAsync(ImagePath, dispatcher);
            }
            finally
            {
                _isLoadingThumbnail = false;
            }
        }

        #endregion

        #region Computed Properties

        public string DisplayImageName => TruncateFileName(ImageFileName);
        public string DisplayVideoName => TruncateFileName(VideoFileName);

        #endregion

        #region Helpers

        private string TruncateFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return fileName;
            string ext = Path.GetExtension(fileName);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            if (nameWithoutExt.Length <= 30) return fileName;
            return $"{nameWithoutExt.Substring(0, 22)}...{nameWithoutExt.Substring(nameWithoutExt.Length - 8)}{ext}";
        }

        #endregion
    }
}
