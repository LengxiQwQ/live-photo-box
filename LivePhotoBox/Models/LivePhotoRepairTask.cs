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
        [ObservableProperty] private string _details = "等待扫描";

        public RepairAnalysisResult? AnalysisResult { get; set; }

        public string DisplayFileName => TruncateFileName(FileName);

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(ThumbnailPlaceholderVisibility))]
        private ImageSource? _thumbnail;

        public Visibility ThumbnailPlaceholderVisibility => Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        private bool _isLoadingThumbnail = false;

        partial void OnFilePathChanged(string value)
        {
            _isLoadingThumbnail = false;
            Thumbnail = ThumbnailService.GetCached(value);
        }

        public async Task EnsureThumbnailAsync(Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
        {
            if (_thumbnail != null || _isLoadingThumbnail || string.IsNullOrWhiteSpace(FilePath)) return;

            if (ThumbnailService.GetCached(FilePath) is { } cachedThumbnail)
            {
                Thumbnail = cachedThumbnail;
                return;
            }

            _isLoadingThumbnail = true;
            try
            {
                dispatcher ??= App.MainWindow?.DispatcherQueue ?? Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                Thumbnail = await ThumbnailService.LoadAsync(FilePath, dispatcher);
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
            return $"{nameWithoutExt.Substring(0, 22)}...{nameWithoutExt.Substring(nameWithoutExt.Length - 8)}{ext}";
        }
    }
}