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
    // 合并任务 — 表示一份照片/视频配对（图片 + 视频）需要合成为一份实况照片。
    // 支持 MVVM 属性变更通知和缩略图懒加载。
    public partial class MergeTask : ObservableObject, IMergeTaskInfo
    {
        #region Observable Properties

        // 任务在队列中的序号
        [ObservableProperty] private int _index;
        // 图片文件名
        [ObservableProperty] private string _imageFileName = string.Empty;
        // 视频文件名
        [ObservableProperty] private string _videoFileName = string.Empty;
        // 图片文件大小（格式化字符串，如 "1.2 MB"）
        [ObservableProperty] private string _imageSize = string.Empty;
        // 视频文件大小（格式化字符串，如 "3.5 MB"）
        [ObservableProperty] private string _videoSize = string.Empty;
        // 图片文件完整路径
        [ObservableProperty] private string _imagePath = string.Empty;
        // 视频文件完整路径
        [ObservableProperty] private string _videoPath = string.Empty;
        // 任务处理状态
        [ObservableProperty] private ProcessStatus _status = ProcessStatus.Pending;
        // 处理详情/错误信息
        [ObservableProperty] private string _details = string.Empty;

        #endregion

        #region Computed Properties

        // 任务失败且有错误详情时返回 true（用于 UI 显示错误图标）
        public bool HasErrorDetails => Status == ProcessStatus.Failed && !string.IsNullOrWhiteSpace(Details);

        // 图片原始大小（字节），用于排序
        public long ImageSizeBytes { get; set; }
        // 视频原始大小（字节），用于排序
        public long VideoSizeBytes { get; set; }
        // 图片拍摄日期（EXIF DateTimeOriginal），用于排序
        public DateTime DateTaken { get; set; }
        // 图片和视频的总大小（字节）
        public long TotalSizeBytes { get; set; }
        // 合并后输出文件的基本名称（不含扩展名）
        public string BaseName { get; set; } = string.Empty;

        // 截断后的图片显示名（过长时省略中间）
        public string DisplayImageName => FileNameFormatter.Truncate(ImageFileName);
        // 截断后的视频显示名（过长时省略中间）
        public string DisplayVideoName => FileNameFormatter.Truncate(VideoFileName);

        #endregion

        #region Thumbnail

        // 缩略图占位符可见性 — 缩略图未加载时显示默认图标
        public Visibility ThumbnailPlaceholderVisibility => ThumbnailService.GetPlaceholderVisibility(_thumbnail);

        private ImageSource? _thumbnail;

        // 任务缩略图（懒加载，优先使用缓存）。
        // TryGetOrLoad 内部用 IsBeingLoaded() 跟踪进行中的加载，
        // 不再需要外部 _isLoadingThumbnail 字段（内部字典有 finally 保证清理，不会卡住）。
        public ImageSource? Thumbnail
        {
            get => ThumbnailService.TryGetOrLoad(ImagePath, value => Thumbnail = value);
            set
            {
                if (SetProperty(ref _thumbnail, value))
                {
                    OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
                }
                // value 为 null 表示加载取消/失败。即使 _thumbnail 原本也是 null
                // （SetProperty 返回 false），也必须触发 PropertyChanged 让 x:Bind
                // 重新调用 getter → TryGetOrLoad 重试。
                if (value == null)
                {
                    OnPropertyChanged(nameof(Thumbnail));
                    OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
                }
            }
        }

        // 图片路径变更时重置缩略图加载状态
        partial void OnImagePathChanged(string value)
        {
            Thumbnail = null;
        }

        // 状态变更时刷新 HasErrorDetails
        partial void OnStatusChanged(ProcessStatus value)
        {
            OnPropertyChanged(nameof(HasErrorDetails));
        }

        // 详情变更时刷新 HasErrorDetails
        partial void OnDetailsChanged(string value)
        {
            OnPropertyChanged(nameof(HasErrorDetails));
        }

        #endregion

    }
}
