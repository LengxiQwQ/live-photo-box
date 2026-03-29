using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoStudio.Models;
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
using Windows.Storage;

namespace LivePhotoStudio.ViewModels
{
    public partial class SharedViewModel : ObservableObject
    {
        public static SharedViewModel Instance { get; } = new SharedViewModel();

        // [核心新增]：全局共享的资源加载器，用于在 C# 逻辑中读取多语言文本
        private static Microsoft.Windows.ApplicationModel.Resources.ResourceLoader? _resLoader;
        public static Microsoft.Windows.ApplicationModel.Resources.ResourceLoader ResLoader =>
            _resLoader ??= new Microsoft.Windows.ApplicationModel.Resources.ResourceLoader();

        private bool _isInitialized = false;

        [ObservableProperty] private string _appStatus = "";
        [ObservableProperty] private double _comboProgress = 0;
        [ObservableProperty] private string _progressText = "0/0";

        [ObservableProperty] private string _inputDirectory = string.Empty;
        [ObservableProperty] private string _outputDirectory = string.Empty;

        [ObservableProperty] private int _totalPairsCount = 0;
        [ObservableProperty] private int _standaloneImagesCount = 0;
        [ObservableProperty] private int _standaloneVideosCount = 0;

        [ObservableProperty] private string _actionBtnText = "";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotProcessing))]
        [NotifyPropertyChangedFor(nameof(SecondaryBtnText))]
        private bool _isProcessing = false;

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(SecondaryBtnText))]
        private bool _isPaused = false;

        public bool IsNotProcessing => !IsProcessing;

        // 动态读取多语言的次要按钮文本 (清空列表 / 暂停 / 继续)
        public string SecondaryBtnText
        {
            get
            {
                if (!IsProcessing) return ResLoader.GetString("Btn_ClearList");
                return IsPaused ? ResLoader.GetString("Btn_Resume") : ResLoader.GetString("Btn_Pause");
            }
        }

        [ObservableProperty] private bool _isMultiThreadEnabled = true;

        [ObservableProperty] private bool _isDirectoryPanelOpen = true;

        private CancellationTokenSource? _cancellationTokenSource;
        private ManualResetEventSlim _pauseEvent = new ManualResetEventSlim(true);

        private string _hwEncoder = "libx265";
        private string _hwEncoderName = "Software CPU";

        private string _lastSortColumn = "Name";
        private bool _sortAscending = true;
        [ObservableProperty] private string _nameSortIcon = "";
        [ObservableProperty] private string _sizeSortIcon = "";
        [ObservableProperty] private string _statusSortIcon = "";

        [ObservableProperty] private int _selectedModeIndex = 1;
        public int[] ThreadOptions { get; } = new[] { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10 };
        [ObservableProperty] private int _threadCount = 4;
        [ObservableProperty] private bool _keepOriginal = true;
        [ObservableProperty] private int _splitVideoFormat = 1;

        [ObservableProperty] private int _languageIndex = 0;
        [ObservableProperty] private int _elementTheme = 0;
        [ObservableProperty] private int _backdropIndex = 0;

        public ObservableCollection<LivePhotoTask> ComboTasks { get; } = new();

        public SharedViewModel()
        {
            // 初始化时赋予多语言的默认文本
            AppStatus = ResLoader.GetString("Status_Init");
            ActionBtnText = ResLoader.GetString("Btn_StartCombo");

            LoadSettings();
            _isInitialized = true;
            PropertyChanged += OnPropertyChangedSave;
            _ = DetectGPUAndInitializeAsync();
        }

        partial void OnLanguageIndexChanged(int value)
        {
            if (!_isInitialized) return;

            string langCode = value switch
            {
                1 => "zh-CN",
                2 => "en-US",
                _ => ""
            };

            Windows.Globalization.ApplicationLanguages.PrimaryLanguageOverride = langCode;

            if (App.MainWindow?.Content?.XamlRoot != null)
            {
                App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
                {
                    var dialog = new ContentDialog
                    {
                        Title = ResLoader.GetString("RestartDialog_Title"),
                        Content = ResLoader.GetString("RestartDialog_Content"),
                        PrimaryButtonText = ResLoader.GetString("RestartDialog_PrimaryButton"),
                        CloseButtonText = ResLoader.GetString("RestartDialog_CloseButton"),
                        XamlRoot = App.MainWindow.Content.XamlRoot,
                        DefaultButton = ContentDialogButton.Primary
                    };

                    var result = await dialog.ShowAsync();
                    if (result == ContentDialogResult.Primary)
                    {
                        Microsoft.Windows.AppLifecycle.AppInstance.Restart("");
                    }
                });
            }
        }

        private async Task DetectGPUAndInitializeAsync()
        {
            string toolsDir = Path.Combine(AppContext.BaseDirectory, "Tools");
            string ffmpegPath = Path.Combine(toolsDir, "ffmpeg.exe");

            if (!File.Exists(ffmpegPath))
            {
                AppStatus = ResLoader.GetString("Status_NoFFmpeg");
                return;
            }

            try
            {
                string output = await RunProcessAndGetOutputAsync(ffmpegPath, "-encoders -v quiet");
                if (output.Contains("hevc_nvenc")) { _hwEncoder = "hevc_nvenc"; _hwEncoderName = "NVIDIA GPU (NVENC)"; }
                else if (output.Contains("hevc_qsv")) { _hwEncoder = "hevc_qsv"; _hwEncoderName = "Intel GPU (QuickSync)"; }
                else if (output.Contains("hevc_amf")) { _hwEncoder = "hevc_amf"; _hwEncoderName = "AMD GPU (AMF)"; }
                else { _hwEncoder = "libx265"; _hwEncoderName = "Software CPU"; }

                AppStatus = string.Format(ResLoader.GetString("Status_GPUFound"), _hwEncoderName);
            }
            catch { AppStatus = ResLoader.GetString("Status_GPUFail"); }
        }

        private void LoadSettings()
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            if (settings.TryGetValue(nameof(LanguageIndex), out var lang)) _languageIndex = (int)lang;
            if (settings.TryGetValue(nameof(SelectedModeIndex), out var mode)) SelectedModeIndex = (int)mode;
            if (settings.TryGetValue(nameof(ThreadCount), out var thread)) ThreadCount = (int)thread;
            if (settings.TryGetValue(nameof(KeepOriginal), out var keep)) KeepOriginal = (bool)keep;
            if (settings.TryGetValue(nameof(SplitVideoFormat), out var split)) SplitVideoFormat = (int)split;
            if (settings.TryGetValue(nameof(ElementTheme), out var theme)) ElementTheme = (int)theme;
            if (settings.TryGetValue(nameof(BackdropIndex), out var backdrop)) BackdropIndex = (int)backdrop;
            if (settings.TryGetValue(nameof(IsMultiThreadEnabled), out var isMulti)) IsMultiThreadEnabled = (bool)isMulti;
        }

        private void OnPropertyChangedSave(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppStatus) || e.PropertyName == nameof(ComboProgress) ||
                e.PropertyName == nameof(ProgressText) || e.PropertyName == nameof(InputDirectory) ||
                e.PropertyName == nameof(OutputDirectory) || e.PropertyName == nameof(TotalPairsCount) ||
                e.PropertyName == nameof(StandaloneImagesCount) || e.PropertyName == nameof(StandaloneVideosCount) ||
                e.PropertyName == nameof(ActionBtnText) || e.PropertyName == nameof(IsProcessing) ||
                e.PropertyName == nameof(IsNotProcessing) || e.PropertyName == nameof(SecondaryBtnText) ||
                e.PropertyName == nameof(IsPaused) || e.PropertyName == nameof(NameSortIcon) ||
                e.PropertyName == nameof(SizeSortIcon) || e.PropertyName == nameof(StatusSortIcon) ||
                e.PropertyName == nameof(IsDirectoryPanelOpen)) return;

            var propertyInfo = GetType().GetProperty(e.PropertyName!);
            if (propertyInfo != null)
            {
                ApplicationData.Current.LocalSettings.Values[e.PropertyName!] = propertyInfo.GetValue(this);
            }
        }

        [RelayCommand]
        private void RestoreDefaultSettings()
        {
            LanguageIndex = 0;
            BackdropIndex = 0;
            ElementTheme = 0;
            SelectedModeIndex = 1;
            ThreadCount = 4;
            KeepOriginal = true;
            SplitVideoFormat = 1;
            IsMultiThreadEnabled = true;
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
                AppStatus = ResLoader.GetString("Status_InvalidInput");
                return;
            }

            ComboTasks.Clear();
            var allFiles = Directory.GetFiles(InputDirectory);

            var images = allFiles.Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)).ToList();
            var videos = allFiles.Where(f => f.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) || f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)).ToList();

            var imgDict = images.ToDictionary(Path.GetFileNameWithoutExtension, f => f, StringComparer.OrdinalIgnoreCase);
            var vidDict = videos.ToDictionary(Path.GetFileNameWithoutExtension, f => f, StringComparer.OrdinalIgnoreCase);

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
                        Details = ResLoader.GetString("Task_Pending")
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

            AppStatus = string.Format(ResLoader.GetString("Status_ScanDone"), TotalPairsCount);
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
                AppStatus = string.Format(ResLoader.GetString("Status_Cleared"), _hwEncoderName);

                IsDirectoryPanelOpen = true;
            }
            else
            {
                if (IsPaused)
                {
                    IsPaused = false;
                    AppStatus = ResLoader.GetString("Status_Resumed");
                    _pauseEvent.Set();
                }
                else
                {
                    IsPaused = true;
                    AppStatus = ResLoader.GetString("Status_Paused");
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

            IEnumerable<LivePhotoTask> sorted;
            switch (columnName)
            {
                case "Name":
                    sorted = _sortAscending ? ComboTasks.OrderBy(x => x.BaseName) : ComboTasks.OrderByDescending(x => x.BaseName);
                    break;
                case "Size":
                    sorted = _sortAscending ? ComboTasks.OrderBy(x => x.TotalSizeBytes) : ComboTasks.OrderByDescending(x => x.TotalSizeBytes);
                    break;
                case "Status":
                    sorted = _sortAscending ? ComboTasks.OrderBy(x => (int)x.Status) : ComboTasks.OrderByDescending(x => (int)x.Status);
                    break;
                default: return;
            }

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
                ActionBtnText = ResLoader.GetString("Btn_Stopping");
                return;
            }

            if (ComboTasks.Count == 0)
            {
                if (App.MainWindow?.Content?.XamlRoot != null)
                {
                    var dialog = new ContentDialog
                    {
                        Title = ResLoader.GetString("Msg_EmptyQueueTitle"),
                        Content = ResLoader.GetString("Msg_EmptyQueue"),
                        CloseButtonText = ResLoader.GetString("Msg_GotIt"),
                        XamlRoot = App.MainWindow.Content.XamlRoot
                    };
                    await dialog.ShowAsync();
                }
                return;
            }

            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                AppStatus = ResLoader.GetString("Status_WarnOutput");
                return;
            }

            IsDirectoryPanelOpen = false;

            _ = RunComboTasksAsync();
        }

        private async Task RunComboTasksAsync()
        {
            IsProcessing = true;
            IsPaused = false;
            _pauseEvent.Set();
            ActionBtnText = ResLoader.GetString("Btn_StopRun");
            ComboProgress = 0;
            ProgressText = $"0/{TotalPairsCount}";
            _cancellationTokenSource = new CancellationTokenSource();

            if (!Directory.Exists(OutputDirectory)) Directory.CreateDirectory(OutputDirectory);

            int completed = 0;
            AppStatus = ResLoader.GetString("Status_Running");
            Stopwatch sw = Stopwatch.StartNew();

            try
            {
                int actualThreadCount = IsMultiThreadEnabled ? ThreadCount : 1;
                var options = new ParallelOptions
                {
                    MaxDegreeOfParallelism = actualThreadCount,
                    CancellationToken = _cancellationTokenSource.Token
                };

                await Parallel.ForEachAsync(ComboTasks, options, async (task, token) =>
                {
                    if (task.Status == ProcessStatus.Success) return;

                    _pauseEvent.Wait(token);

                    App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                    {
                        task.Status = ProcessStatus.Processing;
                        task.Details = ResLoader.GetString("Task_Processing");
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
                AppStatus = ResLoader.GetString("Status_Aborted");
            }
            finally
            {
                sw.Stop();
                IsProcessing = false;
                IsPaused = false;
                _pauseEvent.Set();
                ActionBtnText = ResLoader.GetString("Btn_StartCombo");
                if (ComboProgress >= 100) AppStatus = string.Format(ResLoader.GetString("Status_Done"), sw.Elapsed.TotalSeconds);
            }
        }

        private async Task<(bool IsSuccess, string Details)> ProcessSinglePairAsync(string imagePath, string videoPath, string baseName, CancellationToken token)
        {
            try
            {
                string outputName = SelectedModeIndex == 0 ? $"MVIMG_{baseName}.jpg" : $"{baseName}.MP.jpg";
                string finalOutputPath = Path.Combine(OutputDirectory, outputName);
                string toolsDir = Path.Combine(AppContext.BaseDirectory, "Tools");
                string exiftoolPath = Path.Combine(toolsDir, "exiftool.exe");

                if (!File.Exists(exiftoolPath)) return (false, ResLoader.GetString("Task_MissingExif"));

                File.Copy(imagePath, finalOutputPath, true);
                long videoSize = new FileInfo(videoPath).Length;

                if (SelectedModeIndex == 0)
                {
                    var args = $"-XMP-GCamera:MicroVideo=1 -XMP-GCamera:MicroVideoVersion=1 -XMP-GCamera:MicroVideoOffset={videoSize} -XMP-GCamera:MicroVideoPresentationTimestampUs=0 \"{finalOutputPath}\" -overwrite_original";
                    await RunProcessAsync(exiftoolPath, args, token);
                }
                else
                {
                    string xmpContent = $@"<?xpacket begin="""" id=""W5M0MpCehiHzreSzNTczkc9d""?>
<x:xmpmeta xmlns:x=""adobe:ns:meta/""><rdf:RDF xmlns:rdf=""http://www.w3.org/1999/02/22-rdf-syntax-ns#"">
<rdf:Description rdf:about="""" xmlns:GCamera=""http://ns.google.com/photos/1.0/camera/"" xmlns:Container=""http://ns.google.com/photos/1.0/container/"" xmlns:Item=""http://ns.google.com/photos/1.0/container/item/""
GCamera:MotionPhoto=""1"" GCamera:MotionPhotoVersion=""1"" GCamera:MotionPhotoPresentationTimestampUs=""0"">
<Container:Directory><rdf:Seq><rdf:li rdf:parseType=""Resource""><Container:Item Item:Mime=""image/jpeg"" Item:Semantic=""Primary"" Item:Length=""0"" Item:Padding=""0""/></rdf:li>
<rdf:li rdf:parseType=""Resource""><Container:Item Item:Mime=""video/mp4"" Item:Semantic=""MotionPhoto"" Item:Length=""{videoSize}"" Item:Padding=""0""/></rdf:li>
</rdf:Seq></Container:Directory></rdf:Description></rdf:RDF></x:xmpmeta><?xpacket end=""w""?>";
                    string tempXmp = Path.Combine(OutputDirectory, $"temp_{Guid.NewGuid()}.xmp");
                    File.WriteAllText(tempXmp, xmpContent);
                    var args = $"-xmp<=\"{tempXmp}\" \"{finalOutputPath}\" -overwrite_original";
                    await RunProcessAsync(exiftoolPath, args, token);
                    if (File.Exists(tempXmp)) File.Delete(tempXmp);
                }

                using (var fsOutput = new FileStream(finalOutputPath, FileMode.Append, System.IO.FileAccess.Write))
                using (var fsVideo = new FileStream(videoPath, FileMode.Open, System.IO.FileAccess.Read))
                {
                    await fsVideo.CopyToAsync(fsOutput, token);
                }

                string resultStatus = ResLoader.GetString("Task_Success");
                if (!KeepOriginal)
                {
                    try
                    {
                        var imgFile = await StorageFile.GetFileFromPathAsync(imagePath);
                        await imgFile.DeleteAsync(StorageDeleteOption.Default);
                        var vidFile = await StorageFile.GetFileFromPathAsync(videoPath);
                        await vidFile.DeleteAsync(StorageDeleteOption.Default);
                        resultStatus += ResLoader.GetString("Task_Recycled");
                    }
                    catch (Exception ex) { resultStatus += string.Format(ResLoader.GetString("Task_CleanFail"), ex.Message); }
                }

                return (true, resultStatus);
            }
            catch (Exception ex) { return (false, string.Format(ResLoader.GetString("Task_Error"), ex.Message)); }
        }

        private async Task<string> RunProcessAndGetOutputAsync(string filePath, string args, CancellationToken token = default)
        {
            try
            {
                using var process = new Process { StartInfo = new ProcessStartInfo { FileName = filePath, Arguments = args, UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true } };
                process.Start();
                string output = await process.StandardOutput.ReadToEndAsync(token);
                await process.WaitForExitAsync(token);
                return output;
            }
            catch { return string.Empty; }
        }

        private Task<int> RunProcessAsync(string filePath, string args, CancellationToken token)
        {
            var tcs = new TaskCompletionSource<int>();
            var process = new Process { StartInfo = new ProcessStartInfo { FileName = filePath, Arguments = args, UseShellExecute = false, CreateNoWindow = true }, EnableRaisingEvents = true };

            process.Exited += (sender, e) =>
            {
                tcs.TrySetResult(process.ExitCode);
            };

            try
            {
                process.Start();
            }
            catch (Exception ex)
            {
                tcs.TrySetException(ex);
                return tcs.Task;
            }

            var registration = token.Register(() =>
            {
                try
                {
                    if (!process.HasExited)
                        process.Kill();
                }
                catch { }
                tcs.TrySetCanceled();
            });

            tcs.Task.ContinueWith(_ => registration.Dispose());

            return tcs.Task;
        }
    }
}