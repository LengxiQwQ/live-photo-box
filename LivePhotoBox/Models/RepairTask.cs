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
    public partial class RepairTask : ObservableObject
    {
        #region Observable Properties

        [ObservableProperty] private int _index;
        [ObservableProperty] private string _fileName = string.Empty;
        [ObservableProperty] private string _filePath = string.Empty;
        [ObservableProperty] private ProcessStatus _status = ProcessStatus.Pending;
        [ObservableProperty] private string _issueDescription = string.Empty;
        [ObservableProperty] private bool _needsRepair = false;
        [ObservableProperty] private string _details = string.Empty;

        #endregion

        #region Data Properties

        public RepairAnalysisResult? AnalysisResult { get; set; }

        #endregion

        #region Thumbnail

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

        public Visibility ThumbnailPlaceholderVisibility => Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

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

        #endregion

        #region Computed Properties

        public string DisplayFileName => FileNameFormatter.Truncate(FileName);

        public ProcessStatus DisplayStatus
        {
            get
            {
                // 无需修复的文件直接视为成功（绿色）；避免依赖多语言字符串比较
                if (!NeedsRepair || AnalysisResult?.IssueType == RepairIssueType.Perfect)
                {
                    return ProcessStatus.Success;
                }
                return Status;
            }
        }

        public bool HasErrorDetails => Status == ProcessStatus.Failed && !string.IsNullOrWhiteSpace(Details);

        partial void OnDetailsChanged(string value)
        {
            OnPropertyChanged(nameof(DisplayStatus));
            OnPropertyChanged(nameof(HasErrorDetails));
        }

        partial void OnStatusChanged(ProcessStatus value)
        {
            OnPropertyChanged(nameof(DisplayStatus));
            OnPropertyChanged(nameof(HasErrorDetails));
        }

        #endregion
    }
}
