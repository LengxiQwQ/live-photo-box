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
using System.Threading.Tasks;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.ViewModels
{
    public partial class RepairViewModel : WorkViewModelBase
    {
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

        private IAsyncRelayCommand? _openRepairInputFolderCommand;
        private IRelayCommand? _openRepairOutputFolderCommand;

        public IAsyncRelayCommand OpenRepairInputFolderCommand => _openRepairInputFolderCommand ??= new AsyncRelayCommand(OpenRepairInputFolderAsync, () => !string.IsNullOrWhiteSpace(InputDirectory));
        public IRelayCommand OpenRepairOutputFolderCommand => _openRepairOutputFolderCommand ??= new RelayCommand(OpenRepairOutputFolder, () => !string.IsNullOrWhiteSpace(OutputDirectory));

        public RepairViewModel()
        {
            SetStatus("RepairPage_Status_Ready");
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

        protected override void OnInitializeRunState()
        {
            _repairStoppedByUser = false;
            _repairDone = false;
            Progress = 0;
            SetStatus("Status_Running");
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
        }

        protected override void OnFinalizeRunState()
        {
            if (_cancelledByUser)
            {
                _repairStoppedByUser = true;
            }
            else
            {
                _repairDone = true;
                if (Progress >= 100)
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
            TotalPhotosCount = 0;
            ThumbCorrectCount = 0;
            ThumbErrorCount = 0;
            Progress = 0;
            ProgressText = "0/0";
            _repairStoppedByUser = false;
            _repairDone = false;
            SetStatus("RepairPage_Status_Cleared");
            IsDirectoryPanelOpen = true;
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
        }

        protected override void OnScanningEnded()
        {
            base.OnScanningEnded();
            if (_scanCancelledByUser)
                _repairStoppedByUser = true;
            _scanCancellationTokenSource?.Dispose();
            _scanCancellationTokenSource = null;
            OnPropertyChanged(nameof(IsProcessingAllowed));
            OnPropertyChanged(nameof(ActionBtnText));
        }

        private Stopwatch _stopwatch = new();
        private bool _repairStoppedByUser;
        private bool _repairDone;

        public new string ActionBtnText
        {
            get
            {
                if (IsProcessing) return ResourceService.GetString("Btn_Stopping");
                if (_repairStoppedByUser) return ResourceService.GetString("Btn_RepairStopped");
                if (_repairDone) return ResourceService.GetString("Btn_RepairDone");
                return ResourceService.GetString("Btn_StartRepair");
            }
        }

        public override bool IsProcessingAllowed
        {
            get
            {
                // 正在扫描或正在处理时，按钮可用于取消/停止
                if (IsScanning || IsProcessing) return true;

                // 已停止或已完成，禁止再次操作
                if (_repairStoppedByUser) return false;
                if (_repairDone) return false;

                // 未扫描且无任务时，禁止开始
                if (Tasks.Count == 0) return false;

                return true;
            }
        }

        private async Task ShowRepairAlreadyStoppedDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString("Msg_EmptyQueueTitle"),
                    Content = new TextBlock { Text = ResourceService.GetString("Msg_RepairAlreadyStopped"), FontSize = 16, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = App.MainWindow.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }

        private async Task ShowRepairAlreadyDoneDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString("Msg_EmptyQueueTitle"),
                    Content = new TextBlock { Text = ResourceService.GetString("Msg_RepairAlreadyDone"), FontSize = 16, TextWrapping = TextWrapping.Wrap },
                    CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = App.MainWindow.Content.XamlRoot
                };
                await dialog.ShowAsync();
            }
        }

        private async Task ShowProcessingNotAllowedDialogAsync()
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

        [RelayCommand(AllowConcurrentExecutions = true)]
        public async Task ScanDirectoryAsync()
        {
            if (!TryGuardScanClick()) return;

            // 如果已经在扫描，点击取消
            if (IsScanning)
            {
                CancelScanning();
                SetStatus("Status_ScanCancelling");
                return;
            }

            // 如果已停止，禁止重新扫描
            if (_repairStoppedByUser)
            {
                await ShowRepairAlreadyStoppedDialogAsync();
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
            _repairStoppedByUser = false;
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
                // 状态文字先更新，颜色由 OnIsScanningChanged 在 finally 块中设置
                SetStatus("RepairPage_Status_ScanCancelled");
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
            // 正在运行时，点击停止
            if (IsProcessing)
            {
                SetStatus("RepairPage_Status_Stopping");
                CancelProcessing();
                OnPropertyChanged(nameof(ActionBtnText));
                return;
            }

            // 正在扫描时，点击停止扫描
            if (IsScanning)
            {
                SetStatus("RepairPage_Status_Stopping");
                CancelScanning();
                OnPropertyChanged(nameof(ActionBtnText));
                return;
            }

            // 已停止，禁止重新开始
            if (_repairStoppedByUser)
            {
                await ShowRepairAlreadyStoppedDialogAsync();
                return;
            }

            // 已完成，禁止重新开始
            if (_repairDone)
            {
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

            int completedOrSkipped = 0;
            var token = GetProcessingToken();

            try
            {
                await Task.Run(async () =>
                {
                    foreach (var task in Tasks)
                    {
                        PauseEvent.Wait(token);
                        if (token.IsCancellationRequested)
                            token.ThrowIfCancellationRequested();

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
                            var result = await LivePhotoRepairService.RepairAsync(task.FilePath, targetPath, task.AnalysisResult!, token);
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
                            CheckAndApplyPendingState();
                        });
                    }
                });
            }
            catch (OperationCanceledException)
            {
                SetStatus("RepairPage_Status_Aborted");
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
    }
}