using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Threading;
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

        public long TotalSizeBytes { get; set; }
        public string BaseName { get; set; } = string.Empty;

        private volatile int _thumbnailLoadState;
        private ImageSource? _thumbnail;

        private int _thumbnailGeneration = 0;
        private CancellationTokenSource? _thumbnailCts;

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
                {
                    OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
                }
            }
        }

        public Visibility ThumbnailPlaceholderVisibility => Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        partial void OnImagePathChanged(string value)
        {
            CancelThumbnailLoad();
            Thumbnail = ThumbnailService.GetCached(value);
            OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
        }

        public void CancelThumbnailLoad()
        {
            Interlocked.Increment(ref _thumbnailGeneration);
            if (_thumbnailCts != null)
            {
                try { _thumbnailCts.Cancel(); } catch { }
                _thumbnailCts = null;
            }

            Interlocked.Exchange(ref _thumbnailLoadState, 0);
        }

        public Task EnsureThumbnailAsync(Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null, bool forceLoad = false)
        {
            if (_thumbnail != null || string.IsNullOrWhiteSpace(ImagePath))
            {
                return Task.CompletedTask;
            }

            if (ThumbnailService.GetCached(ImagePath) is { } cachedThumbnail)
            {
                Thumbnail = cachedThumbnail;
                return Task.CompletedTask;
            }

            if (Interlocked.CompareExchange(ref _thumbnailLoadState, 1, 0) != 0)
            {
                return Task.CompletedTask;
            }

            return EnsureThumbnailCoreAsync(dispatcher, forceLoad);
        }

        private async Task EnsureThumbnailCoreAsync(Microsoft.UI.Dispatching.DispatcherQueue? dispatcher, bool forceLoad)
        {
            int currentGen = Interlocked.Increment(ref _thumbnailGeneration);

            var cts = new CancellationTokenSource();
            _thumbnailCts = cts;
            var token = cts.Token;

            try
            {
                if (currentGen != _thumbnailGeneration || token.IsCancellationRequested) return;

                dispatcher ??= App.MainWindow?.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                var loadedThumbnail = await ThumbnailService.LoadAsync(ImagePath, dispatcher, forceLoad ? CancellationToken.None : token);

                if (currentGen == _thumbnailGeneration && !token.IsCancellationRequested && loadedThumbnail != null)
                {
                    Thumbnail = loadedThumbnail;
                }
            }
            catch (OperationCanceledException) { }
            catch { }
            finally
            {
                if (currentGen == _thumbnailGeneration)
                {
                    Interlocked.Exchange(ref _thumbnailLoadState, 0);
                }
            }
        }

        public string DisplayImageName => TruncateFileName(ImageFileName);
        public string DisplayVideoName => TruncateFileName(VideoFileName);

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
