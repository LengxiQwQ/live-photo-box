/*
 * KeyPhotoViewModel.cs
 *
 * 实况照片主图更换页面的 ViewModel。
 * 管理任务队列、输入/输出目录、转换设置、
 * CommandBar 命令及底部状态栏的全部绑定数据。
 *
 * 对应 View：KeyPhotoPage
 * 继承：ViewModelBase
 */

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Models;
using Microsoft.UI.Xaml;
using System.Collections.ObjectModel;
using System.Linq;

namespace LivePhotoBox.ViewModels
{
    public partial class KeyPhotoViewModel : ViewModelBase
    {
        // ================================================================
        // 构造函数
        // ================================================================

        public KeyPhotoViewModel()
        {
#if DEBUG
            // 设计时示例数据：方便在 XAML 设计器中预览 UI 效果。
            // 发布版本中不会包含此段代码。
            PopulateDesignTimeData();
#endif
        }

        // ================================================================
        // 导航状态标签（当前不使用主窗口的 PageStatusBar）
        // ================================================================

        /// <inheritdoc/>
        public override string? PageStatusTag => null;

        // ================================================================
        // 输入 / 输出目录
        // ================================================================

        // 输入目录路径。
        [ObservableProperty]
        private string _inputDirectory = string.Empty;

        // 输出目录路径。
        [ObservableProperty]
        private string _outputDirectory = string.Empty;

        /// <summary>
        /// 浏览选择输入目录。
        /// 实际 FolderPicker 逻辑在 code-behind 中处理，
        /// ViewModel 仅提供命令入口和属性存储。
        /// </summary>
        [RelayCommand]
        private void BrowseInput() { }

        /// <summary>
        /// 浏览选择输出目录。
        /// </summary>
        [RelayCommand]
        private void BrowseOutput() { }

        /// <summary>
        /// 在资源管理器中打开输入目录。
        /// </summary>
        [RelayCommand]
        private void OpenInputDirectory()
        {
            if (!string.IsNullOrWhiteSpace(InputDirectory))
            {
                System.Diagnostics.Process.Start("explorer.exe", InputDirectory);
            }
        }

        /// <summary>
        /// 在资源管理器中打开输出目录。
        /// </summary>
        [RelayCommand]
        private void OpenOutputDirectory()
        {
            if (!string.IsNullOrWhiteSpace(OutputDirectory))
            {
                System.Diagnostics.Process.Start("explorer.exe", OutputDirectory);
            }
        }

        // ================================================================
        // 转换设置
        // ================================================================

        // 转换协议索引：0=MicroVideo V1, 1=MotionPhoto V2, 2=O-Live Photo。
        [ObservableProperty]
        private int _selectedProtocolIndex;

        // 输出格式索引：0=HEIC, 1=JPEG。
        [ObservableProperty]
        private int _selectedOutputFormatIndex;

        // 命名规则索引：0=保持原名, 1=添加后缀 "_modified"。
        [ObservableProperty]
        private int _selectedNamingRuleIndex;

        // 是否保留原文件（不删除原始实况照片）。
        [ObservableProperty]
        private bool _keepOriginalFile = true;

        // 输出目录存在同名文件时是否自动覆盖。
        [ObservableProperty]
        private bool _autoOverwrite;

        // 全部转换完成后是否自动打开输出目录。
        [ObservableProperty]
        private bool _openDirectoryOnComplete = true;

        // ================================================================
        // 统计信息（属性变更时级联通知）
        // ================================================================

        [ObservableProperty]
        private int _totalFiles;

        [ObservableProperty]
        private int _waitingCount;

        [ObservableProperty]
        private int _processingCount;

        [ObservableProperty]
        private int _successCount;

        [ObservableProperty]
        private int _failedCount;

        // ================================================================
        // 任务队列
        // ================================================================

        // 任务集合，绑定到 ListView.ItemsSource。
        public ObservableCollection<KeyPhotoTask> Tasks { get; } = new();

        // 队列是否为空（用于显示空状态占位）。
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(EmptyQueueVisibility))]
        private bool _isQueueEmpty = true;

        // 空队列占位提示的可见性（IsQueueEmpty → Visible）。
        public Visibility EmptyQueueVisibility =>
            IsQueueEmpty ? Visibility.Visible : Visibility.Collapsed;

        // ================================================================
        // CommandBar 命令
        // ================================================================

        // 是否正在处理中（控制开始/暂停/停止按钮的 IsEnabled）。
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(CanStart))]
        [NotifyPropertyChangedFor(nameof(CanPause))]
        [NotifyPropertyChangedFor(nameof(CanStop))]
        private bool _isProcessing;

        // 是否已暂停。
        [ObservableProperty]
        private bool _isPaused;

        /// <summary>
        /// 是否可以开始转换：未在处理中且队列非空。
        /// </summary>
        public bool CanStart => !IsProcessing && !IsQueueEmpty;

        /// <summary>
        /// 是否可以暂停：正在处理中且未暂停。
        /// </summary>
        public bool CanPause => IsProcessing && !IsPaused;

        /// <summary>
        /// 是否可以停止：正在处理中。
        /// </summary>
        public bool CanStop => IsProcessing;

        /// <summary>
        /// 添加文件命令 — 打开文件选择器，将选中的实况照片加入队列。
        /// 实际 FileOpenPicker 逻辑在 code-behind 中处理。
        /// </summary>
        [RelayCommand]
        private void AddFile() { }

        /// <summary>
        /// 添加文件夹命令 — 打开文件夹选择器，扫描目录下所有实况照片并加入队列。
        /// </summary>
        [RelayCommand]
        private void AddFolder() { }

        /// <summary>
        /// 开始转换 — 逐个处理队列中的任务。
        /// </summary>
        [RelayCommand]
        private void StartConvert()
        {
            IsProcessing = true;
            IsPaused = false;
            StatusText = "Processing...";
        }

        /// <summary>
        /// 暂停转换 — 处理完当前任务后暂停。
        /// </summary>
        [RelayCommand]
        private void Pause()
        {
            IsPaused = true;
            StatusText = "Paused";
        }

        /// <summary>
        /// 停止转换 — 中止所有处理。
        /// </summary>
        [RelayCommand]
        private void Stop()
        {
            IsProcessing = false;
            IsPaused = false;
            StatusText = "Stopped";
        }

        /// <summary>
        /// 清空队列 — 移除所有任务（仅在未处理时可用）。
        /// 处理中时不可清空，需先停止。
        /// </summary>
        [RelayCommand]
        private void ClearQueue()
        {
            Tasks.Clear();
            IsQueueEmpty = true;
            RefreshStatistics();
        }

        // ================================================================
        // 底部状态栏
        // ================================================================

        // 状态文本，如 "Ready" / "Processing..." / "Paused" / "Stopped"。
        [ObservableProperty]
        private string _statusText = "Ready";

        // 整体进度（0.0 ~ 100.0）。
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OverallProgressBarValue))]
        private double _overallProgress;

        // 传递给 ProgressBar 控件的值。
        public double OverallProgressBarValue => OverallProgress;

        // 预计剩余时间格式化文本，如 "00:01:35"。
        [ObservableProperty]
        private string _remainingTime = string.Empty;

        // ================================================================
        // 辅助方法
        // ================================================================

        /// <summary>
        /// 从 Tasks 集合重新计算统计信息。
        /// </summary>
        private void RefreshStatistics()
        {
            TotalFiles = Tasks.Count;
            WaitingCount = Tasks.Count(t => t.Status == ProcessStatus.Pending);
            ProcessingCount = Tasks.Count(t => t.Status == ProcessStatus.Processing);
            SuccessCount = Tasks.Count(t => t.Status == ProcessStatus.Success);
            FailedCount = Tasks.Count(t => t.Status == ProcessStatus.Failed);
        }

        // ================================================================
        // 设计时示例数据（仅 DEBUG 构建）
        // ================================================================

#if DEBUG
        /// <summary>
        /// 创建设计时示例任务数据，方便在 Visual Studio XAML 设计器中预览 UI 效果。
        /// 发布版本中此方法不会被编译。
        /// </summary>
        private void PopulateDesignTimeData()
        {
            InputDirectory = @"C:\Users\Example\Pictures\LivePhotos";
            OutputDirectory = @"C:\Users\Example\Pictures\Output";

            Tasks.Add(new KeyPhotoTask
            {
                Index = 1,
                FileName = "IMG_8842.HEIC",
                FilePath = @"C:\Users\Example\Pictures\LivePhotos\IMG_8842.HEIC",
                FileSize = "6.3 MB",
                Status = ProcessStatus.Pending,
                Progress = 0,
                StatusText = "Waiting..."
            });

            Tasks.Add(new KeyPhotoTask
            {
                Index = 2,
                FileName = "IMG_8843.HEIC",
                FilePath = @"C:\Users\Example\Pictures\LivePhotos\IMG_8843.HEIC",
                FileSize = "5.1 MB",
                Status = ProcessStatus.Processing,
                Progress = 45,
                ElapsedTime = "00:02",
                RemainingTime = "00:03",
                StatusText = "Processing..."
            });

            Tasks.Add(new KeyPhotoTask
            {
                Index = 3,
                FileName = "IMG_8844.HEIC",
                FilePath = @"C:\Users\Example\Pictures\LivePhotos\IMG_8844.HEIC",
                FileSize = "8.7 MB",
                Status = ProcessStatus.Success,
                Progress = 100,
                ElapsedTime = "00:05",
                StatusText = "Done"
            });

            Tasks.Add(new KeyPhotoTask
            {
                Index = 4,
                FileName = "VID_2024_01.MOV",
                FilePath = @"C:\Users\Example\Pictures\LivePhotos\VID_2024_01.MOV",
                FileSize = "12.4 MB",
                Status = ProcessStatus.Failed,
                Progress = 78,
                ElapsedTime = "00:08",
                StatusText = "Failed"
            });

            IsQueueEmpty = Tasks.Count == 0;
            RefreshStatistics();
        }
#endif
    }
}
