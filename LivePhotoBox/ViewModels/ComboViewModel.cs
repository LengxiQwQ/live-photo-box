using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Collections;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Documents;
using Microsoft.UI.Xaml.Media;
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
    public partial class ComboViewModel : WorkViewModelBase
    {
        private static readonly TimeSpan MinimumProcessingDisplayDuration = TimeSpan.FromMilliseconds(100);

        private string _hwEncoderName = "Software CPU";
        private Stopwatch _stopwatch = new();
        private bool _comboStoppedByUser;
        private bool _comboDone;
        private readonly Dictionary<MergeTask, DateTimeOffset> _taskProcessingStartTimes = new();

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
        public bool CanEditSelectedMode => !IsScanning && !IsProcessing;

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
            _taskProcessingStartTimes.Clear();
            SetStatus("Status_Running");
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
            OnPropertyChanged(nameof(CanEditSelectedMode));
            _uiUpdateTimer.Start();
        }

        protected override void OnFinalizeRunState()
        {
            _uiUpdateTimer.Stop();
            _taskProcessingStartTimes.Clear();

            if (_cancelledByUser)
            {
                _comboStoppedByUser = true;
            }
            else
            {
                _comboDone = true;

                if (TotalPairsCount > 0)
                {
                    ComboProgress = (_completedTasksCount * 100.0) / TotalPairsCount;
                    Progress = ComboProgress;
                    ProgressText = $"{_completedTasksCount}/{TotalPairsCount}";
                }

                if (ComboProgress >= 100)
                {
                    ProgressBarState = Models.ProgressBarState.Success;
                    CompleteScanSnapshot();

                    // ✨【状态栏统计显示修复】：使用专属多语言词条
                    int total = Tasks.Count;
                    int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                    int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                    double elapsed = _stopwatch.Elapsed.TotalSeconds;

                    // 自动适配中英文的新词条
                    SetStatus("Status_ComboCompletedSummary", total, elapsed, succeeded, failed);
                }
            }
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
            OnPropertyChanged(nameof(CanEditSelectedMode));
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
            _taskProcessingStartTimes.Clear();
            SetStatus("Status_Cleared", _hwEncoderName);
            IsDirectoryPanelOpen = true;
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
            OnPropertyChanged(nameof(CanEditSelectedMode));
        }

        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task ScanDirectoryAsync()
        {
            LogService.Combo($"ScanDirectory requested. Input='{InputDirectory}', Output='{OutputDirectory}'");

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
                {
                    IsDirectoryPanelOpen = true;
                    SetStatus("Status_ScanNoPairs", StandaloneImagesCount, StandaloneVideosCount);
                }
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
                    OnPropertyChanged(nameof(CanEditSelectedMode));
                });

                AppViewModel.Instance.ResetFooterScanCounters();
            }
            catch (Exception ex)
            {
                LogService.Combo($"ScanDirectory error: {ex.Message}", LogLevel.Error, ex);
                SetStatus("Status_Error", ex.Message);
            }
            finally
            {
                IsScanning = false;
                OnScanningEnded();
                NotifyStatusChanged();
                OnPropertyChanged(nameof(CanEditSelectedMode));
            }
        }

        [RelayCommand]
        private void ToggleSecondaryAction()
        {
            LogService.Combo($"ToggleSecondaryAction requested. IsProcessing={IsProcessing}, IsPaused={IsPaused}");

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
            LogService.Combo($"ToggleProcessAsync requested. IsProcessing={IsProcessing}, QueueCount={Tasks.Count}");

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
                    XamlRoot = App.MainWindow.Content.XamlRoot,
                    RequestedTheme = App.CurrentTheme
                };
                await dialog.ShowAsync();
            }
        }

        private async Task ShowComboAlreadyDoneDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                int total = Tasks.Count;
                int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);

                var stack = new StackPanel
                {
                    Spacing = 12,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.GetString("Msg_ComboCompletedTitle"),
                    FontSize = 22,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });

                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.Format("Msg_ComboCompletedSummary", total, succeeded, failed),
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap
                });

                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.GetString("Msg_ComboCompletedDescription"),
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(0, 12, 0, 0),
                    Opacity = 0.85
                });

                var dialog = new ContentDialog
                {
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
                    await OpenComboOutputFolderAsync();
                }
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
                    XamlRoot = App.MainWindow.Content.XamlRoot,
                    RequestedTheme = App.CurrentTheme
                };
                await dialog.ShowAsync();
            }
        }

        private async Task RunTasksAsync()
        {
            InitializeRunState();
            _stopwatch = Stopwatch.StartNew();

            var token = GetProcessingToken();
            int startedCallbacks = 0;
            int finishedUiCallbacks = 0;

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
                    async (task, isSuccess, detailMessage, completedCount) =>
                    {
                        Interlocked.Increment(ref startedCallbacks);
                        _completedTasksCount = completedCount;

                        try
                        {
                            await EnsureMinimumProcessingDisplayAsync(task).ConfigureAwait(false);

                            var tcs = new TaskCompletionSource<bool>();
                            if (App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                            {
                                try
                                {
                                    UpdateTaskCompleted(task, isSuccess, detailMessage);
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
                        }
                        catch (Exception ex)
                        {
                            LogService.Combo($"Update callback error: {ex.Message}", LogLevel.Error, ex);
                        }
                        finally
                        {
                            Interlocked.Increment(ref finishedUiCallbacks);
                        }
                    });
            }
            catch (OperationCanceledException)
            {
                SetStatus("Status_Aborted");
            }
            catch (Exception ex)
            {
                LogService.Combo($"RunTasksAsync error: {ex.Message}", LogLevel.Error, ex);
            }
            finally
            {
                while (Volatile.Read(ref finishedUiCallbacks) < Volatile.Read(ref startedCallbacks))
                {
                    await Task.Delay(20);
                }

                _stopwatch.Stop();
                FinalizeRunState();

                if (!_cancelledByUser && Tasks.Count > 0)
                {
                    await ShowComboAlreadyDoneDialogAsync();
                }
            }
        }

        public event EventHandler<MergeTask>? TaskStartedForScroll;
        public event EventHandler? ProcessingCompletedForScroll;

        private void UpdateTaskStarted(MergeTask task)
        {
            task.Status = ProcessStatus.Processing;
            task.Details = ResourceService.GetString("Task_Processing");
            _taskProcessingStartTimes[task] = DateTimeOffset.UtcNow;
            _ = task.EnsureThumbnailAsync(App.MainWindow?.DispatcherQueue);
            TaskStartedForScroll?.Invoke(this, task);
        }

        private void UpdateTaskCompleted(MergeTask task, bool isSuccess, string detailMessage)
        {
            task.Status = isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
            task.Details = detailMessage;
            _taskProcessingStartTimes.Remove(task);

            if (_completedTasksCount >= Tasks.Count && Tasks.Count > 0)
            {
                ProcessingCompletedForScroll?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task EnsureMinimumProcessingDisplayAsync(MergeTask task)
        {
            if (!_taskProcessingStartTimes.TryGetValue(task, out var startedAt))
            {
                return;
            }

            var remaining = MinimumProcessingDisplayDuration - (DateTimeOffset.UtcNow - startedAt);
            if (remaining > TimeSpan.Zero)
            {
                await Task.Delay(remaining).ConfigureAwait(false);
            }
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
            catch (Exception ex) { LogService.Combo($"OpenComboInput error: {ex.Message}", LogLevel.Error, ex); }
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
            catch (Exception ex) { LogService.Combo($"OpenComboOutput error: {ex.Message}", LogLevel.Error, ex); }
        }

        public new void Cleanup() => CleanupTokens();

        private static string FormatFileSize(long bytes)
        {
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }
    }
}