using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.IO;
using System.Threading.Tasks;

namespace LivePhotoBox.Models
{
    public partial class LivePhotoRepairTask : ObservableObject
    {
        [ObservableProperty] private int _index;
        [ObservableProperty] private string _fileName = string.Empty;
        [ObservableProperty] private string _filePath = string.Empty;
        [ObservableProperty] private ProcessStatus _status = ProcessStatus.Pending;

        [ObservableProperty] private string _issueDescription = string.Empty;
        [ObservableProperty] private bool _needsRepair = false;
        [ObservableProperty] private string _details = ResourceService.GetString("RepairPage_TaskPending");

        public RepairAnalysisResult? AnalysisResult { get; set; }

        public string DisplayFileName => TruncateFileName(FileName);

        public Visibility ThumbnailPlaceholderVisibility => Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        private bool _isLoadingThumbnail = false;
        private ImageSource? _thumbnail;

        public ImageSource? Thumbnail
        {
            get => _thumbnail;
            set
            {
                if (_thumbnail == value) return;

                var dispatcher = App.MainWindow?.DispatcherQueue;
                if (dispatcher != null && !dispatcher.HasThreadAccess)
                {
                    dispatcher.TryEnqueue(() => Thumbnail = value);
                    return;
                }

                SetProperty(ref _thumbnail, value);
                OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
            }
        }

        partial void OnFilePathChanged(string value)
        {
            _isLoadingThumbnail = false;
            Thumbnail = ThumbnailService.GetCached(value);

            if (Thumbnail == null && !string.IsNullOrWhiteSpace(value))
            {
                var dispatcher = App.MainWindow?.DispatcherQueue;
                if (dispatcher != null)
                {
                    _ = AutoLoadThumbnailAsync(value, dispatcher);
                }
            }
        }

        private async Task AutoLoadThumbnailAsync(string path, Microsoft.UI.Dispatching.DispatcherQueue dispatcher)
        {
            if (_isLoadingThumbnail) return;
            _isLoadingThumbnail = true;
            try
            {
                Thumbnail = await ThumbnailService.LoadAsync(path, dispatcher);
            }
            finally
            {
                _isLoadingThumbnail = false;
            }
        }

        public async Task EnsureThumbnailAsync(Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
        {
            if (_thumbnail != null || _isLoadingThumbnail || string.IsNullOrWhiteSpace(FilePath)) return;

            if (ThumbnailService.GetCached(FilePath) is { } cachedThumbnail)
            {
                Thumbnail = cachedThumbnail;
                return;
            }

            dispatcher ??= App.MainWindow?.DispatcherQueue;
            if (dispatcher != null)
            {
                await AutoLoadThumbnailAsync(FilePath, dispatcher);
            }
        }

        // ==========================================
        // UI 显示优化拦截器：仅将 "跳过/无需修复" 的颜色强制转为绿色，不改变文字
        // ==========================================
        public ProcessStatus DisplayStatus
        {
            get
            {
                var skipped = ResourceService.GetString("RepairPage_Task_Skipped");
                var noRepair = ResourceService.GetString("RepairPage_Task_NoRepair");
                // Keep checking for the English word "Perfect" as a fallback
                if (!string.IsNullOrEmpty(Details) && (Details.Contains(skipped) || Details.Contains(noRepair) || Details.Contains("Perfect")))
                {
                    return ProcessStatus.Success; // 强制返回 Success 以触发绿色显示
                }
                return Status;
            }
        }

        partial void OnDetailsChanged(string value)
        {
            OnPropertyChanged(nameof(DisplayStatus));
        }

        partial void OnStatusChanged(ProcessStatus value)
        {
            OnPropertyChanged(nameof(DisplayStatus));
        }

        private string TruncateFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return fileName;
            string ext = Path.GetExtension(fileName);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);
            if (nameWithoutExt.Length <= 30) return fileName;
            return $"{nameWithoutExt.Substring(0, 21)}...{nameWithoutExt.Substring(nameWithoutExt.Length - 8)}{ext}";
        }
    }
}