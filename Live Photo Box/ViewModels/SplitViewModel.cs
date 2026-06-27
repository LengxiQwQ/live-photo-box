using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LivePhotoBox.Collections;
using LivePhotoBox.Helpers;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.ViewModels
{
    // 实况照片拆分页面的 ViewModel。
    // 负责扫描输入目录中的实况照片（MOV/MP4 文件），将其拆分为独立的照片和视频文件，
    // 并支持输出格式选择、并行处理、暂停/取消等操作。
    // 继承自 WorkViewModelBase，复用扫描/处理/暂停/取消等生命周期管理。
    public partial class SplitViewModel : WorkViewModelBase
    {
        #region Properties

        public override string PageStatusTag => "Split";

        protected override string ProcessingStatusKey => "SplitPage_Status_Running";

        protected override string ProcessingStatusText =>
            ResourceService.Format("SplitPage_Status_Running") + GetHardwareSuffix();

        // 输入目录路径。赋值后自动触发扫描（若当前允许）。
        [ObservableProperty]
        private string _inputDirectory = string.Empty;

        partial void OnInputDirectoryChanged(string value)
        {
            _openSplitInputFolderCommand?.NotifyCanExecuteChanged();
            OutputDirectory = string.Empty;

            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            {
                if (ScanDirectoryCommand.CanExecute(null) && !IsScanning)
                {
                    ScanDirectoryCommand.Execute(null);
                }
            }
        }

        // 拆分输出目录路径。
        [ObservableProperty]
        private string _outputDirectory = string.Empty;

        partial void OnOutputDirectoryChanged(string value) => _openSplitOutputFolderCommand?.NotifyCanExecuteChanged();

        // 已入队的拆分任务总数（扫描完成后确定）。
        [ObservableProperty]
        private int _queuedCount = 0;

        // 扫描识别的实况照片文件数（含已识别但可能跳过的）。
        [ObservableProperty]
        private int _recognizedCount = 0;

        // 扫描中跳过的文件数（非实况照片格式等）。
        [ObservableProperty]
        private int _skippedCount = 0;

        [ObservableProperty]
        private bool _isDirectoryPanelOpen = true;

        // 扫描按钮上的动态文本：扫描中显示"取消"，否则显示"扫描"。
        public string ScanButtonText => IsScanning
            ? ResourceService.GetString("SplitPage_DynamicCancelText")
            : ResourceService.GetString("SplitPage_DynamicScanText");

        // 扫描按钮是否可点击（处理中不可点击）。
        public bool CanClickScanButton => !IsProcessing;
        // 输出格式选择是否可编辑（扫描/处理中不可编辑）。
        public bool CanEditSelectedFormat => !IsScanning && !IsProcessing;

        // 所有拆分任务的集合。
        public BulkObservableCollection<SplitTask> Tasks { get; } = [];

        #endregion

        #region Commands

        private IAsyncRelayCommand? _openSplitInputFolderCommand;
        private IRelayCommand? _openSplitOutputFolderCommand;

        public IAsyncRelayCommand OpenSplitInputFolderCommand => _openSplitInputFolderCommand ??= new AsyncRelayCommand(OpenSplitInputFolderAsync, () => !string.IsNullOrWhiteSpace(InputDirectory));
        public IRelayCommand OpenSplitOutputFolderCommand => _openSplitOutputFolderCommand ??= new RelayCommand(OpenSplitOutputFolder, () => !string.IsNullOrWhiteSpace(OutputDirectory));

        #endregion

        #region Constructor

        public SplitViewModel()
        {
            SetStatus("SplitPage_Status_Ready");
            SelectedFormatIndex = AppSettingsService.GetValue(nameof(SelectedFormatIndex), 0);

            _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _uiUpdateTimer.Tick += UiUpdateTimer_Tick;
        }

        #endregion

        #region WorkViewModelBase Overrides

        protected override void OnScanStateChanged(bool isScanning)
        {
            OnPropertyChanged(nameof(ScanButtonText));
        }

        protected override void OnBeginScanSession()
        {
            AppViewModel.Instance.BeginSplitScanSession();
        }

        protected override void OnApplyScanProgress(WorkProgressSnapshot snapshot)
        {
            AppViewModel.Instance.ApplySplitScanProgress(snapshot);
            if (!IsScanning)
            {
                RecognizedCount = snapshot.RecognizedCount;
                SkippedCount = snapshot.SkippedCount;
            }
        }

        protected override void OnCompleteScanSnapshot()
        {
            AppViewModel.Instance.CompleteFooterWorkSnapshot();
        }

        protected override void OnInitializeRunState()
        {
            _splitStoppedByUser = false;
            _splitDone = false;
            _completedTasksCount = 0;
            Progress = 0;
            ProgressText = $"0/{QueuedCount}";
            SetDirectStatus(ProcessingStatusText);
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
            OnPropertyChanged(nameof(CanClickScanButton));
            OnPropertyChanged(nameof(CanEditSelectedFormat));

            _uiUpdateTimer.Start();
        }

        protected override void OnFinalizeRunState()
        {
            _uiUpdateTimer.Stop();

            if (_cancelledByUser)
            {
                _splitStoppedByUser = true;
            }
            else
            {
                _splitDone = true;

                if (QueuedCount > 0)
                {
                    Progress = (_completedTasksCount * 100.0) / QueuedCount;
                    ProgressText = $"{_completedTasksCount}/{QueuedCount}";
                }

                if (Progress >= 100)
                {
                    ProgressBarState = Models.ProgressBarState.Success;
                    CompleteScanSnapshot();

                    // ✨ 修复：与 Merge 保持一致，状态栏显示详细的数据统计
                    int total = Tasks.Count;
                    int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                    int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                    double elapsed = _stopwatch.Elapsed.TotalSeconds;

                    SetStatus("Status_SplitCompletedSummary", total, elapsed, succeeded, failed);
                    LogService.Split($"Split completed: {succeeded} succeeded, {failed} failed in {elapsed:F1}s");
                }
            }
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
            OnPropertyChanged(nameof(CanClickScanButton));
            OnPropertyChanged(nameof(CanEditSelectedFormat));
        }

        protected override void OnClearState()
        {
            Tasks.ReplaceRange([]);
            ThumbnailService.ClearCache();
            QueuedCount = 0;
            RecognizedCount = 0;
            SkippedCount = 0;
            _completedTasksCount = 0;
            Progress = 0;
            ProgressText = "0/0";
            _splitStoppedByUser = false;
            _splitDone = false;
            SetStatus("SplitPage_Status_Cleared");
            IsDirectoryPanelOpen = true;
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
            OnPropertyChanged(nameof(CanClickScanButton));
            OnPropertyChanged(nameof(CanEditSelectedFormat));
        }

        protected override void OnCleanup()
        {
            _uiUpdateTimer.Stop();
            _uiUpdateTimer.Tick -= UiUpdateTimer_Tick;
            Tasks.ReplaceRange([]);
            ThumbnailService.ClearCache();
        }

        protected override void OnScanningEnded()
        {
            base.OnScanningEnded();
            _scanCancellationTokenSource?.Dispose();
            _scanCancellationTokenSource = null;
        }

        #endregion

        #region Fields

        // 拆分处理计时器。
        private Stopwatch _stopwatch = new();
        // 是否被用户手动停止。
        private bool _splitStoppedByUser;
        // 是否自然完成。
        private bool _splitDone;

        // UI 更新定时器（60ms 间隔），用于刷新进度条和文本。
        private readonly DispatcherTimer _uiUpdateTimer;
        // 已完成的任务数（线程安全，volatile）。
        private volatile int _completedTasksCount;

        public override string ActionBtnText
        {
            get
            {
                if (IsProcessing)
                {
                    if (_cancelledByUser) return ResourceService.GetString("Btn_Stopping");
                    return ResourceService.GetString("Btn_Stop");
                }
                return ResourceService.GetString("Btn_StartSplit");
            }
        }

        public override bool IsProcessingAllowed => !IsScanning;

        #endregion

        // 某个拆分任务开始处理时触发，供 ListView 滚动到对应项。
        public event EventHandler<SplitTask>? TaskStartedForScroll;
        // 全部处理完成时触发，供 ListView 滚动回顶部。
        public event EventHandler? ProcessingCompletedForScroll;
        // 扫描进度的批量项刷新到 UI 时触发。
        public event EventHandler? ScanItemsFlushed;

        private void UiUpdateTimer_Tick(object? sender, object e)
        {
            if (QueuedCount == 0) return;
            int currentCompleted = _completedTasksCount;
            Progress = (currentCompleted * 100.0) / QueuedCount;
            ProgressText = $"{currentCompleted}/{QueuedCount}";
            CheckAndApplyPendingState();
        }

        #region Scan

        [RelayCommand(AllowConcurrentExecutions = true)]
        public async Task ScanDirectoryAsync()
        {
            if (!TryGuardScanClick()) return;
            if (IsProcessing) return;

            if (IsScanning)
            {
                CancelScanning();
                SetStatus("SplitPage_Status_ScanCancelling");
                return;
            }

            if (string.IsNullOrWhiteSpace(InputDirectory))
            {
                await ShowNoInputDirectoryDialogAsync("Split");
                return;
            }
            if (!Directory.Exists(InputDirectory))
            {
                await ShowInvalidInputDirectoryDialogAsync();
                return;
            }

            IsScanning = true;
            var token = GetScanningToken();
            IsDirectoryPanelOpen = false;

            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                OutputDirectory = Path.Combine(InputDirectory, ResourceService.GetString("OutputDir_SplitPhotos"));
            }

            LogService.Split($"ScanDirectory requested. Input='{InputDirectory}', Output='{OutputDirectory}'");

            SetStatus("SplitPage_Status_Scanning");
            BeginScanSession();
            await Task.Yield();
            NotifyStatusChanged();

            _scanCancelledByUser = false;
            _splitStoppedByUser = false;
            _splitDone = false;

            try
            {
                ThumbnailService.ClearCache();
                Tasks.ReplaceRange([]);
                QueuedCount = 0;
                var pendingText = ResourceService.GetString("SplitPage_Task_Pending");
                var scanProgress = CreateScanProgressReporter();

                if (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(1000, token); } catch (TaskCanceledException) { }
                }

                // 流式缓冲：每 120ms 刷新到 UI（拆分页面逐文件扫描，需要流式加载）
                var itemBuffer = new List<SplitTask>();
                var bufferLock = new object();
                long lastFlushMs = Environment.TickCount64;
                const long flushIntervalMs = 120;
                int streamIndex = 0;

                void FlushBuffer()
                {
                    List<SplitTask> batch;
                    lock (bufferLock)
                    {
                        if (itemBuffer.Count == 0) return;
                        batch = new List<SplitTask>(itemBuffer);
                        itemBuffer.Clear();
                    }
                    if (batch.Count > 0)
                    {
                        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                        {
                            foreach (var t in batch) Tasks.Add(t);
                            QueuedCount = Tasks.Count;
                            ScanItemsFlushed?.Invoke(this, EventArgs.Empty);
                        });
                    }
                }

                var itemProgress = new Progress<LivePhotoSplitFileInfo>(file =>
                {
                    int idx = Interlocked.Increment(ref streamIndex);
                    var task = new SplitTask
                    {
                        Index = idx,
                        SourceFileName = Path.GetFileName(file.SourcePath),
                        SourcePath = file.SourcePath,
                        FileSize = FileSizeFormatter.Format(file.FileSizeBytes),
                        ProgressText = "0%",
                        Status = ProcessStatus.Pending,
                        Details = pendingText
                    };

                    lock (bufferLock) { itemBuffer.Add(task); }

                    var now = Environment.TickCount64;
                    if (now - lastFlushMs >= flushIntervalMs)
                    {
                        lastFlushMs = now;
                        FlushBuffer();
                    }
                });

                var scanResult = await Task.Run(
                    () => LivePhotoSplitScanService.Scan(InputDirectory, token, scanProgress, itemProgress),
                    token);

                // 刷新残留项，然后用扫描结果的确切数量修正
                FlushBuffer();
                int finalCount = scanResult.Files.Count;
                RecognizedCount = scanResult.RecognizedCount;
                SkippedCount = scanResult.SkippedCount;

                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();

                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    QueuedCount = finalCount;
                    Progress = 0;
                    ProgressText = $"0/{finalCount}";
                });

                FlushPendingScanProgress();
                CompleteScanSnapshot();

                if (finalCount > 0)
                    SetStatus("SplitPage_Status_ScanDone", finalCount);
                else
                {
                    IsDirectoryPanelOpen = true;
                    SetStatus("SplitPage_Status_NoLivePhotos");
                }
            }
            catch (OperationCanceledException)
            {
                SetStatus("SplitPage_Status_ScanCancelled");

                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    Tasks.ReplaceRange([]);
                    ThumbnailService.ClearCache();
                    QueuedCount = 0;
                    RecognizedCount = 0;
                    SkippedCount = 0;
                    Progress = 0;
                    ProgressText = "0/0";
                    OnPropertyChanged(nameof(IsProcessingAllowed));
                    OnPropertyChanged(nameof(CanClickScanButton));
                    OnPropertyChanged(nameof(CanEditSelectedFormat));
                });

                AppViewModel.Instance.ResetFooterScanCounters();
            }
            catch (Exception ex)
            {
                LogService.Split($"ScanDirectory error: {ex.Message}", LogLevel.Error, ex);
                SetStatus("Status_Error", ex.Message);
            }
            finally
            {
                IsScanning = false;
                OnScanningEnded();
                NotifyStatusChanged();
                OnPropertyChanged(nameof(CanClickScanButton));
                OnPropertyChanged(nameof(CanEditSelectedFormat));
            }
        }

        #endregion

        #region Process

        // 切换次要操作（暂停/继续 或 清空列表），取决于当前处理状态。
        [RelayCommand]
        private void ToggleSecondaryAction()
        {
            LogService.Split($"ToggleSecondaryAction requested. IsProcessing={IsProcessing}, IsPaused={IsPaused}");

            if (!IsProcessing)
            {
                ClearState();
            }
            else
            {
                TogglePause();
            }
        }

        // 切换处理状态：运行时点击停止，已完成时弹出结果对话框，空闲时启动拆分处理。
        [RelayCommand(AllowConcurrentExecutions = true)]
        public async Task StartProcessingAsync()
        {
            LogService.Split("StartProcessing requested.");

            if (IsProcessing)
            {
                SetStatus("Status_Stopping");
                CancelProcessing();
                IsDirectoryPanelOpen = true;
                OnPropertyChanged(nameof(ActionBtnText));
                return;
            }

            if (_splitStoppedByUser || _splitDone)
            {
                if (_splitStoppedByUser)
                    await ShowSplitCancelledDialogAsync();
                else
                    await ShowSplitAlreadyDoneDialogAsync();
                return;
            }

            if (Tasks.Count == 0)
            {
                await ShowEmptyQueueDialogAsync("Split");
                return;
            }

            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                OutputDirectory = Path.Combine(InputDirectory, ResourceService.GetString("OutputDir_SplitPhotos"));
            }

            IsDirectoryPanelOpen = false;
            await RunTasksAsync();
        }


        // 弹出一个 ContentDialog 窗口展示拆分被取消时的汇总信息。
        private async Task ShowSplitCancelledDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                int total = Tasks.Count;
                int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                int unprocessed = total - succeeded - failed;

                var stack = new StackPanel { Spacing = 12 };
                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.Format("Msg_SplitCancelledSummary", total, succeeded, failed, unprocessed),
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap
                });
                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.GetString("Msg_SplitCompletedDescription"),
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
                    OpenSplitOutputFolder();
                }
            }
        }

        // 弹出一个 ContentDialog 窗口展示拆分已全部完成时的汇总信息。
        private async Task ShowSplitAlreadyDoneDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                int total = Tasks.Count;
                int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);

                var stack = new StackPanel { Spacing = 12 };
                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.Format("Msg_SplitCompletedSummary", total, succeeded, failed),
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap
                });
                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.GetString("Msg_SplitCompletedDescription"),
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 12, 0, 0),
                    Opacity = 0.85
                });

                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString("Msg_SplitCompletedTitle"),
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
                    OpenSplitOutputFolder();
                }
            }
        }


        private async Task RunTasksAsync()
        {
            InitializeRunState();
            _stopwatch = Stopwatch.StartNew();

            string outputDir = OutputDirectory;
            int formatIndex = SelectedFormatIndex;

            try
            {
                var token = GetProcessingToken();
                await Task.Run(async () =>
                {
                    var tasksToProcess = Tasks.Where(t => t.Status != ProcessStatus.Success).ToList();
                    int maxParallel = AppSettingsService.GetValue("SplitThreadCount", 4);

                    var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
                    var pendingTasks = new List<Task>();
                    int localCompletedCount = 0;
                    var lockObj = new object();

                    async Task ProcessTask(SplitTask task)
                    {
                        await semaphore.WaitAsync(token);

                        bool activeCounted = false;
                        try
                        {
                            PauseEvent.Wait(token);
                            Interlocked.Increment(ref _activeWorkerCount);
                            activeCounted = true;
                            if (token.IsCancellationRequested)
                            {
                                throw new OperationCanceledException();
                            }

                            App.MainWindow?.DispatcherQueue.TryEnqueue(() => UpdateTaskStarted(task));

                            bool isSuccess = false;
                            string detailMessage = string.Empty;
                            bool isCanceled = false;

                            try
                            {
                                await LivePhotoSplitService.SplitAsync(task.SourcePath, outputDir, formatIndex, token, InputDirectory);
                                isSuccess = true;
                                detailMessage = ResourceService.GetString("SplitPage_Task_Success") ?? "Success";
                            }
                            catch (OperationCanceledException)
                            {
                                isCanceled = true;
                                detailMessage = ResourceService.GetString("Status_Aborted") ?? "Aborted";
                            }
                            catch (Exception ex)
                            {
                                isSuccess = false;
                                detailMessage = ResourceService.Format("Task_Error", ex.Message);
                                LogService.Split($"Split failed for {task.SourcePath}: {ex.Message}", LogLevel.Error, ex);
                            }

                            int currentCompleted = 0;
                            if (!isCanceled)
                            {
                                lock (lockObj)
                                {
                                    localCompletedCount++;
                                    currentCompleted = localCompletedCount;
                                    _completedTasksCount = currentCompleted;
                                }
                            }

                            // ✨ 核心修复：死等 UI 线程把状态更新完毕！
                            var tcs = new TaskCompletionSource<bool>();
                            if (App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                            {
                                try
                                {
                                    if (isCanceled)
                                        UpdateTaskCancelled(task, detailMessage);
                                    else
                                        UpdateTaskCompleted(task, isSuccess, detailMessage, currentCompleted);
                                }
                                finally
                                {
                                    tcs.TrySetResult(true);
                                }
                            }) == true)
                            {
                                await tcs.Task;
                            }
                            else
                            {
                                tcs.TrySetResult(true);
                            }

                            if (isCanceled)
                            {
                                throw new OperationCanceledException();
                            }
                        }
                        finally
                        {
                            if (activeCounted)
                                Interlocked.Decrement(ref _activeWorkerCount);
                            try { semaphore.Release(); }
                            catch (ObjectDisposedException) { }
                        }
                    }

                    try
                    {
                        foreach (var task in tasksToProcess)
                        {
                            if (token.IsCancellationRequested)
                            {
                                break;
                            }

                            pendingTasks.Add(ProcessTask(task));

                            // 当达到最大并发数时，等待任意一个完成
                            if (pendingTasks.Count >= maxParallel)
                            {
                                var completedTask = await Task.WhenAny(pendingTasks);
                                pendingTasks.Remove(completedTask);

                                try
                                {
                                    await completedTask;
                                }
                                catch (OperationCanceledException)
                                {
                                    // 取消处理 — break 出循环，后面统一 rethrow
                                    break;
                                }
                            }
                        }

                        // 等待所有剩余任务完全结束（因为内部用了 TaskCompletionSource，执行到这里时所有的 UI 也100%更新完了）
                        if (!token.IsCancellationRequested)
                        {
                            await Task.WhenAll(pendingTasks);
                        }

                        // 如果因取消而退出循环，确保异常传播到外层 catch 更新状态
                        if (token.IsCancellationRequested)
                        {
                            token.ThrowIfCancellationRequested();
                        }
                    }
                    finally
                    {
                        // 先等所有任务退出再 dispose semaphore，避免 ProcessTask 的 finally
                        // 还在调 semaphore.Release() 时 semaphore 已被销毁 → ObjectDisposedException
                        try { await Task.WhenAll(pendingTasks); } catch { }
                        semaphore.Dispose();
                    }
                }, token);
            }
            catch (OperationCanceledException)
            {
                int total = Tasks.Count;
                int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                int unprocessed = total - succeeded - failed;
                double elapsed = _stopwatch.Elapsed.TotalSeconds;
                LogService.Split($"Split processing cancelled by user after {elapsed:F1}s, completed {_completedTasksCount}/{QueuedCount}");
                SetStatus("Status_SplitStoppedSummary", total, elapsed, succeeded, failed, unprocessed);
            }
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                LogService.Split($"RunTasksAsync fatal error: {ex.Message}", LogLevel.Error, ex);
                Environment.ExitCode = unchecked((int)0xE0000001);
                throw;
            }
            finally
            {
                _stopwatch.Stop();
                bool wasCancelled = _cancelledByUser;
                FinalizeRunState();

                // 所有任务结束后统一清理 Temp 目录（确保无残留）
                CleanSplitTempDirectory(outputDir);

                // 关闭中不弹对话框，避免在窗口销毁期间操作 XamlRoot。
                // 多个队列同时完成时 WinUI 只允许一个 ContentDialog，
                // 冲突会抛 COMException，这里吞掉即可（不影响处理结果）。
                if (Tasks.Count > 0 && !_isCleaningUp)
                {
                    try
                    {
                        if (wasCancelled)
                            await ShowSplitCancelledDialogAsync();
                        else
                            await ShowSplitAlreadyDoneDialogAsync();
                    }
                    catch (System.Runtime.InteropServices.COMException ex)
                    {
                        LogService.Debug($"Completion dialog skipped (another dialog already open): {ex.Message}", LogSource.UI);
                    }
                }
            }
        }

        // 更新 Task 为"处理中"状态，触发滚动事件。
        private void UpdateTaskStarted(SplitTask task)
        {
            task.Status = ProcessStatus.Processing;
            task.ProgressText = "0%";
            task.Details = ResourceService.GetString("SplitPage_Task_Processing");

            TaskStartedForScroll?.Invoke(this, task);
        }

        // 更新 Task 为"已完成/失败"状态，触发完成滚动事件（若全部完成）。
        private void UpdateTaskCompleted(SplitTask task, bool isSuccess, string detailMessage, int completedCount)
        {
            task.Status = isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
            task.ProgressText = isSuccess ? "100%" : "0%";
            task.Details = detailMessage;

            if (completedCount >= Tasks.Count && Tasks.Count > 0)
            {
                ProcessingCompletedForScroll?.Invoke(this, EventArgs.Empty);
            }
        }

        // 更新被取消的 Task 的状态（保留 Processing，不标记失败）。
        private void UpdateTaskCancelled(SplitTask task, string detailMessage)
        {
            // 用户取消不标记为"失败"——保留 Processing 状态，颜色中性，只更新详情
            task.ProgressText = "0%";
            task.Details = detailMessage;
        }

        #endregion

        #region Folder Operations

        // 在文件管理器中打开输入文件夹。
        private async Task OpenSplitInputFolderAsync()
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
            catch (Exception ex) { LogService.Split($"OpenSplitInput error: {ex.Message}", LogLevel.Error, ex); }
        }

        // 在文件管理器中打开拆分输出文件夹。
        private void OpenSplitOutputFolder()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(OutputDirectory)) return;
                if (!Directory.Exists(OutputDirectory))
                    Directory.CreateDirectory(OutputDirectory);
                FilePickerService.OpenFolderInExplorer(OutputDirectory);
            }
            catch (Exception ex) { LogService.Split($"OpenSplitOutput error: {ex.Message}", LogLevel.Error, ex); }
        }

        #endregion

        #region Settings

        // 拆分输出格式索引（实时读写 AppSettings）。
        public int SelectedFormatIndex
        {
            get => AppSettingsService.GetValue(nameof(SelectedFormatIndex), 0);
            set
            {
                AppSettingsService.SetValue(nameof(SelectedFormatIndex), value);
                LogService.Split($"Split output format changed to index: {value}");
                OnPropertyChanged();
            }
        }

        #endregion

        // 安全地清理拆分过程的 Temp 目录（全部任务结束后调用）。
        // 所有临时文件已在 SplitAsync 中逐个删除，这里清理可能残留的空 Temp 目录。
        private static void CleanSplitTempDirectory(string outputDir)
        {
            if (string.IsNullOrWhiteSpace(outputDir)) return;
            try
            {
                string tempDir = Path.Combine(outputDir, "Temp");
                if (Directory.Exists(tempDir))
                {
                    Directory.Delete(tempDir, recursive: true);
                    LogService.Split($"Cleaned split Temp directory: {tempDir}");
                }
            }
            catch (Exception ex)
            {
                LogService.Split($"Failed to clean split Temp directory: {ex.Message}", LogLevel.Warning);
            }
        }
    }
}