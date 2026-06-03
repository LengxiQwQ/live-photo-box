using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Collections;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
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
            ActionBtnText = ResourceService.GetString("Btn_StartSplit");
            SelectedFormatIndex = AppSettingsService.GetValue(nameof(SelectedFormatIndex), 0);
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
            ActionBtnText = ResourceService.GetString("Btn_StopRun");
            int completedCount = Tasks.Count(task => task.Status == ProcessStatus.Success);
            Progress = QueuedCount == 0 ? 0 : (completedCount * 100.0) / QueuedCount;
            ProgressText = $"{completedCount}/{QueuedCount}";
            SetStatus("SplitPage_Status_Running");
        }

        protected override void OnFinalizeRunState()
        {
            ActionBtnText = ResourceService.GetString("Btn_StartSplit");
            if (QueuedCount > 0 && Progress >= 100)
            {
                CompleteScanSnapshot();
                SetStatus("SplitPage_Status_Done", _stopwatch.Elapsed.TotalSeconds);
            }
        }

        protected override void OnClearState()
        {
            Tasks.ReplaceRange([]);
            SplitThumbnailService.ClearCache();
            QueuedCount = 0;
            RecognizedCount = 0;
            SkippedCount = 0;
            Progress = 0;
            ProgressText = "0/0";
            SetStatus("SplitPage_Status_Cleared");
            IsDirectoryPanelOpen = true;
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

        #endregion

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
            IsDirectoryPanelOpen = true;

            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                OutputDirectory = Path.Combine(InputDirectory, "Output_SplitPhotos");
            }

            SetStatus("SplitPage_Status_Scanning");
            BeginScanSession();
            await Task.Yield();
            NotifyStatusChanged();

            try
            {
                SplitThumbnailService.ClearCache();
                var pendingText = ResourceService.GetString("SplitPage_Task_Pending");
                var scanProgress = CreateScanProgressReporter();

                _scanCancelledByUser = false;
                try { await Task.Delay(1000, token); } catch (TaskCanceledException) { }

                var scanResult = await Task.Run(
                    () => LivePhotoSplitScanService.Scan(InputDirectory, token, scanProgress),
                    token);

                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();

                var tasks = scanResult.Files.Select((file, index) => new SplitTask
                {
                    Index = index + 1,
                    SourceFileName = Path.GetFileName(file.SourcePath),
                    SourcePath = file.SourcePath,
                    FileSize = FormatFileSize(file.FileSizeBytes),
                    ProgressText = "0%",
                    Status = ProcessStatus.Pending,
                    Details = pendingText
                }).ToList();

                Tasks.ReplaceRange(tasks);
                QueuedCount = scanResult.Files.Count;
                RecognizedCount = scanResult.RecognizedCount;
                SkippedCount = scanResult.SkippedCount;
                Progress = 0;
                ProgressText = $"0/{QueuedCount}";

                FlushPendingScanProgress();
                CompleteScanSnapshot();

                if (QueuedCount > 0)
                    SetStatus("SplitPage_Status_ScanDone", QueuedCount);
                else
                    SetStatus("SplitPage_Status_NoLivePhotos");
            }
            catch (OperationCanceledException)
            {
                if (_scanCancelledByUser)
                    ProgressBarState = Models.ProgressBarState.Cancelled;
                SetStatus("SplitPage_Status_ScanCancelled");
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
                CancelProcessing();
                IsDirectoryPanelOpen = true;
                ActionBtnText = ResourceService.GetString("Btn_Stopping");
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

        private async Task RunTasksAsync()
        {
            InitializeRunState();
            _stopwatch = Stopwatch.StartNew();

            string outputDir = OutputDirectory;
            int formatIndex = SelectedFormatIndex;

            try
            {
                await Task.Run(async () =>
                {
                    int completedCount = Tasks.Count(task => task.Status == ProcessStatus.Success);
                    var tasksToProcess = Tasks.ToList();

                    foreach (var task in tasksToProcess)
                    {
                        if (task.Status == ProcessStatus.Success)
                            continue;

                        PauseEvent.Wait(GetProcessingToken());
                        GetProcessingToken().ThrowIfCancellationRequested();

                        App.MainWindow?.DispatcherQueue.TryEnqueue(() => UpdateTaskStarted(task));

                        bool isSuccess;
                        string detailMessage;

                        try
                        {
                            await LivePhotoSplitService.SplitAsync(task.SourcePath, outputDir, formatIndex, GetProcessingToken());
                            isSuccess = true;
                            detailMessage = ResourceService.GetString("SplitPage_Task_Success");
                        }
                        catch (OperationCanceledException)
                        {
                            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                                UpdateTaskCompleted(task, false, ResourceService.GetString("Status_Aborted") ?? "???", completedCount));
                            throw;
                        }
                        catch (Exception ex)
                        {
                            isSuccess = false;
                            detailMessage = ResourceService.Format("Task_Error", ex.Message);
                        }

                        completedCount++;
                        App.MainWindow?.DispatcherQueue.TryEnqueue(() => UpdateTaskCompleted(task, isSuccess, detailMessage, completedCount));
                    }
                });
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
            }
        }

        private void UpdateTaskStarted(SplitTask task)
        {
            task.Status = ProcessStatus.Processing;
            task.ProgressText = "0%";
            task.Details = ResourceService.GetString("SplitPage_Task_Processing");
        }

        private void UpdateTaskCompleted(SplitTask task, bool isSuccess, string detailMessage, int completedCount)
        {
            task.Status = isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
            task.ProgressText = isSuccess ? "100%" : "0%";
            task.Details = detailMessage;
            Progress = QueuedCount == 0 ? 0 : (completedCount * 100.0) / QueuedCount;
            ProgressText = $"{completedCount}/{QueuedCount}";
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
