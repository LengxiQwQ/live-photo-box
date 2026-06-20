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

        // 记录 IsOutputToDirectory 的上一次值，用于在 OnIsOutputToDirectoryChanged 中判断"切换方向"
        // 源生成器在调用 partial method 之前已经更新了 backing field，所以这里需要单独缓存旧值
        private bool _previousIsOutputToDirectory = false;

        partial void OnIsOutputToDirectoryChanged(bool value)
        {
            AppSettingsService.SetValue(nameof(IsOutputToDirectory), value);
            _openRepairOutputFolderCommand?.NotifyCanExecuteChanged();

            // 只有"从关闭切换到打开"时，且目录面板当前是收起的，才自动展开
            // 关闭方向完全不触碰 IsDirectoryPanelOpen
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
                    OutputDirectory = Path.Combine(InputDirectory, "Output_RepairedPhotos");
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

        [ObservableProperty]
        private bool _isDirectoryPanelOpen = true;

        public string ScanButtonText => IsScanning
            ? ResourceService.GetString("RepairPage_DynamicCancelText")
            : ResourceService.GetString("RepairPage_DynamicScanText");

        public BulkObservableCollection<RepairTask> Tasks { get; } = [];

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

        protected override void OnScanStateChanged(bool isScanning)
        {
            OnPropertyChanged(nameof(ScanButtonText));
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
            if (_totalRepairTasks == 0) return;
            int currentCompleted = _completedTasksCount;
            Progress = (currentCompleted * 100.0) / _totalRepairTasks;
            ProgressText = $"{currentCompleted}/{_totalRepairTasks}";
            CheckAndApplyPendingState();
        }

        protected override void OnInitializeRunState()
        {
            _repairStoppedByUser = false;
            _repairDone = false;

            // 进度只算真正需要修复的任务，跳过的照片不参与进度条
            _completedTasksCount = 0;
            _totalRepairTasks = Tasks.Count(t => t.NeedsRepair && t.Status != ProcessStatus.Success);

            Progress = 0;
            ProgressText = _totalRepairTasks == 0 ? "0/0" : $"0/{_totalRepairTasks}";
            _taskProcessingStartTimes.Clear();

            SetStatus("Status_Running");
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

                if (_totalRepairTasks > 0)
                {
                    Progress = (_completedTasksCount * 100.0) / _totalRepairTasks;
                    ProgressText = $"{_completedTasksCount}/{_totalRepairTasks}";
                }

                if (_totalRepairTasks == 0 || Progress >= 100)
                {
                    ProgressBarState = Models.ProgressBarState.Success;
                    CompleteScanSnapshot();

                    // ✨ 同步组合逻辑：渲染统计并使用多语言词条
                    int total = Tasks.Count;
                    int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success && (t.AnalysisResult == null || t.AnalysisResult.IssueType != RepairIssueType.Perfect));
                    int skipped = Tasks.Count(t => t.AnalysisResult != null && t.AnalysisResult.IssueType == RepairIssueType.Perfect);
                    int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                    double elapsed = _stopwatch.Elapsed.TotalSeconds;

                    SetStatus("Status_RepairCompletedSummary", total, elapsed, succeeded, skipped, failed);
                    LogService.Repair($"Repair completed: {succeeded} repaired, {skipped} skipped, {failed} failed in {elapsed:F1}s");
                }
            }
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
        }

        protected override void OnClearState()
        {
            Tasks.ReplaceRange([]);
            TotalPhotosCount = 0;
            ThumbCorrectCount = 0;
            ThumbErrorCount = 0;
            _completedTasksCount = 0;
            _totalRepairTasks = 0;
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
        }

        private Stopwatch _stopwatch = new();
        private bool _repairStoppedByUser;
        private bool _repairDone;
        private readonly Dictionary<RepairTask, DateTimeOffset> _taskProcessingStartTimes = new();
        private readonly DispatcherTimer _uiUpdateTimer;
        private volatile int _completedTasksCount;
        private int _totalRepairTasks; // 只算真正需要修复的任务数，跳过的照片不参与进度

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
                int total = Tasks.Count;
                int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success && (t.AnalysisResult == null || t.AnalysisResult.IssueType != RepairIssueType.Perfect));
                int skipped = Tasks.Count(t => t.AnalysisResult != null && t.AnalysisResult.IssueType == RepairIssueType.Perfect);
                int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
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
                int total = Tasks.Count;
                int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success && (t.AnalysisResult == null || t.AnalysisResult.IssueType != RepairIssueType.Perfect));
                int skipped = Tasks.Count(t => t.AnalysisResult != null && t.AnalysisResult.IssueType == RepairIssueType.Perfect);
                int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);

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

            if (string.IsNullOrWhiteSpace(InputDirectory) || !Directory.Exists(InputDirectory))
            {
                await ShowNoInputDirectoryDialogAsync("Repair");
                return;
            }

            IsScanning = true;

            if (IsOutputToDirectory && string.IsNullOrWhiteSpace(OutputDirectory))
            {
                OutputDirectory = Path.Combine(InputDirectory, "Output_RepairedPhotos");
            }

            var token = GetScanningToken();
            IsDirectoryPanelOpen = false;

            Tasks.ReplaceRange([]);
            TotalPhotosCount = 0;
            ThumbCorrectCount = 0;
            ThumbErrorCount = 0;
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

            // 用户在 1s 延迟期间点了取消 → 直接跳到 finally 清理状态
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
                                             f.EndsWith(".heic", StringComparison.OrdinalIgnoreCase))
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

                TotalPhotosCount = files.Count;
                var scanProgress = CreateScanProgressReporter();
                scanProgress.Report(new WorkProgressSnapshot(files.Count, 0));

                // 创建常驻 exiftool 进程（路径在外层解析，实例在 Task.Run 内创建和销毁，避免 Dispose 阻塞 UI 线程）
                string exifToolPath = ExternalToolLocator.FindExifTool()
                    ?? Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe");
                bool hasExifTool = File.Exists(exifToolPath);

                int processedCount = 0; // 提到 Task.Run 外层，取消分支需要用到

                await Task.Run(async () =>
                {
                    using var persistentExifTool = hasExifTool
                        ? new PersistentExifTool(exifToolPath) : null;

                    // 流式缓冲：每 200ms 批量刷到 UI（参照 Split 页面做法）
                    // 顺序循环，不搞并发 — exiftool 的 _ioLock 本身就是串行的，并发无意义
                    var itemBuffer = new List<RepairTask>();
                    long lastFlushMs = Environment.TickCount64;
                    const long flushIntervalMs = 200;

                    void FlushBuffer(int countSnapshot)
                    {
                        if (itemBuffer.Count == 0) return;
                        var batch = new List<RepairTask>(itemBuffer);
                        itemBuffer.Clear();
                        lastFlushMs = Environment.TickCount64;

                        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                        {
                            int thumbCorrect = 0, thumbError = 0;
                            foreach (var t in batch)
                            {
                                Tasks.Add(t);
                                if (t.NeedsRepair) thumbError++;
                                else thumbCorrect++;
                            }
                            ThumbCorrectCount += thumbCorrect;
                            ThumbErrorCount += thumbError;
                            Progress = files.Count == 0 ? 0 : (countSnapshot * 100.0) / files.Count;
                            ProgressText = $"{countSnapshot}/{files.Count}";
                            ScanItemsFlushed?.Invoke(this, EventArgs.Empty);
                        });
                    }

                    for (int i = 0; i < files.Count; i++)
                    {
                        // 取消了就干净退出，不抛异常
                        if (token.IsCancellationRequested) break;

                        string file = files[i];
                        RepairAnalysisResult analysis;
                        try
                        {
                            analysis = await LivePhotoRepairService.AnalyzeFileAsync(file, persistentExifTool, token);
                        }
                        catch (OperationCanceledException)
                        {
                            break;
                        }

                        var repairTask = new RepairTask
                        {
                            Index = i + 1,
                            FileName = Path.GetFileName(file),
                            FilePath = file,
                            IssueDescription = analysis.IssueDescription,
                            NeedsRepair = analysis.NeedsRepair,
                            Status = ProcessStatus.Pending,
                            Details = analysis.NeedsRepair
                                ? ResourceService.GetString("RepairPage_Task_WaitingRepair")
                                : ResourceService.GetString("RepairPage_Task_Skipped"),
                            AnalysisResult = analysis
                        };

                        itemBuffer.Add(repairTask);
                        processedCount = i + 1;
                        scanProgress.Report(new WorkProgressSnapshot(files.Count, processedCount));

                        if (Environment.TickCount64 - lastFlushMs >= flushIntervalMs)
                        {
                            FlushBuffer(processedCount);
                        }
                    }

                    // 正常扫描完成才报告 100%，取消时保持实际进度不动
                    if (!token.IsCancellationRequested)
                    {
                        scanProgress.Report(new WorkProgressSnapshot(files.Count, files.Count));
                    }
                    FlushBuffer(processedCount);
                }, token);

                FlushPendingScanProgress();

                if (token.IsCancellationRequested)
                {
                    // 用户取消扫描 → 清空列表、重置进度，等同于没扫描过（参照 Split 页面）
                    LogService.Repair($"Scan cancelled by user, {processedCount}/{TotalPhotosCount} files scanned — clearing list");
                    SetStatus("RepairPage_Status_ScanCancelled");

                    App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                    {
                        Tasks.ReplaceRange([]);
                        TotalPhotosCount = 0;
                        ThumbCorrectCount = 0;
                        ThumbErrorCount = 0;
                        Progress = 0;
                        ProgressText = "0/0";
                    });

                    AppViewModel.Instance.ResetFooterScanCounters();
                }
                else if (TotalPhotosCount > 0)
                {
                    CompleteScanSnapshot();
                    SetStatus("RepairPage_Status_ScanDone", ThumbCorrectCount, ThumbErrorCount);
                    LogService.Repair($"Scan completed: {TotalPhotosCount} files, {ThumbCorrectCount} healthy, {ThumbErrorCount} need repair");
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
                // 外层 Task.Run 被取消时到这里（极少见），同样清空列表
                SetStatus("RepairPage_Status_ScanCancelled");
                LogService.Repair("Scan cancelled via OCE — clearing list");

                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    Tasks.ReplaceRange([]);
                    TotalPhotosCount = 0;
                    ThumbCorrectCount = 0;
                    ThumbErrorCount = 0;
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
                    OutputDirectory = Path.Combine(InputDirectory, "Output_RepairedPhotos");
                if (!Directory.Exists(OutputDirectory))
                    Directory.CreateDirectory(OutputDirectory);
            }

            IsDirectoryPanelOpen = false;
            await RunTasksAsync();
        }

        private async Task RunTasksAsync()
        {
            InitializeRunState();
            _stopwatch = Stopwatch.StartNew();

            var token = GetProcessingToken();

            try
            {
                var repairTasks = Tasks.Where(t => t.NeedsRepair && t.Status != ProcessStatus.Success).ToList();

                // 参照 Split/Combo 的分批调度模式：逐个喂任务，满一批等一个完成再喂下一个。
                // 取消了就不喂了，当前批次跑完即停。不会像 Task.WhenAll 那样一次性全塞进去。
                // Task.Run 包裹是因为 PauseEvent.Wait 是同步阻塞，必须在后台线程执行。
                await Task.Run(async () =>
                {
                    int maxParallel = Math.Clamp(Environment.ProcessorCount / 6, 1, 3);
                    var pending = new List<Task>();

                    async Task ProcessOneAsync(RepairTask task)
                    {
                        await Task.Yield(); // 让调度循环有机会跑

                        try { PauseEvent.Wait(token); }
                        catch (OperationCanceledException) { return; }
                        if (token.IsCancellationRequested) return;

                        Interlocked.Increment(ref _activeWorkerCount);
                        try
                        {
                            App.MainWindow?.DispatcherQueue.TryEnqueue(() => UpdateTaskStarted(task));

                            string targetPath = IsOutputToDirectory
                                ? Path.Combine(OutputDirectory, task.FileName)
                                : task.FilePath;

                            bool isSuccess = false;
                            string detailMessage = string.Empty;
                            bool isCanceled = false;

                            try
                            {
                                var result = await LivePhotoRepairService.RepairAsync(task.FilePath, targetPath, task.AnalysisResult!, token);
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
                                LogService.Repair($"Repair failed for {task.FilePath}: {ex.Message}", LogLevel.Error, ex);
                            }

                            if (!isCanceled)
                                Interlocked.Increment(ref _completedTasksCount);

                            await EnsureMinimumProcessingDisplayAsync(task);

                            var tcs = new TaskCompletionSource<bool>();
                            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                            {
                                try
                                {
                                    if (isCanceled)
                                        UpdateTaskCancelled(task, detailMessage);
                                    else
                                        UpdateTaskCompleted(task, isSuccess, detailMessage);
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

                    foreach (var task in repairTasks)
                    {
                        if (token.IsCancellationRequested) break;

                        pending.Add(ProcessOneAsync(task));

                        if (pending.Count >= maxParallel)
                        {
                            var done = await Task.WhenAny(pending);
                            pending.Remove(done);
                            try { await done; }
                            catch (OperationCanceledException) { break; }
                            catch (InvalidOperationException) { throw; }
                        }
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
                    int total = Tasks.Count;
                    int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success && (t.AnalysisResult == null || t.AnalysisResult.IssueType != RepairIssueType.Perfect));
                    int skipped = Tasks.Count(t => t.AnalysisResult != null && t.AnalysisResult.IssueType == RepairIssueType.Perfect);
                    int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                    int unprocessed = total - succeeded - skipped - failed;
                    double elapsed = _stopwatch.Elapsed.TotalSeconds;
                    LogService.Repair($"Repair cancelled by user after {elapsed:F1}s, completed {_completedTasksCount}/{TotalPhotosCount}");
                    SetStatus("Status_RepairStoppedSummary", total, elapsed, succeeded, skipped, failed, unprocessed);
                }

                FinalizeRunState();

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

        private void UpdateTaskStarted(RepairTask task)
        {
            task.Status = ProcessStatus.Processing;
            task.Details = ResourceService.GetString("Task_Processing");
            _taskProcessingStartTimes[task] = DateTimeOffset.UtcNow;
            TaskStartedForScroll?.Invoke(this, task);
        }

        private void UpdateTaskCompleted(RepairTask task, bool isSuccess, string detailMessage)
        {
            task.Status = isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
            task.Details = detailMessage;
            _taskProcessingStartTimes.Remove(task);

            if (_completedTasksCount >= TotalPhotosCount && TotalPhotosCount > 0)
            {
                ProcessingCompletedForScroll?.Invoke(this, EventArgs.Empty);
            }
        }

        private void UpdateTaskCancelled(RepairTask task, string detailMessage)
        {
            // 用户取消不标记为"失败"——保留 Processing 状态，只更新详情
            task.Details = detailMessage;
            _taskProcessingStartTimes.Remove(task);
        }

        private async Task EnsureMinimumProcessingDisplayAsync(RepairTask task)
        {
            if (!_taskProcessingStartTimes.TryGetValue(task, out var startedAt)) return;
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