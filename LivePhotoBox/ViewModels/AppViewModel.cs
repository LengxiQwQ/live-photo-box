using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Collections;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.ViewModels
{
    public partial class AppViewModel : ObservableObject
    {
        private const string CrashLogLanguageTag = "en-US";

        public static AppViewModel Instance { get; } = new AppViewModel();

        private string _comboStatus = string.Empty;
        private string _splitStatus = string.Empty;
        private string _repairStatus = string.Empty;
        private string _comboStatusForLog = string.Empty;
        private string _splitStatusForLog = string.Empty;
        private string _repairStatusForLog = string.Empty;
        private string? _currentStatusPageTag;
        [ObservableProperty] private double _comboProgress = 0;
        [ObservableProperty] private string _progressText = "0/0";

        public string ComboStatus
        {
            get => _comboStatus;
            set
            {
                if (SetProperty(ref _comboStatus, value))
                {
                    if (CurrentStatusPageTag == "Combo")
                    {
                        OnPropertyChanged(nameof(CurrentPageStatus));
                    }

                    CrashLogService.UpdateSessionState();
                }
            }
        }

        public string SplitStatus
        {
            get => _splitStatus;
            set
            {
                if (SetProperty(ref _splitStatus, value))
                {
                    if (CurrentStatusPageTag == "Split")
                    {
                        OnPropertyChanged(nameof(CurrentPageStatus));
                    }

                    CrashLogService.UpdateSessionState();
                }
            }
        }

        public string RepairStatus
        {
            get => _repairStatus;
            set
            {
                if (SetProperty(ref _repairStatus, value))
                {
                    if (CurrentStatusPageTag == "Repair")
                    {
                        OnPropertyChanged(nameof(CurrentPageStatus));
                    }

                    CrashLogService.UpdateSessionState();
                }
            }
        }

        public string SplitInputDirectory
        {
            get => _splitInputDirectory;
            set => SetProperty(ref _splitInputDirectory, value);
        }

        public string SplitOutputDirectory
        {
            get => _splitOutputDirectory;
            set => SetProperty(ref _splitOutputDirectory, value);
        }

        public int SplitQueuedCount
        {
            get => _splitQueuedCount;
            set => SetProperty(ref _splitQueuedCount, value);
        }

        public int SplitRecognizedCount
        {
            get => _splitRecognizedCount;
            set => SetProperty(ref _splitRecognizedCount, value);
        }

        public int SplitSkippedCount
        {
            get => _splitSkippedCount;
            set => SetProperty(ref _splitSkippedCount, value);
        }

        public string SplitActionBtnText
        {
            get => string.IsNullOrWhiteSpace(_splitActionBtnText)
                ? ResourceService.GetString("Btn_StartSplit")
                : _splitActionBtnText;
            set => SetProperty(ref _splitActionBtnText, value);
        }

        public string SplitClearBtnText
        {
            get
            {
                if (!IsSplitProcessing) return ResourceService.GetString("Btn_ClearList");
                return IsSplitPaused ? ResourceService.GetString("Btn_Resume") : ResourceService.GetString("Btn_Pause");
            }
        }

        public double SplitProgress
        {
            get => _splitProgress;
            set => SetProperty(ref _splitProgress, value);
        }

        public string SplitProgressText
        {
            get => _splitProgressText;
            set => SetProperty(ref _splitProgressText, value);
        }

        public int SelectedSplitFormatIndex
        {
            get => _selectedSplitFormatIndex;
            set => SetProperty(ref _selectedSplitFormatIndex, value);
        }

        public bool IsSplitDirectoryPanelOpen
        {
            get => _isSplitDirectoryPanelOpen;
            set => SetProperty(ref _isSplitDirectoryPanelOpen, value);
        }

        public string CurrentPageStatus => CurrentStatusPageTag switch
        {
            "Combo" => ComboStatus,
            "Split" => SplitStatus,
            "Repair" => RepairStatus,
            _ => string.Empty
        };

        public bool IsStatusBarVisible => CurrentStatusPageTag is "Combo" or "Split" or "Repair";

        public string ComboStatusForLog => _comboStatusForLog;
        public string SplitStatusForLog => _splitStatusForLog;
        public string RepairStatusForLog => _repairStatusForLog;
        public string CurrentPageStatusForLog => CurrentStatusPageTag switch
        {
            "Combo" => ComboStatusForLog,
            "Split" => SplitStatusForLog,
            "Repair" => RepairStatusForLog,
            _ => string.Empty
        };

        public string? CurrentStatusPageTag
        {
            get => _currentStatusPageTag;
            private set
            {
                if (!SetProperty(ref _currentStatusPageTag, value))
                {
                    return;
                }

                OnPropertyChanged(nameof(CurrentPageStatus));
                OnPropertyChanged(nameof(IsStatusBarVisible));
                CrashLogService.UpdateSessionState();
            }
        }

        public void SetCurrentStatusPage(string? pageTag)
        {
            CurrentStatusPageTag = pageTag;
        }

        [ObservableProperty] private string _inputDirectory = string.Empty;
        [ObservableProperty] private string _outputDirectory = string.Empty;

        private string _splitInputDirectory = string.Empty;
        private string _splitOutputDirectory = string.Empty;

        [ObservableProperty] private int _totalPairsCount = 0;
        [ObservableProperty] private int _standaloneImagesCount = 0;
        [ObservableProperty] private int _standaloneVideosCount = 0;

        private int _splitQueuedCount;
        private int _splitRecognizedCount;
        private int _splitSkippedCount;

        [ObservableProperty] private string _actionBtnText = string.Empty;

        private string _splitActionBtnText = string.Empty;
        private double _splitProgress;
        private string _splitProgressText = "0/0";
        private int _selectedSplitFormatIndex;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotProcessing))]
        [NotifyPropertyChangedFor(nameof(SecondaryBtnText))]
        private bool _isProcessing = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SecondaryBtnText))]
        private bool _isPaused = false;

        public bool IsNotProcessing => !IsProcessing;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSplitNotProcessing))]
        [NotifyPropertyChangedFor(nameof(SplitClearBtnText))]
        private bool _isSplitProcessing = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SplitClearBtnText))]
        private bool _isSplitPaused = false;

        public bool IsSplitNotProcessing => !IsSplitProcessing;

        public string SecondaryBtnText
        {
            get
            {
                if (!IsProcessing) return ResourceService.GetString("Btn_ClearList");
                return IsPaused ? ResourceService.GetString("Btn_Resume") : ResourceService.GetString("Btn_Pause");
            }
        }

        [ObservableProperty] private bool _isDirectoryPanelOpen = true;
        private bool _isSplitDirectoryPanelOpen = true;

        private CancellationTokenSource? _cancellationTokenSource;
        private CancellationTokenSource? _splitCancellationTokenSource;
        private readonly ManualResetEventSlim _pauseEvent = new(true);
        private readonly ManualResetEventSlim _splitPauseEvent = new(true);

        private string _hwEncoderName = "Software CPU";

        [ObservableProperty] private int _selectedModeIndex = 1;

        [ObservableProperty] private int _languageIndex;
        [ObservableProperty] private int _elementTheme;
        [ObservableProperty] private int _backdropIndex;

        private string? _latestCrashLogPath;
        private string? _latestCrashDumpPath;
        private string? _latestRecoveredCrashLogPath;
        private IRelayCommand? _openCrashLogFolderActionCommand;
        private IAsyncRelayCommand? _openLatestCrashLogActionCommand;
        private IAsyncRelayCommand? _exportLatestCrashLogActionCommand;
        private IRelayCommand? _clearCrashLogsActionCommand;
        private IRelayCommand? _generateTestCrashLogActionCommand;
        private IAsyncRelayCommand? _openIssueFeedbackActionCommand;

        public BulkObservableCollection<LivePhotoMergeTask> ComboTasks { get; } = [];
        public BulkObservableCollection<LivePhotoSplitTask> SplitTasks { get; } = [];

        public bool HasCrashArtifacts => GetLatestCrashArtifactPath() != null;
        public string LastCrashFileNameText => GetLatestCrashArtifactPath() is string latestCrashArtifactPath
            ? Path.GetFileName(latestCrashArtifactPath)
            : ResourceService.GetString("SettingsPage_CrashNoCrashValue");
        public IRelayCommand OpenCrashLogFolderActionCommand => _openCrashLogFolderActionCommand ??= new RelayCommand(OpenCrashLogFolder);
        public IAsyncRelayCommand OpenLatestCrashLogActionCommand => _openLatestCrashLogActionCommand ??= new AsyncRelayCommand(OpenLatestCrashLogAsync, () => HasCrashArtifacts);
        public IAsyncRelayCommand ExportLatestCrashLogActionCommand => _exportLatestCrashLogActionCommand ??= new AsyncRelayCommand(ExportLatestCrashLogAsync, CanExportLatestCrashLog);
        public IRelayCommand ClearCrashLogsActionCommand => _clearCrashLogsActionCommand ??= new RelayCommand(ClearCrashLogs, CanClearCrashLogs);
        public IRelayCommand GenerateTestCrashLogActionCommand => _generateTestCrashLogActionCommand ??= new RelayCommand(GenerateTestCrashLog);
        public IAsyncRelayCommand OpenIssueFeedbackActionCommand => _openIssueFeedbackActionCommand ??= new AsyncRelayCommand(OpenIssueFeedbackAsync);

        private bool _isInitialized;

        public AppViewModel()
        {
            SetComboStatus("Status_Init");
            SetSplitStatus("SplitPage_Status_Ready");
            SetRepairStatus("RepairPage_Status_Ready");
            ActionBtnText = ResourceService.GetString("Btn_StartCombo");
            SplitActionBtnText = ResourceService.GetString("Btn_StartSplit");
            LoadSettings();
            LanguageService.ApplyLanguageOverride(LanguageIndex);
            RefreshCrashLogs();

            _isInitialized = true;
            PropertyChanged += OnPropertyChangedSave;
            _ = DetectGPUAndInitializeAsync();
        }

        partial void OnLanguageIndexChanged(int oldValue, int newValue)
        {
            if (!_isInitialized) return;
            string oldLang = LanguageService.GetEffectiveLanguage(oldValue);
            string newLang = LanguageService.GetEffectiveLanguage(newValue);
            LanguageService.ApplyLanguageOverride(newLang);

            if (oldLang != newLang)
            {
                _ = LanguageService.ShowRestartPromptAsync(newLang);
            }
        }

        private Task DetectGPUAndInitializeAsync()
        {
            SetComboStatus("Status_Ready");
            _hwEncoderName = "Software CPU";
            return Task.CompletedTask;
        }

        private void SetComboStatus(string resourceKey, params object[] args)
        {
            ComboStatus = ResourceService.Format(resourceKey, args);
            _comboStatusForLog = ResourceService.FormatForLanguage(CrashLogLanguageTag, resourceKey, args);
        }

        private void SetSplitStatus(string resourceKey, params object[] args)
        {
            SplitStatus = ResourceService.Format(resourceKey, args);
            _splitStatusForLog = ResourceService.FormatForLanguage(CrashLogLanguageTag, resourceKey, args);
        }

        private void SetRepairStatus(string resourceKey, params object[] args)
        {
            RepairStatus = ResourceService.Format(resourceKey, args);
            _repairStatusForLog = ResourceService.FormatForLanguage(CrashLogLanguageTag, resourceKey, args);
        }

        private void LoadSettings()
        {
            SelectedModeIndex = AppSettingsService.GetValue(nameof(SelectedModeIndex), 1);
            LanguageIndex = AppSettingsService.GetValue(nameof(LanguageIndex), 0);
            ElementTheme = AppSettingsService.GetValue(nameof(ElementTheme), 0);
            BackdropIndex = AppSettingsService.GetValue(nameof(BackdropIndex), 0);
            SelectedSplitFormatIndex = Math.Clamp(AppSettingsService.GetValue(nameof(SelectedSplitFormatIndex), 0), 0, 2);
        }

        private void OnPropertyChangedSave(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is null) return;
            switch (e.PropertyName)
            {
                case nameof(SelectedModeIndex): AppSettingsService.SetValue(nameof(SelectedModeIndex), SelectedModeIndex); break;
                case nameof(LanguageIndex): AppSettingsService.SetValue(nameof(LanguageIndex), LanguageIndex); break;
                case nameof(ElementTheme): AppSettingsService.SetValue(nameof(ElementTheme), ElementTheme); break;
                case nameof(BackdropIndex): AppSettingsService.SetValue(nameof(BackdropIndex), BackdropIndex); break;
                case nameof(SelectedSplitFormatIndex): AppSettingsService.SetValue(nameof(SelectedSplitFormatIndex), SelectedSplitFormatIndex); break;
            }
        }

        [RelayCommand]
        private void ScanSplitDirectory()
        {
            CrashLogService.RecordBreadcrumb($"ScanSplitDirectory requested. Input='{SplitInputDirectory}', Output='{SplitOutputDirectory}'");

            if (IsSplitProcessing)
            {
                return;
            }

            if (string.IsNullOrWhiteSpace(SplitInputDirectory) || !Directory.Exists(SplitInputDirectory))
            {
                SetSplitStatus("SplitPage_Status_InvalidInput");
                return;
            }

            if (string.IsNullOrWhiteSpace(SplitOutputDirectory))
            {
                SplitOutputDirectory = Path.Combine(SplitInputDirectory, "Output_SplitPhotos");
            }

            SplitThumbnailService.ClearCache();

            var pendingText = ResourceService.GetString("SplitPage_Task_Pending");
            var scanResult = LivePhotoSplitScanService.Scan(SplitInputDirectory);
            var tasks = scanResult.Files.Select((file, index) => new LivePhotoSplitTask
            {
                Index = index + 1,
                SourceFileName = Path.GetFileName(file.SourcePath),
                SourcePath = file.SourcePath,
                FileSize = FormatFileSize(file.FileSizeBytes),
                ProgressText = "0%",
                Status = ProcessStatus.Pending,
                Details = pendingText
            });

            SplitTasks.ReplaceRange(tasks);
            SplitQueuedCount = scanResult.Files.Count;
            SplitRecognizedCount = scanResult.RecognizedCount;
            SplitSkippedCount = scanResult.SkippedCount;
            SplitProgress = 0;
            SplitProgressText = $"0/{SplitQueuedCount}";
            IsSplitDirectoryPanelOpen = SplitQueuedCount == 0;

            if (SplitQueuedCount > 0)
            {
                SetSplitStatus("SplitPage_Status_ScanDone", SplitQueuedCount);
            }
            else
            {
                SetSplitStatus("SplitPage_Status_NoLivePhotos");
            }
        }

        [RelayCommand]
        private void ToggleSplitSecondaryAction()
        {
            CrashLogService.RecordBreadcrumb($"ToggleSplitSecondaryAction requested. IsSplitProcessing={IsSplitProcessing}, IsSplitPaused={IsSplitPaused}");

            if (!IsSplitProcessing)
            {
                ResetSplitQueue();
                SetSplitStatus("SplitPage_Status_Cleared");
                IsSplitDirectoryPanelOpen = true;
            }
            else
            {
                if (IsSplitPaused)
                {
                    IsSplitPaused = false;
                    SetSplitStatus("Status_Resumed");
                    _splitPauseEvent.Set();
                }
                else
                {
                    IsSplitPaused = true;
                    SetSplitStatus("Status_Paused");
                    _splitPauseEvent.Reset();
                }
            }
        }

        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task StartSplit()
        {
            CrashLogService.RecordBreadcrumb("StartSplit requested.");

            if (IsSplitProcessing)
            {
                _splitCancellationTokenSource?.Cancel();
                _splitPauseEvent.Set();
                SplitActionBtnText = ResourceService.GetString("Btn_Stopping");
                return;
            }

            if (SplitTasks.Count == 0)
            {
                SetSplitStatus("SplitPage_Status_EmptyQueue");
                return;
            }

            if (string.IsNullOrWhiteSpace(SplitOutputDirectory))
            {
                SplitOutputDirectory = Path.Combine(SplitInputDirectory, "Output_SplitPhotos");
            }

            if (string.IsNullOrWhiteSpace(SplitOutputDirectory))
            {
                SetSplitStatus("SplitPage_Status_WarnOutput");
                return;
            }

            await RunSplitTasksAsync();
        }

        private void ResetSplitQueue()
        {
            SplitTasks.ReplaceRange([]);
            SplitThumbnailService.ClearCache();
            SplitQueuedCount = 0;
            SplitRecognizedCount = 0;
            SplitSkippedCount = 0;
            SplitProgress = 0;
            SplitProgressText = "0/0";
            SplitActionBtnText = ResourceService.GetString("Btn_StartSplit");
        }

        private void InitializeSplitRunState()
        {
            IsSplitProcessing = true;
            IsSplitPaused = false;
            _splitPauseEvent.Set();
            SplitActionBtnText = ResourceService.GetString("Btn_StopRun");
            _splitCancellationTokenSource?.Dispose();
            _splitCancellationTokenSource = new CancellationTokenSource();

            int completedCount = SplitTasks.Count(task => task.Status == ProcessStatus.Success);
            SplitProgress = SplitQueuedCount == 0 ? 0 : (completedCount * 100.0) / SplitQueuedCount;
            SplitProgressText = $"{completedCount}/{SplitQueuedCount}";
            SetSplitStatus("SplitPage_Status_Running");
        }

        private void FinalizeSplitRunState(Stopwatch stopwatch)
        {
            stopwatch.Stop();
            IsSplitProcessing = false;
            IsSplitPaused = false;
            _splitPauseEvent.Set();
            SplitActionBtnText = ResourceService.GetString("Btn_StartSplit");

            var splitCancellationTokenSource = _splitCancellationTokenSource;
            _splitCancellationTokenSource = null;
            splitCancellationTokenSource?.Dispose();

            if (SplitQueuedCount > 0 && SplitProgress >= 100)
            {
                SetSplitStatus("SplitPage_Status_Done", stopwatch.Elapsed.TotalSeconds);
            }
        }

        private void UpdateSplitTaskStarted(LivePhotoSplitTask task)
        {
            task.Status = ProcessStatus.Processing;
            task.ProgressText = "0%";
            task.Details = ResourceService.GetString("SplitPage_Task_Processing");
        }

        private void UpdateSplitTaskCompleted(LivePhotoSplitTask task, bool isSuccess, string detailMessage, int completedCount)
        {
            task.Status = isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
            task.ProgressText = isSuccess ? "100%" : "0%";
            task.Details = detailMessage;
            SplitProgress = SplitQueuedCount == 0 ? 0 : (completedCount * 100.0) / SplitQueuedCount;
            SplitProgressText = $"{completedCount}/{SplitQueuedCount}";
        }

        // ==========================================
        // 【核心修复】：将循环转移到 Task.Run 后台线程，UI 更新使用 DispatcherQueue
        // ==========================================
        private async Task RunSplitTasksAsync()
        {
            InitializeSplitRunState();
            Stopwatch stopwatch = Stopwatch.StartNew();

            string outputDir = SplitOutputDirectory;
            int formatIndex = SelectedSplitFormatIndex;

            try
            {
                await Task.Run(async () =>
                {
                    int completedCount = SplitTasks.Count(task => task.Status == ProcessStatus.Success);

                    // 为了防止迭代时遇到跨线程读写问题，我们抓取一份需要处理的任务列表快照
                    var tasksToProcess = SplitTasks.ToList();

                    foreach (var task in tasksToProcess)
                    {
                        if (task.Status == ProcessStatus.Success)
                        {
                            continue;
                        }

                        // 这里会在后台线程中等待，不再卡死 UI 主界面
                        _splitPauseEvent.Wait(_splitCancellationTokenSource!.Token);
                        _splitCancellationTokenSource.Token.ThrowIfCancellationRequested();

                        // 任何涉及到界面的数据更新，都必须切回到主线程执行
                        App.MainWindow?.DispatcherQueue.TryEnqueue(() => UpdateSplitTaskStarted(task));

                        bool isSuccess;
                        string detailMessage;

                        try
                        {
                            await LivePhotoSplitService.SplitAsync(task.SourcePath, outputDir, formatIndex, _splitCancellationTokenSource.Token);
                            isSuccess = true;
                            detailMessage = ResourceService.GetString("SplitPage_Task_Success");
                        }
                        catch (OperationCanceledException)
                        {
                            App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                                UpdateSplitTaskCompleted(task, false, ResourceService.GetString("Status_Aborted") ?? "已停止", completedCount)
                            );
                            throw;
                        }
                        catch (Exception ex)
                        {
                            isSuccess = false;
                            detailMessage = ResourceService.Format("Task_Error", ex.Message);
                        }

                        completedCount++;
                        App.MainWindow?.DispatcherQueue.TryEnqueue(() => UpdateSplitTaskCompleted(task, isSuccess, detailMessage, completedCount));
                    }
                });
            }
            catch (OperationCanceledException)
            {
                SetSplitStatus("SplitPage_Status_Aborted");
            }
            finally
            {
                FinalizeSplitRunState(stopwatch);
            }
        }

        private void RefreshCrashLogs()
        {
            _latestCrashLogPath = CrashLogService.GetLatestCrashLogPath();
            _latestCrashDumpPath = CrashLogService.GetLatestCrashDumpPath();
            _latestRecoveredCrashLogPath = CrashLogService.GetLatestRecoveredCrashLogPath();

            if (!string.IsNullOrWhiteSpace(_latestCrashLogPath) && !File.Exists(_latestCrashLogPath))
            {
                _latestCrashLogPath = null;
            }

            if (!string.IsNullOrWhiteSpace(_latestCrashDumpPath) && !File.Exists(_latestCrashDumpPath))
            {
                _latestCrashDumpPath = null;
            }

            if (!string.IsNullOrWhiteSpace(_latestRecoveredCrashLogPath) && !File.Exists(_latestRecoveredCrashLogPath))
            {
                _latestRecoveredCrashLogPath = null;
            }

            OpenLatestCrashLogActionCommand.NotifyCanExecuteChanged();
            ExportLatestCrashLogActionCommand.NotifyCanExecuteChanged();
            ClearCrashLogsActionCommand.NotifyCanExecuteChanged();
            OnPropertyChanged(nameof(HasCrashArtifacts));
            OnPropertyChanged(nameof(LastCrashFileNameText));
        }

        private void OpenCrashLogFolder()
        {
            string logDirectory = CrashLogService.EnsureLogDirectoryPath();
            CrashLogService.RecordBreadcrumb($"OpenCrashLogFolder requested. Path='{logDirectory}'");
            FilePickerService.OpenFolderInExplorer(logDirectory);
        }

        private async Task OpenLatestCrashLogAsync()
        {
            string? latestCrashArtifactPath = GetLatestCrashArtifactPath();
            if (string.IsNullOrWhiteSpace(latestCrashArtifactPath) || !File.Exists(latestCrashArtifactPath))
            {
                RefreshCrashLogs();
                return;
            }

            CrashLogService.RecordBreadcrumb($"OpenLatestCrashArtifact requested. File='{Path.GetFileName(latestCrashArtifactPath)}'");
            await FilePickerService.OpenFileAsync(latestCrashArtifactPath);
        }

        private async Task ExportLatestCrashLogAsync()
        {
            string? latestCrashArtifactPath = GetLatestCrashArtifactPath();
            if (string.IsNullOrWhiteSpace(latestCrashArtifactPath) || !File.Exists(latestCrashArtifactPath))
            {
                RefreshCrashLogs();
                return;
            }

            CrashLogService.RecordBreadcrumb($"ExportLatestCrashArtifact requested. File='{Path.GetFileName(latestCrashArtifactPath)}'");
            await FilePickerService.ExportFileCopyAsync(latestCrashArtifactPath, Path.GetFileName(latestCrashArtifactPath));
        }

        private void ClearCrashLogs()
        {
            CrashLogService.RecordBreadcrumb("ClearCrashLogs requested.");
            CrashLogService.DeleteAllCrashArtifacts();
            RefreshCrashLogs();
        }

        private void GenerateTestCrashLog()
        {
            CrashLogService.RecordBreadcrumb("GenerateTestCrashLog requested.");
            CrashLogService.GenerateTestCrashLog();
            RefreshCrashLogs();
        }

        private async Task OpenIssueFeedbackAsync()
        {
            CrashLogService.RecordBreadcrumb("OpenIssueFeedback requested.");
            await FeedbackService.OpenIssuePageAsync();
        }

        [RelayCommand]
        private void RestoreDefaultSettings()
        {
            LanguageIndex = 0;
            BackdropIndex = 0;
            ElementTheme = 0;
            SelectedModeIndex = 1;
            SelectedSplitFormatIndex = 0;
        }

        private bool CanExportLatestCrashLog()
        {
            return HasCrashArtifacts;
        }

        private bool CanClearCrashLogs()
        {
            return HasCrashArtifacts;
        }

        private string? GetLatestCrashArtifactPath()
        {
            return new[] { _latestCrashLogPath, _latestCrashDumpPath, _latestRecoveredCrashLogPath }
                .Where(path => !string.IsNullOrWhiteSpace(path) && File.Exists(path))
                .OrderByDescending(path => File.GetLastWriteTimeUtc(path!))
                .FirstOrDefault();
        }

        private string FormatFileSize(long bytes)
        {
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }

        [RelayCommand]
        public void ScanDirectory()
        {
            CrashLogService.RecordBreadcrumb($"ScanDirectory requested. Input='{InputDirectory}', Output='{OutputDirectory}'");

            if (IsProcessing) return;
            if (string.IsNullOrWhiteSpace(InputDirectory) || !Directory.Exists(InputDirectory))
            {
                SetComboStatus("Status_InvalidInput");
                return;
            }

            ThumbnailService.ClearCache();

            var pendingText = ResourceService.GetString("Task_Pending");
            var scanResult = LivePhotoScanService.Scan(InputDirectory);
            var tasks = scanResult.Pairs.Select((pair, index) => new LivePhotoMergeTask
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
            });

            ComboTasks.ReplaceRange(tasks);

            if (ComboTasks.Count > 0)
            {
                IsDirectoryPanelOpen = false;
            }
            else
            {
                IsDirectoryPanelOpen = true;
            }

            TotalPairsCount = scanResult.Pairs.Count;
            StandaloneImagesCount = scanResult.StandaloneImagesCount;
            StandaloneVideosCount = scanResult.StandaloneVideosCount;

            ComboProgress = 0;
            ProgressText = $"0/{TotalPairsCount}";

            if (string.IsNullOrWhiteSpace(OutputDirectory) && ComboTasks.Count > 0)
            {
                OutputDirectory = Path.Combine(InputDirectory, "Output_LivePhotos");
            }

            SetComboStatus("Status_ScanDone", TotalPairsCount);
        }

        [RelayCommand]
        private void ToggleSecondaryAction()
        {
            CrashLogService.RecordBreadcrumb($"ToggleSecondaryAction requested. IsProcessing={IsProcessing}, IsPaused={IsPaused}");

            if (!IsProcessing)
            {
                ComboTasks.ReplaceRange([]);
                ThumbnailService.ClearCache();
                TotalPairsCount = 0;
                StandaloneImagesCount = 0;
                StandaloneVideosCount = 0;
                ComboProgress = 0;
                ProgressText = "0/0";
                SetComboStatus("Status_Cleared", _hwEncoderName);

                IsDirectoryPanelOpen = true;
            }
            else
            {
                if (IsPaused)
                {
                    IsPaused = false;
                    SetComboStatus("Status_Resumed");
                    _pauseEvent.Set();
                }
                else
                {
                    IsPaused = true;
                    SetComboStatus("Status_Paused");
                    _pauseEvent.Reset();
                }
            }
        }

        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task ToggleProcessAsync()
        {
            CrashLogService.RecordBreadcrumb($"ToggleProcessAsync requested. IsProcessing={IsProcessing}, QueueCount={ComboTasks.Count}");

            if (IsProcessing)
            {
                _cancellationTokenSource?.Cancel();
                _pauseEvent.Set();
                ActionBtnText = ResourceService.GetString("Btn_Stopping");
                return;
            }

            if (ComboTasks.Count == 0)
            {
                if (App.MainWindow?.Content?.XamlRoot != null)
                {
                    var dialog = new ContentDialog
                    {
                        Title = ResourceService.GetString("Msg_EmptyQueueTitle"),
                        Content = ResourceService.GetString("Msg_EmptyQueue"),
                        CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                        XamlRoot = App.MainWindow.Content.XamlRoot
                    };
                    await dialog.ShowAsync();
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                SetComboStatus("Status_WarnOutput");
                return;
            }

            IsDirectoryPanelOpen = false;
            await RunComboTasksAsync();
        }

        private void InitializeRunState()
        {
            CrashLogService.RecordBreadcrumb($"InitializeRunState. Output='{OutputDirectory}', Mode={SelectedModeIndex}, TotalPairs={TotalPairsCount}");
            IsProcessing = true;
            IsPaused = false;
            _pauseEvent.Set();
            ActionBtnText = ResourceService.GetString("Btn_StopRun");
            ComboProgress = 0;
            ProgressText = $"0/{TotalPairsCount}";
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            SetComboStatus("Status_Running");
        }

        private void FinalizeRunState(Stopwatch stopwatch)
        {
            CrashLogService.RecordBreadcrumb($"FinalizeRunState. ElapsedSeconds={stopwatch.Elapsed.TotalSeconds:F3}, Progress={ComboProgress:F2}");
            stopwatch.Stop();
            IsProcessing = false;
            IsPaused = false;
            _pauseEvent.Set();
            ActionBtnText = ResourceService.GetString("Btn_StartCombo");

            var cancellationTokenSource = _cancellationTokenSource;
            _cancellationTokenSource = null;
            cancellationTokenSource?.Dispose();

            if (ComboProgress >= 100)
            {
                SetComboStatus("Status_Done", stopwatch.Elapsed.TotalSeconds);
            }
        }

        private void UpdateTaskStarted(LivePhotoMergeTask task)
        {
            task.Status = ProcessStatus.Processing;
            task.Details = ResourceService.GetString("Task_Processing");
        }

        private void UpdateTaskCompleted(LivePhotoMergeTask task, bool isSuccess, string detailMessage, int completedCount)
        {
            task.Status = isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
            task.Details = detailMessage;
            ComboProgress = (completedCount * 100.0) / TotalPairsCount;
            ProgressText = $"{completedCount}/{TotalPairsCount}";
        }

        private async Task RunComboTasksAsync()
        {
            InitializeRunState();
            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                var options = new LivePhotoBatchRunOptions
                {
                    OutputDirectory = OutputDirectory,
                    SelectedModeIndex = SelectedModeIndex
                };

                await LivePhotoBatchRunnerService.RunAsync(
                    ComboTasks,
                    options,
                    _pauseEvent,
                    _cancellationTokenSource!.Token,
                    task => App.MainWindow?.DispatcherQueue.TryEnqueue(() => UpdateTaskStarted(task)),
                    (task, isSuccess, detailMessage, completedCount) => App.MainWindow?.DispatcherQueue.TryEnqueue(() => UpdateTaskCompleted(task, isSuccess, detailMessage, completedCount)));
            }
            catch (OperationCanceledException)
            {
                SetComboStatus("Status_Aborted");
            }
            finally
            {
                FinalizeRunState(stopwatch);
            }
        }
        // ==========================================
        // 修复页面 (RepairPage) 绑定的属性与命令 (纯UI占位)
        // ==========================================

        [ObservableProperty] private bool _isRepairDirectoryPanelOpen = true;
        [ObservableProperty] private string _repairInputDirectory = string.Empty;
        [ObservableProperty] private string _repairOutputDirectory = string.Empty;
        [ObservableProperty] private int _totalRepairCount = 0;
        [ObservableProperty] private int _repairSuccessCount = 0;
        [ObservableProperty] private int _repairFailedCount = 0;
        [ObservableProperty] private int _repairSelectedModeIndex = 0;
        [ObservableProperty] private bool _isRepairNotProcessing = true;
        [ObservableProperty] private string _repairProgressText = "0/0";
        [ObservableProperty] private double _repairProgress = 0;
        [ObservableProperty] private string _repairSecondaryBtnText = "取消";
        [ObservableProperty] private string _repairActionBtnText = "开始修复";

        public BulkObservableCollection<LivePhotoRepairTask> RepairTasks { get; } = [];

        [RelayCommand]
        private void ScanRepairDirectory()
        {
            // 占位命令，暂时不写逻辑
        }

        [RelayCommand]
        private void ToggleRepairSecondaryAction()
        {
            // 占位命令，暂时不写逻辑
        }

        [RelayCommand]
        private void ToggleRepairProcess()
        {
            // 占位命令，暂时不写逻辑
        }
    }
}