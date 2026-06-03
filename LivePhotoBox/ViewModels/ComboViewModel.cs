using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Collections;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
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
        #region Fields

        private string _hwEncoderName = "Software CPU";
        private Stopwatch _stopwatch = new();

        #endregion

        #region Properties

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

        #endregion

        #region Commands

        private IAsyncRelayCommand? _openComboInputFolderCommand;
        private IAsyncRelayCommand? _openComboOutputFolderCommand;

        public IAsyncRelayCommand OpenComboInputFolderCommand => _openComboInputFolderCommand ??= new AsyncRelayCommand(OpenComboInputFolderAsync, () => !string.IsNullOrWhiteSpace(InputDirectory));
        public IAsyncRelayCommand OpenComboOutputFolderCommand => _openComboOutputFolderCommand ??= new AsyncRelayCommand(OpenComboOutputFolderAsync, () => !string.IsNullOrWhiteSpace(OutputDirectory));

        #endregion

        #region Constructor

        public ComboViewModel()
        {
            SetStatus("Status_Init");
            ActionBtnText = ResourceService.GetString("Btn_StartCombo");
        }

        #endregion

        #region WorkViewModelBase Overrides

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

        protected override void OnInitializeRunState()
        {
            ActionBtnText = ResourceService.GetString("Btn_StopRun");
            ComboProgress = 0;
            ProgressText = $"0/{TotalPairsCount}";
            SetStatus("Status_Running");
        }

        protected override void OnFinalizeRunState()
        {
            ActionBtnText = ResourceService.GetString("Btn_StartCombo");
            if (ComboProgress >= 100)
            {
                CompleteScanSnapshot();
                SetStatus("Status_Done", _stopwatch.Elapsed.TotalSeconds);
            }
        }

        protected override void OnClearState()
        {
            Tasks.ReplaceRange([]);
            ThumbnailService.ClearCache();
            TotalPairsCount = 0;
            StandaloneImagesCount = 0;
            StandaloneVideosCount = 0;
            ComboProgress = 0;
            ProgressText = "0/0";
            SetStatus("Status_Cleared", _hwEncoderName);
            IsDirectoryPanelOpen = true;
        }

        #endregion

        #region Scan

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
            IsDirectoryPanelOpen = true;

            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                OutputDirectory = Path.Combine(InputDirectory, "Output_LivePhotos");
            }

            SetStatus("Status_Scanning");
            BeginScanSession();
            await Task.Yield();
            NotifyStatusChanged();

            try
            {
                ThumbnailService.ClearCache();
                var pendingText = ResourceService.GetString("Task_Pending");
                var scanProgress = CreateScanProgressReporter();

                _scanCancelledByUser = false;
                try { await Task.Delay(1000, token); } catch (TaskCanceledException) { }

                var scanResult = await Task.Run(
                    () => LivePhotoScanService.Scan(InputDirectory, token, scanProgress),
                    token);

                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();

                var tasks = scanResult.Pairs.Select((pair, index) => new MergeTask
                {
                    Index = index + 1,
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
                }).ToList();

                Tasks.ReplaceRange(tasks);
                TotalPairsCount = scanResult.Pairs.Count;
                StandaloneImagesCount = scanResult.StandaloneImagesCount;
                StandaloneVideosCount = scanResult.StandaloneVideosCount;
                ComboProgress = 0;
                ProgressText = $"0/{TotalPairsCount}";

                FlushPendingScanProgress();
                CompleteScanSnapshot();

                if (TotalPairsCount > 0)
                    SetStatus("Status_ScanDone", TotalPairsCount);
                else
                    SetStatus("Status_ScanNoPairs", StandaloneImagesCount, StandaloneVideosCount);
            }
            catch (OperationCanceledException)
            {
                if (_scanCancelledByUser)
                    ProgressBarState = Models.ProgressBarState.Cancelled;
                SetStatus("Status_ScanCancelled");
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

        #endregion

        #region Process

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
                CancelProcessing();
                IsDirectoryPanelOpen = true;
                ActionBtnText = ResourceService.GetString("Btn_Stopping");
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

        private async Task RunTasksAsync()
        {
            InitializeRunState();
            _stopwatch = Stopwatch.StartNew();

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
                    GetProcessingToken(),
                    task => App.MainWindow?.DispatcherQueue.TryEnqueue(() => UpdateTaskStarted(task)),
                    (task, isSuccess, detailMessage, completedCount) => App.MainWindow?.DispatcherQueue.TryEnqueue(() => UpdateTaskCompleted(task, isSuccess, detailMessage, completedCount)));
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

        private void UpdateTaskStarted(MergeTask task)
        {
            task.Status = ProcessStatus.Processing;
            task.Details = ResourceService.GetString("Task_Processing");
        }

        private void UpdateTaskCompleted(MergeTask task, bool isSuccess, string detailMessage, int completedCount)
        {
            task.Status = isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
            task.Details = detailMessage;
            ComboProgress = TotalPairsCount == 0 ? 0 : (completedCount * 100.0) / TotalPairsCount;
            ProgressText = $"{completedCount}/{TotalPairsCount}";
        }

        #endregion

        #region Folder Operations

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
