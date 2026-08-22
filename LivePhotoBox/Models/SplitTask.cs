using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoBox.Helpers;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;

namespace LivePhotoBox.Models
{
    // 拆分任务 — 表示一个待拆分的实况照片文件。
    // 支持 MVVM 属性变更通知、缩略图懒加载和状态显示。
    public partial class SplitTask : ObservableObject
    {
        #region Observable Properties

        // 任务在队列中的序号
        [ObservableProperty] private int _index;
        // 源文件（实况照片）的文件名
        [ObservableProperty] private string _sourceFileName = string.Empty;
        // 源文件完整路径
        [ObservableProperty] private string _sourcePath = string.Empty;
        // 源文件大小（格式化字符串）
        [ObservableProperty] private string _fileSize = string.Empty;
        // 进度文本（如 "50%"）
        [ObservableProperty] private string _progressText = "0%";
        // 处理状态
        [ObservableProperty] private ProcessStatus _status = ProcessStatus.Pending;
        // 处理详情/错误信息
        [ObservableProperty] private string _details = string.Empty;
        // 是否被用户取消（保留 Processing 状态显示、颜色中性，但计入底部"已取消"统计）
        [ObservableProperty] private bool _isCancelled;

        #endregion

        #region Computed Properties

        // 任务失败且有错误详情时返回 true
        public bool HasErrorDetails =>
            (Status is ProcessStatus.Failed or ProcessStatus.SelfCheckFailed)
            && !string.IsNullOrWhiteSpace(Details);

        // 队列中显示的状态文字：失败时只显示"拆分失败"（简短、不截断），
        // 完整错误详情在点击后弹出的 TeachingTip 中查看；其余状态沿用原详情文案。
        public string DisplayStatusText => Status switch
        {
            ProcessStatus.Failed => ResourceService.GetString("SplitPage_Task_Failed"),
            ProcessStatus.SelfCheckFailed => ResourceService.GetString("SplitPage_Task_SelfCheckFailed"),
            _ => Details,
        };

        // 源文件原始大小（字节），用于排序
        public long FileSizeBytes { get; set; }
        // 源文件拍摄日期（EXIF DateTimeOriginal），用于排序
        public DateTime DateTaken { get; set; }
        // 拆分后输出文件的基本名称（不含扩展名，由命名模板渲染）
        public string BaseName { get; set; } = string.Empty;

        // 追加视频段长度（扫描时填入，灯箱直接读取，避免二次 IO）
        public long AppendedVideoLength { get; set; }

        #endregion

        #region Thumbnail

        private ImageSource? _thumbnail;

        // 源文件缩略图（懒加载，优先使用缓存）。
        // TryGetOrLoad 内部用 IsBeingLoaded() 跟踪进行中的加载，
        // 不再需要外部 _isLoadingThumbnail 字段（内部字典有 finally 保证清理，不会卡住）。
        public ImageSource? Thumbnail
        {
            get => ThumbnailService.TryGetOrLoad(SourcePath, value => Thumbnail = value);
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

        // 缩略图占位符可见性 — 缩略图未加载时显示默认图标
        public Visibility ThumbnailPlaceholderVisibility => ThumbnailService.GetPlaceholderVisibility(_thumbnail);

        // 源路径变更时重置缩略图加载状态
        partial void OnSourcePathChanged(string value)
        {
            Thumbnail = null;
        }

        // 确保缩略图已加载（当前实现为触发 getter 后即返回）
        public Task EnsureThumbnailAsync(Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
        {
            var trigger = Thumbnail;
            return Task.CompletedTask;
        }

        // 取消正在进行的缩略图加载（当前实现为空操作）
        public void CancelThumbnailLoad()
        {
        }

        // 状态变更时刷新 DisplayStatus 和 HasErrorDetails
        partial void OnStatusChanged(ProcessStatus value)
        {
            OnPropertyChanged(nameof(DisplayStatus));
            OnPropertyChanged(nameof(HasErrorDetails));
            OnPropertyChanged(nameof(DisplayStatusText));
        }

        // 详情变更时刷新 DisplayStatus 和 HasErrorDetails
        partial void OnDetailsChanged(string value)
        {
            OnPropertyChanged(nameof(DisplayStatus));
            OnPropertyChanged(nameof(HasErrorDetails));
            OnPropertyChanged(nameof(DisplayStatusText));
        }

        #endregion

        #region Display

        // 截断后的源文件名（过长时省略中间，沿用 MergeTask 的截断方式）
        public string DisplaySourceFileName => FileNameFormatter.Truncate(SourceFileName);

        // 用于 UI 显示的本地化状态文本
        public string DisplayStatus
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Details))
                    return Details;

                return Status switch
                {
                    ProcessStatus.Pending => ResourceService.GetString("SplitPage_Task_Pending"),
                    ProcessStatus.Processing => ResourceService.GetString("SplitPage_Task_Processing"),
                    ProcessStatus.Success => ResourceService.GetString("SplitPage_Task_Success"),
                    ProcessStatus.Failed => ResourceService.GetString("Task_Failed"),
                    _ => Status.ToString()
                };
            }
        }

        #endregion
    }
}
