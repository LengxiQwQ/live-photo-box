using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Threading.Tasks;

namespace LivePhotoStudio.Models
{
    public enum ProcessStatus
    {
        Pending,
        Processing,
        Success,
        Failed
    }

    public partial class LivePhotoTask : ObservableObject
    {
        [ObservableProperty] private int _index;
        [ObservableProperty] private string _fileName = string.Empty;

        [ObservableProperty] private string _imageFileName = string.Empty;
        [ObservableProperty] private string _videoFileName = string.Empty;

        [ObservableProperty] private string _imageSize = string.Empty;
        [ObservableProperty] private string _videoSize = string.Empty;

        public long TotalSizeBytes { get; set; }

        [ObservableProperty] private string _imagePath = string.Empty;
        [ObservableProperty] private string _videoPath = string.Empty;
        [ObservableProperty] private ProcessStatus _status = ProcessStatus.Pending;
        [ObservableProperty] private double _progress = 0;

        // 高性能懒加载缩略图
        private bool _isLoadingThumbnail = false;
        private ImageSource? _thumbnail;
        public ImageSource? Thumbnail
        {
            get
            {
                if (_thumbnail == null && !_isLoadingThumbnail && !string.IsNullOrEmpty(ImagePath))
                {
                    _isLoadingThumbnail = true;
                    var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(ImagePath);
                            // ListView 模式极速读取缓存缩略图
                            var thumb = await file.GetThumbnailAsync(Windows.Storage.FileProperties.ThumbnailMode.ListView, 64, Windows.Storage.FileProperties.ThumbnailOptions.UseCurrentScale);
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
            set => SetProperty(ref _thumbnail, value);
        }

        [ObservableProperty] private string _details = "等待处理";

        public string BaseName { get; set; } = string.Empty;

        public string DisplayImageName => TruncateFileName(ImageFileName);
        public string DisplayVideoName => TruncateFileName(VideoFileName);

        // 智能文件名截断
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