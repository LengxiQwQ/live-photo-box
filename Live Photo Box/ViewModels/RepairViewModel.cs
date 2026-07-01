using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Collections;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.ViewModels
{
    // 实况照片修复页面的 ViewModel。
    // 负责扫描输入目录中的实况照片（JPG/HEIC + MOV/MP4 配对），
    // 分析每对文件的元数据完整性（缩略图、ContentIdentifier、视频时长等），
    // 并对需要修复的文件执行修复操作（原地替换或输出到独立目录）。
    // 继承自 WorkViewModelBase，复用扫描/处理/暂停/取消等生命周期管理。
    public partial class RepairViewModel : WorkViewModelBase
    {
        // 文件修复处理中的最短显示持续时间，避免进度闪烁。
        private static readonly TimeSpan MinimumProcessingDisplayDuration = TimeSpan.FromMilliseconds(100);

        // 导航栏状态标签。
        public override string PageStatusTag => "Repair";

        // 处理中状态的多语言资源键。
        protected override string ProcessingStatusKey => "Status_Running";

        // 输入目录路径。赋值后自动触发扫描（若当前允许）。
        [ObservableProperty]
        private string _inputDirectory = string.Empty;

        partial void OnInputDirectoryChanged(string value)
        {
            _openRepairInputFolderCommand?.NotifyCanExecuteChanged();
            _openRepairOutputFolderCommand?.NotifyCanExecuteChanged();
            OutputDirectory = string.Empty;

            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            {
                if (ScanDirectoryCommand.CanExecute(null) && !IsScanning)
                {
                    ScanDirectoryCommand.Execute(null);
                }
            }
        }

        // 修复输出目录路径（仅在 IsOutputToDirectory 为 true 时使用）。
        [ObservableProperty]
        private string _outputDirectory = string.Empty;

        partial void OnOutputDirectoryChanged(string value) => _openRepairOutputFolderCommand?.NotifyCanExecuteChanged();

        // 是否输出到独立目录。true 时修复结果保存到 OutputDirectory；false 时原地替换。
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OutputGridVisibility))]
        [NotifyPropertyChangedFor(nameof(InputLabelVisibility))]
        [NotifyPropertyChangedFor(nameof(InputOutputLabelVisibility))]
        private bool _isOutputToDirectory = false;

        private bool _previousIsOutputToDirectory = false;

        partial void OnIsOutputToDirectoryChanged(bool value)
        {
            AppSettingsService.SetValue(nameof(IsOutputToDirectory), value);
            _openRepairOutputFolderCommand?.NotifyCanExecuteChanged();

            bool turnedOn = value && !_previousIsOutputToDirectory;
            _previousIsOutputToDirectory = value;

            if (turnedOn && !IsDirectoryPanelOpen)
            {
                IsDirectoryPanelOpen = true;
            }

            if (value)
            {
                LogService.Repair($"Output to separate directory enabled");
                if (string.IsNullOrWhiteSpace(OutputDirectory) && !string.IsNullOrWhiteSpace(InputDirectory) && Directory.Exists(InputDirectory))
                {
                    OutputDirectory = Path.Combine(InputDirectory, ResourceService.GetString("OutputDir_RepairedPhotos"));
                    LogService.Repair($"Output directory auto-set to: {OutputDirectory}");
                }
            }
            else
            {
                LogService.Repair("Output to separate directory disabled (repairs in-place)");
            }
        }

        // 输出目录网格的可见性（启用独立目录时显示）。
        public Visibility OutputGridVisibility =>
            IsOutputToDirectory ? Visibility.Visible : Visibility.Collapsed;

        // 输入标签的可见性（启用独立目录时作为左标签显示）。
        public Visibility InputLabelVisibility =>
            IsOutputToDirectory ? Visibility.Visible : Visibility.Collapsed;

        // 输入/输出合并标签的可见性（原地替换时显示"输入/输出"）。
        public Visibility InputOutputLabelVisibility =>
            IsOutputToDirectory ? Visibility.Collapsed : Visibility.Visible;

        // 扫描到的文件总数（按 Entry 维度计数）。
        [ObservableProperty]
        private int _totalPhotosCount = 0;

        // 文件名配对分析无需修复的文件数。
        [ObservableProperty]
        private int _thumbCorrectCount = 0;

        // 文件名配对分析需要修复的文件数。
        [ObservableProperty]
        private int _thumbErrorCount = 0;

        // 配对成功的实况照片组数（新增统计）
        [ObservableProperty]
        private int _totalPairsCount = 0;

        // 单独照片数（匹配不到视频的孤立照片）
        [ObservableProperty]
        private int _standaloneImagesCount = 0;

        // 单独视频数（匹配不到照片的孤立视频）
        [ObservableProperty]
        private int _standaloneVideosCount = 0;

        // 目录展开面板（输入/输出路径选择区域）是否打开。
        [ObservableProperty]
        private bool _isDirectoryPanelOpen = true;

        // 扫描按钮上的动态文本：扫描中显示"取消"，否则显示"扫描"。
        public string ScanButtonText => IsScanning
            ? ResourceService.GetString("RepairPage_DynamicCancelText")
            : ResourceService.GetString("RepairPage_DynamicScanText");

        // 所有扫描得到的修复任务集合，包含配对和独立项。
        public BulkObservableCollection<RepairTask> Tasks { get; } = [];

        // 筛选后队列（ListView 实际绑定此集合）。
        public BulkObservableCollection<RepairTask> FilteredTasks { get; } = [];

        // 筛选栏可见性 — 有任务时才显示。
        public Visibility FilterBarVisibility => Tasks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        #region Filter

        // 修复状态筛选：0=全部, 1=仅待修复, 2=仅完好
        [ObservableProperty]
        private int _repairStatusFilter;

        partial void OnRepairStatusFilterChanged(int value)
        {
            ApplyFilter();
            OnPropertyChanged(nameof(ViewGroupVisibility));
            OnPropertyChanged(nameof(CombinedFilterText));
        }

        [ObservableProperty]
        private int _filterMode;

        partial void OnFilterModeChanged(int value)
        {
            ApplyFilter();
            OnPropertyChanged(nameof(ViewGroupVisibility));
            OnPropertyChanged(nameof(CombinedFilterText));
        }

        [ObservableProperty]
        private bool _isFilterEnabled;

        partial void OnIsFilterEnabledChanged(bool value)
        {
            if (!value)
            {
                FilterMode = 0;
                RepairStatusFilter = 0;
                FilteredTasks.ReplaceRange([..Tasks]);
            }
            OnPropertyChanged(nameof(ViewGroupVisibility));
        }

        // 合并筛选按钮上显示的文本：例如"实况照片 · 仅待修复"
        public string CombinedFilterText
        {
            get
            {
                string typeText = FilterMode switch
                {
                    1 => ResourceService.GetString("RepairPage_FilterPairs"),
                    2 => ResourceService.GetString("RepairPage_FilterStandaloneImg"),
                    3 => ResourceService.GetString("RepairPage_FilterStandaloneVid"),
                    _ => ResourceService.GetString("RepairPage_FilterAll"),
                };
                string statusText = RepairStatusFilter switch
                {
                    1 => ResourceService.GetString("RepairPage_FilterStatusRepair"),
                    2 => ResourceService.GetString("RepairPage_FilterStatusPerfect"),
                    _ => ResourceService.GetString("RepairPage_FilterStatusAll"),
                };
                return $"{typeText}  •  {statusText}";
            }
        }

        // 设置类型筛选（实况照片组合/单独照片/单独视频）。
        [RelayCommand]
        private void SetTypeFilter(object parameter)
        {
            if (parameter is int i) FilterMode = i;
            else if (parameter is string s && int.TryParse(s, out var r)) FilterMode = r;
        }

        // 设置状态筛选（全部/仅待修复/仅完好）。
        [RelayCommand]
        private void SetStatusFilter(object parameter)
        {
            if (parameter is int i) RepairStatusFilter = i;
            else if (parameter is string s && int.TryParse(s, out var r)) RepairStatusFilter = r;
        }

        // 重置筛选条件到默认（全部）。
        [RelayCommand]
        private void ResetFilter()
        {
            FilterMode = 0;
            RepairStatusFilter = 0;
        }

        // 当前浏览的分组名称（由滚动位置决定），如"实况照片组合"
        private string _currentViewGroup = string.Empty;
        public string CurrentViewGroup
        {
            get => _currentViewGroup;
            set
            {
                if (SetProperty(ref _currentViewGroup, value))
                    OnPropertyChanged(nameof(CurrentViewGroupText));
            }
        }

        // "当前显示：实况照片组合" 之类的完整文本
        public string CurrentViewGroupText
        {
            get
            {
                if (string.IsNullOrEmpty(CurrentViewGroup)) return string.Empty;
                string label = ResourceService.GetString("RepairPage_ShowingLabel");
                return $"{label} {CurrentViewGroup}";
            }
        }

        // 分组标签可见性 — 仅"全部"、有任务、非扫描中时显示
        public Visibility ViewGroupVisibility => FilterMode == 0 && Tasks.Count > 0 && !IsScanning ? Visibility.Visible : Visibility.Collapsed;

        // 根据任务确定其所属分组名称
        public static string GetTaskGroupName(RepairTask task)
        {
            if (task.IsPaired) return ResourceService.GetString("RepairPage_GroupHeaderPairs");
            if (task.File1IsImage) return ResourceService.GetString("RepairPage_GroupHeaderStandaloneImg");
            return ResourceService.GetString("RepairPage_GroupHeaderStandaloneVid");
        }

        private void ApplyFilter()
        {
            // 全部 → 恢复原始序号（扫描时的自然顺序）
            if (FilterMode == 0 && RepairStatusFilter == 0)
            {
                int fileSeq = 1;
                for (int i = 0; i < Tasks.Count; i++)
                {
                    var task = Tasks[i];
                    task.Index = i + 1;
                    task.File1Index = fileSeq++;
                    if (task.File2Entry != null)
                        task.File2Index = fileSeq++;
                    else
                        task.File2Index = 0;
                }
                FilteredTasks.ReplaceRange([..Tasks]);
                OnPropertyChanged(nameof(ViewGroupVisibility));
                return;
            }

            List<RepairTask> result = FilterMode switch
            {
                // 实况照片组合 → 仅配对项（一个格子里有两个文件）
                1 => Tasks.Where(t => t.IsPaired).ToList(),
                // 单独照片 → 仅含一个文件且为图片（排除实况照片中的图片）
                2 => Tasks.Where(t => t.Entries.Count == 1 && t.File1IsImage).ToList(),
                // 单独视频 → 仅含一个文件且为视频（排除实况照片中的视频）
                3 => Tasks.Where(t => t.Entries.Count == 1 && !t.File1IsImage).ToList(),
                _ => [..Tasks],
            };

            // 状态筛选：按修复状态过滤
            if (RepairStatusFilter == 1)
            {
                // 仅待修复：至少有一个 entry 需要修复
                result = result.Where(t => t.Entries.Any(e => e.NeedsRepair)).ToList();
            }
            else if (RepairStatusFilter == 2)
            {
                // 仅完好：所有 entry 都不需要修复
                result = result.Where(t => t.Entries.All(e => !e.NeedsRepair)).ToList();
            }

            // 重新编号：不管筛选哪个分类，序号始终从 1 开始
            int seq = 1;
            for (int i = 0; i < result.Count; i++)
            {
                var task = result[i];
                task.Index = i + 1;
                task.File1Index = seq++;
                if (task.File2Entry != null)
                    task.File2Index = seq++;
                else
                    task.File2Index = 0;
            }

            FilteredTasks.ReplaceRange(result);
            OnPropertyChanged(nameof(ViewGroupVisibility));
        }

        // 在扫描/处理状态切换时调用，更新 IsFilterEnabled 和筛选状态
        private void UpdateFilterEnabled()
        {
            bool canFilter = !IsScanning && !IsProcessing && Tasks.Count > 0;
            if (canFilter != IsFilterEnabled)
            {
                IsFilterEnabled = canFilter;
            }
            OnPropertyChanged(nameof(FilterBarVisibility));
        }

        #endregion

        // 打开输入文件夹命令的后备字段。
        private IAsyncRelayCommand? _openRepairInputFolderCommand;
        // 打开输出文件夹命令的后备字段。
        private IRelayCommand? _openRepairOutputFolderCommand;

        // 在文件管理器中打开输入文件夹的命令。
        public IAsyncRelayCommand OpenRepairInputFolderCommand => _openRepairInputFolderCommand ??= new AsyncRelayCommand(OpenRepairInputFolderAsync, () => !string.IsNullOrWhiteSpace(InputDirectory));
        // 在文件管理器中打开输出文件夹的命令。
        public IRelayCommand OpenRepairOutputFolderCommand => _openRepairOutputFolderCommand ??= new RelayCommand(OpenRepairOutputFolder, CanOpenRepairOutputFolder);

        // 初始化 RepairViewModel，加载设置并启动 UI 更新定时器。
        public RepairViewModel()
        {
            SetStatus("RepairPage_Status_Ready");
            _isOutputToDirectory = AppSettingsService.GetValue(nameof(IsOutputToDirectory), false);
            _previousIsOutputToDirectory = _isOutputToDirectory;
            _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _uiUpdateTimer.Tick += UiUpdateTimer_Tick;
        }

        protected override string ProcessingStatusText =>
            ResourceService.Format("Status_Running") + GetHardwareSuffix();

        protected override void OnScanStateChanged(bool isScanning)
        {
            OnPropertyChanged(nameof(ScanButtonText));
            UpdateFilterEnabled();
        }

        protected override void OnBeginScanSession()
        {
            AppViewModel.Instance.BeginRepairScanSession();
        }

        protected override void OnApplyScanProgress(WorkProgressSnapshot snapshot)
        {
            AppViewModel.Instance.ApplyRepairScanProgress(snapshot);
        }

        protected override void OnCompleteScanSnapshot()
        {
            AppViewModel.Instance.CompleteFooterWorkSnapshot();
        }

        private void UiUpdateTimer_Tick(object? sender, object e)
        {
            if (_totalRepairEntries == 0) return;
            int currentCompleted = _completedEntriesCount;
            Progress = (currentCompleted * 100.0) / _totalRepairEntries;
            ProgressText = $"{currentCompleted}/{_totalRepairEntries}";
            CheckAndApplyPendingState();
        }

        protected override void OnInitializeRunState()
        {
            _repairStoppedByUser = false;
            _repairDone = false;

            // 进度按 Entry（文件）算，不是按 Task（格子）算 — 配对格子里的两个文件各算一个
            _completedEntriesCount = 0;
            bool copyPerfect = IsOutputToDirectory && AppSettingsService.GetValue("IsCopyPerfectToOutput", false);
            var allRepairEntries = Tasks.SelectMany(t => t.Entries)
                .Where(e => e.Status != ProcessStatus.Success)
                .Where(e => e.NeedsRepair || (copyPerfect && e.AnalysisResult?.IssueType == RepairIssueType.Perfect))
                .ToList();
            _totalRepairEntries = allRepairEntries.Count;

            Progress = 0;
            ProgressText = _totalRepairEntries == 0 ? "0/0" : $"0/{_totalRepairEntries}";
            _taskProcessingStartTimes.Clear();

            SetDirectStatus(ProcessingStatusText);
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
            _uiUpdateTimer.Start();
        }

        protected override void OnFinalizeRunState()
        {
            _uiUpdateTimer.Stop();
            _taskProcessingStartTimes.Clear();

            if (_cancelledByUser)
            {
                _repairStoppedByUser = true;
            }
            else
            {
                _repairDone = true;

                if (_totalRepairEntries > 0)
                {
                    Progress = (_completedEntriesCount * 100.0) / _totalRepairEntries;
                    ProgressText = $"{_completedEntriesCount}/{_totalRepairEntries}";
                }

                if (_totalRepairEntries == 0 || Progress >= 100)
                {
                    ProgressBarState = Models.ProgressBarState.Success;
                    CompleteScanSnapshot();

                    int totalEntries = Tasks.Sum(t => t.Entries.Count);
                    int succeeded = Tasks.SelectMany(t => t.Entries)
                        .Count(e => e.Status == ProcessStatus.Success && (e.AnalysisResult == null || e.AnalysisResult.IssueType != RepairIssueType.Perfect));
                    int skipped = Tasks.SelectMany(t => t.Entries)
                        .Count(e => e.AnalysisResult != null && e.AnalysisResult.IssueType == RepairIssueType.Perfect);
                    int failed = Tasks.SelectMany(t => t.Entries)
                        .Count(e => e.Status == ProcessStatus.Failed);
                    double elapsed = _stopwatch.Elapsed.TotalSeconds;

                    SetStatus("Status_RepairCompletedSummary", totalEntries, elapsed, succeeded, skipped, failed);
                    LogService.Repair($"Repair completed: {succeeded} repaired, {skipped} skipped, {failed} failed in {elapsed:F1}s");
                }
            }
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
        }

        protected override void OnClearState()
        {
            Tasks.ReplaceRange([]);
            FilteredTasks.ReplaceRange([]);
            TotalPhotosCount = 0;
            ThumbCorrectCount = 0;
            ThumbErrorCount = 0;
            TotalPairsCount = 0;
            StandaloneImagesCount = 0;
            StandaloneVideosCount = 0;
            _completedEntriesCount = 0;
            _totalRepairEntries = 0;
            Progress = 0;
            ProgressText = "0/0";
            _repairStoppedByUser = false;
            _repairDone = false;
            _scanCancelledByUser = false;
            _taskProcessingStartTimes.Clear();
            SetStatus("RepairPage_Status_Cleared");
            IsDirectoryPanelOpen = true;
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
            UpdateFilterEnabled();
        }

        protected override void OnCleanup()
        {
            _uiUpdateTimer.Stop();
            _uiUpdateTimer.Tick -= UiUpdateTimer_Tick;
            Tasks.ReplaceRange([]);
            FilteredTasks.ReplaceRange([]);
            ThumbnailService.ClearCache();
        }

        protected override void OnScanningEnded()
        {
            base.OnScanningEnded();
            _scanCancellationTokenSource?.Dispose();
            _scanCancellationTokenSource = null;
            OnPropertyChanged(nameof(IsProcessingAllowed));
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(ViewGroupVisibility));
            UpdateFilterEnabled();
        }

        // 修复处理计时器。
        private Stopwatch _stopwatch = new();
        // 是否被用户手动停止。
        private bool _repairStoppedByUser;
        // 是否自然完成。
        private bool _repairDone;
        // 每个 Entry 的处理开始时间，用于保证最短显示时长。
        private readonly Dictionary<RepairFileEntry, DateTimeOffset> _taskProcessingStartTimes = new();
        // UI 更新定时器（60ms 间隔），用于刷新进度条和文本。
        private readonly DispatcherTimer _uiUpdateTimer;
        // 已完成处理的 Entry 计数（线程安全，volatile）。
        private volatile int _completedEntriesCount;
        // 待修复的 Entry 总数（按文件计数，配对格子里两个文件各算一个）。
        private int _totalRepairEntries;

        public override string ActionBtnText
        {
            get
            {
                if (IsProcessing)
                {
                    if (_cancelledByUser) return ResourceService.GetString("Btn_Stopping");
                    return ResourceService.GetString("Btn_Stop");
                }
                return ResourceService.GetString("Btn_StartRepair");
            }
        }

        public override bool IsProcessingAllowed => !IsScanning;

        // 弹出一个 ContentDialog 窗口展示修复被取消时的汇总信息。
        private async Task ShowRepairCancelledDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                int total = Tasks.Sum(t => t.Entries.Count);
                int succeeded = Tasks.SelectMany(t => t.Entries)
                    .Count(e => e.Status == ProcessStatus.Success && (e.AnalysisResult == null || e.AnalysisResult.IssueType != RepairIssueType.Perfect));
                int skipped = Tasks.SelectMany(t => t.Entries)
                    .Count(e => e.AnalysisResult != null && e.AnalysisResult.IssueType == RepairIssueType.Perfect);
                int failed = Tasks.SelectMany(t => t.Entries)
                    .Count(e => e.Status == ProcessStatus.Failed);
                int unprocessed = total - succeeded - skipped - failed;

                var stack = new StackPanel { Spacing = 12 };
                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.Format("Msg_RepairCancelledSummary", total, succeeded, skipped, failed, unprocessed),
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap
                });
                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.GetString("Msg_RepairCompletedDescription"),
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 12, 0, 0),
                    Opacity = 0.85
                });

                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString("Msg_TaskCancelledTitle"),
                    Content = stack,
                    PrimaryButtonText = ResourceService.GetString("Msg_OpenOutputFolder"),
                    CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = App.MainWindow.Content.XamlRoot,
                    RequestedTheme = App.CurrentTheme
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    OpenRepairOutputFolder();
                }
            }
        }

        // 弹出一个 ContentDialog 窗口展示修复已全部完成时的汇总信息。
        private async Task ShowRepairAlreadyDoneDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                int total = Tasks.Sum(t => t.Entries.Count);
                int succeeded = Tasks.SelectMany(t => t.Entries)
                    .Count(e => e.Status == ProcessStatus.Success && (e.AnalysisResult == null || e.AnalysisResult.IssueType != RepairIssueType.Perfect));
                int skipped = Tasks.SelectMany(t => t.Entries)
                    .Count(e => e.AnalysisResult != null && e.AnalysisResult.IssueType == RepairIssueType.Perfect);
                int failed = Tasks.SelectMany(t => t.Entries)
                    .Count(e => e.Status == ProcessStatus.Failed);

                var stack = new StackPanel { Spacing = 12 };
                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.Format("Msg_RepairCompletedSummary", total, succeeded, skipped, failed),
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap
                });
                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.GetString("Msg_RepairCompletedDescription"),
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 12, 0, 0),
                    Opacity = 0.85
                });

                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString("Msg_RepairCompletedTitle"),
                    Content = stack,
                    PrimaryButtonText = ResourceService.GetString("Msg_OpenOutputFolder"),
                    CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = App.MainWindow.Content.XamlRoot,
                    RequestedTheme = App.CurrentTheme
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    OpenRepairOutputFolder();
                }
            }
        }

        // 查找某个 RepairFileEntry 所属的 RepairTask（用于滚动事件等）
        private RepairTask? FindParentTask(RepairFileEntry entry)
        {
            return Tasks.FirstOrDefault(t => t.Entries.Contains(entry));
        }

        [RelayCommand(AllowConcurrentExecutions = true)]
        public async Task ScanDirectoryAsync()
        {
            LogService.Repair($"ScanDirectory requested. Input='{InputDirectory}', Output='{OutputDirectory}'");

            if (!TryGuardScanClick()) return;

            if (IsScanning)
            {
                CancelScanning();
                SetStatus("Status_ScanCancelling");
                return;
            }

            if (string.IsNullOrWhiteSpace(InputDirectory))
            {
                await ShowNoInputDirectoryDialogAsync("Repair");
                return;
            }
            if (!Directory.Exists(InputDirectory))
            {
                await ShowInvalidInputDirectoryDialogAsync();
                return;
            }

            IsScanning = true;

            if (IsOutputToDirectory && string.IsNullOrWhiteSpace(OutputDirectory))
            {
                OutputDirectory = Path.Combine(InputDirectory, ResourceService.GetString("OutputDir_RepairedPhotos"));
            }

            var token = GetScanningToken();
            IsDirectoryPanelOpen = false;

            Tasks.ReplaceRange([]);
            FilteredTasks.ReplaceRange([]);
            TotalPhotosCount = 0;
            ThumbCorrectCount = 0;
            ThumbErrorCount = 0;
            TotalPairsCount = 0;
            StandaloneImagesCount = 0;
            StandaloneVideosCount = 0;
            Progress = 0;
            ProgressText = "0/0";

            SetStatus("Status_Scanning");
            BeginScanSession();
            await Task.Yield();
            NotifyStatusChanged();

            _scanCancelledByUser = false;
            _repairStoppedByUser = false;
            _repairDone = false;

            if (!token.IsCancellationRequested)
            {
                try { await Task.Delay(1000, token); } catch (TaskCanceledException) { }
            }

            token.ThrowIfCancellationRequested();

            try
            {
                var files = await Task.Run(() =>
                {
                    try
                    {
                        bool recursive = AppSettingsService.GetValue("IsRecursiveScanEnabled", false);
                        var searchOption = recursive ? SearchOption.AllDirectories : SearchOption.TopDirectoryOnly;
                        return Directory.GetFiles(InputDirectory, "*.*", searchOption)
                                 .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                             f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                             f.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) ||
                                             f.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
                                             f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase))
                                 .ToList();
                    }
                    catch (UnauthorizedAccessException ex)
                    {
                        LogService.Repair($"Access denied to repair scan directory: {InputDirectory}", LogLevel.Error, ex);
                        return new List<string>();
                    }
                    catch (DirectoryNotFoundException ex)
                    {
                        LogService.Repair($"Repair scan directory not found: {InputDirectory}", LogLevel.Error, ex);
                        return new List<string>();
                    }
                    catch (IOException ex)
                    {
                        LogService.Repair($"IO error scanning repair directory: {InputDirectory}", LogLevel.Error, ex);
                        return new List<string>();
                    }
                }, token);

                // ── 按文件名配对（参照 MergeScanService）──
                var imgDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var vidDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var file in files)
                {
                    string ext = Path.GetExtension(file).ToLowerInvariant();

                    // 递归扫描时用包含子文件夹的 key 避免跨文件夹同名文件冲突
                    string key = PathHelper.GetPairingKey(InputDirectory, file);

                    if (ext == ".jpg" || ext == ".jpeg" || ext == ".heic" || ext == ".heif")
                    {
                        imgDict[key] = file;
                    }
                    else if (ext == ".mov" || ext == ".mp4")
                    {
                        vidDict[key] = file;
                    }
                }

                // 组装各组工作列表，各组内按文件名排序
                var pairList = new List<(string imagePath, string videoPath, string baseName)>();
                var standaloneImgList = new List<(string imagePath, string baseName)>();
                var standaloneVidList = new List<(string videoPath, string baseName)>();

                foreach (var kvp in imgDict)
                {
                    if (vidDict.TryGetValue(kvp.Key, out var vidPath))
                    {
                        pairList.Add((kvp.Value, vidPath, kvp.Key));
                        vidDict.Remove(kvp.Key); // 已配对，不再作为单独视频
                    }
                    else
                    {
                        standaloneImgList.Add((kvp.Value, kvp.Key));
                    }
                }
                foreach (var kvp in vidDict)
                {
                    standaloneVidList.Add((kvp.Value, kvp.Key));
                }

                // 各组内按文件名排序
                pairList.Sort((a, b) => string.Compare(a.baseName, b.baseName, StringComparison.OrdinalIgnoreCase));
                standaloneImgList.Sort((a, b) => string.Compare(a.baseName, b.baseName, StringComparison.OrdinalIgnoreCase));
                standaloneVidList.Sort((a, b) => string.Compare(a.baseName, b.baseName, StringComparison.OrdinalIgnoreCase));

                // 组装有序工作列表：实况照片组合（文件名匹配）→ 单独照片 → 单独视频
                var pairedWorkItems = new List<(string? imagePath, string? videoPath, string baseName, bool isPaired)>();
                var standaloneWorkItems = new List<(string? imagePath, string? videoPath, string baseName, bool isPaired)>();
                foreach (var (img, vid, name) in pairList)
                    pairedWorkItems.Add((img, vid, name, true));
                foreach (var (img, name) in standaloneImgList)
                    standaloneWorkItems.Add((img, null, name, false));
                foreach (var (vid, name) in standaloneVidList)
                    standaloneWorkItems.Add((null, vid, name, false));

                // CidOnly / MetadataOnly 模式：跳过文件名匹配，全部放入独立列表走元数据匹配
                int matchingMode = AppSettingsService.GetValue("MetadataMatchingModeIndex", 0);
                bool skipFilenameMatch = matchingMode == (int)MetadataMatchingMode.CidOnly
                                      || matchingMode == (int)MetadataMatchingMode.MetadataOnly;
                if (skipFilenameMatch)
                {
                    foreach (var item in pairedWorkItems)
                    {
                        if (item.imagePath != null)
                            standaloneWorkItems.Add((item.imagePath, null, item.baseName, false));
                        if (item.videoPath != null)
                            standaloneWorkItems.Add((null, item.videoPath, item.baseName, false));
                    }
                    pairedWorkItems.Clear();
                }

                // ── Apple 设备预检测：收集 Apple 文件路径集，不跳过文件 ──
                bool appleOnlyScan = AppSettingsService.GetValue("IsAppleOnlyScanEnabled", true);
                HashSet<string>? appleFiles = null;
                if (appleOnlyScan)
                {
                    string appleFilterExifTool = ExternalToolLocator.FindExifTool()
                        ?? Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe");
                    if (File.Exists(appleFilterExifTool))
                    {
                        var allFilePaths = new List<string>();
                        foreach (var item in pairedWorkItems)
                        {
                            if (item.imagePath != null) allFilePaths.Add(item.imagePath);
                            if (item.videoPath != null) allFilePaths.Add(item.videoPath);
                        }
                        foreach (var item in standaloneWorkItems)
                        {
                            if (item.imagePath != null) allFilePaths.Add(item.imagePath);
                            if (item.videoPath != null) allFilePaths.Add(item.videoPath);
                        }

                        using (var checkTool = new PersistentExifTool(appleFilterExifTool))
                        {
                            appleFiles = await LivePhotoMetadataMatcher.FilterAppleDevicesAsync(
                                allFilePaths, checkTool, token);
                        }

                        LogService.Repair(
                            $"Apple-only scan: {appleFiles.Count}/{allFilePaths.Count} Apple files detected, " +
                            $"non-Apple files will be marked as skipped");
                    }
                    else
                    {
                        LogService.Repair("Apple-only scan: exiftool not found, skipping detection", LogLevel.Warning);
                    }
                }

                // 计算统计

                // 计算统计
                int pairCount = pairedWorkItems.Count;
                int standaloneImg = standaloneWorkItems.Count(w => w.imagePath != null);
                int standaloneVid = standaloneWorkItems.Count(w => w.videoPath != null);
                int totalFiles = pairCount * 2 + standaloneImg + standaloneVid;

                TotalPhotosCount = totalFiles;
                TotalPairsCount = pairCount;
                StandaloneImagesCount = standaloneImg;
                StandaloneVideosCount = standaloneVid;

                var scanProgress = CreateScanProgressReporter();
                scanProgress.Report(new WorkProgressSnapshot(totalFiles, 0));

                // 创建常驻 exiftool 进程
                string exifToolPath = ExternalToolLocator.FindExifTool()
                    ?? Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe");
                bool hasExifTool = File.Exists(exifToolPath);

                int processedCount = 0;  // 已分析的文件数（Entry 维度）
                int entryIndex = 0;      // 按文件（Entry）维度的序号，配对格子里两个文件各算一个
                int taskGridIndex = 0;  // 格子序号，供滚动定位

                await Task.Run(async () =>
                {
                    // ── 创建 exiftool 并行实例池 ──
                    // 多个独立 PersistentExifTool 实例可绕过单实例 SemaphoreSlim(1) 的串行化瓶颈，
                    // 实现真正并行的文件分析。每对实况照片消耗 2 个实例（照片+视频并行），
                    // 独立文件各消耗 1 个实例。默认 4 个实例，可由用户通过设置调整。
                    int exifToolParallelCount = AppSettingsService.GetValue("ExifToolParallelCount", 4);
                    exifToolParallelCount = Math.Clamp(exifToolParallelCount, 1, 8);
                    var exifToolPool = new List<PersistentExifTool>(exifToolParallelCount);
                    if (hasExifTool)
                    {
                        for (int i = 0; i < exifToolParallelCount; i++)
                        {
                            var tool = new PersistentExifTool(exifToolPath);
                            int toolIndex = i; // 捕获用于日志
                            tool.OnRestarted += (msg) =>
                            {
                                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                                {
                                    AppendDirectStatus($"[exiftool#{toolIndex}] {msg}");
                                });
                            };
                            exifToolPool.Add(tool);
                        }
                    }

                    // 从池中按索引取实例（轮转分配，避免单实例过载）
                    PersistentExifTool? GetExifTool(int index) =>
                        exifToolPool.Count > 0 ? exifToolPool[index % exifToolPool.Count] : null;

                    try
                    {
                    var itemBuffer = new List<RepairTask>();
                    long lastFlushMs = Environment.TickCount64;
                    const long flushIntervalMs = 120;

                    void FlushBuffer(int entryCountSnapshot)
                    {
                        if (itemBuffer.Count == 0) return;
                        var batch = new List<RepairTask>(itemBuffer);
                        itemBuffer.Clear();
                        lastFlushMs = Environment.TickCount64;

                        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                        {
                            int thumbCorrect = 0, thumbError = 0;
                            foreach (var task in batch)
                            {
                                Tasks.Add(task);
                                FilteredTasks.Add(task);
                                foreach (var entry in task.Entries)
                                {
                                    if (entry.NeedsRepair) thumbError++;
                                    else thumbCorrect++;
                                }
                            }
                            ThumbCorrectCount += thumbCorrect;
                            ThumbErrorCount += thumbError;
                            Progress = totalFiles == 0 ? 0 : (entryCountSnapshot * 100.0) / totalFiles;
                            ProgressText = $"{entryCountSnapshot}/{totalFiles}";
                            ScanItemsFlushed?.Invoke(this, EventArgs.Empty);
                        });
                    }

                    bool heicRepairEnabled = AppSettingsService.GetValue("IsHeicRepairEnabled", false);

                    // 待元数据匹配的独立文件缓冲（不在第一遍循环中创建 Task）
                    var pendingStandaloneImages = new List<(string baseName, RepairFileEntry entry, int analysisEntryIndex)>();
                    var pendingStandaloneVideos = new List<(string baseName, RepairFileEntry entry, int analysisEntryIndex)>();

                    // ── 第一遍：批量并行处理文件名配对的项 ──
                    // 每批处理 exifToolPool.Count/2 对（每对消耗 2 个 exiftool 实例），
                    // 批内所有照片+视频分析完全并行，充分利用多实例池的吞吐能力
                    int pairBatchSize = Math.Max(1, exifToolPool.Count / 2);
                    for (int batchStart = 0; batchStart < pairedWorkItems.Count; batchStart += pairBatchSize)
                    {
                        if (token.IsCancellationRequested) break;

                        int batchEnd = Math.Min(batchStart + pairBatchSize, pairedWorkItems.Count);
                        int batchCount = batchEnd - batchStart;

                        // ── 启动批次内所有分析任务（照片+视频各用独立的池实例并行执行）──
                        var batchTasks = new (int wi, string? imagePath, string? videoPath, string baseName,
                            Task<RepairFileEntry?> imageTask, Task<RepairFileEntry?> videoTask)[batchCount];

                        for (int bi = 0; bi < batchCount; bi++)
                        {
                            int wi = batchStart + bi;
                            var (imagePath, videoPath, baseName, _) = pairedWorkItems[wi];

                            var imgTool = GetExifTool(bi * 2);
                            var vidTool = GetExifTool(bi * 2 + 1);

                            var imageTask = AnalyzeFileAndCreateEntry(imagePath!, imgTool, heicRepairEnabled, token, appleFiles);
                            var videoTask = AnalyzeFileAndCreateEntry(videoPath!, vidTool, heicRepairEnabled, token, appleFiles);

                            batchTasks[bi] = (wi, imagePath, videoPath, baseName, imageTask, videoTask);
                        }

                        // ── 按原始顺序等待结果并创建 RepairTask（保证 entryIndex 连续一致）──
                        for (int bi = 0; bi < batchCount; bi++)
                        {
                            var (wi, imagePath, videoPath, baseName, imageTask, videoTask) = batchTasks[bi];

                            RepairFileEntry? imageEntry = null, videoEntry = null;
                            try { imageEntry = await imageTask; } catch (OperationCanceledException) { }
                            try { videoEntry = await videoTask; } catch (OperationCanceledException) { }

                            if (token.IsCancellationRequested) break;
                            if (imageEntry == null && videoEntry == null) continue;

                            taskGridIndex = wi + 1;

                            if (imageEntry != null) { entryIndex++; processedCount++; }
                            if (videoEntry != null) { entryIndex++; processedCount++; }

                            // 未启用"修复非实况照片视频" → 时长 > 3.5s 的非实况视频直接标为已跳过
                            bool repairNonLivePhoto = AppSettingsService.GetValue("IsNonLivePhotoVideoRepairEnabled", false);
                            if (!repairNonLivePhoto && videoEntry?.AnalysisResult != null
                                && videoEntry.AnalysisResult.VideoDurationSeconds > LivePhotoConstants.MaxLivePhotoVideoDurationSeconds)
                            {
                                videoEntry.NeedsRepair = false;
                                videoEntry.Details = ResourceService.GetString("RepairPage_Task_SkippedNonLivePhoto");
                            }

                            // 检查视频时长：> 3.5s 不是实况照片，已配对的拆开
                            bool isLivePhotoVideo = videoEntry != null
                                && (videoEntry.AnalysisResult?.VideoDurationSeconds ?? 0) <= LivePhotoConstants.MaxLivePhotoVideoDurationSeconds;
                            bool effectivePaired = isLivePhotoVideo;

                            // ── 更严格的实况照片扫描：通过 ContentIdentifier UUID 验证配对 ──
                            bool strictScan = AppSettingsService.GetValue("IsStrictLivePhotoScanEnabled", false);
                            if (strictScan && effectivePaired && imageEntry != null && videoEntry != null)
                            {
                                string? imgCid = imageEntry.AnalysisResult?.ContentIdentifier;
                                string? vidCid = videoEntry.AnalysisResult?.ContentIdentifier;
                                bool bothHaveCid = !string.IsNullOrWhiteSpace(imgCid) && !string.IsNullOrWhiteSpace(vidCid);
                                bool cidsMatch = bothHaveCid && string.Equals(imgCid, vidCid, StringComparison.OrdinalIgnoreCase);
                                if (!cidsMatch)
                                {
                                    effectivePaired = false;
                                    LogService.Repair($"Strict scan: unpaired '{baseName}' — ContentIdentifier mismatch (img={imgCid ?? "none"}, vid={vidCid ?? "none"})");
                                }
                            }

                            if (!effectivePaired)
                            {
                                // 文件名配对被 strict scan / 时长检查拆开 → 直接创建独立 Task
                                if (imageEntry != null)
                                {
                                    int imgIdx = entryIndex - (videoEntry != null ? 1 : 0);
                                    var imgTask = new RepairTask(imgIdx, 0, baseName, false, imageEntry, null);
                                    imgTask.Index = taskGridIndex;
                                    itemBuffer.Add(imgTask);
                                }
                                if (videoEntry != null)
                                {
                                    var vidTask = new RepairTask(entryIndex, 0, baseName, false, videoEntry, null);
                                    vidTask.Index = taskGridIndex + 1;
                                    itemBuffer.Add(vidTask);
                                }
                            }
                            else
                            {
                                // 有效的实况照片配对
                                var file1 = imageEntry ?? videoEntry!;
                                var file2 = (imageEntry != null ? videoEntry : imageEntry);
                                int file1Idx = imageEntry != null ? entryIndex - 1 : entryIndex;
                                int file2Idx = entryIndex;

                                var repairTask = new RepairTask(file1Idx, file2Idx, baseName, true, file1, file2);
                                repairTask.Index = taskGridIndex;
                                itemBuffer.Add(repairTask);
                            }

                            scanProgress.Report(new WorkProgressSnapshot(totalFiles, processedCount));
                        }

                        // 批次完成后刷新 UI 缓冲区
                        if (Environment.TickCount64 - lastFlushMs >= flushIntervalMs)
                        {
                            FlushBuffer(processedCount);
                        }
                    }

                    // ── 第二遍：分析独立文件，暂存缓冲（等元数据匹配后再创建 Task）──
                    for (int wi = 0; wi < standaloneWorkItems.Count; wi++)
                    {
                        if (token.IsCancellationRequested) break;

                        var (imagePath, videoPath, baseName, _) = standaloneWorkItems[wi];

                        RepairFileEntry? imageEntry = null;
                        RepairFileEntry? videoEntry = null;

                        if (imagePath != null)
                        {
                            imageEntry = await AnalyzeFileAndCreateEntry(
                                imagePath, GetExifTool(0), heicRepairEnabled, token, appleFiles);
                            if (imageEntry != null)
                            {
                                entryIndex++; processedCount++;

                                // 严格模式下标记"曾是实况照片但视频缺失"的独立照片
                                bool strictScan = AppSettingsService.GetValue("IsStrictLivePhotoScanEnabled", false);
                                if (strictScan && imageEntry.AnalysisResult?.HasContentIdentifier == true)
                                {
                                    imageEntry.IssueDescription = ResourceService.GetString("RepairPage_LivePhotoVideoMissing") ?? "Live Photo (video missing)";
                                    imageEntry.NeedsRepair = false;
                                }

                                pendingStandaloneImages.Add((baseName, imageEntry, entryIndex));

                                // 创建临时独立 Task 以支持渐进式 UI 显示（元数据匹配后可能重组为配对 Task）
                                taskGridIndex++;
                                var provisionalTask = new RepairTask(entryIndex, 0, baseName, false, imageEntry);
                                provisionalTask.Index = taskGridIndex;
                                itemBuffer.Add(provisionalTask);
                            }
                        }

                        if (videoPath != null)
                        {
                            videoEntry = await AnalyzeFileAndCreateEntry(
                                videoPath, GetExifTool(1), heicRepairEnabled, token, appleFiles);
                            if (videoEntry != null)
                            {
                                entryIndex++; processedCount++;
                                pendingStandaloneVideos.Add((baseName, videoEntry, entryIndex));

                                // 创建临时独立 Task 以支持渐进式 UI 显示（元数据匹配后可能重组为配对 Task）
                                taskGridIndex++;
                                var provisionalVidTask = new RepairTask(entryIndex, 0, baseName, false, videoEntry);
                                provisionalVidTask.Index = taskGridIndex;
                                itemBuffer.Add(provisionalVidTask);

                                bool repairNonLivePhoto = AppSettingsService.GetValue("IsNonLivePhotoVideoRepairEnabled", false);
                                if (!repairNonLivePhoto && videoEntry.AnalysisResult != null
                                    && videoEntry.AnalysisResult.VideoDurationSeconds > LivePhotoConstants.MaxLivePhotoVideoDurationSeconds)
                                {
                                    videoEntry.NeedsRepair = false;
                                    videoEntry.Details = ResourceService.GetString("RepairPage_Task_SkippedNonLivePhoto");
                                }
                            }
                        }

                        if (token.IsCancellationRequested) break;

                        scanProgress.Report(new WorkProgressSnapshot(totalFiles, processedCount));

                        if (Environment.TickCount64 - lastFlushMs >= flushIntervalMs)
                        {
                            FlushBuffer(processedCount);
                        }
                    }

                    // ── 元数据匹配：尝试将独立文件重新配对 ──
                    // 收集元数据匹配后的最终独立文件 Task 列表
                    // （第二遍循环中已通过临时独立 Task 逐步显示到 UI，元数据匹配后可能需要重组）
                    var finalStandaloneTasks = new List<RepairTask>();
                    bool metadataMatchesFound = false;
                    // 组合匹配：FilenameCidAndMetadata 或 MetadataOnly 模式时启用
                    // 注：Repair 页面 GPS/设备暂未支持 (Phase 2)，仅日期匹配生效
                    bool runCombined = matchingMode == (int)MetadataMatchingMode.FilenameCidAndMetadata
                                    || matchingMode == (int)MetadataMatchingMode.MetadataOnly;
                    bool runCid = matchingMode != (int)MetadataMatchingMode.MetadataOnly;
                    if (matchingMode != (int)MetadataMatchingMode.FilenameOnly
                        && pendingStandaloneImages.Count > 0 && pendingStandaloneVideos.Count > 0
                        && !token.IsCancellationRequested)
                    {
                        try
                        {
                            var imgAnalysisList = pendingStandaloneImages
                                .Select(e => (path: e.entry.FilePath, analysis: e.entry.AnalysisResult!))
                                .Where(x => x.analysis != null)
                                .ToList();
                            var vidAnalysisList = pendingStandaloneVideos
                                .Select(e => (path: e.entry.FilePath, analysis: e.entry.AnalysisResult!))
                                .Where(x => x.analysis != null)
                                .ToList();

                            if (imgAnalysisList.Count > 0 && vidAnalysisList.Count > 0)
                            {
                                var matchOutput = LivePhotoMetadataMatcher.MatchFromAnalysis(imgAnalysisList, vidAnalysisList, runCombined, runCid);

                                if (matchOutput.Pairs.Count > 0)
                                {
                                    metadataMatchesFound = true;
                                }

                                // 元数据匹配成功 → 创建配对 RepairTask（加入最终列表）
                                var matchedImgPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                                var matchedVidPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

                                foreach (var pair in matchOutput.Pairs)
                                {
                                    var imgData = pendingStandaloneImages.First(e => e.entry.FilePath == pair.ImagePath);
                                    var vidData = pendingStandaloneVideos.First(e => e.entry.FilePath == pair.VideoPath);

                                    matchedImgPaths.Add(pair.ImagePath);
                                    matchedVidPaths.Add(pair.VideoPath);

                                    string pairBaseName = Path.GetFileNameWithoutExtension(pair.ImagePath);
                                    taskGridIndex++;
                                    var pairedTask = new RepairTask(
                                        imgData.analysisEntryIndex,
                                        vidData.analysisEntryIndex,
                                        pairBaseName, true, imgData.entry, vidData.entry);
                                    pairedTask.Index = taskGridIndex;
                                    finalStandaloneTasks.Add(pairedTask);

                                    LogService.Repair($"Metadata matching: paired '{pairBaseName}' via {pair.Source}");
                                }

                                // 移除已匹配的，保留剩余的
                                pendingStandaloneImages.RemoveAll(e => matchedImgPaths.Contains(e.entry.FilePath));
                                pendingStandaloneVideos.RemoveAll(e => matchedVidPaths.Contains(e.entry.FilePath));

                                // 更新独立文件计数
                                standaloneImg = pendingStandaloneImages.Count;
                                standaloneVid = pendingStandaloneVideos.Count;
                            }
                        }
                        catch (Exception ex)
                        {
                            LogService.Repair($"Metadata matching failed, keeping standalone as-is: {ex.Message}", LogLevel.Warning);
                        }
                    }

                    // ── 正常扫描完成才报告 100% ──
                    if (!token.IsCancellationRequested)
                    {
                        scanProgress.Report(new WorkProgressSnapshot(totalFiles, totalFiles));
                    }

                    if (metadataMatchesFound)
                    {
                        // ── 元数据匹配找到了新配对 → 创建剩余独立 Task 并在 UI 线程重建列表 ──
                        foreach (var (bn, entry, entryIdx) in pendingStandaloneImages)
                        {
                            taskGridIndex++;
                            var standalone = new RepairTask(entryIdx, 0, bn, false, entry);
                            standalone.Index = taskGridIndex;
                            finalStandaloneTasks.Add(standalone);
                        }
                        foreach (var (bn, entry, entryIdx) in pendingStandaloneVideos)
                        {
                            taskGridIndex++;
                            var standalone = new RepairTask(entryIdx, 0, bn, false, entry);
                            standalone.Index = taskGridIndex;
                            finalStandaloneTasks.Add(standalone);
                        }

                        // 清空 itemBuffer（第二遍循环中的临时独立 Task 已逐步 Flush 到 UI，这里不再需要）
                        itemBuffer.Clear();

                        // 在 UI 线程上重建整个任务列表：
                        // 第一遍的文件名配对 Task 保持不变，独立文件部分用元数据匹配后的最终列表替换
                        var capturedFinalStandalone = finalStandaloneTasks;
                        var capturedTotalFiles = totalFiles;
                        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                        {
                            // 保留第一遍的文件名配对 Task（它们不受元数据匹配影响）
                            int pairedCount = 0;
                            foreach (var t in Tasks)
                            {
                                if (t.IsPaired) pairedCount++;
                                else break;
                            }

                            var rebuilt = new List<RepairTask>();
                            for (int i = 0; i < pairedCount; i++)
                                rebuilt.Add(Tasks[i]);

                            int gridIdx = pairedCount;
                            foreach (var task in capturedFinalStandalone)
                            {
                                gridIdx++;
                                task.Index = gridIdx;
                                rebuilt.Add(task);
                            }

                            Tasks.ReplaceRange(rebuilt);
                            FilteredTasks.ReplaceRange(rebuilt);

                            // 同步更新计数器
                            ThumbCorrectCount = rebuilt.Sum(t => t.Entries.Count(e => !e.NeedsRepair));
                            ThumbErrorCount = rebuilt.Sum(t => t.Entries.Count(e => e.NeedsRepair));
                            Progress = capturedTotalFiles == 0 ? 0 : 100;
                            ProgressText = $"{capturedTotalFiles}/{capturedTotalFiles}";
                            ScanItemsFlushed?.Invoke(this, EventArgs.Empty);
                        });
                    }
                    else
                    {
                        // ── 没有元数据匹配 → 第二遍循环中已逐步显示的临时独立 Task 就是最终结果 ──
                        // 只需确保 itemBuffer 中的剩余项（如果有）被 Flush
                        FlushBuffer(processedCount);
                    }
                    }
                    finally
                    {
                        // 释放所有常驻 exiftool 实例（替代旧代码的 using var 自动释放）
                        foreach (var tool in exifToolPool)
                            tool.Dispose();
                    }
                }, token);

                FlushPendingScanProgress();

                if (token.IsCancellationRequested)
                {
                    LogService.Repair($"Scan cancelled by user, {processedCount}/{totalFiles} entries scanned — clearing list");
                    SetStatus("RepairPage_Status_ScanCancelled");

                    App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                    {
                        Tasks.ReplaceRange([]);
                        FilteredTasks.ReplaceRange([]);
                        ThumbCorrectCount = 0;
                        ThumbErrorCount = 0;
                        TotalPairsCount = 0;
                        StandaloneImagesCount = 0;
                        StandaloneVideosCount = 0;
                        Progress = 0;
                        ProgressText = "0/0";
                    });

                    AppViewModel.Instance.ResetFooterScanCounters();
                }
                else if (totalFiles > 0)
                {
                    CompleteScanSnapshot();
                    SetStatus("RepairPage_Status_ScanDone", ThumbCorrectCount, ThumbErrorCount);
                    LogService.Repair($"Scan completed: {totalFiles} entries, {pairCount} pairs, {standaloneImg} imgs, {standaloneVid} vids — {ThumbCorrectCount} healthy, {ThumbErrorCount} need repair");
                }
                else
                {
                    CompleteScanSnapshot();
                    IsDirectoryPanelOpen = true;
                    SetStatus("RepairPage_Status_ScanNoFiles");
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus("RepairPage_Status_ScanCancelled");
                LogService.Repair("Scan cancelled via OCE — clearing list");

                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    Tasks.ReplaceRange([]);
                    TotalPhotosCount = 0;
                    ThumbCorrectCount = 0;
                    ThumbErrorCount = 0;
                    TotalPairsCount = 0;
                    StandaloneImagesCount = 0;
                    StandaloneVideosCount = 0;
                    Progress = 0;
                    ProgressText = "0/0";
                });

                AppViewModel.Instance.ResetFooterScanCounters();
            }
            catch (Exception ex)
            {
                LogService.Repair($"ScanDirectory error: {ex.Message}", LogLevel.Error, ex);
                SetStatus("Status_Error", ex.Message);
            }
            finally
            {
                IsScanning = false;
                OnScanningEnded();
                NotifyStatusChanged();
                _cancelledByUser = false;
            }
        }

        // 分析单个文件并创建 RepairFileEntry。返回 null 表示被取消。
        private async Task<RepairFileEntry?> AnalyzeFileAndCreateEntry(
            string filePath, PersistentExifTool? persistentExifTool,
            bool heicRepairEnabled, CancellationToken token,
            HashSet<string>? appleFiles = null)
        {
            bool isImage = !(filePath.EndsWith(".mov", StringComparison.OrdinalIgnoreCase)
                          || filePath.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase));
            bool isHeicFile = filePath.EndsWith(".heic", StringComparison.OrdinalIgnoreCase)
                           || filePath.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);

            RepairAnalysisResult analysis;

            // HEIC修复开关只管"修不修"，不管"匹不匹配" — 诊断和配对始终执行
            try
            {
                analysis = await LivePhotoRepairService.AnalyzeFileAsync(filePath, persistentExifTool, token);
            }
            catch (OperationCanceledException)
            {
                return null;
            }

            // HEIC 修复关闭时：诊断结果保留（ContentIdentifier 用于匹配），但不修复
            if (isHeicFile && !heicRepairEnabled && analysis.NeedsRepair)
            {
                analysis.IssueType = RepairIssueType.Perfect;
                analysis.IssueDescription = ResourceService.GetString("Status_HeicRepairDisabled");
            }

            // Apple 设备检测：开启后非 Apple 文件标记为跳过
            if (appleFiles != null && !appleFiles.Contains(filePath))
            {
                analysis.IssueType = RepairIssueType.NonApple;
                analysis.IssueDescription = ResourceService.GetString("RepairPage_Diagnosis_NonApple");
            }

            return new RepairFileEntry
            {
                FileName = Path.GetFileName(filePath),
                FilePath = filePath,
                IsImage = isImage,
                IssueDescription = analysis.IssueDescription,
                NeedsRepair = analysis.NeedsRepair,
                Status = ProcessStatus.Pending,
                Details = analysis.NeedsRepair
                    ? ResourceService.GetString("RepairPage_Task_WaitingRepair")
                    : ResourceService.GetString("RepairPage_Task_Skipped"),
                AnalysisResult = analysis
            };
        }

        // 切换次要操作（暂停/继续 或 清空列表），取决于当前处理状态。
        [RelayCommand]
        private void ToggleSecondaryAction()
        {
            LogService.Repair($"ToggleSecondaryAction requested. IsProcessing={IsProcessing}, IsPaused={IsPaused}");

            if (!IsProcessing)
            {
                ClearState();
            }
            else
            {
                TogglePause();
            }
        }

        // 切换处理状态：运行时点击停止，停止后（已取消或已完成）点击弹出结果对话框，空闲时启动修复。
        [RelayCommand(AllowConcurrentExecutions = true)]
        public async Task ToggleProcessAsync()
        {
            LogService.Repair($"ToggleProcessAsync requested. IsProcessing={IsProcessing}, QueueCount={Tasks.Count}");

            if (IsProcessing)
            {
                SetStatus("RepairPage_Status_Stopping");
                CancelProcessing();
                OnPropertyChanged(nameof(ActionBtnText));
                return;
            }

            if (_repairStoppedByUser || _repairDone)
            {
                if (_repairStoppedByUser)
                    await ShowRepairCancelledDialogAsync();
                else
                    await ShowRepairAlreadyDoneDialogAsync();
                return;
            }

            if (Tasks.Count == 0)
            {
                await ShowEmptyQueueDialogAsync("Repair");
                return;
            }

            if (IsOutputToDirectory)
            {
                if (string.IsNullOrWhiteSpace(OutputDirectory))
                    OutputDirectory = Path.Combine(InputDirectory, ResourceService.GetString("OutputDir_RepairedPhotos"));
                if (!Directory.Exists(OutputDirectory))
                    Directory.CreateDirectory(OutputDirectory);
            }

            IsDirectoryPanelOpen = false;
            await RunTasksAsync();
        }

        private async Task RunTasksAsync()
        {
            InitializeRunState();
            // 开始修复：强制回"全部"、取消状态筛选、禁用筛选
            FilterMode = 0;
            RepairStatusFilter = 0;
            UpdateFilterEnabled();
            _stopwatch = Stopwatch.StartNew();

            var token = GetProcessingToken();

            try
            {
                // 扁平化：从所有 Task 中提取需要处理的 Entry，配对格子里的两个文件分别处理
                bool repairNonLivePhoto = AppSettingsService.GetValue("IsNonLivePhotoVideoRepairEnabled", false);
                bool copyPerfectToOutput = IsOutputToDirectory && AppSettingsService.GetValue("IsCopyPerfectToOutput", false);
                var repairEntries = Tasks.SelectMany(t => t.Entries)
                    .Where(e => e.Status != ProcessStatus.Success)
                    .Where(e =>
                    {
                        // 输出目录模式 + 启用"同时输出无需修复的文件"：包含完美文件（仅复制）
                        if (copyPerfectToOutput && e.AnalysisResult?.IssueType == RepairIssueType.Perfect)
                            return true;
                        // 需要修复的文件
                        if (!e.NeedsRepair) return false;
                        // 未启用"修复非实况照片视频"：跳过时长 > 3.5s 的普通长视频
                        if (!repairNonLivePhoto && !e.IsImage && (e.AnalysisResult?.VideoDurationSeconds ?? 0) > LivePhotoConstants.MaxLivePhotoVideoDurationSeconds)
                            return false;
                        return true;
                    }).ToList();

                await Task.Run(async () =>
                {
                    int userThreads = AppSettingsService.GetValue("SplitThreadCount", 4);
                    var pending = new List<Task>();

                    async Task ProcessOneAsync(RepairFileEntry entry)
                    {
                        await Task.Yield();

                        try { PauseEvent.Wait(token); }
                        catch (OperationCanceledException) { return; }
                        if (token.IsCancellationRequested) return;

                        Interlocked.Increment(ref _activeWorkerCount);
                        try
                        {
                            // 通知滚动（找到父 Task 用于 Index）
                            var parentTask = FindParentTask(entry);
                            if (parentTask != null)
                            {
                                App.MainWindow?.DispatcherQueue.TryEnqueue(() => UpdateEntryStarted(entry, parentTask));
                            }

                            string targetPath;
                            if (IsOutputToDirectory)
                            {
                                string? subDir = null;
                                if (AppSettingsService.GetValue("IsOutputPreserveSubfolderStructure", false))
                                    subDir = PathHelper.GetRelativeSubDirectory(InputDirectory, entry.FilePath);
                                targetPath = PathHelper.GetUniqueFilePath(OutputDirectory, entry.FileName, subDir);
                            }
                            else
                            {
                                targetPath = entry.FilePath;
                            }

                            bool isSuccess = false;
                            string detailMessage = string.Empty;
                            bool isCanceled = false;

                            // 无需修复的完美文件：直接复制到输出目录（不调用 RepairAsync）
                            if (entry.AnalysisResult?.IssueType == RepairIssueType.Perfect)
                            {
                                try
                                {
                                    string? outDir = Path.GetDirectoryName(targetPath);
                                    if (!string.IsNullOrEmpty(outDir))
                                        Directory.CreateDirectory(outDir);
                                    File.Copy(entry.FilePath, targetPath, overwrite: true);
                                    isSuccess = true;
                                    detailMessage = ResourceService.GetString("RepairPage_Task_Copied") ?? "已复制";
                                }
                                catch (Exception ex)
                                {
                                    isSuccess = false;
                                    detailMessage = ex.Message;
                                    LogService.Repair($"Copy perfect file failed for {entry.FilePath}: {ex.Message}", LogLevel.Error, ex);
                                }
                            }
                            else
                            {
                                try
                                {
                                    var result = await LivePhotoRepairService.RepairAsync(entry.FilePath, targetPath, entry.AnalysisResult!, token);
                                    isSuccess = result.Success;
                                    detailMessage = result.Message;
                                }
                                catch (OperationCanceledException)
                                {
                                    isCanceled = true;
                                    detailMessage = ResourceService.GetString("Status_Aborted") ?? "???";
                                }
                                catch (Exception ex)
                                {
                                    isSuccess = false;
                                    detailMessage = ex.Message;
                                    LogService.Repair($"Repair failed for {entry.FilePath}: {ex.Message}", LogLevel.Error, ex);
                                }
                            }

                            if (!isCanceled)
                                Interlocked.Increment(ref _completedEntriesCount);

                            await EnsureMinimumProcessingDisplayAsync(entry);

                            var tcs = new TaskCompletionSource<bool>();
                            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                            {
                                try
                                {
                                    if (isCanceled)
                                        UpdateEntryCancelled(entry, detailMessage);
                                    else
                                        UpdateEntryCompleted(entry, isSuccess, detailMessage);
                                }
                                finally { tcs.TrySetResult(true); }
                            });
                            await tcs.Task;

                            if (isCanceled)
                                return;
                        }
                        finally
                        {
                            Interlocked.Decrement(ref _activeWorkerCount);
                        }
                    }

                    foreach (var entry in repairEntries)
                    {
                        if (token.IsCancellationRequested) break;

                        bool hw = EncoderHelper.IsUsingHardwareAcceleration();
                        int maxParallel = hw ? userThreads : 2;

                        while (pending.Count >= maxParallel)
                        {
                            var done = await Task.WhenAny(pending);
                            pending.Remove(done);
                            try { await done; }
                            catch (OperationCanceledException) { break; }
                            catch (InvalidOperationException) { throw; }
                        }

                        if (token.IsCancellationRequested) break;
                        pending.Add(ProcessOneAsync(entry));
                    }

                    try { await Task.WhenAll(pending); }
                    catch (OperationCanceledException) { }
                }, token);
            }
            catch (OperationCanceledException)
            {
                // 用户取消 — 正常退出路径，下面 finally 块会处理状态更新
            }
            catch (Exception ex)
            {
                LogService.Repair($"RunTasksAsync fatal error: {ex.Message}", LogLevel.Error, ex);
                Environment.ExitCode = unchecked((int)0xE0000001);
                throw;
            }
            finally
            {
                _stopwatch.Stop();
                bool wasCancelled = _cancelledByUser;

                if (wasCancelled)
                {
                    int total = Tasks.Sum(t => t.Entries.Count);
                    int succeeded = Tasks.SelectMany(t => t.Entries)
                        .Count(e => e.Status == ProcessStatus.Success && (e.AnalysisResult == null || e.AnalysisResult.IssueType != RepairIssueType.Perfect));
                    int skipped = Tasks.SelectMany(t => t.Entries)
                        .Count(e => e.AnalysisResult != null && e.AnalysisResult.IssueType == RepairIssueType.Perfect);
                    int failed = Tasks.SelectMany(t => t.Entries)
                        .Count(e => e.Status == ProcessStatus.Failed);
                    int unprocessed = total - succeeded - skipped - failed;
                    double elapsed = _stopwatch.Elapsed.TotalSeconds;
                    LogService.Repair($"Repair cancelled by user after {elapsed:F1}s, completed {_completedEntriesCount}/{_totalRepairEntries}");
                    SetStatus("Status_RepairStoppedSummary", total, elapsed, succeeded, skipped, failed, unprocessed);
                }

                FinalizeRunState();
                // 修复结束：恢复筛选可用
                UpdateFilterEnabled();

                // 关闭中不弹对话框；多个队列同时完成时 WinUI 只允许一个 ContentDialog，
                // 冲突的 COMException 吞掉即可（不影响处理结果）。
                if (Tasks.Count > 0 && !_isCleaningUp)
                {
                    try
                    {
                        if (wasCancelled)
                            await ShowRepairCancelledDialogAsync();
                        else
                            await ShowRepairAlreadyDoneDialogAsync();
                    }
                    catch (System.Runtime.InteropServices.COMException ex)
                    {
                        LogService.Debug($"Completion dialog skipped (another dialog already open): {ex.Message}", LogSource.UI);
                    }
                }
            }
        }

        // 某个修复任务开始处理时触发，供 ListView 滚动到对应项。
        public event EventHandler<RepairTask>? TaskStartedForScroll;
        // 全部处理完成时触发，供 ListView 滚动回顶部。
        public event EventHandler? ProcessingCompletedForScroll;
        // 扫描进度的批量项刷新到 UI 时触发。
        public event EventHandler? ScanItemsFlushed;

        // 更新 Entry 为"处理中"状态，记录开始时间，触发滚动事件。
        private void UpdateEntryStarted(RepairFileEntry entry, RepairTask parentTask)
        {
            entry.Status = ProcessStatus.Processing;
            entry.Details = ResourceService.GetString("Task_Processing");
            _taskProcessingStartTimes[entry] = DateTimeOffset.UtcNow;
            TaskStartedForScroll?.Invoke(this, parentTask);
        }

        // 更新 Entry 为"已完成/失败"状态，触发完成滚动事件（若全部完成）。
        private void UpdateEntryCompleted(RepairFileEntry entry, bool isSuccess, string detailMessage)
        {
            entry.Status = isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
            entry.Details = detailMessage;
            _taskProcessingStartTimes.Remove(entry);

            if (_completedEntriesCount >= _totalRepairEntries && _totalRepairEntries > 0)
            {
                ProcessingCompletedForScroll?.Invoke(this, EventArgs.Empty);
            }
        }

        // 更新被取消的 Entry 的状态详情（保持 Processing，不标记失败）。
        private void UpdateEntryCancelled(RepairFileEntry entry, string detailMessage)
        {
            entry.Details = detailMessage;
            _taskProcessingStartTimes.Remove(entry);
        }

        // 确保每个 Entry 至少显示了最短持续时间（100ms），避免进度闪烁。
        private async Task EnsureMinimumProcessingDisplayAsync(RepairFileEntry entry)
        {
            if (!_taskProcessingStartTimes.TryGetValue(entry, out var startedAt)) return;
            var remaining = MinimumProcessingDisplayDuration - (DateTimeOffset.UtcNow - startedAt);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining).ConfigureAwait(false);
        }

        // 通过文件夹选择器选取输入目录。
        [RelayCommand]
        private async Task PickInputDirectoryAsync()
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null)
            {
                InputDirectory = folder.Path;
            }
        }

        // 通过文件夹选择器选取修复输出目录。
        [RelayCommand]
        private async Task PickOutputDirectoryAsync()
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null)
            {
                OutputDirectory = folder.Path;
            }
        }

        // 在文件管理器中打开修复输入文件夹。
        private async Task OpenRepairInputFolderAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(InputDirectory)) return;
                if (!Directory.Exists(InputDirectory))
                {
                    await ShowInvalidInputDirectoryDialogAsync();
                    return;
                }
                FilePickerService.OpenFolderInExplorer(InputDirectory);
            }
            catch (Exception ex) { LogService.Repair($"OpenRepairInput error: {ex.Message}", LogLevel.Error, ex); }
        }

        // 判断是否可以打开修复输出文件夹。
        private bool CanOpenRepairOutputFolder()
        {
            var folderPath = GetRepairResultFolderPath();
            return !string.IsNullOrWhiteSpace(folderPath);
        }

        // 获取修复结果所在的文件夹路径（依据 IsOutputToDirectory 决定）。
        private string GetRepairResultFolderPath()
        {
            return IsOutputToDirectory ? OutputDirectory : InputDirectory;
        }

        // 在文件管理器中打开修复输出文件夹。
        private void OpenRepairOutputFolder()
        {
            try
            {
                var folderPath = GetRepairResultFolderPath();
                if (string.IsNullOrWhiteSpace(folderPath)) return;
                if (!Directory.Exists(folderPath))
                    Directory.CreateDirectory(folderPath);
                FilePickerService.OpenFolderInExplorer(folderPath);
            }
            catch (Exception ex) { LogService.Repair($"OpenRepairOutput error: {ex.Message}", LogLevel.Error, ex); }
        }
    }
}
