using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LivePhotoBox.Collections;
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
    public partial class SplitViewModel : WorkViewModelBase
    {
        #region Properties

        public override string PageStatusTag => "Split";

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

        [ObservableProperty]
        private string _outputDirectory = string.Empty;

        partial void OnOutputDirectoryChanged(string value) => _openSplitOutputFolderCommand?.NotifyCanExecuteChanged();

        [ObservableProperty]
        private int _queuedCount = 0;

        [ObservableProperty]
        private int _recognizedCount = 0;

        [ObservableProperty]
        private int _skippedCount = 0;

        [ObservableProperty]
        private bool _isDirectoryPanelOpen = true;

        public string ScanButtonText => IsScanning
            ? ResourceService.GetString("SplitPage_DynamicCancelText")
            : ResourceService.GetString("SplitPage_DynamicScanText");

        public bool CanClickScanButton => !IsProcessing;
        public bool CanEditSelectedFormat => !IsScanning && !IsProcessing;

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
            SetStatus("SplitPage_Status_Running");
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
                    SetStatus("SplitPage_Status_Done", _stopwatch.Elapsed.TotalSeconds);
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

        protected override void OnScanningEnded()
        {
            base.OnScanningEnded();
            _scanCancellationTokenSource?.Dispose();
            _scanCancellationTokenSource = null;
        }

        #endregion

        #region Fields

        private Stopwatch _stopwatch = new();
        private bool _splitStoppedByUser;
        private bool _splitDone;

        private readonly DispatcherTimer _uiUpdateTimer;
        private volatile int _completedTasksCount;

        public new string ActionBtnText
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

        public event EventHandler<SplitTask>? TaskStartedForScroll;
        public event EventHandler? ProcessingCompletedForScroll;

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
            AppLogService.Split($"ScanDirectory requested. Input='{InputDirectory}', Output='{OutputDirectory}'");

            if (!TryGuardScanClick()) return;
            if (IsProcessing) return;

            if (IsScanning)
            {
                CancelScanning();
                SetStatus("SplitPage_Status_ScanCancelling");
                return;
            }

            if (string.IsNullOrWhiteSpace(InputDirectory) || !Directory.Exists(InputDirectory))
            {
                await ShowNoInputDirectoryDialogAsync("Split");
                return;
            }

            IsScanning = true;
            var token = GetScanningToken();
            IsDirectoryPanelOpen = false;

            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                OutputDirectory = Path.Combine(InputDirectory, "Output_SplitPhotos");
            }

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
                var pendingText = ResourceService.GetString("SplitPage_Task_Pending");
                var scanProgress = CreateScanProgressReporter();

                try { await Task.Delay(1000, token); } catch (TaskCanceledException) { }

                var scanResult = await Task.Run(
                    () => LivePhotoSplitScanService.Scan(InputDirectory, token, scanProgress),
                    token);

                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();

                int index = 0;
                var tempTasks = scanResult.Files.Select(file =>
                {
                    index++;
                    return new SplitTask
                    {
                        Index = index,
                        SourceFileName = Path.GetFileName(file.SourcePath),
                        SourcePath = file.SourcePath,
                        FileSize = FormatFileSize(file.FileSizeBytes),
                        ProgressText = "0%",
                        Status = ProcessStatus.Pending,
                        Details = pendingText
                    };
                }).ToList();

                Tasks.ReplaceRange(tempTasks);
                QueuedCount = scanResult.Files.Count;
                RecognizedCount = scanResult.RecognizedCount;
                SkippedCount = scanResult.SkippedCount;

                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    Progress = 0;
                    ProgressText = $"0/{QueuedCount}";
                });

                FlushPendingScanProgress();
                CompleteScanSnapshot();

                if (QueuedCount > 0)
                    SetStatus("SplitPage_Status_ScanDone", QueuedCount);
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
                AppLogService.Split($"ScanDirectory error: {ex.Message}", LogLevel.Error, ex);
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

        [RelayCommand]
        private void ToggleSecondaryAction()
        {
            AppLogService.Split($"ToggleSecondaryAction requested. IsProcessing={IsProcessing}, IsPaused={IsPaused}");

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
        public async Task StartProcessingAsync()
        {
            AppLogService.Split("StartProcessing requested.");

            if (IsProcessing)
            {
                SetStatus("SplitPage_Status_Aborted");
                CancelProcessing();
                IsDirectoryPanelOpen = true;
                OnPropertyChanged(nameof(ActionBtnText));
                return;
            }

            if (_splitStoppedByUser || _splitDone)
            {
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
                OutputDirectory = Path.Combine(InputDirectory, "Output_SplitPhotos");
            }

            IsDirectoryPanelOpen = false;
            await RunTasksAsync();
        }

        private async Task ShowSplitAlreadyStoppedDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString("Msg_EmptyQueueTitle"),
                    Content = new TextBlock { Text = ResourceService.GetString("Msg_SplitAlreadyStopped"), FontSize = 16, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = App.MainWindow.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }

        private async Task ShowSplitAlreadyDoneDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                int total = Tasks.Count;
                int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                var summary = ResourceService.Format("Msg_SplitCompletedSummary", total, succeeded, failed, Environment.NewLine);

                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString("Msg_SplitCompletedTitle"),
                    Content = new TextBlock
                    {
                        Text = summary,
                        FontSize = 16,
                        TextWrapping = TextWrapping.Wrap
                    },
                    PrimaryButtonText = ResourceService.GetString("Msg_OpenOutputFolder"),
                    CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = App.MainWindow.Content.XamlRoot
                };

                var result = await dialog.ShowAsync();
                if (result == ContentDialogResult.Primary)
                {
                    OpenSplitOutputFolder();
                }
            }
        }

        private async Task ShowSplitNotAllowedDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString("Msg_EmptyQueueTitle"),
                    Content = new TextBlock { Text = ResourceService.GetString("Msg_ProcessingNotAllowed"), FontSize = 16, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = App.MainWindow.Content.XamlRoot
                };
                await dialog.ShowAsync();
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
                    int maxParallel = AppSettingsService.GetValue("SplitThreadCount", Environment.ProcessorCount);

                    // 使用 SemaphoreSlim 限制并发数
                    using var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
                    var pendingTasks = new List<Task>();
                    int localCompletedCount = 0;
                    var lockObj = new object();

                    async Task ProcessTask(SplitTask task)
                    {
                        await semaphore.WaitAsync(token);

                        try
                        {
                            PauseEvent.Wait(token);
                            if (token.IsCancellationRequested)
                            {
                                throw new OperationCanceledException();
                            }

                            App.MainWindow?.DispatcherQueue.TryEnqueue(() => UpdateTaskStarted(task));

                            bool isSuccess;
                            string detailMessage;

                            try
                            {
                                await LivePhotoSplitService.SplitAsync(task.SourcePath, outputDir, formatIndex, token);
                                isSuccess = true;
                                detailMessage = ResourceService.GetString("SplitPage_Task_Success");
                            }
                            catch (OperationCanceledException)
                            {
                                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                                    UpdateTaskCompleted(task, false, ResourceService.GetString("Status_Aborted") ?? "???", _completedTasksCount));
                                throw;
                            }
                            catch (Exception ex)
                            {
                                isSuccess = false;
                                detailMessage = ResourceService.Format("Task_Error", ex.Message);
                            }

                            lock (lockObj)
                            {
                                localCompletedCount++;
                                _completedTasksCount = localCompletedCount;
                            }

                            App.MainWindow?.DispatcherQueue.TryEnqueue(() => UpdateTaskCompleted(task, isSuccess, detailMessage, _completedTasksCount));
                        }
                        finally
                        {
                            semaphore.Release();
                        }
                    }

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
                                // 取消处理
                                break;
                            }
                        }
                    }

                    // 等待所有剩余任务完成
                    if (!token.IsCancellationRequested)
                    {
                        await Task.WhenAll(pendingTasks);
                    }
                }, token);
            }
            catch (OperationCanceledException)
            {
                SetStatus("SplitPage_Status_Aborted");
            }
            catch (Exception ex)
            {
                AppLogService.Split($"RunTasksAsync error: {ex.Message}", LogLevel.Error, ex);
            }
            finally
            {
                _stopwatch.Stop();
                FinalizeRunState();

                if (!_cancelledByUser && Tasks.Count > 0)
                {
                    await ShowSplitAlreadyDoneDialogAsync();
                }
            }
        }

        private void UpdateTaskStarted(SplitTask task)
        {
            task.Status = ProcessStatus.Processing;
            task.ProgressText = "0%";
            task.Details = ResourceService.GetString("SplitPage_Task_Processing");

            _ = task.EnsureThumbnailAsync(App.MainWindow?.DispatcherQueue);

            TaskStartedForScroll?.Invoke(this, task);
        }

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

        #endregion

        #region Folder Operations

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
            catch (Exception ex) { AppLogService.Split($"OpenSplitInput error: {ex.Message}", LogLevel.Error, ex); }
        }

        private void OpenSplitOutputFolder()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(OutputDirectory)) return;
                if (!Directory.Exists(OutputDirectory))
                    Directory.CreateDirectory(OutputDirectory);
                FilePickerService.OpenFolderInExplorer(OutputDirectory);
            }
            catch (Exception ex) { AppLogService.Split($"OpenSplitOutput error: {ex.Message}", LogLevel.Error, ex); }
        }

        #endregion

        #region Settings

        public int SelectedFormatIndex
        {
            get => AppSettingsService.GetValue(nameof(SelectedFormatIndex), 0);
            set
            {
                AppSettingsService.SetValue(nameof(SelectedFormatIndex), value);
                OnPropertyChanged();
            }
        }

        #endregion

        #region Helpers

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }

        #endregion
    }
}