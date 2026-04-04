using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.IO;
using System.Threading.Tasks;

namespace LivePhotoBox.Models
{
    public partial class LivePhotoSplitTask : ObservableObject
    {
        [ObservableProperty] private int _index;
        [ObservableProperty] private string _sourceFileName = string.Empty;
        [ObservableProperty] private string _sourcePath = string.Empty;
        [ObservableProperty] private string _fileSize = string.Empty;
        [ObservableProperty] private string _progressText = "0%";
        [ObservableProperty] private ProcessStatus _status = ProcessStatus.Pending;
        [ObservableProperty] private string _details = string.Empty;

        private bool _isLoadingThumbnail;
        private ImageSource? _thumbnail;

        public string DisplaySourceFileName => TruncateFileName(SourceFileName);
        public string DisplayStatus => Status switch
        {
            ProcessStatus.Pending => ResourceService.GetString("Task_Pending"),
            ProcessStatus.Processing => ResourceService.GetString("Task_Processing"),
            ProcessStatus.Success => ResourceService.GetString("Task_Success"),
            ProcessStatus.Failed => ResourceService.GetString("Task_Failed"),
            _ => Status.ToString()
        };
        public Visibility ThumbnailPlaceholderVisibility => Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        public ImageSource? Thumbnail
        {
            get
            {
                if (_thumbnail != null)
                {
                    return _thumbnail;
                }

                if (string.IsNullOrWhiteSpace(SourcePath))
                {
                    return null;
                }

                if (SplitThumbnailService.GetCached(SourcePath) is { } cachedThumbnail)
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

        partial void OnSourcePathChanged(string value)
        {
            _isLoadingThumbnail = false;
            Thumbnail = SplitThumbnailService.GetCached(value);
            OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
        }

        partial void OnStatusChanged(ProcessStatus value)
        {
            OnPropertyChanged(nameof(DisplayStatus));
        }

        public async Task EnsureThumbnailAsync(Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
        {
            if (_thumbnail != null || _isLoadingThumbnail || string.IsNullOrWhiteSpace(SourcePath))
            {
                return;
            }

            if (SplitThumbnailService.GetCached(SourcePath) is { } cachedThumbnail)
            {
                Thumbnail = cachedThumbnail;
                return;
            }

            _isLoadingThumbnail = true;
            try
            {
                dispatcher ??= App.MainWindow?.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                Thumbnail = await SplitThumbnailService.LoadAsync(SourcePath, dispatcher);
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
