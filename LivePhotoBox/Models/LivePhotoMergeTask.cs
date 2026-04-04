using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Threading.Tasks;

namespace LivePhotoBox.Models
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
        public Visibility ThumbnailPlaceholderVisibility => Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        // ==========================================
        // 完全使用老版本 LivePhotoTask.cs 的极速加载逻辑
        // ==========================================
        private bool _isLoadingThumbnail = false;
        private ImageSource? _thumbnail;
        public ImageSource? Thumbnail
        {
            get
            {
                if (_thumbnail != null)
                {
                    return _thumbnail;
                }

                if (string.IsNullOrWhiteSpace(ImagePath))
                {
                    return null;
                }

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
                {
                    OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
                }
            }
        }

        partial void OnImagePathChanged(string value)
        {
            _isLoadingThumbnail = false;
            Thumbnail = ThumbnailService.GetCached(value);
            OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
        }

        public async Task EnsureThumbnailAsync(Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
        {
            if (_thumbnail != null || _isLoadingThumbnail || string.IsNullOrWhiteSpace(ImagePath))
            {
                return;
            }

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

        private string TruncateFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return fileName;

            string ext = Path.GetExtension(fileName);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

            if (nameWithoutExt.Length <= 30) return fileName;

            string leftStr = nameWithoutExt.Substring(0, 22);
            string rightStr = nameWithoutExt.Substring(nameWithoutExt.Length - 8);

            return $"{leftStr}...{rightStr}{ext}";
        }
    }
}