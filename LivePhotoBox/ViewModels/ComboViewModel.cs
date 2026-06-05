using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Collections;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.ViewModels
{
    public partial class ComboViewModel : WorkViewModelBase
    {
        private string _hwEncoderName = "Software CPU";
        private Stopwatch _stopwatch = new();
        private bool _comboStoppedByUser;
        private bool _comboDone;

        // 【新增 UI 节流 Timer 与原子进度计数器】
        private readonly DispatcherTimer _uiUpdateTimer;
        private volatile int _completedTasksCount;

        public override string PageStatusTag => "Combo";

        [ObservableProperty]
        private string _inputDirectory = string.Empty;

        partial void OnInputDirectoryChanged(string value)
        {
            _openComboInputFolderCommand?.NotifyCanExecuteChanged();
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

        partial void OnOutputDirectoryChanged(string value) => _openComboOutputFolderCommand?.NotifyCanExecuteChanged();

        [ObservableProperty]
        private int _totalPairsCount = 0;

        [ObservableProperty]
        private int _standaloneImagesCount = 0;

        [ObservableProperty]
        private int _standaloneVideosCount = 0;

        [ObservableProperty]
        private bool _isDirectoryPanelOpen = true;

        [ObservableProperty]
        private double _comboProgress = 0;

        public string ScanButtonText => IsScanning
            ? ResourceService.GetString("ComboPage_DynamicCancelText")
            : ResourceService.GetString("ComboPage_DynamicScanText");

        public BulkObservableCollection<MergeTask> Tasks { get; } = [];

        public int SelectedModeIndex
        {
            get => AppSettingsService.GetValue(nameof(SelectedModeIndex), 1);
            set
            {
                AppSettingsService.SetValue(nameof(SelectedModeIndex), value);
                OnPropertyChanged();
            }
        }

        private IAsyncRelayCommand? _openComboInputFolderCommand;
        private IAsyncRelayCommand? _openComboOutputFolderCommand;

        public IAsyncRelayCommand OpenComboInputFolderCommand => _openComboInputFolderCommand ??= new AsyncRelayCommand(OpenComboInputFolderAsync, () => !string.IsNullOrWhiteSpace(InputDirectory));
        public IAsyncRelayCommand OpenComboOutputFolderCommand => _openComboOutputFolderCommand ??= new AsyncRelayCommand(OpenComboOutputFolderAsync, () => !string.IsNullOrWhiteSpace(OutputDirectory));

        public ComboViewModel()
        {
            SetStatus("Status_Init");

            // 初始化 UI 节流 Timer，约 16fps (60ms) 更新一次进度条
            _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _uiUpdateTimer.Tick += UiUpdateTimer_Tick;
        }

        public new string ActionBtnText
        {
            get
            {
                if (IsProcessing)
                {
                    if (_cancelledByUser) return ResourceService.GetString("Btn_Stopping");
                    return ResourceService.GetString("Btn_Stop");
                }
                return ResourceService.GetString("Btn_StartCombo");
            }
        }

        public override bool IsProcessingAllowed => !IsScanning;

        protected override void OnScanStateChanged(bool isScanning)
        {
            OnPropertyChanged(nameof(ScanButtonText));
        }

        protected override void OnBeginScanSession()
        {
            AppViewModel.Instance.BeginComboScanSession();
        }

        protected override void OnApplyScanProgress(WorkProgressSnapshot snapshot)
        {
            AppViewModel.Instance.ApplyComboScanProgress(snapshot);
        }

        protected override void OnCompleteScanSnapshot()
        {
            AppViewModel.Instance.CompleteFooterWorkSnapshot();
        }

        protected override void OnScanningEnded()
        {
            base.OnScanningEnded();
        }

        // 定时拉取进度并更新 UI (解决卡死)
        private void UiUpdateTimer_Tick(object? sender, object e)
        {
            if (TotalPairsCount == 0) return;
            int currentCompleted = _completedTasksCount;
            ComboProgress = (currentCompleted * 100.0) / TotalPairsCount;
            Progress = ComboProgress;
            ProgressText = $"{currentCompleted}/{TotalPairsCount}";
            CheckAndApplyPendingState();
        }

        protected override void OnInitializeRunState()
        {
            _comboStoppedByUser = false;
            _comboDone = false;
            _completedTasksCount = 0;
            ComboProgress = 0;
            Progress = 0;
            ProgressText = $"0/{TotalPairsCount}";
            SetStatus("Status_Running");
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));

            // 开始更新进度条
            _uiUpdateTimer.Start();
        }

        protected override void OnFinalizeRunState()
        {
            _uiUpdateTimer.Stop();

            if (_cancelledByUser)
            {
                _comboStoppedByUser = true;
            }
            else
            {
                _comboDone = true;

                // 强制最后一次 100% 同步
                if (TotalPairsCount > 0)
                {
                    ComboProgress = (_completedTasksCount * 100.0) / TotalPairsCount;
                    Progress = ComboProgress;
                    ProgressText = $"{_completedTasksCount}/{TotalPairsCount}";
                }

                if (ComboProgress >= 100)
                {
                    CompleteScanSnapshot();
                    SetStatus("Status_Done", _stopwatch.Elapsed.TotalSeconds);
                }
            }
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
        }

        protected override void OnClearState()
        {
            Tasks.ReplaceRange([]);
            ThumbnailService.ClearCache();
            TotalPairsCount = 0;
            StandaloneImagesCount = 0;
            StandaloneVideosCount = 0;
            _completedTasksCount = 0;
            ComboProgress = 0;
            Progress = 0;
            ProgressText = "0/0";
            _comboStoppedByUser = false;
            _comboDone = false;
            SetStatus("Status_Cleared", _hwEncoderName);
            IsDirectoryPanelOpen = true;
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
        }

        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task ScanDirectoryAsync()
        {
            AppLogService.Combo($"ScanDirectory requested. Input='{InputDirectory}', Output='{OutputDirectory}'");

            if (!TryGuardScanClick()) return;
            if (IsProcessing) return;

            if (IsScanning)
            {
                CancelScanning();
                SetStatus("Status_ScanCancelling");
                return;
            }

            if (string.IsNullOrWhiteSpace(InputDirectory) || !Directory.Exists(InputDirectory))
            {
                await ShowNoInputDirectoryDialogAsync("Combo");
                return;
            }

            IsScanning = true;
            var token = GetScanningToken();
            IsDirectoryPanelOpen = false;

            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                OutputDirectory = Path.Combine(InputDirectory, "Output_LivePhotos");
            }

            SetStatus("Status_Scanning");
            BeginScanSession();
            await Task.Yield();
            NotifyStatusChanged();

            _scanCancelledByUser = false;
            _comboStoppedByUser = false;
            _comboDone = false;

            try
            {
                ThumbnailService.ClearCache();
                var pendingText = ResourceService.GetString("Task_Pending");
                var scanProgress = CreateScanProgressReporter();

                try { await Task.Delay(1000, token); } catch (TaskCanceledException) { }

                var scanResult = await Task.Run(
                    () => LivePhotoScanService.Scan(InputDirectory, token, scanProgress),
                    token);

                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();

                int index = 0;
                var tempTasks = scanResult.Pairs.Select(pair =>
                {
                    index++;
                    return new MergeTask
                    {
                        Index = index,
                        ImageFileName = Path.GetFileName(pair.ImagePath),
                        VideoFileName = Path.GetFileName(pair.VideoPath),
                        ImageSize = FormatFileSize(pair.ImageSizeBytes),
                        VideoSize = FormatFileSize(pair.VideoSizeBytes),
                        TotalSizeBytes = pair.ImageSizeBytes + pair.VideoSizeBytes,
                        BaseName = pair.BaseName,
                        ImagePath = pair.ImagePath,
                        VideoPath = pair.VideoPath,
                        Status = ProcessStatus.Pending,
                        Details = pendingText
                    };
                }).ToList();

                Tasks.ReplaceRange(tempTasks);
                TotalPairsCount = scanResult.Pairs.Count;
                StandaloneImagesCount = scanResult.StandaloneImagesCount;
                StandaloneVideosCount = scanResult.StandaloneVideosCount;

                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    ComboProgress = 0;
                    Progress = 0;
                    ProgressText = $"0/{TotalPairsCount}";
                });

                FlushPendingScanProgress();
                CompleteScanSnapshot();

                if (TotalPairsCount > 0)
                    SetStatus("Status_ScanDone", TotalPairsCount);
                else
                    SetStatus("Status_ScanNoPairs", StandaloneImagesCount, StandaloneVideosCount);
            }
            catch (OperationCanceledException)
            {
                SetStatus("Status_ScanCancelled");

                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    Tasks.ReplaceRange([]);
                    ThumbnailService.ClearCache();
                    TotalPairsCount = 0;
                    StandaloneImagesCount = 0;
                    StandaloneVideosCount = 0;
                    ComboProgress = 0;
                    Progress = 0;
                    ProgressText = "0/0";
                    OnPropertyChanged(nameof(IsProcessingAllowed));
                });

                AppViewModel.Instance.ResetFooterScanCounters();
            }
            catch (Exception ex)
            {
                AppLogService.Combo($"ScanDirectory error: {ex.Message}", LogLevel.Error, ex);
                SetStatus("Status_Error", ex.Message);
            }
            finally
            {
                IsScanning = false;
                OnScanningEnded();
                NotifyStatusChanged();
            }
        }

        [RelayCommand]
        private void ToggleSecondaryAction()
        {
            AppLogService.Combo($"ToggleSecondaryAction requested. IsProcessing={IsProcessing}, IsPaused={IsPaused}");

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
        private async Task ToggleProcessAsync()
        {
            AppLogService.Combo($"ToggleProcessAsync requested. IsProcessing={IsProcessing}, QueueCount={Tasks.Count}");

            if (IsProcessing)
            {
                SetStatus("Status_Aborted");
                CancelProcessing();
                IsDirectoryPanelOpen = true;
                OnPropertyChanged(nameof(ActionBtnText));
                return;
            }

            if (_comboStoppedByUser || _comboDone)
            {
                await ShowComboAlreadyDoneDialogAsync();
                return;
            }

            if (Tasks.Count == 0)
            {
                await ShowEmptyQueueDialogAsync("Combo");
                return;
            }

            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                SetStatus("Status_WarnOutput");
                return;
            }

            IsDirectoryPanelOpen = false;
            await RunTasksAsync();
        }

        private async Task ShowComboAlreadyStoppedDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString("Msg_EmptyQueueTitle"),
                    Content = new TextBlock { Text = ResourceService.GetString("Msg_ComboAlreadyStopped"), FontSize = 16, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = App.MainWindow.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }

        private async Task ShowComboAlreadyDoneDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString("Msg_EmptyQueueTitle"),
                    Content = new TextBlock { Text = ResourceService.GetString("Msg_ComboAlreadyDone"), FontSize = 16, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = App.MainWindow.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }

        private async Task ShowComboNotAllowedDialogAsync()
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

            var token = GetProcessingToken();

            try
            {
                var options = new LivePhotoBatchRunOptions
                {
                    OutputDirectory = OutputDirectory,
                    SelectedModeIndex = SelectedModeIndex
                };

                await LivePhotoBatchRunnerService.RunAsync(
                    Tasks,
                    options,
                    PauseEvent,
                    token,
                    task => App.MainWindow?.DispatcherQueue.TryEnqueue(() => UpdateTaskStarted(task)),
                    (task, isSuccess, detailMessage, completedCount) =>
                    {
                        // 先原子化更新数量供 Timer 使用
                        _completedTasksCount = completedCount;
                        App.MainWindow?.DispatcherQueue.TryEnqueue(() => UpdateTaskCompleted(task, isSuccess, detailMessage));
                    });
            }
            catch (OperationCanceledException)
            {
                SetStatus("Status_Aborted");
            }
            catch (Exception ex)
            {
                AppLogService.Combo($"RunTasksAsync error: {ex.Message}", LogLevel.Error, ex);
            }
            finally
            {
                _stopwatch.Stop();
                FinalizeRunState();
            }
        }

        // 【新增】：通知 UI 平滑追踪滚动的事件
        public event EventHandler<MergeTask>? TaskStartedForScroll;

        private void UpdateTaskStarted(MergeTask task)
        {
            task.Status = ProcessStatus.Processing;
            task.Details = ResourceService.GetString("Task_Processing");

            _ = task.EnsureThumbnailAsync(App.MainWindow?.DispatcherQueue, forceLoad: true);
            TaskStartedForScroll?.Invoke(this, task);
        }

        private void UpdateTaskCompleted(MergeTask task, bool isSuccess, string detailMessage)
        {
            // UI 线程中只更新单条 Item 属性即可，全局进度和文本交给 Timer
            task.Status = isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
            task.Details = detailMessage;
        }

        private async Task OpenComboInputFolderAsync()
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
            catch (Exception ex) { AppLogService.Combo($"OpenComboInput error: {ex.Message}", LogLevel.Error, ex); }
        }

        private async Task OpenComboOutputFolderAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(OutputDirectory)) return;
                if (!Directory.Exists(OutputDirectory))
                    Directory.CreateDirectory(OutputDirectory);
                FilePickerService.OpenFolderInExplorer(OutputDirectory);
            }
            catch (Exception ex) { AppLogService.Combo($"OpenComboOutput error: {ex.Message}", LogLevel.Error, ex); }
        }

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }
    }
}