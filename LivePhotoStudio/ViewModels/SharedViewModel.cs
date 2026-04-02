using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoStudio.Models;
using LivePhotoStudio.Services;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

// 测试注释功能
namespace LivePhotoStudio.ViewModels
{
    public partial class SharedViewModel : ObservableObject
    {
        public static SharedViewModel Instance { get; } = new SharedViewModel();

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

        [ObservableProperty] private bool _keepOriginal = true;
        [ObservableProperty] private int _splitVideoFormat = 1;

        // 语言：0=跟随系统, 1=中文, 2=English
        [ObservableProperty] private int _languageIndex = 0;
        [ObservableProperty] private int _elementTheme = 0;
        [ObservableProperty] private int _backdropIndex = 0;

        public ObservableCollection<LivePhotoTask> ComboTasks { get; } = [];

        private bool _isInitialized = false;

        public SharedViewModel()
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
            KeepOriginal = AppSettingsService.GetValue(nameof(KeepOriginal), true);
            SplitVideoFormat = AppSettingsService.GetValue(nameof(SplitVideoFormat), 1);
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
                case nameof(KeepOriginal):
                    AppSettingsService.SetValue(nameof(KeepOriginal), KeepOriginal);
                    break;
                case nameof(SplitVideoFormat):
                    AppSettingsService.SetValue(nameof(SplitVideoFormat), SplitVideoFormat);
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
            KeepOriginal = true;
            SplitVideoFormat = 1;
        }

        private string FormatFileSize(long bytes)
        {
            if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
            return $"{bytes / (1024.0 * 1024.0):F2} MB";
        }

        private Dictionary<string, string> CreateFileMap(IEnumerable<string> files)
        {
            return files
                .GroupBy(Path.GetFileNameWithoutExtension, StringComparer.OrdinalIgnoreCase)
                .ToDictionary(
                    group => group.Key,
                    group => group.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).First(),
                    StringComparer.OrdinalIgnoreCase);
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
            var allFiles = Directory.GetFiles(InputDirectory);

            var images = allFiles.Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)).ToList();
            var videos = allFiles.Where(f => f.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)).ToList();

            var imgDict = CreateFileMap(images);
            var vidDict = CreateFileMap(videos);

            int standaloneImg = 0, standaloneVid = 0, currentIndex = 1;

            foreach (var kvp in imgDict)
            {
                if (vidDict.TryGetValue(kvp.Key, out var vidPath))
                {
                    long imgBytes = new FileInfo(kvp.Value).Length;
                    long vidBytes = new FileInfo(vidPath).Length;

                    ComboTasks.Add(new LivePhotoTask
                    {
                        Index = currentIndex++,
                        ImageFileName = Path.GetFileName(kvp.Value),
                        VideoFileName = Path.GetFileName(vidPath),
                        ImageSize = FormatFileSize(imgBytes),
                        VideoSize = FormatFileSize(vidBytes),
                        TotalSizeBytes = imgBytes + vidBytes,
                        BaseName = kvp.Key,
                        ImagePath = kvp.Value,
                        VideoPath = vidPath,
                        Status = ProcessStatus.Pending,
                        Details = ResourceService.GetString("Task_Pending")
                    });
                }
                else standaloneImg++;
            }

            foreach (var kvp in vidDict) if (!imgDict.ContainsKey(kvp.Key)) standaloneVid++;

            TotalPairsCount = ComboTasks.Count;
            StandaloneImagesCount = standaloneImg;
            StandaloneVideosCount = standaloneVid;

            ComboProgress = 0;
            ProgressText = $"0/{TotalPairsCount}";

            _lastSortColumn = "Name";
            _sortAscending = true;
            NameSortIcon = ""; SizeSortIcon = ""; StatusSortIcon = "";

            if (string.IsNullOrWhiteSpace(OutputDirectory) && ComboTasks.Count > 0)
                OutputDirectory = Path.Combine(InputDirectory, "Output_LivePhotos");

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

            IEnumerable<LivePhotoTask> sorted = columnName switch
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

        private async Task RunComboTasksAsync()
        {
            IsProcessing = true;
            IsPaused = false;
            _pauseEvent.Set();
            ActionBtnText = ResourceService.GetString("Btn_StopRun");
            ComboProgress = 0;
            ProgressText = $"0/{TotalPairsCount}";
            _cancellationTokenSource?.Dispose();
            _cancellationTokenSource = new CancellationTokenSource();

            if (!Directory.Exists(OutputDirectory)) Directory.CreateDirectory(OutputDirectory);

            int completed = 0;
            AppStatus = ResourceService.GetString("Status_Running");
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                int actualThreadCount = Math.Min(Environment.ProcessorCount, 20);
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = actualThreadCount,
                    CancellationToken = _cancellationTokenSource.Token
                };

                await Parallel.ForEachAsync(ComboTasks, options, async (task, token) =>
                {
                    if (task.Status == ProcessStatus.Success) return;

                    _pauseEvent.Wait(token);
                    token.ThrowIfCancellationRequested();

                    App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                    {
                        task.Status = ProcessStatus.Processing;
                        task.Details = ResourceService.GetString("Task_Processing");
                    });

                    var (isSuccess, detailMsg) = await ProcessSinglePairAsync(task.ImagePath, task.VideoPath, task.BaseName, token);

                    App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                    {
                        task.Status = isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
                        task.Details = detailMsg;

                        Interlocked.Increment(ref completed);
                        ComboProgress = (completed * 100.0) / TotalPairsCount;
                        ProgressText = $"{completed}/{TotalPairsCount}";
                    });
                });
            }
            catch (OperationCanceledException)
            {
                AppStatus = ResourceService.GetString("Status_Aborted");
            }
            finally
            {
                sw.Stop();
                IsProcessing = false;
                IsPaused = false;
                _pauseEvent.Set();
                ActionBtnText = ResourceService.GetString("Btn_StartCombo");

                var cts = _cancellationTokenSource;
                _cancellationTokenSource = null;
                cts?.Dispose();

                if (ComboProgress >= 100) AppStatus = ResourceService.Format("Status_Done", sw.Elapsed.TotalSeconds);
            }
        }

        private async Task<(bool IsSuccess, string Details)> ProcessSinglePairAsync(string imagePath, string videoPath, string baseName, CancellationToken token)
        {
            try
            {
                token.ThrowIfCancellationRequested();

                string outputName = LivePhotoCompositionService.CreateOutputFileName(baseName, SelectedModeIndex);
                string finalOutputPath = Path.Combine(OutputDirectory, outputName);

                await LivePhotoCompositionService.WriteLivePhotoAsync(imagePath, videoPath, finalOutputPath, SelectedModeIndex, token);

                string resultStatus = ResourceService.GetString("Task_Success");
                if (!KeepOriginal)
                {
                    try
                    {
                        var imgFile = await StorageFile.GetFileFromPathAsync(imagePath);
                        await imgFile.DeleteAsync(StorageDeleteOption.Default);
                        var vidFile = await StorageFile.GetFileFromPathAsync(videoPath);
                        await vidFile.DeleteAsync(StorageDeleteOption.Default);
                        resultStatus += ResourceService.GetString("Task_Recycled");
                    }
                    catch (Exception ex) { resultStatus += ResourceService.Format("Task_CleanFail", ex.Message); }
                }

                return (true, resultStatus);
            }
            catch (Exception ex) { return (false, ResourceService.Format("Task_Error", ex.Message)); }
        }
    }
}