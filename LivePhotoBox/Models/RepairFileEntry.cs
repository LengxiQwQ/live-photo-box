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
    // 修复队列中的单个文件条目 — 表示一个待诊断/修复的照片或视频。
    // 一个 RepairTask（格子）包含 1 个（单独文件）或 2 个（配对实况照片）RepairFileEntry。
    public partial class RepairFileEntry : ObservableObject
    {
        #region Observable Properties

        // 文件名
        [ObservableProperty] private string _fileName = string.Empty;
        // 文件完整路径
        [ObservableProperty] private string _filePath = string.Empty;
        // 处理状态
        [ObservableProperty] private ProcessStatus _status = ProcessStatus.Pending;
        // 问题描述文本
        [ObservableProperty] private string _issueDescription = string.Empty;
        // 是否需要修复
        [ObservableProperty] private bool _needsRepair = false;
        // 详细错误信息
        [ObservableProperty] private string _details = string.Empty;
        // 是否被用户取消（保留 Processing 状态显示、颜色中性，但计入底部"已取消"统计）
        [ObservableProperty] private bool _isCancelled;
        // true=照片, false=视频（决定图标和缩略图展示）
        [ObservableProperty] private bool _isImage = true;

        #endregion

        #region Data Properties

        // 诊断分析结果（由诊断步骤填充）
        public RepairAnalysisResult? AnalysisResult { get; set; }

        #endregion

        #region Thumbnail

        private bool _isLoadingThumbnail = false;
        private ImageSource? _thumbnail;

        // 文件缩略图（支持 UI 线程切换，自动加载缓存）
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

        // 缩略图占位符可见性 — 缩略图未加载时显示默认图标
        public Visibility ThumbnailPlaceholderVisibility => Thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        // 文件路径变更时重置缩略图，尝试从缓存或异步加载
        partial void OnFilePathChanged(string value)
        {
            _isLoadingThumbnail = false;
            Thumbnail = ThumbnailService.GetCached(value);

            if (Thumbnail == null && !string.IsNullOrWhiteSpace(value))
            {
                if (ThumbnailService.IsVideoFilePath(value))
                {
                    // 设置开关：关 = 扫描时不加载视频（由 ContainerContentChanging 可见时加载）
                    bool loadScan = AppSettingsService.GetValue("IsRepairScanLoadThumbnail", false);
                    if (loadScan)
                        ThumbnailService.BackgroundVideoLoad(value, App.MainWindow?.DispatcherQueue);
                    return;
                }

                var dispatcher = App.MainWindow?.DispatcherQueue;
                if (dispatcher != null)
                {
                    _ = AutoLoadThumbnailAsync(value, dispatcher);
                }
            }
        }

        // 异步加载缩略图（确保同一时间只有一个加载操作）
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

        // 确保缩略图已加载（有缓存则直接使用，否则异步加载）
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

        // 截断后的文件名（过长时省略中间）
        public string DisplayFileName => FileNameFormatter.Truncate(FileName);

        // 用于 UI 显示的最终状态。
        // 无需修复的文件直接视为成功（绿色），避免依赖多语言字符串比较。
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

        // 任务失败且有错误详情时返回 true（用于 UI 显示错误图标）
        public bool HasErrorDetails => Status == ProcessStatus.Failed && !string.IsNullOrWhiteSpace(Details);

        // 诊断阶段报错 → 诊断结果文字标红 + 可点击查看详情
        public bool IsDiagnosisError => AnalysisResult?.IssueType == RepairIssueType.Error;

        // 详情变更时刷新 DisplayStatus 和 HasErrorDetails
        partial void OnDetailsChanged(string value)
        {
            OnPropertyChanged(nameof(DisplayStatus));
            OnPropertyChanged(nameof(HasErrorDetails));
        }

        // 状态变更时刷新 DisplayStatus 和 HasErrorDetails
        partial void OnStatusChanged(ProcessStatus value)
        {
            OnPropertyChanged(nameof(DisplayStatus));
            OnPropertyChanged(nameof(HasErrorDetails));
        }

        #endregion
    }
}
