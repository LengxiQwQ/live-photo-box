using CommunityToolkit.Mvvm.ComponentModel;
using System.IO;

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

        // 新增：文件大小字符串
        [ObservableProperty] private string _imageSize = string.Empty;
        [ObservableProperty] private string _videoSize = string.Empty;

        [ObservableProperty] private string _imagePath = string.Empty;
        [ObservableProperty] private string _videoPath = string.Empty;
        [ObservableProperty] private ProcessStatus _status = ProcessStatus.Pending;
        [ObservableProperty] private double _progress = 0;
        [ObservableProperty] private string _details = "等待处理";

        public string BaseName { get; set; } = string.Empty;

        public string DisplayImageName => TruncateFileName(ImageFileName);
        public string DisplayVideoName => TruncateFileName(VideoFileName);

        // 更新的截断逻辑：前面保留22位，后面保留8位，中间用...
        private string TruncateFileName(string fileName)
        {
            if (string.IsNullOrEmpty(fileName)) return fileName;

            string ext = Path.GetExtension(fileName);
            string nameWithoutExt = Path.GetFileNameWithoutExtension(fileName);

            // 如果文件名（不含后缀）小于等于 30 位 (22 + 8)，则直接完整显示
            if (nameWithoutExt.Length <= 30) return fileName;

            string leftStr = nameWithoutExt.Substring(0, 22);
            string rightStr = nameWithoutExt.Substring(nameWithoutExt.Length - 8);

            return $"{leftStr}...{rightStr}{ext}";
        }
    }
}