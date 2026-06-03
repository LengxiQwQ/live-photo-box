using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Collections;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.ViewModels
{
    public partial class RepairViewModel : WorkViewModelBase
    {
        #region Properties

        public override string PageStatusTag => "Repair";

        [ObservableProperty]
        private string _inputDirectory = string.Empty;

        partial void OnInputDirectoryChanged(string value)
        {
            _openRepairInputFolderCommand?.NotifyCanExecuteChanged();
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

        partial void OnOutputDirectoryChanged(string value) => _openRepairOutputFolderCommand?.NotifyCanExecuteChanged();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(OutputGridVisibility))]
        private bool _isOutputToDirectory = false;

        partial void OnIsOutputToDirectoryChanged(bool value)
        {
            if (value && string.IsNullOrWhiteSpace(OutputDirectory) && !string.IsNullOrWhiteSpace(InputDirectory) && Directory.Exists(InputDirectory))
            {
                OutputDirectory = Path.Combine(InputDirectory, "Output_RepairedPhotos");
            }
        }

        public Visibility OutputGridVisibility =>
            IsOutputToDirectory ? Visibility.Visible : Visibility.Collapsed;

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

        #endregion

        #region Commands

        private IAsyncRelayCommand? _openRepairInputFolderCommand;
        private IRelayCommand? _openRepairOutputFolderCommand;

        public IAsyncRelayCommand OpenRepairInputFolderCommand => _openRepairInputFolderCommand ??= new AsyncRelayCommand(OpenRepairInputFolderAsync, () => !string.IsNullOrWhiteSpace(InputDirectory));
        public IRelayCommand OpenRepairOutputFolderCommand => _openRepairOutputFolderCommand ??= new RelayCommand(OpenRepairOutputFolder, () => !string.IsNullOrWhiteSpace(OutputDirectory));

        #endregion

        #region Constructor

        public RepairViewModel()
        {
            SetStatus("RepairPage_Status_Ready");
            ActionBtnText = ResourceService.GetString("Btn_StartRepair");
        }

        #endregion

        #region WorkViewModelBase Overrides

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

        protected override void OnInitializeRunState()
        {
            ActionBtnText = ResourceService.GetString("Btn_StopRun");
            SetStatus("Status_Running");
        }

        protected override void OnFinalizeRunState()
        {
            ActionBtnText = ResourceService.GetString("Btn_StartRepair");
            if (Progress >= 100)
            {
                CompleteScanSnapshot();
                SetStatus("Status_Done", _stopwatch.Elapsed.TotalSeconds);
            }
        }

        protected override void OnClearState()
        {
            Tasks.ReplaceRange([]);
            TotalPhotosCount = 0;
            ThumbCorrectCount = 0;
            ThumbErrorCount = 0;
            Progress = 0;
            ProgressText = "0/0";
            SetStatus("RepairPage_Status_Cleared");
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
                await ShowNoInputDirectoryDialogAsync("Repair");
                return;
            }

            IsScanning = true;

            if (IsOutputToDirectory && string.IsNullOrWhiteSpace(OutputDirectory))
            {
                OutputDirectory = Path.Combine(InputDirectory, "Output_RepairedPhotos");
            }

            var token = GetScanningToken();

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
            try { await Task.Delay(1000, token); } catch (TaskCanceledException) { }

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
                    catch (UnauthorizedAccessException) { return new List<string>(); }
                    catch (DirectoryNotFoundException) { return new List<string>(); }
                    catch (IOException) { return new List<string>(); }
                }, token);

                TotalPhotosCount = files.Count;
                var scanProgress = CreateScanProgressReporter();
                scanProgress.Report(new WorkProgressSnapshot(files.Count, 0));

                await Task.Run(async () =>
                {
                    int index = 0;
                    foreach (var file in files)
                    {
                        if (token.IsCancellationRequested)
                            token.ThrowIfCancellationRequested();

                        var analysis = await LivePhotoRepairService.AnalyzeFileAsync(file);
                        index++;

                        var task = new RepairTask
                        {
                            Index = index,
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

                        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                        {
                            Tasks.Add(task);
                            if (analysis.NeedsRepair) ThumbErrorCount++;
                            else ThumbCorrectCount++;
                        });

                        scanProgress.Report(new WorkProgressSnapshot(files.Count, index));
                    }

                    scanProgress.Report(new WorkProgressSnapshot(files.Count, files.Count));
                }, token);

                FlushPendingScanProgress();
                CompleteScanSnapshot();

                if (TotalPhotosCount > 0)
                    SetStatus("RepairPage_Status_ScanDone", TotalPhotosCount);
                else
                    SetStatus("RepairPage_Status_ScanNoFiles");
            }
            catch (OperationCanceledException)
            {
                if (_scanCancelledByUser)
                    ProgressBarState = Models.ProgressBarState.Cancelled;
                SetStatus("Status_ScanCancelled");
            }
            catch (Exception ex)
            {
                AppLogService.Repair($"ScanDirectory error: {ex.Message}", LogLevel.Error, ex);
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
            if (IsProcessing)
            {
                CancelProcessing();
                ActionBtnText = ResourceService.GetString("Btn_Stopping");
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

            int completedOrSkipped = 0;

            try
            {
                await Task.Run(async () =>
                {
                    foreach (var task in Tasks)
                    {
                        PauseEvent.Wait(GetProcessingToken());
                        GetProcessingToken().ThrowIfCancellationRequested();

                        if (!task.NeedsRepair || task.Status == ProcessStatus.Success)
                        {
                            completedOrSkipped++;
                            continue;
                        }

                        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                        {
                            task.Status = ProcessStatus.Processing;
                            task.Details = ResourceService.GetString("Task_Processing");
                        });

                        string targetPath = IsOutputToDirectory
                            ? Path.Combine(OutputDirectory, task.FileName)
                            : task.FilePath;

                        try
                        {
                            var result = await LivePhotoRepairService.RepairAsync(task.FilePath, targetPath, task.AnalysisResult!, GetProcessingToken());
                            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                            {
                                task.Status = result.Success ? ProcessStatus.Success : ProcessStatus.Failed;
                                task.Details = result.Message;
                            });
                        }
                        catch (OperationCanceledException)
                        {
                            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                            {
                                task.Status = ProcessStatus.Failed;
                                task.Details = ResourceService.GetString("Status_Aborted") ?? "???";
                            });
                            throw;
                        }

                        completedOrSkipped++;
                        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                        {
                            Progress = TotalPhotosCount == 0 ? 0 : (completedOrSkipped * 100.0) / TotalPhotosCount;
                            ProgressText = $"{completedOrSkipped}/{TotalPhotosCount}";
                        });
                    }
                });
            }
            catch (OperationCanceledException)
            {
                SetStatus("Status_Aborted");
            }
            catch (Exception ex)
            {
                AppLogService.Repair($"RunTasksAsync error: {ex.Message}", LogLevel.Error, ex);
            }
            finally
            {
                _stopwatch.Stop();
                FinalizeRunState();
            }
        }

        #endregion

        #region Folder Operations

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
            catch (Exception ex) { AppLogService.Repair($"OpenRepairInput error: {ex.Message}", LogLevel.Error, ex); }
        }

        private void OpenRepairOutputFolder()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(OutputDirectory)) return;
                if (!Directory.Exists(OutputDirectory))
                    Directory.CreateDirectory(OutputDirectory);
                FilePickerService.OpenFolderInExplorer(OutputDirectory);
            }
            catch (Exception ex) { AppLogService.Repair($"OpenRepairOutput error: {ex.Message}", LogLevel.Error, ex); }
        }

        #endregion
    }
}
