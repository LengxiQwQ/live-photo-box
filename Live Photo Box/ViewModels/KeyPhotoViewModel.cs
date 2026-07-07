/*
 * KeyPhotoViewModel.cs
 *
 * 实况照片主图更换页面的 ViewModel。
 * 管理资源浏览（文件夹选择 → 自动扫描 → ListView 文件列表）、
 * 选中文件信息展示、CommandBar 命令及时间轴数据的绑定。
 *
 * 继承 ViewModelBase（轻量基类），不继承 WorkViewModelBase，
 * 因为此页面是"浏览 + 编辑"模式，不是批处理工作流。
 *
 * 扫描触发：TextBox 失去焦点时（LostFocus）或浏览按钮选择文件夹后，
 * 由 View 层调用 TriggerScan()。
 *
 * 属性读取：使用 PersistentExifTool 单实例，选中文件时查询 EXIF/GPS/视频元数据。
 */

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Helpers;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.ViewModels
{
    public partial class KeyPhotoViewModel : ViewModelBase
    {
        // ══════════════════════════════════════════════════════════════
        //  支持的文件扩展名（图片 + 视频）
        // ══════════════════════════════════════════════════════════════
        private static readonly HashSet<string> SupportedImageExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".heic", ".heif", ".jpg", ".jpeg", ".png", ".bmp", ".gif", ".tiff", ".tif", ".webp"
        };

        private static readonly HashSet<string> SupportedVideoExtensions = new(StringComparer.OrdinalIgnoreCase)
        {
            ".mov", ".mp4"
        };

        /// <summary>exiftool 并行实例数（扫描阶段分辨率读取用）</summary>
        private const int ExifToolPoolSize = 2;

        // ══════════════════════════════════════════════════════════════
        //  构造函数 & 生命周期
        // ══════════════════════════════════════════════════════════════

        public KeyPhotoViewModel()
        {
            // 默认路径为空，用户需要点击"浏览"选择文件夹或手动输入路径
        }

        public override string? PageStatusTag => null;

        /// <summary>页面卸载时清理 exiftool 进程</summary>
        public void Cleanup()
        {
            _propLoadCts?.Cancel();
            _geoCts?.Cancel();
            DisposeExifTool();
        }

        // ══════════════════════════════════════════════════════════════
        //  目录路径
        // ══════════════════════════════════════════════════════════════

        [ObservableProperty]
        private string _currentDirectory = string.Empty;

        // ══════════════════════════════════════════════════════════════
        //  扫描状态
        // ══════════════════════════════════════════════════════════════

        [ObservableProperty]
        private bool _isScanning;

        private CancellationTokenSource? _scanCts;

        // ══════════════════════════════════════════════════════════════
        //  搜索 & 排序（暂时占位，后续适配）
        // ══════════════════════════════════════════════════════════════

        [ObservableProperty]
        private string _searchText = string.Empty;

        [ObservableProperty]
        private int _selectedSortIndex;

        [ObservableProperty]
        private bool _isSortAscending = true;

        /// <summary>排序方向图标：升序 ↑ / 降序 ↓</summary>
        public string SortDirectionGlyph => IsSortAscending ? "" : "";

        /// <summary>实况照片总数（仅已确认协议的）</summary>
        [ObservableProperty]
        private int _livePhotoCount;

        /// <summary>其他文件数（非实况 + 未确认协议的）</summary>
        [ObservableProperty]
        private int _otherCount;

        /// <summary>文件统计摘要：共 M 个实况照片，K 个其他照片（多语言）</summary>
        public string FileCountSummary => ResourceService.Format("KeyPhoto_FileCountSummary", LivePhotoCount, OtherCount);

        partial void OnLivePhotoCountChanged(int value) => OnPropertyChanged(nameof(FileCountSummary));
        partial void OnOtherCountChanged(int value) => OnPropertyChanged(nameof(FileCountSummary));

        /// <summary>照片过滤：0=所有照片 / 1=实况照片 / 2=普通照片</summary>
        [ObservableProperty]
        private int _selectedFilterIndex;

        partial void OnSelectedFilterIndexChanged(int value) => ApplySortAndFilter();

        // ══════════════════════════════════════════════════════════════
        //  文件列表
        // ══════════════════════════════════════════════════════════════

        public ObservableCollection<KeyPhotoFileItem> FileItems { get; } = new();

        /// <summary>未过滤的完整文件列表（排序/搜索的后备存储）</summary>
        private List<KeyPhotoFileItem> _allFileItems = new();

        // ══════════════════════════════════════════════════════════════
        //  排序 & 搜索实现
        // ══════════════════════════════════════════════════════════════

        partial void OnSelectedSortIndexChanged(int value) => ApplySortAndFilter();
        partial void OnSearchTextChanged(string value) => ApplySortAndFilter();
        partial void OnSelectedFilePathChanged(string? value) => OnPropertyChanged(nameof(HasSelectedFile));

        [RelayCommand]
        private void ToggleSortDirection()
        {
            IsSortAscending = !IsSortAscending;
            OnPropertyChanged(nameof(SortDirectionGlyph));
            ApplySortAndFilter();
        }

        private void ApplySortAndFilter()
        {
            var sorted = SelectedSortIndex switch
            {
                0 => IsSortAscending
                    ? _allFileItems.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                    : _allFileItems.OrderByDescending(f => f.FileName, StringComparer.OrdinalIgnoreCase),
                1 => IsSortAscending
                    ? _allFileItems.OrderBy(f => f.DateTaken)
                    : _allFileItems.OrderByDescending(f => f.DateTaken),
                2 => IsSortAscending
                    ? _allFileItems.OrderBy(f => f.FileSize)
                    : _allFileItems.OrderByDescending(f => f.FileSize),
                _ => IsSortAscending
                    ? _allFileItems.OrderBy(f => f.FileName, StringComparer.OrdinalIgnoreCase)
                    : _allFileItems.OrderByDescending(f => f.FileName, StringComparer.OrdinalIgnoreCase)
            };

            var filtered = string.IsNullOrWhiteSpace(SearchText)
                ? sorted
                : sorted.Where(f => f.FileName.Contains(SearchText, StringComparison.OrdinalIgnoreCase));

            filtered = SelectedFilterIndex switch
            {
                1 => filtered.Where(f => f.HasConfirmedProtocol),       // 仅实况照片
                2 => filtered.Where(f => !f.HasConfirmedProtocol),     // 仅普通照片
                _ => filtered                                          // 所有照片
            };

            var dispatcher = App.MainWindow?.DispatcherQueue;
            dispatcher?.TryEnqueue(() =>
            {
                FileItems.Clear();
                foreach (var f in filtered) FileItems.Add(f);
                OnPropertyChanged(nameof(HasAnyFiles));
            });
        }

        // ══════════════════════════════════════════════════════════════
        //  选中文件信息（右下角信息面板绑定）
        // ══════════════════════════════════════════════════════════════

        [ObservableProperty] private string _photoFileName = string.Empty;
        [ObservableProperty] private string _photoInfoLine = string.Empty;
        [ObservableProperty] private string _videoInfoLine = string.Empty;
        [ObservableProperty] private string _protocolLine = string.Empty;

        [ObservableProperty] private string _exifCamera = string.Empty;
        [ObservableProperty] private string _exifLensParams = string.Empty;
        [ObservableProperty] private string _exifShootingParams = string.Empty;
        // 手写属性：XAML 编译器对 [ObservableProperty] 新加属性不稳定，手动实现
        private string _exifPlaceName = string.Empty;
        public string ExifPlaceName { get => _exifPlaceName; set { if (SetProperty(ref _exifPlaceName, value)) OnPropertyChanged(nameof(ExifPlaceName)); } }

        [ObservableProperty] private string _timelineInfo = string.Empty;
        public int TimelineThumbnailCount => 14;

        [ObservableProperty] private bool _isModified;

        [ObservableProperty]
        private string? _selectedFilePath;

        /// <summary>是否有文件被选中（用于控制信息面板图标和分隔线可见性）</summary>
        public bool HasSelectedFile => !string.IsNullOrEmpty(SelectedFilePath);

        /// <summary>当前目录是否加载了文件（控制折叠按钮可见性）</summary>
        public bool HasAnyFiles => FileItems.Count > 0;

        /// <summary>选中文件是否为独立视频（控制信息面板照片行可见性）</summary>
        private bool _isSelectedFileVideo;
        public bool IsSelectedFileVideo
        {
            get => _isSelectedFileVideo;
            set
            {
                if (SetProperty(ref _isSelectedFileVideo, value))
                {
                    OnPropertyChanged(nameof(IsSelectedFileVideo));
                    OnPropertyChanged(nameof(IsPhotoRowVisible));
                    OnPropertyChanged(nameof(IsVideoRowVisible));
                }
            }
        }

        /// <summary>照片信息行可见（非视频文件时显示）</summary>
        public bool IsPhotoRowVisible => !IsSelectedFileVideo;

        /// <summary>选中文件是否为已确认协议的实况照片</summary>
        private bool _isSelectedLivePhoto;
        public bool IsSelectedLivePhoto
        {
            get => _isSelectedLivePhoto;
            set
            {
                if (SetProperty(ref _isSelectedLivePhoto, value))
                {
                    OnPropertyChanged(nameof(IsSelectedLivePhoto));
                    OnPropertyChanged(nameof(IsVideoRowVisible));
                }
            }
        }

        /// <summary>视频信息行可见（实况照片或有视频数据的独立视频时显示）</summary>
        public bool IsVideoRowVisible => IsSelectedFileVideo || IsSelectedLivePhoto;

        /// <summary>选中文件的缩略图（信息面板用，直接复用列表已加载的）</summary>
        private Microsoft.UI.Xaml.Media.ImageSource? _selectedFileThumbnail;
        public Microsoft.UI.Xaml.Media.ImageSource? SelectedFileThumbnail
        {
            get => _selectedFileThumbnail;
            set { if (SetProperty(ref _selectedFileThumbnail, value)) OnPropertyChanged(nameof(SelectedFileThumbnailPlaceholderVisibility)); }
        }

        /// <summary>信息面板缩略图占位符可见性</summary>
        public Microsoft.UI.Xaml.Visibility SelectedFileThumbnailPlaceholderVisibility =>
            _selectedFileThumbnail == null ? Microsoft.UI.Xaml.Visibility.Visible : Microsoft.UI.Xaml.Visibility.Collapsed;

        // ══════════════════════════════════════════════════════════════
        //  CommandBar 命令
        // ══════════════════════════════════════════════════════════════

        [RelayCommand] private void GoBack() { }
        [RelayCommand] private void Restore() { }
        [RelayCommand] private void Save() { IsModified = false; }
        [RelayCommand] private void SaveAs() { }
        [RelayCommand] private void Export() { }
        [RelayCommand] private void BrowseFolder() { }
        [RelayCommand] private void ViewFullProperties() { }


        // ══════════════════════════════════════════════════════════════
        //  文件选中 → 加载属性
        // ══════════════════════════════════════════════════════════════

        /// <summary>属性加载取消令牌</summary>
        private CancellationTokenSource? _propLoadCts;

        /// <summary>反向地理编码速率限制：上次请求完成的时间戳</summary>
        private long _lastGeoRequestTicks;
        private const long GeoCooldownTicks = 12_000_000; // 1.2 秒 (Ticks)
        private CancellationTokenSource? _geoCts;

        /// <summary>View 层选中变更时调用，异步加载 EXIF 元数据填充信息面板</summary>
        public void SelectFile(string? filePath)
        {
            SelectedFilePath = filePath;

            // 取消上一次未完成的属性加载
            _propLoadCts?.Cancel();
            _propLoadCts?.Dispose();
            _propLoadCts = null;
            _geoCts?.Cancel();

            if (string.IsNullOrEmpty(filePath) || !File.Exists(filePath))
            {
                ClearFileInfo();
                return;
            }

            // 判断文件类型
            var fileExt = Path.GetExtension(filePath);
            IsSelectedFileVideo = SupportedVideoExtensions.Contains(fileExt);
            IsSelectedLivePhoto = false; // 默认，下面从 item 读取

            // 先从 FileItems 找基础信息，立即显示（名称、大小、分辨率）
            var item = FileItems.FirstOrDefault(f =>
                string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                IsSelectedLivePhoto = item.HasConfirmedProtocol;
                PhotoFileName = item.FileName;
                var ext = Path.GetExtension(item.FileName).TrimStart('.').ToUpperInvariant();
                var photoSize = GetPhotoSizeDisplay(item);
                PhotoInfoLine = string.IsNullOrEmpty(item.Resolution)
                    ? $"{photoSize}  │  {ext}"
                    : $"{item.Resolution}  │  {photoSize}  │  {ext}";
                VideoInfoLine = string.Empty;
                // DualFile：Phase 2 已确认的才有协议名；其余走 XMP 检测
                string? proto = item.LivePhotoType == LivePhotoType.DualFile
                    ? (item.HasConfirmedProtocol
                        ? ResourceService.GetString("KeyPhoto_Protocol_Apple")
                        : ResourceService.GetString("KeyPhoto_Protocol_NonLive"))
                    : GetProtocolName(item.LivePhotoType, filePath);
                ProtocolLine = proto != null ? $"{proto}  ·  {item.DateTaken}" : item.DateTaken;
                SelectedFileThumbnail = item.Thumbnail;
            }
            ExifCamera = string.Empty;
            ExifLensParams = string.Empty;
            ExifShootingParams = string.Empty;
            ExifPlaceName = string.Empty;

            // 异步加载完整属性
            _propLoadCts = new CancellationTokenSource();
            var token = _propLoadCts.Token;
            string? videoPath = null;
            long embeddedVideoLen = 0;
            if (item?.LivePhotoType == LivePhotoType.DualFile && !string.IsNullOrEmpty(item.PairedVideoPath))
            {
                videoPath = item.PairedVideoPath;
            }
            else if (item?.LivePhotoType == LivePhotoType.SingleFileJpeg && item.AppendedVideoLength > 0)
            {
                embeddedVideoLen = item.AppendedVideoLength;
            }
            LogService.Debug($"KeyPhoto SelectFile: type={item?.LivePhotoType}, videoPath={videoPath ?? "null"}, embeddedVideoLen={embeddedVideoLen}", LogSource.UI);
            _ = LoadPropertiesAsync(filePath, videoPath, embeddedVideoLen, token);
        }

        /// <summary>清空信息面板</summary>
        private void ClearFileInfo()
        {
            IsSelectedFileVideo = false;
            IsSelectedLivePhoto = false;
            PhotoFileName = string.Empty;
            PhotoInfoLine = string.Empty;
            VideoInfoLine = string.Empty;
            ProtocolLine = string.Empty;
            ExifCamera = string.Empty;
            ExifLensParams = string.Empty;
            ExifShootingParams = string.Empty;
            ExifPlaceName = string.Empty;
            TimelineInfo = string.Empty;
            SelectedFileThumbnail = null;
        }

        // ══════════════════════════════════════════════════════════════
        //  exiftool 属性加载（选中文件时）
        // ══════════════════════════════════════════════════════════════

        /// <summary>属性查询用的 PersistentExifTool 单例（懒加载，一个足够）</summary>
        private PersistentExifTool? _propExifTool;

        private PersistentExifTool GetPropExifTool()
        {
            if (_propExifTool != null) return _propExifTool;

            string? exifToolPath = ExternalToolLocator.FindExifTool()
                ?? Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe");
            if (!File.Exists(exifToolPath)) throw new InvalidOperationException("exiftool not found");

            _propExifTool = new PersistentExifTool(exifToolPath);
            _propExifTool.OnRestarted += (msg) =>
                LogService.FileOp($"[KeyPhoto exiftool] {msg}", LogLevel.Warning);
            return _propExifTool;
        }

        private void DisposeExifTool()
        {
            try { _propExifTool?.Dispose(); } catch { }
            _propExifTool = null;
        }

        /// <summary>全部 exiftool 查询标签（一次查询拿到所有属性）</summary>
        private static readonly string[] PropTags =
        {
            "-j",
            // 图片尺寸 & 格式
            "-ImageWidth", "-ImageHeight", "-MIMEType",
            // 相机
            "-Make", "-Model",
            // 拍摄参数
            "-FocalLength", "-FocalLengthIn35mmFormat", "-FNumber", "-ISO",
            "-ExposureTime", "-ExposureCompensation",
            // 镜头 & HDR
            "-LensModel", "-HDRImageType",
            // 小米私有 JSON 标签（SensorType=rear/front, ZoomMultiple）
            "-SensorType", "-ZoomMultiple",
            // 日期
            "-DateTimeOriginal",
            // GPS
            "-GPSLatitude", "-GPSLatitudeRef", "-GPSLongitude", "-GPSLongitudeRef", "-GPSAltitude",
            // 视频
            "-MediaDuration", "-AvgBitrate", "-CompressorID",
            // 实况照片
            "-ContentIdentifier"
        };

        /// <summary>异步加载照片 EXIF + 配对视频属性（并行查询，同时更新）</summary>
        private async Task LoadPropertiesAsync(string imagePath, string? videoPath, long embeddedVideoLen, CancellationToken token)
        {
            LogService.Debug($"KeyPhoto LoadProperties: image='{Path.GetFileName(imagePath)}', video='{(videoPath != null ? Path.GetFileName(videoPath) : "none")}', embedded={embeddedVideoLen}", LogSource.UI);

            string? tempVideoPath = null;
            try
            {
                if (!IsImageOrVideo(imagePath)) return;

                PersistentExifTool exifTool;
                try { exifTool = GetPropExifTool(); }
                catch (InvalidOperationException) { return; }

                // 单文件实况照片：从 JPEG 尾部提取视频段到临时文件
                if (videoPath == null && embeddedVideoLen > 0)
                {
                    tempVideoPath = Path.Combine(Path.GetTempPath(), $"lpb_vid_{Guid.NewGuid():N}.mp4");
                    var fileSize = new FileInfo(imagePath).Length;
                    long offset = fileSize - embeddedVideoLen;
                    await Task.Run(() =>
                    {
                        using var src = new FileStream(imagePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
                        src.Seek(offset, SeekOrigin.Begin);
                        using var dst = new FileStream(tempVideoPath, FileMode.Create, FileAccess.Write, FileShare.None);
                        var buf = new byte[81920];
                        long remain = embeddedVideoLen;
                        while (remain > 0)
                        {
                            token.ThrowIfCancellationRequested();
                            int r = src.Read(buf, 0, (int)Math.Min(buf.Length, remain));
                            if (r == 0) break;
                            dst.Write(buf, 0, r);
                            remain -= r;
                        }
                    }, token);
                    videoPath = tempVideoPath;
                    LogService.Debug($"KeyPhoto embedded video extracted to temp: {tempVideoPath}", LogSource.UI);
                }

                // 构建照片查询
                var imgArgs = new string[PropTags.Length + 1];
                Array.Copy(PropTags, imgArgs, PropTags.Length);
                imgArgs[^1] = imagePath;

                // 并行查询照片 + 配对视频
                var imgTask = exifTool.SendCommandAsync(token, imgArgs);
                var vidTask = videoPath != null
                    ? exifTool.SendCommandAsync(token,
                        "-j", "-ImageWidth", "-ImageHeight",
                        "-AvgBitrate", "-CompressorID", "-MediaDuration", videoPath)
                    : Task.FromResult(string.Empty);

                await Task.WhenAll(imgTask, vidTask);

                // 捕获 exiftool stderr（外部工具警告/错误）
                var stderr = exifTool.FlushStderr();
                if (!string.IsNullOrWhiteSpace(stderr))
                    LogService.Debug($"[KeyPhoto exiftool stderr] {stderr.Trim()}", LogSource.UI);

                if (token.IsCancellationRequested) return;

                var imgJson = imgTask.Result;
                var vidJson = vidTask.Result;

                LogService.Debug($"KeyPhoto exiftool image output: {TruncateJson(imgJson)}", LogSource.UI);
                if (!string.IsNullOrWhiteSpace(vidJson))
                    LogService.Debug($"KeyPhoto exiftool video output: {TruncateJson(vidJson)}", LogSource.UI);

                var dispatcher = App.MainWindow?.DispatcherQueue;
                if (dispatcher == null) return;

                var imgProps = ParseExifProperties(imgJson, imagePath);
                var vidProps = ParseVideoProperties(vidJson);
                vidProps.FileSizeBytes = videoPath != null ? new FileInfo(videoPath).Length : 0;

                LogService.Debug($"KeyPhoto video props: W={vidProps.Width} H={vidProps.Height} Size={vidProps.FileSizeBytes} BR={vidProps.AvgBitrate} Codec={vidProps.CompressorID} Dur={vidProps.MediaDuration}", LogSource.UI);

                dispatcher.TryEnqueue(() =>
                {
                    ApplyProperties(imgProps);
                    ApplyVideoProperties(vidProps);
                });
            }
            catch (OperationCanceledException)
            {
                LogService.FileOp($"KeyPhoto property load cancelled for '{Path.GetFileName(imagePath)}'", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                LogService.FileOp($"KeyPhoto property load failed for '{Path.GetFileName(imagePath)}': {ex.Message}", LogLevel.Error, ex);
            }
            finally
            {
                if (tempVideoPath != null)
                {
                    try { File.Delete(tempVideoPath); } catch { }
                }
            }
        }

        private static string TruncateJson(string json, int maxLen = 300)
        {
            if (string.IsNullOrWhiteSpace(json)) return "(empty)";
            return json.Length <= maxLen ? json : json[..maxLen] + "...";
        }

        private static VideoProperties ParseVideoProperties(string json)
        {
            var vp = new VideoProperties();
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("["))
                return vp;
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement[0];
                vp.Width = GetJsonInt(root, "ImageWidth");
                vp.Height = GetJsonInt(root, "ImageHeight");
                vp.AvgBitrate = GetJsonStr(root, "AvgBitrate");
                vp.CompressorID = GetJsonStr(root, "CompressorID");
                vp.MediaDuration = GetJsonStr(root, "MediaDuration");
            }
            catch { }
            return vp;
        }

        private void ApplyVideoProperties(VideoProperties vp)
        {
            if (vp.Width <= 0 || vp.Height <= 0 || string.IsNullOrWhiteSpace(vp.MediaDuration))
                return;

            var parts = new List<string> { $"{vp.Width} × {vp.Height}" };
            if (vp.FileSizeBytes > 0)
                parts.Add($"{vp.FileSizeBytes / 1_000_000.0:F2} MB");
            if (!string.IsNullOrWhiteSpace(vp.CompressorID))
                parts.Add(CodecToDisplay(vp.CompressorID));
            if (double.TryParse(vp.MediaDuration,
                System.Globalization.NumberStyles.Any,
                System.Globalization.CultureInfo.InvariantCulture, out var dur))
                parts.Add($"{dur:F1}s");

            VideoInfoLine = string.Join("  │  ", parts);
        }

        /// <summary>解析 exiftool JSON 输出为结构化属性</summary>
        private static ExifProperties ParseExifProperties(string json, string filePath)
        {
            var p = new ExifProperties();

            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("["))
                return p;

            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement[0];

                // 图片尺寸
                p.ImageWidth = GetJsonInt(root, "ImageWidth");
                p.ImageHeight = GetJsonInt(root, "ImageHeight");

                // 格式
                p.MimeType = GetJsonStr(root, "MIMEType");

                // 相机：厂商 + 型号。若型号已包含厂商则不重复（如 "Xiaomi 14 Pro" 不拼成 "Xiaomi Xiaomi 14 Pro"）
                string? make = GetJsonStr(root, "Make");
                string? model = GetJsonStr(root, "Model");
                if (!string.IsNullOrWhiteSpace(model) && !string.IsNullOrWhiteSpace(make)
                    && model.Contains(make, StringComparison.OrdinalIgnoreCase))
                    p.Camera = model;
                else if (!string.IsNullOrWhiteSpace(make) || !string.IsNullOrWhiteSpace(model))
                    p.Camera = $"{make} {model}".Trim();
                else
                    p.Camera = "";

                // 拍摄参数
                p.FocalLength = GetJsonStr(root, "FocalLength");
                p.FocalLengthIn35mmFormat = GetJsonStr(root, "FocalLengthIn35mmFormat");
                p.FNumber = GetJsonStr(root, "FNumber");
                p.ISO = GetJsonInt(root, "ISO");
                p.ExposureTime = GetJsonStr(root, "ExposureTime");
                p.ExposureCompensation = GetJsonStr(root, "ExposureCompensation");
                // 镜头 & HDR
                p.LensModel = GetJsonStr(root, "LensModel");
                p.HDRImageType = GetJsonStr(root, "HDRImageType");
                // 小米私有字段（SensorType=rear/front, ZoomMultiple）
                p.SensorType = GetJsonStr(root, "SensorType");
                p.ZoomMultiple = GetJsonInt(root, "ZoomMultiple");

                // 日期
                p.DateTimeOriginal = GetJsonStr(root, "DateTimeOriginal");

                // GPS
                p.GpsLatitude = GetJsonStr(root, "GPSLatitude");
                p.GpsLatitudeRef = GetJsonStr(root, "GPSLatitudeRef");
                p.GpsLongitude = GetJsonStr(root, "GPSLongitude");
                p.GpsLongitudeRef = GetJsonStr(root, "GPSLongitudeRef");
                p.GpsAltitude = GetJsonStr(root, "GPSAltitude");

                // 视频
                p.MediaDuration = GetJsonStr(root, "MediaDuration");
                p.AvgBitrate = GetJsonStr(root, "AvgBitrate");
                p.CompressorID = GetJsonStr(root, "CompressorID");

                // 实况照片标识
                p.ContentIdentifier = GetJsonStr(root, "ContentIdentifier");
            }
            catch (Exception ex)
            {
                LogService.FileOp($"Parse exiftool JSON failed: {ex.Message}", LogLevel.Warning);
            }

            return p;
        }

        /// <summary>将解析好的属性写入绑定字段</summary>
        private void ApplyProperties(ExifProperties p)
        {
            var ext = Path.GetExtension(PhotoFileName).TrimStart('.').ToUpperInvariant();
            var item = FileItems.FirstOrDefault(f =>
                string.Equals(f.FilePath, SelectedFilePath, StringComparison.OrdinalIgnoreCase));

            // ── PhotoInfoLine：分辨率 │ 大小 │ 格式 ──
            string resolution;
            if (p.ImageWidth > 0 && p.ImageHeight > 0)
            {
                resolution = $"{p.ImageWidth} × {p.ImageHeight}";
                // 同步到 FileItem（扫描阶段可能没读到）
                if (item != null && string.IsNullOrEmpty(item.Resolution))
                    item.Resolution = resolution;
            }
            else
            {
                resolution = item?.Resolution ?? "";
            }
            var photoSize = GetPhotoSizeDisplay(item);
            PhotoInfoLine = string.IsNullOrEmpty(resolution)
                ? $"{photoSize}  │  {ext}"
                : $"{resolution}  │  {photoSize}  │  {ext}";

            // ── VideoInfoLine：视频分辨率 │ 大小 │ 编码 │ 时长 ──
            // 仅当实际读到视频数据时才更新（双文件实况照片由 LoadVideoFilePropertiesAsync 单独填充）
            if (p.ImageWidth > 0 && p.ImageHeight > 0 && !string.IsNullOrWhiteSpace(p.MediaDuration))
            {
                var parts = new List<string>();
                parts.Add($"{p.ImageWidth} × {p.ImageHeight}");
                if (item != null)
                {
                    // 单文件实况照片：视频大小 = 内嵌视频段长度；其他：整个文件大小
                    long videoBytes = item.AppendedVideoLength > 0
                        ? item.AppendedVideoLength
                        : new FileInfo(item.FilePath).Length;
                    parts.Add($"{videoBytes / 1_000_000.0:F2} MB");
                }
                if (!string.IsNullOrWhiteSpace(p.CompressorID))
                    parts.Add(CodecToDisplay(p.CompressorID));
                if (!string.IsNullOrWhiteSpace(p.MediaDuration) &&
                    double.TryParse(p.MediaDuration, out var dur))
                    parts.Add($"{dur:F1}s");
                VideoInfoLine = string.Join("  │  ", parts);
            }

            // ── ProtocolLine：协议 · 日期 ──
            string date = !string.IsNullOrWhiteSpace(p.DateTimeOriginal)
                ? FormatDateTime(p.DateTimeOriginal) : item?.DateTaken ?? "";
            string? protocol = GetProtocolName(item?.LivePhotoType ?? LivePhotoType.None,
                SelectedFilePath, p.ContentIdentifier);
            ProtocolLine = protocol != null ? $"{protocol}  ·  {date}" : date;

            // ── ExifCamera（Line 1）：拍摄设备 ──
            ExifCamera = !string.IsNullOrWhiteSpace(p.Camera)
                ? p.Camera
                : ResourceService.GetString("KeyPhoto_UnknownDevice");

            // ── ExifLensParams（Line 2）：镜头描述（关键词映射 + 焦段 + 光圈）──
            ExifLensParams = BuildLensDisplayName(p);

            // ── ExifShootingParams（Line 3）：ISO │ EV │ 快门 │ HDR ──
            var shootParts = new List<string>();
            if (p.ISO > 0)
                shootParts.Add($"ISO {p.ISO}");
            if (!string.IsNullOrWhiteSpace(p.ExposureCompensation))
            {
                var ev = p.ExposureCompensation.Trim();
                if (double.TryParse(ev, System.Globalization.NumberStyles.Any,
                    System.Globalization.CultureInfo.InvariantCulture, out var evVal))
                    shootParts.Add($"EV{(evVal >= 0 ? "+" : "")}{evVal:F1}");
                else
                    shootParts.Add($"EV{ev}");
            }
            if (!string.IsNullOrWhiteSpace(p.ExposureTime))
                shootParts.Add(FormatExposureTime(p.ExposureTime));
            if (!string.IsNullOrWhiteSpace(p.HDRImageType))
                shootParts.Add("HDR");
            ExifShootingParams = string.Join("  │  ", shootParts);

            // ── ExifPlaceName：反向地理编码地名 ──
            ExifPlaceName = string.Empty;
            // 有 GPS 坐标才查询（坐标仅用于 API 调用，不显示）
            double? lat = DmsToDecimal(p.GpsLatitude, p.GpsLatitudeRef);
            double? lon = DmsToDecimal(p.GpsLongitude, p.GpsLongitudeRef);
            if (lat != null && lon != null && SelectedFilePath != null)
                _ = TriggerGeoLookupAsync(lat.Value, lon.Value, SelectedFilePath);
        }

        // ══════════════════════════════════════════════════════════════
        //  扫描入口（由 View 层调用）
        // ══════════════════════════════════════════════════════════════

        public void TriggerScan()
        {
            var path = CurrentDirectory;
            if (string.IsNullOrWhiteSpace(path) || !Directory.Exists(path))
                return;
            if (IsScanning) return;
            _ = ScanDirectoryAsync(path);
        }

        // ══════════════════════════════════════════════════════════════
        //  目录扫描（阶段 1 枚举 + 阶段 2 分辨率）
        // ══════════════════════════════════════════════════════════════

        private async Task ScanDirectoryAsync(string directoryPath)
        {
            _scanCts?.Cancel();
            _scanCts?.Dispose();
            _scanCts = new CancellationTokenSource();
            var token = _scanCts.Token;
            IsScanning = true;

            try
            {
                var dispatcher = App.MainWindow?.DispatcherQueue;
                LogService.FileOp($"KeyPhoto scan started: '{directoryPath}'");

                // 阶段 1：使用统一发现服务扫描（识别实况照片类型）
                var discoveryResult = await Task.Run(
                    () => LivePhotoDiscoveryService.ScanAsync(
                        directoryPath,
                        DiscoveryScanMode.JpegMarkers | DiscoveryScanMode.HeicTrack
                            | DiscoveryScanMode.CidMatch, token),
                    token);

                if (token.IsCancellationRequested) return;

                var files = discoveryResult.Items
                    .Where(d => !(d.LivePhotoType == LivePhotoType.DualFile && d.IsVideo))
                    .Select(d =>
                    {
                        // 有协议确认的：JPEG XMP 标记 / HEIC 视频轨。
                        // DualFile 先标 false，Phase 2 exiftool 查到 ContentIdentifier 后再确认。
                        bool confirmed = d.LivePhotoType is LivePhotoType.SingleFileJpeg
                            or LivePhotoType.SingleFileHeic;
                        return new KeyPhotoFileItem
                        {
                            FileName = Path.GetFileName(d.FilePath),
                            FilePath = d.FilePath,
                            FileSize = FileSizeFormatter.Format(d.FileSizeBytes),
                            DateTaken = d.LastWriteTime.ToString("yyyy/MM/dd HH:mm"),
                            Resolution = string.Empty,
                            LivePhotoType = d.LivePhotoType,
                            PairedVideoPath = d.PairedVideoPath,
                            AppendedVideoLength = d.AppendedVideoLength,
                            DetectionMethod = d.DetectionMethod,
                            HasConfirmedProtocol = confirmed
                        };
                    }).ToList();

                // 按分类统计（仅已确认协议的才算实况照片）
                int confirmedCount = files.Count(f => f.HasConfirmedProtocol);
                int dualCount = files.Count(f => f.LivePhotoType == LivePhotoType.DualFile);
                int singleJpegCount = files.Count(f => f.LivePhotoType == LivePhotoType.SingleFileJpeg);
                int singleHeicCount = files.Count(f => f.LivePhotoType == LivePhotoType.SingleFileHeic);
                LogService.FileOp($"KeyPhoto scan done: {files.Count} files total, " +
                    $"DualFile={dualCount}, SingleFileJpeg={singleJpegCount}, " +
                    $"SingleFileHeic={singleHeicCount}, Confirmed={confirmedCount}, " +
                    $"Regular={files.Count - dualCount - singleJpegCount - singleHeicCount}");

                _allFileItems = files;
                LivePhotoCount = confirmedCount;
                OtherCount = files.Count - confirmedCount;

                ThumbnailService.ClearCache();
                ApplySortAndFilter();
                ClearFileInfo();

                LogService.FileOp($"KeyPhoto scan phase 1: {files.Count} files ({LivePhotoCount} live photos) in '{directoryPath}'");

                // 阶段 2：exiftool 并行读取分辨率 + EXIF 日期
                if (files.Count > 0)
                {
                    await ReadResolutionsAsync(files, token);
                    // Phase 2 可能确认了新的 DualFile 协议 → 刷新列表
                    ApplySortAndFilter();
                }
            }
            catch (OperationCanceledException)
            {
                LogService.FileOp("KeyPhoto scan cancelled", LogLevel.Warning);
            }
            catch (Exception ex)
            {
                LogService.FileOp($"KeyPhoto scan failed: {ex.Message}", LogLevel.Error, ex);
            }
            finally
            {
                IsScanning = false;
                _scanCts?.Dispose();
                _scanCts = null;
            }
        }

        private async Task ReadResolutionsAsync(List<KeyPhotoFileItem> files, CancellationToken token)
        {
            string? exifToolPath = ExternalToolLocator.FindExifTool()
                ?? Path.Combine(AppContext.BaseDirectory, "Tools", "exiftool.exe");
            if (!File.Exists(exifToolPath))
            {
                LogService.FileOp("KeyPhoto: exiftool not found, skipping resolution reading", LogLevel.Warning);
                return;
            }

            int resSuccess = 0, resFail = 0;

            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher == null) return;

            var pool = new List<PersistentExifTool>(ExifToolPoolSize);
            try
            {
                for (int i = 0; i < ExifToolPoolSize; i++)
                {
                    var tool = new PersistentExifTool(exifToolPath);
                    int toolIdx = i;
                    tool.OnRestarted += (msg) =>
                        dispatcher.TryEnqueue(() =>
                            LogService.FileOp($"[KeyPhoto exiftool#{toolIdx}] {msg}", LogLevel.Warning));
                    pool.Add(tool);
                }

                int batchSize = ExifToolPoolSize;
                for (int batchStart = 0; batchStart < files.Count; batchStart += batchSize)
                {
                    if (token.IsCancellationRequested) break;
                    int batchEnd = Math.Min(batchStart + batchSize, files.Count);
                    int batchCount = batchEnd - batchStart;

                    var batchTasks = new Task<(int index, int width, int height, string? dateTaken, string? cid)>[batchCount];
                    for (int bi = 0; bi < batchCount; bi++)
                    {
                        int fileIndex = batchStart + bi;
                        var file = files[fileIndex];
                        var tool = pool[bi % ExifToolPoolSize];
                        batchTasks[bi] = Task.Run(async () =>
                        {
                            try
                            {
                                string json = await tool.SendCommandAsync(
                                    token, "-j", "-ImageWidth", "-ImageHeight", "-DateTimeOriginal",
                                    "-ContentIdentifier", file.FilePath);
                                var (w, h, date, cid) = ParseExifInfo(json);
                                return (fileIndex, w, h, date, cid);
                            }
                            catch (OperationCanceledException) { return (fileIndex, 0, 0, null, null); }
                            catch (Exception ex)
                            {
                                LogService.FileOp($"exiftool resolution failed: {ex.Message}", LogLevel.Warning);
                                return (fileIndex, 0, 0, null, null);
                            }
                        }, token);
                    }

                    var results = await Task.WhenAll(batchTasks);
                    dispatcher.TryEnqueue(() =>
                    {
                        foreach (var (index, width, height, dateTaken, cid) in results)
                        {
                            if (width > 0 && height > 0)
                            {
                                files[index].Resolution = $"{width} × {height}";
                                resSuccess++;
                            }
                            else resFail++;
                            if (!string.IsNullOrWhiteSpace(dateTaken))
                                files[index].DateTaken = dateTaken;
                            // DualFile + ContentIdentifier → 确认为 Apple，显示 LIVE 徽标
                            if (!string.IsNullOrWhiteSpace(cid) &&
                                files[index].LivePhotoType == LivePhotoType.DualFile &&
                                !files[index].HasConfirmedProtocol)
                            {
                                files[index].HasConfirmedProtocol = true;
                                LivePhotoCount++;
                                OtherCount--;
                            }
                        }
                    });
                }
            }
            finally
            {
                foreach (var tool in pool) try { tool.Dispose(); } catch { }
            }

            LogService.FileOp($"KeyPhoto resolution reading done: {resSuccess} success, {resFail} failed (out of {files.Count})");
        }

        private static (int width, int height, string? dateTaken, string? contentIdentifier) ParseExifInfo(string json)
        {
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("[")) return (0, 0, null, null);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement[0];
                int w = 0, h = 0;
                if (root.TryGetProperty("ImageWidth", out var wp)) w = ParseIntFromJson(wp);
                if (root.TryGetProperty("ImageHeight", out var hp)) h = ParseIntFromJson(hp);

                // 解析 EXIF DateTimeOriginal，格式化为 "yyyy/MM/dd HH:mm"
                string? dateTaken = null;
                if (root.TryGetProperty("DateTimeOriginal", out var dto) && dto.ValueKind == JsonValueKind.String)
                {
                    var raw = dto.GetString();
                    if (!string.IsNullOrWhiteSpace(raw))
                    {
                        if (DateTime.TryParseExact(raw, "yyyy:MM:dd HH:mm:ss",
                            System.Globalization.CultureInfo.InvariantCulture,
                            System.Globalization.DateTimeStyles.None, out var dt))
                        {
                            dateTaken = dt.ToString("yyyy/MM/dd HH:mm");
                        }
                    }
                }

                // ContentIdentifier — Apple Live Photo UUID（仅 HEIC/Apple 文件有此字段）
                string? cid = GetJsonStr(root, "ContentIdentifier");

                return (w, h, dateTaken, cid);
            }
            catch { return (0, 0, null, null); }
        }

        // ══════════════════════════════════════════════════════════════
        //  格式化工具方法
        // ══════════════════════════════════════════════════════════════

        private static bool IsImageOrVideo(string path) =>
            SupportedImageExtensions.Contains(Path.GetExtension(path)) ||
            SupportedVideoExtensions.Contains(Path.GetExtension(path));

        private static int ParseIntFromJson(JsonElement e) => e.ValueKind switch
        {
            JsonValueKind.Number => e.TryGetInt32(out var n) ? n : (int)e.GetDouble(),
            JsonValueKind.String => int.TryParse(e.GetString(), out var s) ? s : 0,
            _ => 0
        };

        private static string? GetJsonStr(JsonElement root, string key) =>
            root.TryGetProperty(key, out var p)
                ? p.ValueKind switch
                {
                    JsonValueKind.String => p.GetString(),
                    JsonValueKind.Number => p.GetRawText(),
                    _ => null
                }
                : null;

        private static int GetJsonInt(JsonElement root, string key) =>
            root.TryGetProperty(key, out var p) ? ParseIntFromJson(p) : 0;

        /// <summary>FFmpeg 风格的编码器名 → 可读名称</summary>
        private static string CodecToDisplay(string codec)
        {
            var c = codec.ToLowerInvariant();
            if (c.Contains("hvc") || c.Contains("hev")) return "H.265";
            if (c.Contains("avc") || c.Contains("h264")) return "H.264";
            if (c.Contains("mp4v")) return "MPEG-4";
            return codec;
        }

        /// <summary>exiftool 的 "x.xx s" 或数字字符串 → 格式化快门</summary>
        private static string FormatExposureTime(string val)
        {
            val = val.Trim();
            if (double.TryParse(val, out var d) && d < 1 && d > 0)
                return $"1/{Math.Round(1.0 / d)} s";
            return $"{val} s";
        }

        /// <summary>exiftool 的 "xx mm" 或数字 → "xx mm"</summary>
        private static string FormatFocalLength(string val)
        {
            val = val.Trim().TrimEnd('m', 'M', ' ');
            if (double.TryParse(val, out var d))
                return $"{d:F0} mm";
            return $"{val} mm";
        }

        /// <summary>exiftool 的 f-number 格式化</summary>
        private static string FormatFNumber(string val)
        {
            val = val.Trim();
            if (double.TryParse(val, out var d))
                return $"f/{d:F2}";
            return $"f/{val}";
        }

        /// <summary>镜头类型关键词 → 资源键（按优先级排列，先匹配先生效）</summary>
        private static readonly (string[] Keywords, string ResourceKey)[] LensTypeMap =
        {
            (new[] { "ultrawide", "ultra wide" }, "KeyPhoto_Lens_UltraWide"),
            (new[] { "wide" },                  "KeyPhoto_Lens_Wide"),
            (new[] { "telephoto" },             "KeyPhoto_Lens_Telephoto"),
            (new[] { "tele" },                  "KeyPhoto_Lens_Telephoto"),
            (new[] { "macro" },                 "KeyPhoto_Lens_Macro"),
            (new[] { "main" },                  "KeyPhoto_Lens_Main"),
            (new[] { "periscope" },             "KeyPhoto_Lens_Periscope"),
            (new[] { "depth", "portrait" },     "KeyPhoto_Lens_Depth"),
        };

        /// <summary>在 LensModel 字符串中按优先级匹配镜头类型关键词</summary>
        private static string? MatchLensType(string lens)
        {
            foreach (var (keywords, resourceKey) in LensTypeMap)
            {
                foreach (var kw in keywords)
                {
                    if (lens.Contains(kw, StringComparison.OrdinalIgnoreCase))
                        return ResourceService.GetString(resourceKey);
                }
            }
            return null;
        }

        /// <summary>根据 LensModel / 小米 SensorType+ZoomMultiple / 焦段+光圈构建镜头描述。</summary>
        private static string BuildLensDisplayName(ExifProperties p)
        {
            string? lens = p.LensModel;

            bool hasLensInfo = !string.IsNullOrWhiteSpace(lens)
                || !string.IsNullOrWhiteSpace(p.SensorType)
                || !string.IsNullOrWhiteSpace(p.FocalLengthIn35mmFormat)
                || !string.IsNullOrWhiteSpace(p.FNumber);

            if (!hasLensInfo)
                return ResourceService.GetString("KeyPhoto_UnknownCamera");

            // ── 位置：LensModel 关键词 → 小米 SensorType → 默认后置 ──
            bool isFront = (!string.IsNullOrWhiteSpace(lens) && lens.Contains("front", StringComparison.OrdinalIgnoreCase))
                || (!string.IsNullOrWhiteSpace(p.SensorType) && p.SensorType.Equals("front", StringComparison.OrdinalIgnoreCase));

            string position = isFront
                ? ResourceService.GetString("KeyPhoto_Lens_Front")
                : ResourceService.GetString("KeyPhoto_Lens_Rear");

            // ── 类型：LensModel 关键词 → 小米 ZoomMultiple ──
            string? type = lens != null ? MatchLensType(lens) : null;

            if (type == null && p.ZoomMultiple > 0)
            {
                type = p.ZoomMultiple switch
                {
                    <= 1 => ResourceService.GetString("KeyPhoto_Lens_Main"),
                    2 or 3 => ResourceService.GetString("KeyPhoto_Lens_Telephoto"),
                    _ => ResourceService.GetString("KeyPhoto_Lens_Periscope")
                };
            }

            var parts = new List<string>
            {
                type != null ? $"{position}{type}" : $"{position}{ResourceService.GetString("KeyPhoto_Lens_Camera")}"
            };

            if (!string.IsNullOrWhiteSpace(p.FocalLengthIn35mmFormat))
                parts.Add(FormatFocalLength(p.FocalLengthIn35mmFormat));
            if (!string.IsNullOrWhiteSpace(p.FNumber))
                parts.Add(FormatFNumber(p.FNumber));

            return string.Join(" · ", parts);
        }

        /// <summary>exiftool 日期 "2024:12:15 14:32:00" → "2024/12/15 14:32"</summary>
        private static string FormatDateTime(string val)
        {
            if (DateTime.TryParseExact(val.Trim(), "yyyy:MM:dd HH:mm:ss",
                System.Globalization.CultureInfo.InvariantCulture,
                System.Globalization.DateTimeStyles.None, out var dt))
                return dt.ToString("yyyy/MM/dd HH:mm");
            return val.Trim().Length >= 16 ? val.Trim()[..16] : val.Trim();
        }

        /// <summary>JPEG 实况照片协议关键词 → 资源键（OPPO/小米优先于 Google：它们复用 Google XMP 结构）</summary>
        private static readonly (string Keyword, string ResourceKey)[] JpegProtocolMap =
        {
            ("OpCamera:VideoLength",            "KeyPhoto_Protocol_OPPO"),
            ("MiCamera:VideoLength",            "KeyPhoto_Protocol_Xiaomi"),
            ("GCamera:MicroVideo",              "KeyPhoto_Protocol_GoogleV1"),
            ("GCamera:MotionPhoto",             "KeyPhoto_Protocol_GoogleV2"),
            ("Container:Directory",             "KeyPhoto_Protocol_GoogleV2"),
        };

        /// <summary>根据 LivePhotoType + XMP 内容 + ContentIdentifier，确定协议显示名</summary>
        private static string? GetProtocolName(LivePhotoType type, string? filePath,
            string? contentIdentifier = null)
        {
            switch (type)
            {
                case LivePhotoType.DualFile:
                    if (!string.IsNullOrWhiteSpace(contentIdentifier))
                        return ResourceService.GetString("KeyPhoto_Protocol_Apple");
                    return ResourceService.GetString("KeyPhoto_Protocol_NonLive");

                case LivePhotoType.SingleFileJpeg:
                    if (filePath != null)
                    {
                        try
                        {
                            var text = LivePhotoSplitService.ReadMetadataTextSync(filePath);
                            foreach (var (keyword, resourceKey) in JpegProtocolMap)
                            {
                                if (text.Contains(keyword))
                                    return ResourceService.GetString(resourceKey);
                            }
                        }
                        catch { }
                    }
                    return ResourceService.GetString("KeyPhoto_Protocol_JpegGeneric");

                case LivePhotoType.SingleFileHeic:
                    return ResourceService.GetString("KeyPhoto_Protocol_HeicEmbedded");

                default:
                    return ResourceService.GetString("KeyPhoto_Protocol_NonLive");
            }
        }

        /// <summary>获取照片部分的大小（单文件实况照片需扣除视频段）</summary>
        private static string GetPhotoSizeDisplay(KeyPhotoFileItem? item)
        {
            if (item == null) return "—";
            if (item.LivePhotoType is LivePhotoType.SingleFileJpeg or LivePhotoType.SingleFileHeic
                && item.AppendedVideoLength > 0)
            {
                try
                {
                    var totalBytes = new FileInfo(item.FilePath).Length;
                    var photoBytes = totalBytes - item.AppendedVideoLength;
                    if (photoBytes > 0)
                        return FileSizeFormatter.Format(photoBytes);
                }
                catch { }
            }
            return item.FileSize;
        }

        /// <summary>exiftool DMS → 十进制度数。DMS 里可能自带方向字母，ref 仅在没有时才用。</summary>
        private static double? DmsToDecimal(string? dms, string? refStr)
        {
            if (string.IsNullOrWhiteSpace(dms)) return null;
            try
            {
                // 去除 "deg"、多余空格，保留数字和方向字母
                string cleaned = dms.Replace("deg", "°").Trim();
                // 检查末尾是否有方向字母 (N/S/E/W)，有则用它决定正负
                char last = cleaned[cleaned.Length - 1];
                bool negative = false;
                if (last == 'N' || last == 'n') { cleaned = cleaned[..^1]; }
                else if (last == 'S' || last == 's') { cleaned = cleaned[..^1]; negative = true; }
                else if (last == 'E' || last == 'e') { cleaned = cleaned[..^1]; }
                else if (last == 'W' || last == 'w') { cleaned = cleaned[..^1]; negative = true; }
                else if (!string.IsNullOrWhiteSpace(refStr))
                {
                    negative = refStr.Trim().Equals("S", StringComparison.OrdinalIgnoreCase)
                            || refStr.Trim().Equals("W", StringComparison.OrdinalIgnoreCase);
                }

                var parts = cleaned.Split('°', '\'', '"');
                double deg = 0, min = 0, sec = 0;
                if (parts.Length > 0) double.TryParse(parts[0].Trim(), out deg);
                if (parts.Length > 1) double.TryParse(parts[1].Trim(), out min);
                if (parts.Length > 2) double.TryParse(parts[2].Trim(), out sec);
                double result = deg + min / 60.0 + sec / 3600.0;
                return negative ? -result : result;
            }
            catch { return null; }
        }

        /// <summary>反向地理编码：速率限制 1.2s，语言跟系统 CultureInfo</summary>
        private async Task TriggerGeoLookupAsync(double lat, double lon, string filePath)
        {
            // 取消旧的待处理请求
            _geoCts?.Cancel();
            _geoCts = new CancellationTokenSource();
            var token = _geoCts.Token;

            long now = DateTime.UtcNow.Ticks;
            long elapsed = now - _lastGeoRequestTicks;
            long delayTicks = GeoCooldownTicks - elapsed;

            if (delayTicks > 0)
            {
                try { await Task.Delay((int)(delayTicks / TimeSpan.TicksPerMillisecond), token); }
                catch (TaskCanceledException) { return; }
            }

            if (token.IsCancellationRequested) return;
            _lastGeoRequestTicks = DateTime.UtcNow.Ticks;

            try
            {
                // 语言跟系统当前 UI 文化：中文 → zh，其余 → en
                string lang = System.Globalization.CultureInfo.CurrentUICulture.Name.StartsWith("zh") ? "zh" : "en";
                string url = $"https://nominatim.openstreetmap.org/reverse?format=json&lat={lat:F6}&lon={lon:F6}&zoom=10&accept-language={lang}";
                using var client = new System.Net.Http.HttpClient();
                client.DefaultRequestHeaders.Add("User-Agent", "LivePhotoBox/2.0");
                client.Timeout = TimeSpan.FromSeconds(5);
                var response = await client.GetStringAsync(url, token);
                if (string.IsNullOrWhiteSpace(response)) return;

                using var doc = JsonDocument.Parse(response);
                string? name = doc.RootElement.TryGetProperty("display_name", out var dn)
                    ? dn.GetString() : null;

                if (!string.IsNullOrWhiteSpace(name) && !token.IsCancellationRequested)
                {
                    var dispatcher = App.MainWindow?.DispatcherQueue;
                    dispatcher?.TryEnqueue(() => ExifPlaceName = name);
                }
            }
            catch (TaskCanceledException) { }
            catch (Exception ex)
            {
                LogService.FileOp($"Geo lookup failed: {ex.Message}", LogLevel.Warning);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  结构化属性（exiftool 解析结果）
        // ══════════════════════════════════════════════════════════════

        private class VideoProperties
        {
            public int Width, Height;
            public long FileSizeBytes;
            public string? AvgBitrate, CompressorID, MediaDuration;
        }

        private class ExifProperties
        {
            public int ImageWidth, ImageHeight;
            public string? MimeType;
            public string? Camera;
            public string? FocalLength, FocalLengthIn35mmFormat, FNumber, ExposureTime, ExposureCompensation;
            public string? LensModel, HDRImageType;
            public string? SensorType;
            public int ZoomMultiple;
            public int ISO;
            public string? DateTimeOriginal;
            public string? GpsLatitude, GpsLatitudeRef, GpsLongitude, GpsLongitudeRef, GpsAltitude;
            public string? MediaDuration, AvgBitrate, CompressorID;
            public string? ContentIdentifier;
        }
    }
}
