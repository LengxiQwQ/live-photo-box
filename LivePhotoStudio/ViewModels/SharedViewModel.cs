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

        [ObservableProperty] private string _appStatus = "正在初始化组件";
        [ObservableProperty] private double _comboProgress = 0;
        [ObservableProperty] private string _progressText = "0/0";

        [ObservableProperty] private string _inputDirectory = string.Empty;
        [ObservableProperty] private string _outputDirectory = string.Empty;

        [ObservableProperty] private int _totalPairsCount = 0;
        [ObservableProperty] private int _standaloneImagesCount = 0;
        [ObservableProperty] private int _standaloneVideosCount = 0;

        [ObservableProperty] private string _actionBtnText = "开始合成";

        [ObservableProperty]
        [NotifyPropertyChangedFor(nameof(IsNotProcessing))]
        private bool _isProcessing = false;

        public bool IsNotProcessing => !IsProcessing;

        [ObservableProperty] private bool _isMultiThreadEnabled = true;

        private CancellationTokenSource? _cancellationTokenSource;

        private string _hwEncoder = "libx265";
        private string _hwEncoderName = "Software CPU";

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
            LoadSettings();
            PropertyChanged += OnPropertyChangedSave;

            _ = DetectGPUAndInitializeAsync();
        }

        private async Task DetectGPUAndInitializeAsync()
        {
            string toolsDir = Path.Combine(AppContext.BaseDirectory, "Tools");
            string ffmpegPath = Path.Combine(toolsDir, "ffmpeg.exe");

            if (!File.Exists(ffmpegPath))
            {
                AppStatus = "就绪 | 未检测到 FFmpeg 组件";
                return;
            }

            try
            {
                string output = await RunProcessAndGetOutputAsync(ffmpegPath, "-encoders -v quiet");
                if (output.Contains("hevc_nvenc")) { _hwEncoder = "hevc_nvenc"; _hwEncoderName = "NVIDIA GPU (NVENC)"; }
                else if (output.Contains("hevc_qsv")) { _hwEncoder = "hevc_qsv"; _hwEncoderName = "Intel GPU (QuickSync)"; }
                else if (output.Contains("hevc_amf")) { _hwEncoder = "hevc_amf"; _hwEncoderName = "AMD GPU (AMF)"; }
                else { _hwEncoder = "libx265"; _hwEncoderName = "Software CPU"; }

                AppStatus = $"就绪 | 已识别视频加速: {_hwEncoderName}";
            }
            catch
            {
                AppStatus = "就绪 | 显卡检测失败，默认使用 CPU 编码";
            }
        }

        private void LoadSettings()
        {
            var settings = ApplicationData.Current.LocalSettings.Values;
            if (settings.TryGetValue(nameof(SelectedModeIndex), out var mode)) SelectedModeIndex = (int)mode;
            if (settings.TryGetValue(nameof(ThreadCount), out var thread)) ThreadCount = (int)thread;
            if (settings.TryGetValue(nameof(KeepOriginal), out var keep)) KeepOriginal = (bool)keep;
            if (settings.TryGetValue(nameof(SplitVideoFormat), out var split)) SplitVideoFormat = (int)split;
            if (settings.TryGetValue(nameof(LanguageIndex), out var lang)) LanguageIndex = (int)lang;
            if (settings.TryGetValue(nameof(ElementTheme), out var theme)) ElementTheme = (int)theme;
            if (settings.TryGetValue(nameof(BackdropIndex), out var backdrop)) BackdropIndex = (int)backdrop;
            if (settings.TryGetValue(nameof(IsMultiThreadEnabled), out var isMulti)) IsMultiThreadEnabled = (bool)isMulti;
        }

        private void OnPropertyChangedSave(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppStatus) ||
                e.PropertyName == nameof(ComboProgress) ||
                e.PropertyName == nameof(ProgressText) ||
                e.PropertyName == nameof(InputDirectory) ||
                e.PropertyName == nameof(OutputDirectory) ||
                e.PropertyName == nameof(TotalPairsCount) ||
                e.PropertyName == nameof(StandaloneImagesCount) ||
                e.PropertyName == nameof(StandaloneVideosCount) ||
                e.PropertyName == nameof(ActionBtnText) ||
                e.PropertyName == nameof(IsProcessing) ||
                e.PropertyName == nameof(IsNotProcessing)) return;

            var settings = ApplicationData.Current.LocalSettings.Values;
            var propertyInfo = GetType().GetProperty(e.PropertyName!);
            if (propertyInfo != null)
            {
                settings[e.PropertyName!] = propertyInfo.GetValue(this);
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

        [RelayCommand]
        private void ClearList()
        {
            if (IsProcessing) return;

            ComboTasks.Clear();
            TotalPairsCount = 0;
            StandaloneImagesCount = 0;
            StandaloneVideosCount = 0;
            ComboProgress = 0;
            ProgressText = "0/0";

            AppStatus = $"已清空列表 | 就绪环境: {_hwEncoderName}";
        }

        [RelayCommand]
        public void ScanDirectory()
        {
            if (IsProcessing) return;
            if (string.IsNullOrWhiteSpace(InputDirectory) || !Directory.Exists(InputDirectory))
            {
                AppStatus = "输入目录无效，请检查路径";
                return;
            }

            ComboTasks.Clear();
            var allFiles = Directory.GetFiles(InputDirectory);

            var images = allFiles.Where(f => f.EndsWith(".jpg", StringComparison.OrdinalIgnoreCase) ||
                                             f.EndsWith(".jpeg", StringComparison.OrdinalIgnoreCase)).ToList();
            var videos = allFiles.Where(f => f.EndsWith(".mov", StringComparison.OrdinalIgnoreCase) ||
                                             f.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase)).ToList();

            var imgDict = images.ToDictionary(Path.GetFileNameWithoutExtension, f => f, StringComparer.OrdinalIgnoreCase);
            var vidDict = videos.ToDictionary(Path.GetFileNameWithoutExtension, f => f, StringComparer.OrdinalIgnoreCase);

            int standaloneImg = 0;
            int standaloneVid = 0;

            // 新增：序号计数器
            int currentIndex = 1;

            foreach (var kvp in imgDict)
            {
                if (vidDict.TryGetValue(kvp.Key, out var vidPath))
                {
                    // 新增：赋值序号、图片名和视频名
                    ComboTasks.Add(new LivePhotoTask
                    {
                        Index = currentIndex++,
                        ImageFileName = Path.GetFileName(kvp.Value),
                        VideoFileName = Path.GetFileName(vidPath),
                        BaseName = kvp.Key,
                        ImagePath = kvp.Value,
                        VideoPath = vidPath,
                        Status = ProcessStatus.Pending,
                        Details = "等待处理"
                    });
                }
                else
                {
                    standaloneImg++;
                }
            }

            foreach (var kvp in vidDict)
            {
                if (!imgDict.ContainsKey(kvp.Key)) standaloneVid++;
            }

            TotalPairsCount = ComboTasks.Count;
            StandaloneImagesCount = standaloneImg;
            StandaloneVideosCount = standaloneVid;

            ComboProgress = 0;
            ProgressText = $"0/{TotalPairsCount}";

            if (string.IsNullOrWhiteSpace(OutputDirectory) && ComboTasks.Count > 0)
            {
                OutputDirectory = Path.Combine(InputDirectory, "Output_LivePhotos");
            }

            AppStatus = $"扫描完成：已成功匹配 {TotalPairsCount} 组实况文件";
        }

        [RelayCommand]
        private async Task ToggleProcessAsync()
        {
            if (IsProcessing)
            {
                _cancellationTokenSource?.Cancel();
                ActionBtnText = "正在停止...";
                return;
            }

            // 新增逻辑：如果是空列表，弹出提示框阻止运行
            if (ComboTasks.Count == 0)
            {
                if (App.MainWindow?.Content?.XamlRoot != null)
                {
                    var dialog = new ContentDialog
                    {
                        Title = "操作提示",
                        Content = "队列为空！\n\n请先设置输入目录，并点击【扫描实况照片】以读取需要合成的文件",
                        CloseButtonText = "我知道了",
                        XamlRoot = App.MainWindow.Content.XamlRoot
                    };
                    await dialog.ShowAsync();
                }
                AppStatus = "警告：队列为空，请先扫描实况照片";
                return;
            }

            if (string.IsNullOrWhiteSpace(OutputDirectory))
            {
                AppStatus = "警告：请先设置输出保存目录";
                return;
            }

            // 将发后即忘后台任务包装调用，以解绑 UI
            _ = RunComboTasksAsync();
        }

        private async Task RunComboTasksAsync()
        {
            IsProcessing = true;
            ActionBtnText = "停止运行";
            ComboProgress = 0;
            ProgressText = $"0/{TotalPairsCount}";
            _cancellationTokenSource = new CancellationTokenSource();

            // 点击开始合成时，才真正在硬盘上创建这个目录
            if (!Directory.Exists(OutputDirectory)) Directory.CreateDirectory(OutputDirectory);

            int completed = 0;
            AppStatus = "正在全速进行原生合并 (纯I/O模式)...";

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

                    App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                    {
                        task.Status = ProcessStatus.Processing;
                        task.Details = "正在合并...";
                    });

                    var (isSuccess, detailMsg) = await ProcessSinglePairAsync(task.ImagePath, task.VideoPath, task.BaseName, token);

                    App.MainWindow?.DispatcherQueue.TryEnqueue(() =>
                    {
                        task.Status = isSuccess ? ProcessStatus.Success : ProcessStatus.Failed;
                        task.Details = detailMsg;
                        task.Progress = isSuccess ? 100 : 0;

                        Interlocked.Increment(ref completed);
                        ComboProgress = (completed * 100.0) / TotalPairsCount;
                        ProgressText = $"{completed}/{TotalPairsCount}";
                    });
                });
            }
            catch (OperationCanceledException)
            {
                AppStatus = "任务已手动中止";
            }
            finally
            {
                sw.Stop();
                IsProcessing = false;
                ActionBtnText = "开始合成";
                if (ComboProgress >= 100)
                {
                    AppStatus = $"全部合成任务完成！总共耗时 {sw.Elapsed.TotalSeconds:F1} 秒";
                }
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

                if (!File.Exists(exiftoolPath))
                {
                    return (false, "缺少 ExifTool，请检查 Tools 文件夹");
                }

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

                using (var fsOutput = new FileStream(finalOutputPath, FileMode.Append, FileAccess.Write))
                using (var fsVideo = new FileStream(videoPath, FileMode.Open, FileAccess.Read))
                {
                    await fsVideo.CopyToAsync(fsOutput, token);
                }

                string resultStatus = "原生合成成功";
                if (!KeepOriginal)
                {
                    try
                    {
                        var imgFile = await StorageFile.GetFileFromPathAsync(imagePath);
                        await imgFile.DeleteAsync(StorageDeleteOption.Default);

                        var vidFile = await StorageFile.GetFileFromPathAsync(videoPath);
                        await vidFile.DeleteAsync(StorageDeleteOption.Default);
                        resultStatus += " (已移至回收站)";
                    }
                    catch (Exception ex) { resultStatus += $" (清理失败: {ex.Message})"; }
                }

                return (true, resultStatus);
            }
            catch (Exception ex)
            {
                return (false, "错误: " + ex.Message);
            }
        }

        private async Task<string> RunProcessAndGetOutputAsync(string filePath, string args, CancellationToken token = default)
        {
            try
            {
                using var process = new Process
                {
                    StartInfo = new ProcessStartInfo
                    {
                        FileName = filePath,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true
                    }
                };
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
            var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = filePath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true
                },
                EnableRaisingEvents = true
            };

            process.Exited += (sender, e) => { tcs.TrySetResult(process.ExitCode); };

            using (token.Register(() =>
            {
                try { if (!process.HasExited) process.Kill(); } catch { }
                tcs.TrySetCanceled();
            }))
            {
                process.Start();
                return tcs.Task;
            }
        }
    }
}