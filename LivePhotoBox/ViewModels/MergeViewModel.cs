using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Collections;
using LivePhotoBox.Helpers;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.Services.Protocols;
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
    public partial class MergeViewModel : WorkViewModelBase
    {
        private Stopwatch _stopwatch = new();
        private bool _mergeStoppedByUser;
        private bool _mergeDone;

        private readonly DispatcherTimer _uiUpdateTimer;
        private volatile int _completedTasksCount;

        public override string PageStatusTag => "Merge";

        protected override string ProcessingStatusKey => "Status_Running";

        protected override string ProcessingStatusText =>
            ResourceService.Format("Status_Running") + " | " +
            LivePhotoProtocol.FromIndex(SelectedModeIndex).DisplayName +
            GetHardwareSuffix();

        [ObservableProperty]
        private string _inputDirectory = string.Empty;

        partial void OnInputDirectoryChanged(string value)
        {
            _openMergeInputFolderCommand?.NotifyCanExecuteChanged();
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

        partial void OnOutputDirectoryChanged(string value) => _openMergeOutputFolderCommand?.NotifyCanExecuteChanged();

        [ObservableProperty]
        private int _totalPairsCount = 0;

        [ObservableProperty]
        private int _standaloneImagesCount = 0;

        [ObservableProperty]
        private int _standaloneVideosCount = 0;

        [ObservableProperty]
        private bool _isDirectoryPanelOpen = true;

        [ObservableProperty]
        private double _mergeProgress = 0;

        public string ScanButtonText => IsScanning
            ? ResourceService.GetString("MergePage_DynamicCancelText")
            : ResourceService.GetString("MergePage_DynamicScanText");

        public BulkObservableCollection<MergeTask> Tasks { get; } = [];

        public int SelectedModeIndex
        {
            get => AppSettingsService.GetValue(nameof(SelectedModeIndex), 1);
            set
            {
                AppSettingsService.SetValue(nameof(SelectedModeIndex), value);
                LogService.Merge($"Live Photo format changed to index: {value}");
                OnPropertyChanged();
            }
        }

        private IAsyncRelayCommand? _openMergeInputFolderCommand;
        private IAsyncRelayCommand? _openMergeOutputFolderCommand;

        public IAsyncRelayCommand OpenMergeInputFolderCommand => _openMergeInputFolderCommand ??= new AsyncRelayCommand(OpenMergeInputFolderAsync, () => !string.IsNullOrWhiteSpace(InputDirectory));
        public IAsyncRelayCommand OpenMergeOutputFolderCommand => _openMergeOutputFolderCommand ??= new AsyncRelayCommand(OpenMergeOutputFolderAsync, () => !string.IsNullOrWhiteSpace(OutputDirectory));

        public MergeViewModel()
        {
            SetStatus("Status_Init");
            _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _uiUpdateTimer.Tick += UiUpdateTimer_Tick;
        }

        public override string ActionBtnText
        {
            get
            {
                if (IsProcessing)
                {
                    if (_cancelledByUser) return ResourceService.GetString("Btn_Stopping");
                    return ResourceService.GetString("Btn_Stop");
                }
                return ResourceService.GetString("Btn_StartMerge");
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
            AppViewModel.Instance.BeginMergeScanSession();
        }

        protected override void OnApplyScanProgress(WorkProgressSnapshot snapshot)
        {
            AppViewModel.Instance.ApplyMergeScanProgress(snapshot);
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
            MergeProgress = (currentCompleted * 100.0) / TotalPairsCount;
            Progress = MergeProgress;
            ProgressText = $"{currentCompleted}/{TotalPairsCount}";
            CheckAndApplyPendingState();
        }

        protected override void OnInitializeRunState()
        {
            _mergeStoppedByUser = false;
            _mergeDone = false;
            _completedTasksCount = 0;
            MergeProgress = 0;
            Progress = 0;
            ProgressText = $"0/{TotalPairsCount}";
            SetDirectStatus(ProcessingStatusText);
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
            OnPropertyChanged(nameof(CanEditSelectedMode));
            _uiUpdateTimer.Start();
        }

        protected override void OnFinalizeRunState()
        {
            _uiUpdateTimer.Stop();

            if (_cancelledByUser)
            {
                _mergeStoppedByUser = true;
            }
            else
            {
                _mergeDone = true;

                if (TotalPairsCount > 0)
                {
                    MergeProgress = (_completedTasksCount * 100.0) / TotalPairsCount;
                    Progress = MergeProgress;
                    ProgressText = $"{_completedTasksCount}/{TotalPairsCount}";
                }

                if (MergeProgress >= 100)
                {
                    ProgressBarState = Models.ProgressBarState.Success;
                    CompleteScanSnapshot();

                    // ✨【状态栏统计显示修复】：使用专属多语言词条
                    int total = Tasks.Count;
                    int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                    int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                    double elapsed = _stopwatch.Elapsed.TotalSeconds;

                    SetStatus("Status_MergeCompletedSummary", total, elapsed, succeeded, failed);
                    LogService.Merge($"Merge completed: {succeeded} succeeded, {failed} failed in {elapsed:F1}s");
                }
            }
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
            OnPropertyChanged(nameof(CanEditSelectedMode));
            IsDirectoryPanelOpen = true;
        }

        protected override void OnClearState()
        {
            Tasks.ReplaceRange([]);
            ThumbnailService.ClearCache();
            TotalPairsCount = 0;
            StandaloneImagesCount = 0;
            StandaloneVideosCount = 0;
            _completedTasksCount = 0;
            MergeProgress = 0;
            Progress = 0;
            ProgressText = "0/0";
            _mergeStoppedByUser = false;
            _mergeDone = false;
            SetStatus("Status_Cleared");
            IsDirectoryPanelOpen = true;
            OnPropertyChanged(nameof(ActionBtnText));
            OnPropertyChanged(nameof(IsProcessingAllowed));
            OnPropertyChanged(nameof(CanEditSelectedMode));
        }

        protected override void OnCleanup()
        {
            _uiUpdateTimer.Stop();
        }

        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task ScanDirectoryAsync()
        {
            if (!TryGuardScanClick()) return;
            if (IsProcessing) return;

            if (IsScanning)
            {
                CancelScanning();
                SetStatus("Status_ScanCancelling");
                return;
            }

            if (string.IsNullOrWhiteSpace(InputDirectory))
            {
                await ShowNoInputDirectoryDialogAsync("Merge");
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
                OutputDirectory = Path.Combine(InputDirectory, ResourceService.GetString("OutputDir_LivePhotos"));
            }

            LogService.Merge($"ScanDirectory requested. Input='{InputDirectory}', Output='{OutputDirectory}'");

            SetStatus("Status_Scanning");
            BeginScanSession();
            await Task.Yield();
            NotifyStatusChanged();

            _scanCancelledByUser = false;
            _mergeStoppedByUser = false;
            _mergeDone = false;

            try
            {
                ThumbnailService.ClearCache();
                var pendingText = ResourceService.GetString("Task_Pending");
                var scanProgress = CreateScanProgressReporter();

                if (!token.IsCancellationRequested)
                {
                    try { await Task.Delay(1000, token); } catch (TaskCanceledException) { }
                }

                var scanResult = await Task.Run(
                    () => LivePhotoMergeScanService.Scan(InputDirectory, token, scanProgress),
                    token);

                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();

                // ── 元数据匹配：根据设置模式决定是否补充 / 替换文件名匹配结果 ──
                int matchingMode = AppSettingsService.GetValue("MetadataMatchingModeIndex", 0);
                int pairsFromFilename = scanResult.Pairs.Count;
                int standaloneImg = scanResult.StandaloneImagesCount;
                int standaloneVid = scanResult.StandaloneVideosCount;
                var allPairs = new List<LivePhotoFilePairInfo>(scanResult.Pairs);
                var metadataPairs = new List<MetadataPair>();

                if (matchingMode != (int)MetadataMatchingMode.FilenameOnly)
                {
                    string exifToolPath = ExternalToolLocator.FindExifTool()
                        ?? System.IO.Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe");

                    if (System.IO.File.Exists(exifToolPath))
                    {
                        try
                        {
                            bool enableDate = matchingMode == (int)MetadataMatchingMode.BothWithDate;

                            // MetadataOnly: 所有文件都走元数据匹配，丢弃文件名匹配结果
                            // Both / BothWithDate: 只对文件名匹配剩余的文件做元数据匹配
                            var imgList = matchingMode == (int)MetadataMatchingMode.MetadataOnly
                                ? scanResult.Pairs.Select(p => p.ImagePath)
                                    .Concat(scanResult.StandaloneImagePaths).ToList()
                                : scanResult.StandaloneImagePaths.ToList();

                            var vidList = matchingMode == (int)MetadataMatchingMode.MetadataOnly
                                ? scanResult.Pairs.Select(p => p.VideoPath)
                                    .Concat(scanResult.StandaloneVideoPaths).ToList()
                                : scanResult.StandaloneVideoPaths.ToList();

                            if (imgList.Count > 0 && vidList.Count > 0)
                            {
                                var matchOutput = await Task.Run(
                                    () => LivePhotoMetadataMatcher.MatchAsync(imgList, vidList, exifToolPath, token, enableDate),
                                    token);

                                metadataPairs.AddRange(matchOutput.Pairs);
                                standaloneImg = matchOutput.RemainingImages;
                                standaloneVid = matchOutput.RemainingVideos;

                                LogService.Merge($"Metadata matching: found {matchOutput.Pairs.Count} additional pairs " +
                                    $"(mode={matchingMode}, dateEnabled={enableDate}, filenamePairs={pairsFromFilename})");
                            }

                            if (matchingMode == (int)MetadataMatchingMode.MetadataOnly)
                            {
                                // 元数据优先模式：丢弃文件名匹配结果，只用元数据配对
                                allPairs.Clear();
                                pairsFromFilename = 0;
                            }
                        }
                        catch (OperationCanceledException) { throw; }
                        catch (Exception ex)
                        {
                            LogService.Merge($"Metadata matching failed, falling back to filename-only: {ex.Message}", LogLevel.Warning);
                            // 出错时退回纯文件名匹配结果
                            standaloneImg = scanResult.StandaloneImagesCount;
                            standaloneVid = scanResult.StandaloneVideosCount;
                            metadataPairs.Clear();
                        }
                    }
                }

                int index = 0;
                // 文件名匹配的结果（Both 模式下保留，MetadataOnly 模式下已清空）
                var tempTasks = allPairs.Select(pair =>
                {
                    index++;
                    return new MergeTask
                    {
                        Index = index,
                        ImageFileName = Path.GetFileName(pair.ImagePath),
                        VideoFileName = Path.GetFileName(pair.VideoPath),
                        ImageSize = FileSizeFormatter.Format(pair.ImageSizeBytes),
                        VideoSize = FileSizeFormatter.Format(pair.VideoSizeBytes),
                        TotalSizeBytes = pair.ImageSizeBytes + pair.VideoSizeBytes,
                        BaseName = pair.BaseName,
                        ImagePath = pair.ImagePath,
                        VideoPath = pair.VideoPath,
                        Status = ProcessStatus.Pending,
                        Details = pendingText
                    };
                }).ToList();

                // 元数据匹配的结果（Both 或 MetadataOnly 模式下可能有）
                foreach (var mp in metadataPairs)
                {
                    index++;
                    string baseName = Path.GetFileNameWithoutExtension(mp.ImagePath);
                    try
                    {
                        long imgSize = new System.IO.FileInfo(mp.ImagePath).Length;
                        long vidSize = new System.IO.FileInfo(mp.VideoPath).Length;
                        tempTasks.Add(new MergeTask
                        {
                            Index = index,
                            ImageFileName = Path.GetFileName(mp.ImagePath),
                            VideoFileName = Path.GetFileName(mp.VideoPath),
                            ImageSize = FileSizeFormatter.Format(imgSize),
                            VideoSize = FileSizeFormatter.Format(vidSize),
                            TotalSizeBytes = imgSize + vidSize,
                            BaseName = baseName,
                            ImagePath = mp.ImagePath,
                            VideoPath = mp.VideoPath,
                            Status = ProcessStatus.Pending,
                            Details = pendingText
                        });
                    }
                    catch (System.IO.IOException ex)
                    {
                        LogService.Merge($"Failed to get file size for metadata pair {baseName}", LogLevel.Warning, ex);
                    }
                }

                int totalPairs = tempTasks.Count;
                Tasks.ReplaceRange(tempTasks);
                TotalPairsCount = totalPairs;
                StandaloneImagesCount = standaloneImg;
                StandaloneVideosCount = standaloneVid;

                App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                {
                    MergeProgress = 0;
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
                    MergeProgress = 0;
                    Progress = 0;
                    ProgressText = "0/0";
                    OnPropertyChanged(nameof(IsProcessingAllowed));
                    OnPropertyChanged(nameof(CanEditSelectedMode));
                });

                AppViewModel.Instance.ResetFooterScanCounters();
            }
            catch (Exception ex)
            {
                LogService.Merge($"ScanDirectory error: {ex.Message}", LogLevel.Error, ex);
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
            LogService.Merge($"ToggleSecondaryAction requested. IsProcessing={IsProcessing}, IsPaused={IsPaused}");

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
            LogService.Merge($"ToggleProcessAsync requested. IsProcessing={IsProcessing}, QueueCount={Tasks.Count}");

            if (IsProcessing)
            {
                SetStatus("Status_Stopping");
                CancelProcessing();
                IsDirectoryPanelOpen = true;
                OnPropertyChanged(nameof(ActionBtnText));
                return;
            }

            if (_mergeStoppedByUser || _mergeDone)
            {
                if (_mergeStoppedByUser)
                    await ShowMergeCancelledDialogAsync();
                else
                    await ShowMergeAlreadyDoneDialogAsync();
                return;
            }

            if (Tasks.Count == 0)
            {
                await ShowEmptyQueueDialogAsync("Merge");
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


        private async Task ShowMergeAlreadyDoneDialogAsync()
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
                    Text = ResourceService.GetString("Msg_MergeCompletedTitle"),
                    FontSize = 22,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });

                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.Format("Msg_MergeCompletedSummary", total, succeeded, failed),
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap
                });

                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.GetString("Msg_MergeCompletedDescription"),
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
                    await OpenMergeOutputFolderAsync();
                }
            }
        }

        private async Task ShowMergeCancelledDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                int total = Tasks.Count;
                int succeeded = Tasks.Count(t => t.Status == ProcessStatus.Success);
                int failed = Tasks.Count(t => t.Status == ProcessStatus.Failed);
                int unprocessed = total - succeeded - failed;

                var stack = new StackPanel
                {
                    Spacing = 12,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.GetString("Msg_TaskCancelledTitle"),
                    FontSize = 22,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap
                });

                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.Format("Msg_MergeCancelledSummary", total, succeeded, failed, unprocessed),
                    FontSize = 16,
                    TextWrapping = TextWrapping.Wrap
                });

                stack.Children.Add(new TextBlock
                {
                    Text = ResourceService.GetString("Msg_MergeCompletedDescription"),
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
                    await OpenMergeOutputFolderAsync();
                }
            }
        }


        private async Task RunTasksAsync()
        {
            InitializeRunState();
            _stopwatch = Stopwatch.StartNew();

            var token = GetProcessingToken();
            string outputDir = OutputDirectory;
            int modeIndex = SelectedModeIndex;
            Directory.CreateDirectory(outputDir);

            // 所有临时文件统一放在 Temp 子目录，处理完毕后整体删除
            string tempDir = Path.Combine(outputDir, "Temp");
            Directory.CreateDirectory(tempDir);

            try
            {
                await Task.Run(async () =>
                {
                    var tasksToProcess = Tasks.Where(t => t.Status != ProcessStatus.Success).ToList();

                    // 智能并行数：含 HEIC 用保守值，纯 JPG 直接拉满
                    bool hasHeic = tasksToProcess.Any(t => HeicConverterService.IsHeicFile(t.ImagePath));
                    int maxParallel = hasHeic
                        ? AppSettingsService.GetValue("MergeThreadCount", 4)
                        : 20;
                    LogService.Merge($"Parallel: {maxParallel} (hasHeic={hasHeic}, {tasksToProcess.Count} tasks)", LogLevel.Debug);

                    var semaphore = new SemaphoreSlim(maxParallel, maxParallel);
                    var pendingTasks = new List<Task>();
                    int localCompletedCount = 0;
                    var lockObj = new object();

                    async Task ProcessTask(MergeTask task)
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

                            var protocol = LivePhotoProtocol.FromIndex(modeIndex);
                            string outputName = LivePhotoMergeService.CreateOutputFileName(task.BaseName, modeIndex);
                            string finalPath = Path.Combine(outputDir, outputName);
                            string workingImagePath = task.ImagePath;
                            string workingVideoPath = task.VideoPath;
                            var tempFiles = new System.Collections.Generic.List<string>();

                            try
                            {
                                if (HeicConverterService.IsHeicFile(workingImagePath))
                                {
                                    workingImagePath = await HeicConverterService.ConvertToJpegAsync(
                                        workingImagePath, tempDir, token);
                                    tempFiles.Add(workingImagePath);
                                }

                                // Google协议是否强制转MP4由用户设置控制，OPPO始终转
                                bool forceMp4 = modeIndex == 2 ||
                                    AppSettingsService.GetValue("IsGoogleProtocolForceMp4", false);
                                (workingVideoPath, bool vt) =
                                    await VideoTranscodeService.EnsureMp4Async(
                                        task.VideoPath, tempDir, token, forceMp4);
                                if (vt) tempFiles.Add(workingVideoPath);

                                string prepared = await protocol.PrepareImageAsync(
                                    workingImagePath, tempDir, token);
                                if (prepared != workingImagePath)
                                {
                                    workingImagePath = prepared;
                                    tempFiles.Add(workingImagePath);
                                }

                                await LivePhotoMergeService.WriteLivePhotoAsync(
                                    workingImagePath, workingVideoPath, finalPath, modeIndex, token);

                                isSuccess = true;
                                detailMessage = ResourceService.GetString("Task_Success");
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
                                LogService.Merge($"Merge task failed for {task.BaseName}: {ex.Message}", LogLevel.Error, ex);
                            }
                            finally
                            {
                                if (!isSuccess)
                                    try { if (File.Exists(finalPath)) File.Delete(finalPath); } catch { }
                                foreach (var f in tempFiles)
                                    try { if (File.Exists(f)) File.Delete(f); } catch { }
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
                LogService.Merge($"Processing cancelled by user after {elapsed:F1}s, completed {_completedTasksCount}/{TotalPairsCount}");
                SetStatus("Status_MergeStoppedSummary", total, elapsed, succeeded, failed, unprocessed);
            }
            catch (Exception ex)
            {
                LogService.Merge($"RunTasksAsync error: {ex.Message}", LogLevel.Error, ex);
            }
            finally
            {
                // 清理所有临时文件
                try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
                catch (Exception ex) { LogService.Merge($"Failed to clean temp dir: {ex.Message}", LogLevel.Warning); }

                _stopwatch.Stop();
                _stopwatch.Stop();
                bool wasCancelled = _cancelledByUser;
                FinalizeRunState();

                // 关闭中不弹对话框，避免在窗口销毁期间操作 XamlRoot
                if (Tasks.Count > 0 && !_isCleaningUp)
                {
                    if (wasCancelled)
                        await ShowMergeCancelledDialogAsync();
                    else
                        await ShowMergeAlreadyDoneDialogAsync();
                }
            }
        }

        public event EventHandler<MergeTask>? TaskStartedForScroll;
        public event EventHandler? ProcessingCompletedForScroll;

        private void UpdateTaskStarted(MergeTask task)
        {
            task.Status = ProcessStatus.Processing;
            task.Details = ResourceService.GetString("Task_Processing");
            TaskStartedForScroll?.Invoke(this, task);
        }

        private void UpdateTaskCancelled(MergeTask task, string detailMessage)
        {
            // 用户取消不标记为"失败"——保留 Processing 状态，颜色中性，只更新详情
            task.Details = detailMessage;
        }

        private void UpdateTaskCompleted(MergeTask task, bool isSuccess, string detailMessage, int completedCount)
        {
            task.Status = isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
            task.Details = detailMessage;

            if (completedCount >= Tasks.Count && Tasks.Count > 0)
            {
                ProcessingCompletedForScroll?.Invoke(this, EventArgs.Empty);
            }
        }

        private async Task OpenMergeInputFolderAsync()
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
            catch (Exception ex) { LogService.Merge($"OpenMergeInput error: {ex.Message}", LogLevel.Error, ex); }
        }

        private async Task OpenMergeOutputFolderAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(OutputDirectory)) return;
                if (!Directory.Exists(OutputDirectory))
                    Directory.CreateDirectory(OutputDirectory);
                FilePickerService.OpenFolderInExplorer(OutputDirectory);
            }
            catch (Exception ex) { LogService.Merge($"OpenMergeOutput error: {ex.Message}", LogLevel.Error, ex); }
        }
    }
}
