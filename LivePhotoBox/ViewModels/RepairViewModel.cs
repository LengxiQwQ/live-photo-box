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
    public partial class RepairViewModel : WorkViewModelBase
    {
        private static readonly TimeSpan MinimumProcessingDisplayDuration = TimeSpan.FromMilliseconds(100);

        public override string PageStatusTag => "Repair";

        protected override string ProcessingStatusKey => "Status_Running";

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
                    if (Tasks.Count > 0) ClearState();
                    ScanDirectoryCommand.Execute(null);
                }
            }
        }

        [ObservableProperty]
        private string _outputDirectory = string.Empty;

        partial void OnOutputDirectoryChanged(string value) => _openRepairOutputFolderCommand?.NotifyCanExecuteChanged();

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

        public Visibility OutputGridVisibility =>
            IsOutputToDirectory ? Visibility.Visible : Visibility.Collapsed;

        public Visibility InputLabelVisibility =>
            IsOutputToDirectory ? Visibility.Visible : Visibility.Collapsed;

        public Visibility InputOutputLabelVisibility =>
            IsOutputToDirectory ? Visibility.Collapsed : Visibility.Visible;

        [ObservableProperty]
        private int _totalPhotosCount = 0;

        [ObservableProperty]
        private int _thumbCorrectCount = 0;

        [ObservableProperty]
        private int _thumbErrorCount = 0;

        /// <summary>配对成功的实况照片组数（新增统计）</summary>
        [ObservableProperty]
        private int _totalPairsCount = 0;

        /// <summary>单独照片数（匹配不到视频的孤立照片）</summary>
        [ObservableProperty]
        private int _standaloneImagesCount = 0;

        /// <summary>单独视频数（匹配不到照片的孤立视频）</summary>
        [ObservableProperty]
        private int _standaloneVideosCount = 0;

        [ObservableProperty]
        private bool _isDirectoryPanelOpen = true;

        public string ScanButtonText => IsScanning
            ? ResourceService.GetString("RepairPage_DynamicCancelText")
            : ResourceService.GetString("RepairPage_DynamicScanText");

        public BulkObservableCollection<RepairTask> Tasks { get; } = [];

        /// <summary>筛选后队列（ListView 实际绑定此集合）</summary>
        public BulkObservableCollection<RepairTask> FilteredTasks { get; } = [];

        /// <summary>筛选栏可见性 — 有任务时才显示</summary>
        public Visibility FilterBarVisibility => Tasks.Count > 0 ? Visibility.Visible : Visibility.Collapsed;

        #region Filter

        /// <summary>
        /// 修复状态筛选：0=全部, 1=仅待修复, 2=仅完好
        /// </summary>
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

        /// <summary>合并筛选按钮上显示的文本：例如"实况照片 · 仅待修复"</summary>
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

        [RelayCommand]
        private void SetTypeFilter(object parameter)
        {
            if (parameter is int i) FilterMode = i;
            else if (parameter is string s && int.TryParse(s, out var r)) FilterMode = r;
        }

        [RelayCommand]
        private void SetStatusFilter(object parameter)
        {
            if (parameter is int i) RepairStatusFilter = i;
            else if (parameter is string s && int.TryParse(s, out var r)) RepairStatusFilter = r;
        }

        [RelayCommand]
        private void ResetFilter()
        {
            FilterMode = 0;
            RepairStatusFilter = 0;
        }

        /// <summary>当前浏览的分组名称（由滚动位置决定），如"实况照片组合"</summary>
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

        /// <summary>"当前显示：实况照片组合" 之类的完整文本</summary>
        public string CurrentViewGroupText
        {
            get
            {
                if (string.IsNullOrEmpty(CurrentViewGroup)) return string.Empty;
                string label = ResourceService.GetString("RepairPage_ShowingLabel");
                return $"{label} {CurrentViewGroup}";
            }
        }

        /// <summary>分组标签可见性 — 仅"全部"、有任务、非扫描中时显示</summary>
        public Visibility ViewGroupVisibility => FilterMode == 0 && Tasks.Count > 0 && !IsScanning ? Visibility.Visible : Visibility.Collapsed;

        /// <summary>根据任务确定其所属分组名称</summary>
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

        /// <summary>在扫描/处理状态切换时调用，更新 IsFilterEnabled 和筛选状态</summary>
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

        private IAsyncRelayCommand? _openRepairInputFolderCommand;
        private IRelayCommand? _openRepairOutputFolderCommand;

        public IAsyncRelayCommand OpenRepairInputFolderCommand => _openRepairInputFolderCommand ??= new AsyncRelayCommand(OpenRepairInputFolderAsync, () => !string.IsNullOrWhiteSpace(InputDirectory));
        public IRelayCommand OpenRepairOutputFolderCommand => _openRepairOutputFolderCommand ??= new RelayCommand(OpenRepairOutputFolder, CanOpenRepairOutputFolder);

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
            var allRepairEntries = Tasks.SelectMany(t => t.Entries)
                .Where(e => e.NeedsRepair && e.Status != ProcessStatus.Success).ToList();
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

        private Stopwatch _stopwatch = new();
        private bool _repairStoppedByUser;
        private bool _repairDone;
        private readonly Dictionary<RepairFileEntry, DateTimeOffset> _taskProcessingStartTimes = new();
        private readonly DispatcherTimer _uiUpdateTimer;
        private volatile int _completedEntriesCount;
        private int _totalRepairEntries; // 按 Entry（文件）计数，配对格子里的两个文件各算一个

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

        /// <summary>
        /// 查找某个 RepairFileEntry 所属的 RepairTask（用于滚动事件等）
        /// </summary>
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
                        return Directory.GetFiles(InputDirectory, "*.*", SearchOption.TopDirectoryOnly)
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

                // ── 按文件名配对（参照 ComboScanService）──
                var imgDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                var vidDict = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

                foreach (var file in files)
                {
                    string stem = Path.GetFileNameWithoutExtension(file);
                    string ext = Path.GetExtension(file).ToLowerInvariant();
                    if (ext == ".jpg" || ext == ".jpeg" || ext == ".heic" || ext == ".heif")
                    {
                        // 同名文件冲突：后来的覆盖前面的
                        imgDict[stem] = file;
                    }
                    else if (ext == ".mov" || ext == ".mp4")
                    {
                        vidDict[stem] = file;
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

                // 组装有序工作列表：实况照片组合 → 单独照片 → 单独视频
                var workItems = new List<(string? imagePath, string? videoPath, string baseName, bool isPaired)>();
                foreach (var (img, vid, name) in pairList)
                    workItems.Add((img, vid, name, true));
                foreach (var (img, name) in standaloneImgList)
                    workItems.Add((img, null, name, false));
                foreach (var (vid, name) in standaloneVidList)
                    workItems.Add((null, vid, name, false));

                // 计算统计
                int pairCount = workItems.Count(w => w.isPaired);
                int standaloneImg = workItems.Count(w => !w.isPaired && w.imagePath != null);
                int standaloneVid = workItems.Count(w => !w.isPaired && w.videoPath != null);
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
                    using var persistentExifTool = hasExifTool
                        ? new PersistentExifTool(exifToolPath) : null;

                    // 订阅 exiftool 崩溃自动重启事件 → 状态栏通知用户
                    if (persistentExifTool != null)
                    {
                        persistentExifTool.OnRestarted += (msg) =>
                        {
                            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                            {
                                AppendDirectStatus(msg);
                            });
                        };
                    }

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

                    for (int wi = 0; wi < workItems.Count; wi++)
                    {
                        if (token.IsCancellationRequested) break;

                        var (imagePath, videoPath, baseName, isPaired) = workItems[wi];

                        taskGridIndex = wi + 1;

                        RepairFileEntry? imageEntry = null;
                        RepairFileEntry? videoEntry = null;

                        // 分析照片（如果有）
                        if (imagePath != null)
                        {
                            imageEntry = await AnalyzeFileAndCreateEntry(
                                imagePath, persistentExifTool, heicRepairEnabled, token);
                            if (imageEntry != null) { entryIndex++; processedCount++; }
                        }

                        // 分析视频（如果有）
                        if (videoPath != null)
                        {
                            videoEntry = await AnalyzeFileAndCreateEntry(
                                videoPath, persistentExifTool, heicRepairEnabled, token);
                            if (videoEntry != null) { entryIndex++; processedCount++; }

                            // 未启用"修复非实况照片视频" → 时长 > 3.5s 的非实况视频直接标为已跳过
                            bool repairNonLivePhoto = AppSettingsService.GetValue("IsNonLivePhotoVideoRepairEnabled", false);
                            if (!repairNonLivePhoto && videoEntry?.AnalysisResult != null
                                && videoEntry.AnalysisResult.VideoDurationSeconds > LivePhotoConstants.MaxLivePhotoVideoDurationSeconds)
                            {
                                videoEntry.NeedsRepair = false;
                                videoEntry.Details = ResourceService.GetString("RepairPage_Task_SkippedNonLivePhoto");
                            }
                        }

                        // 如果两个都被取消了，直接退出
                        if (token.IsCancellationRequested) break;

                        if (imageEntry == null && videoEntry == null) continue;

                        // 检查视频时长：> 3.5s 不是实况照片，已配对的拆开
                        bool isLivePhotoVideo = videoEntry != null
                            && (videoEntry.AnalysisResult?.VideoDurationSeconds ?? 0) <= LivePhotoConstants.MaxLivePhotoVideoDurationSeconds;
                        bool effectivePaired = isPaired && isLivePhotoVideo;

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

                        // 严格模式下，单独照片检测是否曾是实况照片（有 UUID 但视频缺失）
                        if (strictScan && !effectivePaired && imageEntry != null && videoEntry == null)
                        {
                            if (imageEntry.AnalysisResult?.HasContentIdentifier == true)
                            {
                                imageEntry.IssueDescription = ResourceService.GetString("RepairPage_LivePhotoVideoMissing") ?? "Live Photo (video missing)";
                                imageEntry.NeedsRepair = false;
                            }
                        }

                        if (isPaired && !effectivePaired)
                        {
                            // 拆为两个独立项
                            if (imageEntry != null)
                            {
                                // 照片独立项：序号回退到分析照片时的值
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
                            // 构建 RepairTask：照片永远是 File1Entry（参照 ComboTask 以 Image 为主）
                            var file1 = imageEntry ?? videoEntry!;
                            var file2 = effectivePaired ? (imageEntry != null ? videoEntry : imageEntry) : null;

                            // 序号：配对时 File1 和 File2 各占一个序号
                            int file1Idx = effectivePaired ? (imageEntry != null ? entryIndex - 1 : entryIndex) : entryIndex;
                            int file2Idx = effectivePaired ? entryIndex : 0;

                            var repairTask = new RepairTask(file1Idx, file2Idx, baseName, effectivePaired, file1, file2);
                            repairTask.Index = taskGridIndex; // 格子序号供滚动定位
                            itemBuffer.Add(repairTask);
                        }

                        scanProgress.Report(new WorkProgressSnapshot(totalFiles, processedCount));

                        if (Environment.TickCount64 - lastFlushMs >= flushIntervalMs)
                        {
                            FlushBuffer(processedCount);
                        }
                    }

                    // 正常扫描完成才报告 100%
                    if (!token.IsCancellationRequested)
                    {
                        scanProgress.Report(new WorkProgressSnapshot(totalFiles, totalFiles));
                    }
                    FlushBuffer(processedCount);
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

        /// <summary>
        /// 分析单个文件并创建 RepairFileEntry。返回 null 表示被取消。
        /// </summary>
        private async Task<RepairFileEntry?> AnalyzeFileAndCreateEntry(
            string filePath, PersistentExifTool? persistentExifTool,
            bool heicRepairEnabled, CancellationToken token)
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
                // 扁平化：从所有 Task 中提取需要修复的 Entry，配对格子里的两个文件分别处理
                bool repairNonLivePhoto = AppSettingsService.GetValue("IsNonLivePhotoVideoRepairEnabled", false);
                var repairEntries = Tasks.SelectMany(t => t.Entries)
                    .Where(e => e.NeedsRepair && e.Status != ProcessStatus.Success)
                    .Where(e =>
                    {
                        // 未启用"修复非实况照片视频"：跳过时长 > 3.5s 的普通长视频
                        if (repairNonLivePhoto) return true;
                        if (!e.IsImage && (e.AnalysisResult?.VideoDurationSeconds ?? 0) > LivePhotoConstants.MaxLivePhotoVideoDurationSeconds)
                            return false;
                        return true;
                    }).ToList();

                await Task.Run(async () =>
                {
                    int userThreads = AppSettingsService.GetValue("SplitThreadCount", Environment.ProcessorCount);
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

                            string targetPath = IsOutputToDirectory
                                ? Path.Combine(OutputDirectory, entry.FileName)
                                : entry.FilePath;

                            bool isSuccess = false;
                            string detailMessage = string.Empty;
                            bool isCanceled = false;

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
            catch (Exception ex)
            {
                LogService.Repair($"RunTasksAsync error: {ex.Message}", LogLevel.Error, ex);
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

                if (Tasks.Count > 0 && !_isCleaningUp)
                {
                    if (wasCancelled)
                        await ShowRepairCancelledDialogAsync();
                    else
                        await ShowRepairAlreadyDoneDialogAsync();
                }
            }
        }

        public event EventHandler<RepairTask>? TaskStartedForScroll;
        public event EventHandler? ProcessingCompletedForScroll;
        public event EventHandler? ScanItemsFlushed;

        private void UpdateEntryStarted(RepairFileEntry entry, RepairTask parentTask)
        {
            entry.Status = ProcessStatus.Processing;
            entry.Details = ResourceService.GetString("Task_Processing");
            _taskProcessingStartTimes[entry] = DateTimeOffset.UtcNow;
            TaskStartedForScroll?.Invoke(this, parentTask);
        }

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

        private void UpdateEntryCancelled(RepairFileEntry entry, string detailMessage)
        {
            entry.Details = detailMessage;
            _taskProcessingStartTimes.Remove(entry);
        }

        private async Task EnsureMinimumProcessingDisplayAsync(RepairFileEntry entry)
        {
            if (!_taskProcessingStartTimes.TryGetValue(entry, out var startedAt)) return;
            var remaining = MinimumProcessingDisplayDuration - (DateTimeOffset.UtcNow - startedAt);
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining).ConfigureAwait(false);
        }

        [RelayCommand]
        private async Task PickInputDirectoryAsync()
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null)
            {
                InputDirectory = folder.Path;
            }
        }

        [RelayCommand]
        private async Task PickOutputDirectoryAsync()
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null)
            {
                OutputDirectory = folder.Path;
            }
        }

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

        private bool CanOpenRepairOutputFolder()
        {
            var folderPath = GetRepairResultFolderPath();
            return !string.IsNullOrWhiteSpace(folderPath);
        }

        private string GetRepairResultFolderPath()
        {
            return IsOutputToDirectory ? OutputDirectory : InputDirectory;
        }

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
