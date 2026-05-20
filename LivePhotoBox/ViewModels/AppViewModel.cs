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
        public event EventHandler<string>? RequestNavigateToPage;

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

        [RelayCommand]
        private void GoToTutorial(string feature)
        {
            RequestNavigateToPage?.Invoke(this, $"Home_{feature}");
        }

        private async Task ShowEmptyQueueDialogAsync(string targetFeature)
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString("Msg_EmptyQueueTitle"),
                    Content = new TextBlock
                    {
                        Text = ResourceService.GetString("Msg_EmptyQueue"),
                        FontSize = 16,
                        TextWrapping = TextWrapping.Wrap
                    },
                    PrimaryButtonText = ResourceService.GetString("Msg_GoToTutorial"),
                    CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                    DefaultButton = ContentDialogButton.Primary,
                    XamlRoot = App.MainWindow.Content.XamlRoot
                };

                var result = await dialog.ShowAsync();

                if (result == ContentDialogResult.Primary)
                {
                    RequestNavigateToPage?.Invoke(this, $"Home_{targetFeature}");
                }
            }
        }

        private async Task ShowNoInputDirectoryDialogAsync(string targetFeature)
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString($"{targetFeature}Page_Msg_NoInputDirectoryTitle"),
                    Content = new TextBlock
                    {
                        Text = ResourceService.GetString($"{targetFeature}Page_Msg_NoInputDirectory"),
                        FontSize = 16,
                        TextWrapping = TextWrapping.Wrap
                    },
                    CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = App.MainWindow.Content.XamlRoot
                };

                await dialog.ShowAsync();
            }
        }

        private async Task ShowInvalidInputDirectoryDialogAsync()
        {
            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                var dialog = new ContentDialog
                {
                    Title = ResourceService.GetString("Msg_InvalidInputDirectoryTitle"),
                    Content = new TextBlock
                    {
                        Text = ResourceService.GetString("Msg_InvalidInputDirectory"),
                        FontSize = 16,
                        TextWrapping = TextWrapping.Wrap
                    },
                    CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                    DefaultButton = ContentDialogButton.Close,
                    XamlRoot = App.MainWindow.Content.XamlRoot
                };

                await dialog.ShowAsync();
            }
        }

        public void SetCurrentStatusPage(string? pageTag)
        {
            CurrentStatusPageTag = pageTag;
        }

        // ==========================================
        // Combo Page 逻辑
        // ==========================================
        [ObservableProperty] private string _inputDirectory = string.Empty;

        partial void OnInputDirectoryChanged(string value)
        {
            _openComboInputFolderCommand?.NotifyCanExecuteChanged();

            // 核心痛点修复：一旦更换输入目录，立刻清空旧的输出目录，防止照片混在一起
            OutputDirectory = string.Empty;

            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            {
                if (ScanDirectoryCommand.CanExecute(null) && !IsScanning)
                {
                    ScanDirectoryCommand.Execute(null);
                }
            }
        }

        [ObservableProperty] private string _outputDirectory = string.Empty;
        partial void OnOutputDirectoryChanged(string value) => _openComboOutputFolderCommand?.NotifyCanExecuteChanged();

        // ==========================================
        // Split Page 逻辑统一升级
        // ==========================================
        [ObservableProperty] private string _splitInputDirectory = string.Empty;

        partial void OnSplitInputDirectoryChanged(string value)
        {
            _openSplitInputFolderCommand?.NotifyCanExecuteChanged();

            // 核心痛点修复：彻底统一 Split 页面的清空和自动扫描逻辑
            SplitOutputDirectory = string.Empty;

            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            {
                if (ScanSplitDirectoryCommand.CanExecute(null) && !IsSplitScanning)
                {
                    ScanSplitDirectoryCommand.Execute(null);
                }
            }
        }

        [ObservableProperty] private string _splitOutputDirectory = string.Empty;
        partial void OnSplitOutputDirectoryChanged(string value) => _openSplitOutputFolderCommand?.NotifyCanExecuteChanged();

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

        public bool IsNotProcessing => !IsProcessing;

        // --- 全新加入的扫描取消与按钮动态文本状态机制 ---
        private CancellationTokenSource? _scanCancellationTokenSource;
        private CancellationTokenSource? _splitScanCancellationTokenSource;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotScanning))]
        [NotifyPropertyChangedFor(nameof(ComboScanBtnText))]
        private bool _isScanning = false;

        public bool IsNotScanning => !IsScanning;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSplitNotScanning))]
        [NotifyPropertyChangedFor(nameof(SplitScanBtnText))]
        private bool _isSplitScanning = false;

        public bool IsSplitNotScanning => !IsSplitScanning;

        public string ComboScanBtnText => IsScanning
            ? ResourceService.GetString("ComboPage_DynamicCancelText")
            : ResourceService.GetString("ComboPage_DynamicScanText");

        public string SplitScanBtnText => IsSplitScanning
            ? ResourceService.GetString("SplitPage_DynamicCancelText")
            : ResourceService.GetString("SplitPage_DynamicScanText");
        // ------------------------------------------------

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SecondaryBtnText))]
        private bool _isPaused = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsSplitNotProcessing))]
        [NotifyPropertyChangedFor(nameof(SplitClearBtnText))]
        private bool _isSplitProcessing = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SplitClearBtnText))]
        private bool _isSplitPaused = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RepairScanBtnText))]
        [NotifyPropertyChangedFor(nameof(IsRepairNotScanning))]
        private bool _isRepairScanning = false;
        public bool IsRepairNotScanning => !IsRepairScanning;
        private CancellationTokenSource? _repairScanCancellationTokenSource;

        public string RepairScanBtnText => IsRepairScanning
            ? ResourceService.GetString("RepairPage_DynamicCancelText")
            : ResourceService.GetString("RepairPage_DynamicScanText");

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

        private IAsyncRelayCommand? _openComboInputFolderCommand;
        private IRelayCommand? _openComboOutputFolderCommand;
        private IAsyncRelayCommand? _openSplitInputFolderCommand;
        private IRelayCommand? _openSplitOutputFolderCommand;
        private IAsyncRelayCommand? _openRepairInputFolderCommand;
        private IRelayCommand? _openRepairOutputFolderCommand;

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

        public IAsyncRelayCommand OpenComboInputFolderCommand => _openComboInputFolderCommand ??= new AsyncRelayCommand(OpenComboInputFolderAsync, () => !string.IsNullOrWhiteSpace(InputDirectory));
        public IRelayCommand OpenComboOutputFolderCommand => _openComboOutputFolderCommand ??= new RelayCommand(OpenComboOutputFolder, () => !string.IsNullOrWhiteSpace(OutputDirectory));
        public IAsyncRelayCommand OpenSplitInputFolderCommand => _openSplitInputFolderCommand ??= new AsyncRelayCommand(OpenSplitInputFolderAsync, () => !string.IsNullOrWhiteSpace(SplitInputDirectory));
        public IRelayCommand OpenSplitOutputFolderCommand => _openSplitOutputFolderCommand ??= new RelayCommand(OpenSplitOutputFolder, () => !string.IsNullOrWhiteSpace(SplitOutputDirectory));
        public IAsyncRelayCommand OpenRepairInputFolderCommand => _openRepairInputFolderCommand ??= new AsyncRelayCommand(OpenRepairInputFolderAsync, () => !string.IsNullOrWhiteSpace(RepairInputDirectory));
        public IRelayCommand OpenRepairOutputFolderCommand => _openRepairOutputFolderCommand ??= new RelayCommand(OpenRepairOutputFolder, () => !string.IsNullOrWhiteSpace(RepairOutputDirectory));

        private bool _isInitialized;

        public AppViewModel()
        {
            SetComboStatus("Status_Init");
            SetSplitStatus("SplitPage_Status_Ready");
            SetRepairStatus("RepairPage_Status_Ready");
            ActionBtnText = ResourceService.GetString("Btn_StartCombo");
            SplitActionBtnText = ResourceService.GetString("Btn_StartSplit");
            RepairActionBtnText = ResourceService.GetString("Btn_StartRepair");
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
            IsRepairOutputToDirectory = AppSettingsService.GetValue(nameof(IsRepairOutputToDirectory), false);
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
                case nameof(IsRepairOutputToDirectory): AppSettingsService.SetValue(nameof(IsRepairOutputToDirectory), IsRepairOutputToDirectory); break;
            }
        }

        [RelayCommand(AllowConcurrentExecutions = true)]
        public async Task ScanSplitDirectoryAsync()
        {
            CrashLogService.RecordBreadcrumb($"ScanSplitDirectory requested. Input='{SplitInputDirectory}', Output='{SplitOutputDirectory}'");

            if (IsSplitProcessing) return;

            if (IsSplitScanning)
            {
                // 点击取消时触发 Cancel
                _splitScanCancellationTokenSource?.Cancel();
                return;
            }

            if (string.IsNullOrWhiteSpace(SplitInputDirectory) || !Directory.Exists(SplitInputDirectory))
            {
                await ShowNoInputDirectoryDialogAsync("Split");
                return;
            }

            IsSplitScanning = true;
            _splitScanCancellationTokenSource = new CancellationTokenSource();
            var token = _splitScanCancellationTokenSource.Token;

            // 逻辑前移：一旦开始扫描，就提前填写输出目录
            if (string.IsNullOrWhiteSpace(SplitOutputDirectory))
            {
                SplitOutputDirectory = Path.Combine(SplitInputDirectory, "Output_SplitPhotos");
            }

            try
            {
                SplitThumbnailService.ClearCache();
                var pendingText = ResourceService.GetString("SplitPage_Task_Pending");

                // 传入 cancellation token 允许后台抛出取消异常
                var scanResult = await Task.Run(() => LivePhotoSplitScanService.Scan(SplitInputDirectory), token);

                // 如果已经触发取消，直接抛出异常阻断后续 UI 更新
                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();

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

                IsSplitDirectoryPanelOpen = true;

                if (SplitQueuedCount > 0)
                {
                    SetSplitStatus("SplitPage_Status_ScanDone", SplitQueuedCount);
                }
                else
                {
                    SetSplitStatus("SplitPage_Status_NoLivePhotos");
                }
            }
            catch (OperationCanceledException)
            {
                SetSplitStatus("Status_Aborted");
            }
            catch (Exception ex)
            {
                CrashLogService.RecordBreadcrumb($"ScanSplitDirectory error: {ex.Message}");
                SetSplitStatus("Status_Error", ex.Message);
            }
            finally
            {
                IsSplitScanning = false;
                _splitScanCancellationTokenSource?.Dispose();
                _splitScanCancellationTokenSource = null;
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
                IsSplitDirectoryPanelOpen = true;
                return;
            }

            if (SplitTasks.Count == 0)
            {
                SetSplitStatus("SplitPage_Status_EmptyQueue");
                await ShowEmptyQueueDialogAsync("Split");
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

            IsSplitDirectoryPanelOpen = false;

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
                    var tasksToProcess = SplitTasks.ToList();

                    foreach (var task in tasksToProcess)
                    {
                        if (task.Status == ProcessStatus.Success)
                        {
                            continue;
                        }

                        _splitPauseEvent.Wait(_splitCancellationTokenSource!.Token);
                        _splitCancellationTokenSource.Token.ThrowIfCancellationRequested();

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
            catch (Exception ex)
            {
                CrashLogService.RecordBreadcrumb($"RunSplitTasksAsync error: {ex.Message}");
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
            catch (Exception ex) { CrashLogService.RecordBreadcrumb($"OpenComboInput error: {ex.Message}"); }
        }

        private void OpenComboOutputFolder()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(OutputDirectory)) return;

                if (!Directory.Exists(OutputDirectory))
                {
                    Directory.CreateDirectory(OutputDirectory);
                }
                FilePickerService.OpenFolderInExplorer(OutputDirectory);
            }
            catch (Exception ex) { CrashLogService.RecordBreadcrumb($"OpenComboOutput error: {ex.Message}"); }
        }

        private async Task OpenSplitInputFolderAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(SplitInputDirectory)) return;

                if (!Directory.Exists(SplitInputDirectory))
                {
                    await ShowInvalidInputDirectoryDialogAsync();
                    return;
                }
                FilePickerService.OpenFolderInExplorer(SplitInputDirectory);
            }
            catch (Exception ex) { CrashLogService.RecordBreadcrumb($"OpenSplitInput error: {ex.Message}"); }
        }

        private void OpenSplitOutputFolder()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(SplitOutputDirectory)) return;
                if (!Directory.Exists(SplitOutputDirectory))
                {
                    Directory.CreateDirectory(SplitOutputDirectory);
                }
                FilePickerService.OpenFolderInExplorer(SplitOutputDirectory);
            }
            catch (Exception ex) { CrashLogService.RecordBreadcrumb($"OpenSplitOutput error: {ex.Message}"); }
        }

        private async Task OpenRepairInputFolderAsync()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(RepairInputDirectory)) return;

                if (!Directory.Exists(RepairInputDirectory))
                {
                    await ShowInvalidInputDirectoryDialogAsync();
                    return;
                }
                FilePickerService.OpenFolderInExplorer(RepairInputDirectory);
            }
            catch (Exception ex) { CrashLogService.RecordBreadcrumb($"OpenRepairInput error: {ex.Message}"); }
        }

        private void OpenRepairOutputFolder()
        {
            try
            {
                if (string.IsNullOrWhiteSpace(RepairOutputDirectory)) return;
                if (!Directory.Exists(RepairOutputDirectory))
                {
                    Directory.CreateDirectory(RepairOutputDirectory);
                }
                FilePickerService.OpenFolderInExplorer(RepairOutputDirectory);
            }
            catch (Exception ex) { CrashLogService.RecordBreadcrumb($"OpenRepairOutput error: {ex.Message}"); }
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
            IsRepairOutputToDirectory = false;
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

        [RelayCommand(AllowConcurrentExecutions = true)]
        public async Task ScanDirectoryAsync()
        {
            CrashLogService.RecordBreadcrumb($"ScanDirectory requested. Input='{InputDirectory}', Output='{OutputDirectory}'");

            if (IsProcessing) return;

            if (IsScanning)
            {
                // 点击取消时触发 Cancel
                _scanCancellationTokenSource?.Cancel();
                return;
            }

            if (string.IsNullOrWhiteSpace(InputDirectory) || !Directory.Exists(InputDirectory))
            {
                await ShowNoInputDirectoryDialogAsync("Combo");
                return;
            }

            IsScanning = true;
            _scanCancellationTokenSource = new CancellationTokenSource();
            var token = _scanCancellationTokenSource.Token;

            // 逻辑前移：一旦开始扫描，就提前填写输出目录
            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                OutputDirectory = Path.Combine(InputDirectory, "Output_LivePhotos");
            }

            try
            {
                ThumbnailService.ClearCache();
                var pendingText = ResourceService.GetString("Task_Pending");

                // 传入 cancellation token 允许后台抛出取消异常
                var scanResult = await Task.Run(() => LivePhotoScanService.Scan(InputDirectory), token);

                // 如果已经触发取消，直接抛出异常阻断后续 UI 更新
                if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();

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
                IsDirectoryPanelOpen = true;

                TotalPairsCount = scanResult.Pairs.Count;
                StandaloneImagesCount = scanResult.StandaloneImagesCount;
                StandaloneVideosCount = scanResult.StandaloneVideosCount;

                ComboProgress = 0;
                ProgressText = $"0/{TotalPairsCount}";

                    SetComboStatus("Status_ScanDone", TotalPairsCount);
                }
                catch (OperationCanceledException)
                {
                    SetComboStatus("Status_Aborted");
                }
                catch (Exception ex)
                {
                    CrashLogService.RecordBreadcrumb($"ScanDirectory error: {ex.Message}");
                    SetComboStatus("Status_Error", ex.Message);
                }
                finally
                {
                    IsScanning = false;
                    _scanCancellationTokenSource?.Dispose();
                    _scanCancellationTokenSource = null;
                }
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
                IsDirectoryPanelOpen = true;
                return;
            }

            if (ComboTasks.Count == 0)
            {
                await ShowEmptyQueueDialogAsync("Combo");
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
            catch (Exception ex)
            {
                CrashLogService.RecordBreadcrumb($"RunComboTasksAsync error: {ex.Message}");
            }
            finally
            {
                FinalizeRunState(stopwatch);
            }
        }

        [ObservableProperty] private bool _isRepairDirectoryPanelOpen = true;

        [ObservableProperty] private string _repairInputDirectory = string.Empty;

        partial void OnRepairInputDirectoryChanged(string value)
        {
            _openRepairInputFolderCommand?.NotifyCanExecuteChanged();

            // 核心痛点修复：更换输入目录时立刻清空旧输出，防止照片混在一起
            RepairOutputDirectory = string.Empty;

            if (!string.IsNullOrWhiteSpace(value) && Directory.Exists(value))
            {
                if (ScanRepairDirectoryCommand.CanExecute(null) && !IsRepairScanning)
                {
                    ScanRepairDirectoryCommand.Execute(null);
                }
            }
        }

        [ObservableProperty] private string _repairOutputDirectory = string.Empty;
        partial void OnRepairOutputDirectoryChanged(string value) => _openRepairOutputFolderCommand?.NotifyCanExecuteChanged();

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RepairOutputGridVisibility))]
        private bool _isRepairOutputToDirectory = false;

        partial void OnIsRepairOutputToDirectoryChanged(bool value)
        {
            if (value && string.IsNullOrWhiteSpace(RepairOutputDirectory) && !string.IsNullOrWhiteSpace(RepairInputDirectory) && Directory.Exists(RepairInputDirectory))
            {
                RepairOutputDirectory = Path.Combine(RepairInputDirectory, "Output_RepairedPhotos");
            }
        }

        public Microsoft.UI.Xaml.Visibility RepairOutputGridVisibility =>
            IsRepairOutputToDirectory ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

        [ObservableProperty] private int _repairTotalPhotosCount = 0;
        [ObservableProperty] private int _repairThumbCorrectCount = 0;
        [ObservableProperty] private int _repairThumbErrorCount = 0;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RepairSecondaryBtnText))]
        [NotifyPropertyChangedFor(nameof(RepairActionBtnText))]
        [NotifyPropertyChangedFor(nameof(IsRepairNotProcessing))]
        private bool _isRepairProcessing = false;

        public bool IsRepairNotProcessing => !IsRepairProcessing;

        [ObservableProperty] private string _repairProgressText = "0/0";
        [ObservableProperty] private double _repairProgress = 0;
        private string _repairActionBtnText = string.Empty;
        public string RepairActionBtnText
        {
            get => string.IsNullOrWhiteSpace(_repairActionBtnText)
                ? ResourceService.GetString("RepairPage_StartButton.Content")
                : _repairActionBtnText;
            set => SetProperty(ref _repairActionBtnText, value);
        }
        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(RepairSecondaryBtnText))]
        private bool _isRepairPaused = false;

        public BulkObservableCollection<LivePhotoRepairTask> RepairTasks { get; } = [];

        private CancellationTokenSource? _repairCancellationTokenSource;
        private readonly ManualResetEventSlim _repairPauseEvent = new(true);

        public string RepairSecondaryBtnText
        {
            get
            {
                if (!IsRepairProcessing) return ResourceService.GetString("Btn_ClearList");
                return IsRepairPaused ? ResourceService.GetString("Btn_Resume") : ResourceService.GetString("Btn_Pause");
            }
        }

        [RelayCommand]
        private async Task PickRepairInputDirectoryAsync()
        {
            var folder = await Services.FilePickerService.PickFolderAsync();
            if (folder != null)
            {
                RepairInputDirectory = folder.Path;
            }
        }

        [RelayCommand]
        private async Task PickRepairOutputDirectoryAsync()
        {
            var folder = await Services.FilePickerService.PickFolderAsync();
            if (folder != null)
            {
                RepairOutputDirectory = folder.Path;
            }
        }

        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task ScanRepairDirectoryAsync()
        {
            if (IsRepairProcessing) return;

            if (IsRepairScanning)
            {
                _repairScanCancellationTokenSource?.Cancel();
                return;
            }

            if (string.IsNullOrWhiteSpace(RepairInputDirectory) || !Directory.Exists(RepairInputDirectory))
            {
                await ShowNoInputDirectoryDialogAsync("Repair");
                return;
            }

            IsRepairScanning = true;

            // 逻辑前移：提前填写输出目录
            if (IsRepairOutputToDirectory && string.IsNullOrWhiteSpace(RepairOutputDirectory))
            {
                RepairOutputDirectory = Path.Combine(RepairInputDirectory, "Output_RepairedPhotos");
            }

            _repairScanCancellationTokenSource = new CancellationTokenSource();
            var token = _repairScanCancellationTokenSource.Token;

            RepairTasks.ReplaceRange([]);
            RepairTotalPhotosCount = 0;
            RepairThumbCorrectCount = 0;
            RepairThumbErrorCount = 0;
            RepairProgress = 0;
            RepairProgressText = "0/0";

            try
            {
                var files = await Task.Run(() =>
                    Directory.GetFiles(RepairInputDirectory, "*.*", SearchOption.TopDirectoryOnly)
                             .Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase) ||
                                         f.EndsWith(".heic", StringComparison.OrdinalIgnoreCase))
                             .ToList(), token);

                RepairTotalPhotosCount = files.Count;

                await Task.Run(async () =>
                {
                    int index = 1;
                    foreach (var file in files)
                    {
                        if (token.IsCancellationRequested)
                            token.ThrowIfCancellationRequested();

                        var analysis = await LivePhotoRepairService.AnalyzeFileAsync(file);

                        var task = new LivePhotoRepairTask
                        {
                            Index = index++,
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
                            RepairTasks.Add(task);
                            if (analysis.NeedsRepair) RepairThumbErrorCount++;
                            else RepairThumbCorrectCount++;
                        });
                    }
                }, token);

                SetRepairStatus("Status_ScanDone", RepairTotalPhotosCount);
            }
            catch (OperationCanceledException)
            {
                SetRepairStatus("Status_Aborted");
            }
            catch (Exception ex)
            {
                CrashLogService.RecordBreadcrumb($"ScanRepairDirectory error: {ex.Message}");
            }
            finally
            {
                IsRepairScanning = false;
                _repairScanCancellationTokenSource?.Dispose();
                _repairScanCancellationTokenSource = null;
            }
        }

        [RelayCommand]
        private void ToggleRepairSecondaryAction()
        {
            if (!IsRepairProcessing)
            {
                RepairTasks.ReplaceRange([]);
                RepairTotalPhotosCount = 0;
                RepairThumbCorrectCount = 0;
                RepairThumbErrorCount = 0;
                RepairProgress = 0;
                RepairProgressText = "0/0";
                IsRepairDirectoryPanelOpen = true;
                SetRepairStatus("Status_Cleared");
            }
            else
            {
                if (IsRepairPaused)
                {
                    IsRepairPaused = false;
                    SetRepairStatus("Status_Resumed");
                    _repairPauseEvent.Set();
                }
                else
                {
                    IsRepairPaused = true;
                    SetRepairStatus("Status_Paused");
                    _repairPauseEvent.Reset();
                }
            }
        }

        [RelayCommand(AllowConcurrentExecutions = true)]
        private async Task ToggleRepairProcessAsync()
        {
            if (IsRepairProcessing)
            {
                _repairCancellationTokenSource?.Cancel();
                _repairPauseEvent.Set();
                RepairActionBtnText = ResourceService.GetString("Btn_Stopping");
                return;
            }

            if (RepairTasks.Count == 0)
            {
                await ShowEmptyQueueDialogAsync("Repair");
                return;
            }

            if (IsRepairOutputToDirectory)
            {
                if (string.IsNullOrWhiteSpace(RepairOutputDirectory))
                {
                    RepairOutputDirectory = Path.Combine(RepairInputDirectory, "Output_RepairedPhotos");
                }
                if (!Directory.Exists(RepairOutputDirectory))
                {
                    Directory.CreateDirectory(RepairOutputDirectory);
                }
            }

            IsRepairDirectoryPanelOpen = false;
            await RunRepairTasksAsync();
        }

        private async Task RunRepairTasksAsync()
        {
            IsRepairProcessing = true;
            RepairActionBtnText = ResourceService.GetString("Btn_StopRun");
            SetRepairStatus("Status_Running");

            _repairCancellationTokenSource?.Dispose();
            _repairCancellationTokenSource = new CancellationTokenSource();

            int completedOrSkipped = 0;
            Stopwatch stopwatch = Stopwatch.StartNew();

            try
            {
                await Task.Run(async () =>
                {
                    foreach (var task in RepairTasks)
                    {
                        _repairPauseEvent.Wait(_repairCancellationTokenSource!.Token);
                        _repairCancellationTokenSource.Token.ThrowIfCancellationRequested();

                        if (!task.NeedsRepair || task.Status == ProcessStatus.Success)
                        {
                            completedOrSkipped++;
                            continue;
                        }

                        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                        {
                            task.Status = ProcessStatus.Processing;
                            task.Details = "修复中...";
                        });

                        string targetPath = IsRepairOutputToDirectory
                            ? Path.Combine(RepairOutputDirectory, task.FileName)
                            : task.FilePath;

                        try
                        {
                            var result = await LivePhotoRepairService.RepairAsync(task.FilePath, targetPath, task.AnalysisResult!, _repairCancellationTokenSource.Token);
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
                                task.Details = ResourceService.GetString("Status_Aborted") ?? "已停止";
                            });
                            throw;
                        }

                        completedOrSkipped++;
                        App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                        {
                            RepairProgress = (completedOrSkipped * 100.0) / RepairTotalPhotosCount;
                            RepairProgressText = $"{completedOrSkipped}/{RepairTotalPhotosCount}";
                        });
                    }
                });
            }
            catch (OperationCanceledException)
            {
                SetRepairStatus("Status_Aborted");
            }
            catch (Exception ex)
            {
                CrashLogService.RecordBreadcrumb($"RunRepairTasksAsync error: {ex.Message}");
            }
            finally
            {
                stopwatch.Stop();
                IsRepairProcessing = false;
                RepairActionBtnText = ResourceService.GetString("Btn_StartRepair");
                IsRepairPaused = false;

                if (RepairProgress >= 100)
                {
                    SetRepairStatus("Status_Done", stopwatch.Elapsed.TotalSeconds);
                }
            }
        }
    }
}