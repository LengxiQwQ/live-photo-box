using CommunityToolkit.Mvvm.ComponentModel;
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

        public long TotalSizeBytes { get; set; }
        public string BaseName { get; set; } = string.Empty;

        public string DisplayImageName => TruncateFileName(ImageFileName);
        public string DisplayVideoName => TruncateFileName(VideoFileName);

        public Visibility ThumbnailPlaceholderVisibility => Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        // ==========================================
        // ✨ 完全复刻：老版本的高性能极简懒加载
        // ==========================================
        private bool _isLoadingThumbnail = false;
        private ImageSource? _thumbnail;

        public ImageSource? Thumbnail
        {
            get
            {
                // 当 UI 试图显示这个图片，且还没加载过时，触发此逻辑
                if (_thumbnail == null && !_isLoadingThumbnail && !string.IsNullOrEmpty(ImagePath))
                {
                    _isLoadingThumbnail = true;
                    var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(ImagePath);
                            // 极速读取模式
                            var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.ListView, 80, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);

                            if (thumb != null && dispatcher != null)
                            {
                                dispatcher.TryEnqueue(async () =>
                                {
                                    try
                                    {
                                        var bitmap = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage();
                                        await bitmap.SetSourceAsync(thumb);
                                        Thumbnail = bitmap;
                                    }
                                    catch { }
                                });
                            }
                        }
                        catch { }
                    });
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
            Thumbnail = null;
        }

        // 兼容 ViewModel 的调用，但里面什么都不需要做了，全靠 Getter 触发！
        public Task EnsureThumbnailAsync(Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
        {
            var trigger = Thumbnail; // 触发一下 getter
            return Task.CompletedTask;
        }

        public void CancelThumbnailLoad()
        {
            // 老版本没有取消，不取消才是最稳的！
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