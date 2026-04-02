using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
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
        public static AppViewModel Instance { get; } = new AppViewModel();

        [ObservableProperty] private string _appStatus = string.Empty;
        [ObservableProperty] private double _comboProgress = 0;
        [ObservableProperty] private string _progressText = "0/0";

        [ObservableProperty] private string _inputDirectory = string.Empty;
        [ObservableProperty] private string _outputDirectory = string.Empty;

        [ObservableProperty] private int _totalPairsCount = 0;
        [ObservableProperty] private int _standaloneImagesCount = 0;
        [ObservableProperty] private int _standaloneVideosCount = 0;

        [ObservableProperty] private string _actionBtnText = string.Empty;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotProcessing))]
        [NotifyPropertyChangedFor(nameof(SecondaryBtnText))]
        private bool _isProcessing = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SecondaryBtnText))]
        private bool _isPaused = false;

        public bool IsNotProcessing => !IsProcessing;

        public string SecondaryBtnText
        {
            get
            {
                if (!IsProcessing) return ResourceService.GetString("Btn_ClearList");
                return IsPaused ? ResourceService.GetString("Btn_Resume") : ResourceService.GetString("Btn_Pause");
            }
        }

        [ObservableProperty] private bool _isDirectoryPanelOpen = true;

        private CancellationTokenSource? _cancellationTokenSource;
        private readonly ManualResetEventSlim _pauseEvent = new(true);

        private string _hwEncoderName = "Software CPU";

        private string _lastSortColumn = "Name";
        private bool _sortAscending = true;
        [ObservableProperty] private string _nameSortIcon = "";
        [ObservableProperty] private string _sizeSortIcon = "";
        [ObservableProperty] private string _statusSortIcon = "";

        [ObservableProperty] private int _selectedModeIndex = 1;

        [ObservableProperty] private int _languageIndex;
        [ObservableProperty] private int _elementTheme;
        [ObservableProperty] private int _backdropIndex;

        public ObservableCollection<LivePhotoMergeTask> ComboTasks { get; } = [];

        private bool _isInitialized;

        public AppViewModel()
        {
            AppStatus = ResourceService.GetString("Status_Init");
            ActionBtnText = ResourceService.GetString("Btn_StartCombo");

            LoadSettings();
            LanguageService.ApplyLanguageOverride(LanguageIndex);

            _isInitialized = true;
            PropertyChanged += OnPropertyChangedSave;
            _ = DetectGPUAndInitializeAsync();
        }

        // ==========================================
        // 核心修复：纯粹读取你已有的 resw 并执行真正的重启
        // ==========================================
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
            AppStatus = ResourceService.GetString("Status_Ready");
            _hwEncoderName = "Software CPU";
            return Task.CompletedTask;
        }

        private void LoadSettings()
        {
            SelectedModeIndex = AppSettingsService.GetValue(nameof(SelectedModeIndex), 1);
            LanguageIndex = AppSettingsService.GetValue(nameof(LanguageIndex), 0);
            ElementTheme = AppSettingsService.GetValue(nameof(ElementTheme), 0);
            BackdropIndex = AppSettingsService.GetValue(nameof(BackdropIndex), 0);
        }

        private void OnPropertyChangedSave(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is null) return;

            switch (e.PropertyName)
            {
                case nameof(SelectedModeIndex):
                    AppSettingsService.SetValue(nameof(SelectedModeIndex), SelectedModeIndex);
                    break;
                case nameof(LanguageIndex):
                    AppSettingsService.SetValue(nameof(LanguageIndex), LanguageIndex);
                    break;
                case nameof(ElementTheme):
                    AppSettingsService.SetValue(nameof(ElementTheme), ElementTheme);
                    break;
                case nameof(BackdropIndex):
                    AppSettingsService.SetValue(nameof(BackdropIndex), BackdropIndex);
                    break;
            }
        }

        [RelayCommand]
        private void RestoreDefaultSettings()
        {
            LanguageIndex = 0;
            BackdropIndex = 0;
            ElementTheme = 0;
            SelectedModeIndex = 1;
        }

        private string FormatFileSize(long bytes)
        {
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }

        [RelayCommand]
        public void ScanDirectory()
        {
            if (IsProcessing) return;
            if (string.IsNullOrWhiteSpace(InputDirectory) || !Directory.Exists(InputDirectory))
            {
                AppStatus = ResourceService.GetString("Status_InvalidInput");
                return;
            }

            ComboTasks.Clear();
            var scanResult = LivePhotoScanService.Scan(InputDirectory);

            int currentIndex = 1;
            foreach (var pair in scanResult.Pairs)
            {
                ComboTasks.Add(new LivePhotoMergeTask
                {
                    Index = currentIndex++,
                    ImageFileName = Path.GetFileName(pair.ImagePath),
                    VideoFileName = Path.GetFileName(pair.VideoPath),
                    ImageSize = FormatFileSize(pair.ImageSizeBytes),
                    VideoSize = FormatFileSize(pair.VideoSizeBytes),
                    TotalSizeBytes = pair.ImageSizeBytes + pair.VideoSizeBytes,
                    BaseName = pair.BaseName,
                    ImagePath = pair.ImagePath,
                    VideoPath = pair.VideoPath,
                    Status = ProcessStatus.Pending,
                    Details = ResourceService.GetString("Task_Pending")
                });
            }

            TotalPairsCount = ComboTasks.Count;
            StandaloneImagesCount = scanResult.StandaloneImagesCount;
            StandaloneVideosCount = scanResult.StandaloneVideosCount;

            ComboProgress = 0;
            ProgressText = $"0/{TotalPairsCount}";

            _lastSortColumn = "Name";
            _sortAscending = true;
            NameSortIcon = string.Empty;
            SizeSortIcon = string.Empty;
            StatusSortIcon = string.Empty;

            if (string.IsNullOrWhiteSpace(OutputDirectory) && ComboTasks.Count > 0)
            {
                OutputDirectory = Path.Combine(InputDirectory, "Output_LivePhotos");
            }

            AppStatus = ResourceService.Format("Status_ScanDone", TotalPairsCount);
        }

        [RelayCommand]
        private void ToggleSecondaryAction()
        {
            if (!IsProcessing)
            {
                ComboTasks.Clear();
                TotalPairsCount = 0;
                StandaloneImagesCount = 0;
                StandaloneVideosCount = 0;
                ComboProgress = 0;
                ProgressText = "0/0";
                AppStatus = ResourceService.Format("Status_Cleared", _hwEncoderName);

                IsDirectoryPanelOpen = true;
            }
            else
            {
                if (IsPaused)
                {
                    IsPaused = false;
                    AppStatus = ResourceService.GetString("Status_Resumed");
                    _pauseEvent.Set();
                }
                else
                {
                    IsPaused = true;
                    AppStatus = ResourceService.GetString("Status_Paused");
                    _pauseEvent.Reset();
                }
            }
        }

        [RelayCommand]
        private void Sort(string columnName)
        {
            if (IsProcessing || ComboTasks.Count == 0) return;

            if (_lastSortColumn == columnName)
                _sortAscending = !_sortAscending;
            else
            {
                _sortAscending = true;
                _lastSortColumn = columnName;
            }

            NameSortIcon = ""; SizeSortIcon = ""; StatusSortIcon = "";
            string iconStr = _sortAscending ? "▲" : "▼";

            switch (columnName)
            {
                case "Name": NameSortIcon = iconStr; break;
                case "Size": SizeSortIcon = iconStr; break;
                case "Status": StatusSortIcon = iconStr; break;
            }

            IEnumerable<LivePhotoMergeTask> sorted = columnName switch
            {
                "Name" => _sortAscending ? ComboTasks.OrderBy(x => x.BaseName) : ComboTasks.OrderByDescending(x => x.BaseName),
                "Size" => _sortAscending ? ComboTasks.OrderBy(x => x.TotalSizeBytes) : ComboTasks.OrderByDescending(x => x.TotalSizeBytes),
                "Status" => _sortAscending ? ComboTasks.OrderBy(x => (int)x.Status) : ComboTasks.OrderByDescending(x => (int)x.Status),
                _ => ComboTasks
            };

            var sortedList = sorted.ToList();
            ComboTasks.Clear();
            foreach (var item in sortedList) ComboTasks.Add(item);
        }

        [RelayCommand]
        private async Task ToggleProcessAsync()
        {
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
                AppStatus = ResourceService.GetString("Status_WarnOutput");
                return;
            }

            IsDirectoryPanelOpen = false;
            await RunComboTasksAsync();
        }

        private void InitializeRunState()
        {
            IsProcessing = true;
            IsPaused = false;
            _pauseEvent.Set();
            ActionBtnText = ResourceService.GetString("Btn_StopRun");
            ComboProgress = 0;
            ProgressText = $"0/{TotalPairsCount}";
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();
            AppStatus = ResourceService.GetString("Status_Running");
        }

        private void FinalizeRunState(Stopwatch stopwatch)
        {
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
                AppStatus = ResourceService.Format("Status_Done", stopwatch.Elapsed.TotalSeconds);
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
                AppStatus = ResourceService.GetString("Status_Aborted");
            }
            finally
            {
                FinalizeRunState(stopwatch);
            }
        }
    }
}