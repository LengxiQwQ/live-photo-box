// <copyright file="MergeViewModel.cs" company="Live Photo Box">
// Copyright (c) Live Photo Box. All rights reserved.
// </copyright>

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
    // 实况照片合并页面的 ViewModel，对应 MergePage。
    // 负责扫描输入文件夹中的图片-视频配对，选择合并协议（Google/HEIC/OPPO），
    // 以及执行合并任务并将结果写入输出文件夹。
    public partial class MergeViewModel : WorkViewModelBase
    {
        // 用于统计合并耗时的计时器。
        private Stopwatch _stopwatch = new();

        // 合并是否被用户手动停止。
        private bool _mergeStoppedByUser;

        // 所有合并任务是否已完成（成功/失败都算）。
        private bool _mergeDone;

        // UI 更新计时器（约 60ms 间隔），用于在合并过程中刷新进度条和进度文本。
        private readonly DispatcherTimer _uiUpdateTimer;

        // 当前已完成的合并任务数（线程安全，使用 volatile）。
        private volatile int _completedTasksCount;

        // <inheritdoc/>
        public override string PageStatusTag => "Merge";

        // <inheritdoc/>
        protected override string ProcessingStatusKey => "Status_Running";

        // 处理中的状态文本，包含当前选中的协议名称和硬件加速信息。
        protected override string ProcessingStatusText =>
            ResourceService.Format("Status_Running") + " | " +
            LivePhotoProtocol.FromIndex(SelectedModeIndex).DisplayName +
            GetHardwareSuffix();

        #region Observable Properties

        // 输入文件夹路径（用户选择的待扫描目录）。
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
                    ScanDirectoryCommand.Execute(null);
                }
            }
        }

        // 输出文件夹路径（合并后的实况照片存放目录）。默认在输入目录下创建子目录。
        [ObservableProperty]
        private string _outputDirectory = string.Empty;

        partial void OnOutputDirectoryChanged(string value) => _openMergeOutputFolderCommand?.NotifyCanExecuteChanged();

        // 扫描到的有效配对总数。
        [ObservableProperty]
        private int _totalPairsCount = 0;

        // 扫描到的无配对图片文件数。
        [ObservableProperty]
        private int _standaloneImagesCount = 0;

        // 扫描到的无配对视频文件数。
        [ObservableProperty]
        private int _standaloneVideosCount = 0;

        // 目录选择面板是否展开显示。
        [ObservableProperty]
        private bool _isDirectoryPanelOpen = true;

        // 合并进度百分比（0~100）。
        [ObservableProperty]
        private double _mergeProgress = 0;

        #endregion

        #region Properties

        // 扫描按钮的文本，扫描中显示"取消"，其余显示"扫描"。
        public string ScanButtonText => IsScanning
            ? ResourceService.GetString("MergePage_DynamicCancelText")
            : ResourceService.GetString("MergePage_DynamicScanText");

        // 合并任务列表（Observable 集合，支持高性能批量更新）。
        public BulkObservableCollection<MergeTask> Tasks { get; } = [];

        // 当前选中的合并模式（协议）索引，持久化存储到设置中。
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

        // 在文件资源管理器中打开输入文件夹的命令。
        public IAsyncRelayCommand OpenMergeInputFolderCommand => _openMergeInputFolderCommand ??= new AsyncRelayCommand(OpenMergeInputFolderAsync, () => !string.IsNullOrWhiteSpace(InputDirectory));

        // 在文件资源管理器中打开输出文件夹的命令。
        public IAsyncRelayCommand OpenMergeOutputFolderCommand => _openMergeOutputFolderCommand ??= new AsyncRelayCommand(OpenMergeOutputFolderAsync, () => !string.IsNullOrWhiteSpace(OutputDirectory));

        #endregion

        #region Constructor

        public MergeViewModel()
        {
            SetStatus("Status_Init");
            _uiUpdateTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
            _uiUpdateTimer.Tick += UiUpdateTimer_Tick;
        }

        #endregion

        #region Command-Related Properties

        // 主操作按钮（开始/停止合并）的文本。
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

        // 当前是否允许开始处理（扫描中不允许）。
        public override bool IsProcessingAllowed => !IsScanning;

        // 当前是否可以编辑合并模式选择（扫描和处理中均不允许）。
        public bool CanEditSelectedMode => !IsScanning && !IsProcessing;

        #endregion

        #region WorkViewModelBase Overrides

        // <inheritdoc/>
        protected override void OnScanStateChanged(bool isScanning)
        {
            OnPropertyChanged(nameof(ScanButtonText));
        }

        // <inheritdoc/>
        protected override void OnBeginScanSession()
        {
            AppViewModel.Instance.BeginMergeScanSession();
        }

        // <inheritdoc/>
        protected override void OnApplyScanProgress(WorkProgressSnapshot snapshot)
        {
            AppViewModel.Instance.ApplyMergeScanProgress(snapshot);
        }

        // <inheritdoc/>
        protected override void OnCompleteScanSnapshot()
        {
            AppViewModel.Instance.CompleteFooterWorkSnapshot();
        }

        // <inheritdoc/>
        protected override void OnScanningEnded()
        {
            base.OnScanningEnded();
        }

        #endregion

        #region UI Update Timer

        // UI 更新定时器回调，定期刷新合并进度和进度文本。
        // 通过 _completedTasksCount 和 TotalPairsCount 计算百分比。
        private void UiUpdateTimer_Tick(object? sender, object e)
        {
            if (TotalPairsCount == 0) return;
            int currentCompleted = _completedTasksCount;
            MergeProgress = (currentCompleted * 100.0) / TotalPairsCount;
            Progress = MergeProgress;
            ProgressText = $"{currentCompleted}/{TotalPairsCount}";
            CheckAndApplyPendingState();
        }

        #endregion

        #region Run State Lifecycle

        // <inheritdoc/>
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

        // <inheritdoc/>
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

        // <inheritdoc/>
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

        // <inheritdoc/>
        protected override void OnCleanup()
        {
            _uiUpdateTimer.Stop();
            _uiUpdateTimer.Tick -= UiUpdateTimer_Tick;
            Tasks.ReplaceRange([]);
            ThumbnailService.ClearCache();
        }

        #endregion

        #region Scan Command

        // 扫描输入文件夹中的图片-视频配对，支持文件名匹配和元数据匹配两种模式。
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

        #endregion

        #region Secondary / Toggle Commands

        // 切换次要操作：未处理时清除状态，处理中则切换暂停/继续。
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

        // 切换合并处理状态：开始合并或停止合并。
        // 停止时显示已取消对话框，完成后显示完成对话框。
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

        #endregion

        #region Result Dialogs

        // 显示合并已完成对话框，可打开输出文件夹。
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

        // 显示合并已被用户取消的结果对话框，汇总成功/失败/未处理数量。
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


        #endregion

        #region Task Execution

        // 执行所有合并任务的异步核心方法。
        // 处理流程：初始化状态 → 创建输出/临时目录 → 按并发限制并行处理任务 →
        // 对 HEIC 图片转码 JPEG、视频转码 MP4、协议预处理 → 写入实况照片文件 →
        // 清理临时文件 → 显示结果对话框。
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
                            string? mergeSubDir = null;
                            if (AppSettingsService.GetValue("IsOutputPreserveSubfolderStructure", false))
                                mergeSubDir = PathHelper.GetRelativeSubDirectory(InputDirectory, task.ImagePath);
                            string finalPath = PathHelper.GetUniqueFilePath(outputDir, outputName, mergeSubDir);
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
            catch (Exception ex) when (ex is not OperationCanceledException)
            {
                // 非取消类异常 = 程序缺陷或系统资源耗尽。
                // 不能悄悄吞掉——否则进程退出码为 0，用户无法感知崩溃。
                LogService.Merge($"RunTasksAsync fatal error: {ex.Message}", LogLevel.Error, ex);
                Environment.ExitCode = unchecked((int)0xE0000001);
                throw;
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

                // 关闭中不弹对话框，避免在窗口销毁期间操作 XamlRoot。
                // 多个队列同时完成时 WinUI 只允许一个 ContentDialog，
                // 冲突会抛 COMException，这里吞掉即可（不影响处理结果）。
                if (Tasks.Count > 0 && !_isCleaningUp)
                {
                    try
                    {
                        if (wasCancelled)
                            await ShowMergeCancelledDialogAsync();
                        else
                            await ShowMergeAlreadyDoneDialogAsync();
                    }
                    catch (System.Runtime.InteropServices.COMException ex)
                    {
                        LogService.Debug($"Completion dialog skipped (another dialog already open): {ex.Message}", LogSource.UI);
                    }
                }
            }
        }

        #endregion

        #region Task Status Events

        // 当某个合并任务开始时触发，可用于自动滚动到当前处理的任务。
        public event EventHandler<MergeTask>? TaskStartedForScroll;

        // 当所有合并任务处理完毕（全部完成或停止）时触发，可用于滚动到列表顶部。
        public event EventHandler? ProcessingCompletedForScroll;

        // 标记任务开始处理（设置为 Processing 状态）。
        private void UpdateTaskStarted(MergeTask task)
        {
            task.Status = ProcessStatus.Processing;
            task.Details = ResourceService.GetString("Task_Processing");
            TaskStartedForScroll?.Invoke(this, task);
        }

        // 标记任务被用户取消（保留 Processing 状态，颜色中性，只更新详情）。
        private void UpdateTaskCancelled(MergeTask task, string detailMessage)
        {
            // 用户取消不标记为"失败"——保留 Processing 状态，颜色中性，只更新详情
            task.Details = detailMessage;
        }

        // 更新任务完成状态（成功/失败），如果所有任务完成则触发 ProcessingCompletedForScroll 事件。
        private void UpdateTaskCompleted(MergeTask task, bool isSuccess, string detailMessage, int completedCount)
        {
            task.Status = isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
            task.Details = detailMessage;

            if (completedCount >= Tasks.Count && Tasks.Count > 0)
            {
                ProcessingCompletedForScroll?.Invoke(this, EventArgs.Empty);
            }
        }

        #endregion

        #region Folder Commands

        // 在文件资源管理器中打开输入文件夹。
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

        // 在文件资源管理器中打开输出文件夹（不存在则自动创建）。
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

        #endregion
    }
}
