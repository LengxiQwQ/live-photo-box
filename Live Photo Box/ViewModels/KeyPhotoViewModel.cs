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

        // ══════════════════════════════════════════════════════════════
        //  文件列表
        // ══════════════════════════════════════════════════════════════

        public ObservableCollection<KeyPhotoFileItem> FileItems { get; } = new();

        // ══════════════════════════════════════════════════════════════
        //  选中文件信息（右下角信息面板绑定）
        // ══════════════════════════════════════════════════════════════

        [ObservableProperty] private string _photoFileName = string.Empty;
        [ObservableProperty] private string _photoInfoLine = string.Empty;
        [ObservableProperty] private string _videoInfoLine = string.Empty;
        [ObservableProperty] private string _protocolLine = string.Empty;

        [ObservableProperty] private string _exifCamera = string.Empty;
        [ObservableProperty] private string _exifShootingParams = string.Empty;
        [ObservableProperty] private string _exifColorGps = string.Empty;

        // 手写属性：XAML 编译器对 [ObservableProperty] 新加属性不稳定，手动实现
        private string _exifLocation = string.Empty;
        public string ExifLocation { get => _exifLocation; set { if (SetProperty(ref _exifLocation, value)) OnPropertyChanged(nameof(ExifLocation)); } }
        private string _exifPlaceName = string.Empty;
        public string ExifPlaceName { get => _exifPlaceName; set { if (SetProperty(ref _exifPlaceName, value)) OnPropertyChanged(nameof(ExifPlaceName)); } }

        [ObservableProperty] private string _timelineInfo = string.Empty;
        public int TimelineThumbnailCount => 14;

        [ObservableProperty] private bool _isModified;

        [ObservableProperty]
        private string? _selectedFilePath;

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

            // 先从 FileItems 找基础信息，立即显示（名称、大小、分辨率）
            var item = FileItems.FirstOrDefault(f =>
                string.Equals(f.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
            if (item != null)
            {
                PhotoFileName = item.FileName;
                var ext = Path.GetExtension(item.FileName).TrimStart('.').ToUpperInvariant();
                PhotoInfoLine = string.IsNullOrEmpty(item.Resolution)
                    ? $"{item.FileSize}  │  {ext}"
                    : $"{item.Resolution}  │  {item.FileSize}  │  {ext}";
                VideoInfoLine = string.Empty;
                ProtocolLine = item.DateTaken;
            }
            ExifCamera = string.Empty;
            ExifShootingParams = string.Empty;
            ExifColorGps = string.Empty;
            ExifLocation = string.Empty;
            ExifPlaceName = string.Empty;

            // 异步加载完整 EXIF 属性
            _propLoadCts = new CancellationTokenSource();
            var token = _propLoadCts.Token;
            var path = filePath;
            _ = LoadFilePropertiesAsync(path, token);
        }

        /// <summary>清空信息面板</summary>
        private void ClearFileInfo()
        {
            PhotoFileName = string.Empty;
            PhotoInfoLine = string.Empty;
            VideoInfoLine = string.Empty;
            ProtocolLine = string.Empty;
            ExifCamera = string.Empty;
            ExifShootingParams = string.Empty;
            ExifColorGps = string.Empty;
            ExifLocation = string.Empty;
            ExifPlaceName = string.Empty;
            TimelineInfo = string.Empty;
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
            "-FocalLength", "-FNumber", "-ISO", "-ExposureTime",
            // 日期
            "-DateTimeOriginal",
            // GPS
            "-GPSLatitude", "-GPSLatitudeRef", "-GPSLongitude", "-GPSLongitudeRef", "-GPSAltitude",
            // 视频
            "-MediaDuration", "-AvgBitrate", "-CompressorID",
            // 实况照片
            "-ContentIdentifier"
        };

        /// <summary>异步加载文件 EXIF 属性并更新信息面板</summary>
        private async Task LoadFilePropertiesAsync(string filePath, CancellationToken token)
        {
            try
            {
                if (!IsImageOrVideo(filePath)) return;

                PersistentExifTool exifTool;
                try { exifTool = GetPropExifTool(); }
                catch (InvalidOperationException) { return; }

                // 组装参数：tags + filePath
                var args = new string[PropTags.Length + 1];
                Array.Copy(PropTags, args, PropTags.Length);
                args[^1] = filePath;

                string json = await exifTool.SendCommandAsync(token, args);

                if (token.IsCancellationRequested) return;

                var dispatcher = App.MainWindow?.DispatcherQueue;
                if (dispatcher == null) return;

                // 在后台线程解析 JSON，UI 线程更新属性
                var props = ParseExifProperties(json, filePath);
                dispatcher.TryEnqueue(() => ApplyProperties(props));
            }
            catch (OperationCanceledException) { }
            catch (Exception ex)
            {
                LogService.FileOp($"Property load failed for '{Path.GetFileName(filePath)}': {ex.Message}", LogLevel.Warning);
            }
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

                // 相机
                string? make = GetJsonStr(root, "Make");
                string? model = GetJsonStr(root, "Model");
                if (!string.IsNullOrWhiteSpace(make) || !string.IsNullOrWhiteSpace(model))
                    p.Camera = $"{make} {model}".Trim();

                // 拍摄参数
                p.FocalLength = GetJsonStr(root, "FocalLength");
                p.FNumber = GetJsonStr(root, "FNumber");
                p.ISO = GetJsonInt(root, "ISO");
                p.ExposureTime = GetJsonStr(root, "ExposureTime");

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
            string format = !string.IsNullOrWhiteSpace(p.MimeType)
                ? MimeToDisplay(p.MimeType) : ext;
            PhotoInfoLine = string.IsNullOrEmpty(resolution)
                ? $"{item?.FileSize ?? "—"}  │  {format}"
                : $"{resolution}  │  {item?.FileSize ?? "—"}  │  {format}";

            // ── VideoInfoLine：视频分辨率 │ 码率 │ 编码 │ 时长 ──
            if (p.ImageWidth > 0 && p.ImageHeight > 0 && !string.IsNullOrWhiteSpace(p.MediaDuration))
            {
                var parts = new List<string>();
                parts.Add($"{p.ImageWidth} × {p.ImageHeight}");
                if (!string.IsNullOrWhiteSpace(p.AvgBitrate))
                    parts.Add(FormatBitrate(p.AvgBitrate));
                if (!string.IsNullOrWhiteSpace(p.CompressorID))
                    parts.Add(CodecToDisplay(p.CompressorID));
                if (!string.IsNullOrWhiteSpace(p.MediaDuration) &&
                    double.TryParse(p.MediaDuration, out var dur))
                    parts.Add($"{dur:F1}s");
                VideoInfoLine = string.Join("  │  ", parts);
            }
            else
            {
                VideoInfoLine = string.Empty;
            }

            // ── ProtocolLine：协议 · 日期 ──
            string date = !string.IsNullOrWhiteSpace(p.DateTimeOriginal)
                ? FormatDateTime(p.DateTimeOriginal) : item?.DateTaken ?? "";
            string protocol = !string.IsNullOrWhiteSpace(p.ContentIdentifier)
                ? "Live Photo" : "";
            ProtocolLine = string.IsNullOrEmpty(protocol) ? date : $"{protocol}  ·  {date}";

            // ── ExifCamera ──
            ExifCamera = p.Camera ?? "";

            // ── ExifShootingParams：焦段 │ 光圈 │ ISO │ 快门 ──
            var shootParts = new List<string>();
            if (!string.IsNullOrWhiteSpace(p.FocalLength))
                shootParts.Add(FormatFocalLength(p.FocalLength));
            if (!string.IsNullOrWhiteSpace(p.FNumber))
                shootParts.Add(FormatFNumber(p.FNumber));
            if (p.ISO > 0)
                shootParts.Add($"ISO {p.ISO}");
            if (!string.IsNullOrWhiteSpace(p.ExposureTime))
                shootParts.Add(FormatExposureTime(p.ExposureTime));
            ExifShootingParams = string.Join("  │  ", shootParts);

            // ── ExifColorGps：色彩空间等（暂为空，后续可扩展）──
            ExifColorGps = string.Empty;

            // ── ExifLocation：海拔（纯数字 + 单位，不显示坐标）──
            ExifLocation = string.Empty;
            if (!string.IsNullOrWhiteSpace(p.GpsAltitude))
            {
                string altLabel = ResourceService.GetString("KeyPhotoPage_Altitude");
                if (string.IsNullOrEmpty(altLabel)) altLabel = "Altitude";
                ExifLocation = $"{altLabel} {FormatAltitude(p.GpsAltitude)}";
            }

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

                // 阶段 1：快速文件枚举
                var files = await Task.Run(() =>
                {
                    var result = new List<KeyPhotoFileItem>();
                    try
                    {
                        var dirInfo = new DirectoryInfo(directoryPath);
                        foreach (var file in dirInfo.GetFiles())
                        {
                            token.ThrowIfCancellationRequested();
                            var ext = file.Extension;
                            if (!SupportedImageExtensions.Contains(ext) &&
                                !SupportedVideoExtensions.Contains(ext)) continue;
                            result.Add(new KeyPhotoFileItem
                            {
                                FileName = file.Name,
                                FilePath = file.FullName,
                                FileSize = FileSizeFormatter.Format(file.Length),
                                DateTaken = file.LastWriteTime.ToString("yyyy/MM/dd HH:mm"),
                                Resolution = string.Empty
                            });
                        }
                        result.Sort((a, b) =>
                            string.Compare(a.FileName, b.FileName, StringComparison.OrdinalIgnoreCase));
                    }
                    catch (OperationCanceledException) { }
                    catch (Exception ex)
                    {
                        LogService.FileOp($"KeyPhoto scan error: {ex.Message}", LogLevel.Error, ex);
                    }
                    return result;
                }, token);

                if (token.IsCancellationRequested) return;

                ThumbnailService.ClearCache();
                dispatcher?.TryEnqueue(() =>
                {
                    FileItems.Clear();
                    ClearFileInfo();
                    foreach (var f in files) FileItems.Add(f);
                });

                LogService.FileOp($"KeyPhoto scan phase 1: {files.Count} files in '{directoryPath}'");

                // 阶段 2：exiftool 并行读取分辨率
                if (files.Count > 0)
                    await ReadResolutionsAsync(files, token);
            }
            catch (OperationCanceledException)
            {
                LogService.FileOp("KeyPhoto scan cancelled");
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
                LogService.FileOp("KeyPhoto: exiftool not found, skipping resolution reading");
                return;
            }

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

                    var batchTasks = new Task<(int index, int width, int height)>[batchCount];
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
                                    token, "-j", "-ImageWidth", "-ImageHeight", file.FilePath);
                                var (w, h) = ParseImageDimensions(json, file.FilePath);
                                return (fileIndex, w, h);
                            }
                            catch (OperationCanceledException) { return (fileIndex, 0, 0); }
                            catch (Exception ex)
                            {
                                LogService.FileOp($"exiftool resolution failed: {ex.Message}", LogLevel.Warning);
                                return (fileIndex, 0, 0);
                            }
                        }, token);
                    }

                    var results = await Task.WhenAll(batchTasks);
                    dispatcher.TryEnqueue(() =>
                    {
                        foreach (var (index, width, height) in results)
                            if (width > 0 && height > 0)
                                files[index].Resolution = $"{width} × {height}";
                    });
                }
            }
            finally
            {
                foreach (var tool in pool) try { tool.Dispose(); } catch { }
            }
        }

        private static (int width, int height) ParseImageDimensions(string json, string filePath)
        {
            if (string.IsNullOrWhiteSpace(json) || !json.TrimStart().StartsWith("[")) return (0, 0);
            try
            {
                using var doc = JsonDocument.Parse(json);
                var root = doc.RootElement[0];
                int w = 0, h = 0;
                if (root.TryGetProperty("ImageWidth", out var wp)) w = ParseIntFromJson(wp);
                if (root.TryGetProperty("ImageHeight", out var hp)) h = ParseIntFromJson(hp);
                return (w, h);
            }
            catch { return (0, 0); }
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
            root.TryGetProperty(key, out var p) && p.ValueKind == JsonValueKind.String
                ? p.GetString() : null;

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

        /// <summary>MIME 类型 → 显示格式</summary>
        private static string MimeToDisplay(string mime)
        {
            if (mime.Contains("heic") || mime.Contains("heif")) return "HEIC";
            if (mime.Contains("jpeg") || mime.Contains("jpg")) return "JPEG";
            if (mime.Contains("png")) return "PNG";
            if (mime.Contains("bmp")) return "BMP";
            if (mime.Contains("gif")) return "GIF";
            if (mime.Contains("tiff")) return "TIFF";
            if (mime.Contains("webp")) return "WebP";
            if (mime.Contains("quicktime") || mime.Contains("mov")) return "MOV";
            if (mime.Contains("mp4")) return "MP4";
            return mime.Split('/').LastOrDefault()?.ToUpperInvariant() ?? "?";
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

        /// <summary>exiftool 日期 "2024:12:15 14:32:00" → "2024/12/15 14:32"</summary>
        private static string FormatDateTime(string val) =>
            val.Trim().Replace(':', '/').Replace("-", "/") is { } s && s.Length >= 16
                ? s[..16] : val.Trim();

        /// <summary>比特率 "12800000" → "12.8 Mbps"</summary>
        private static string FormatBitrate(string val)
        {
            if (double.TryParse(val.Trim(), out var bps) && bps > 0)
                return $"{bps / 1_000_000:F1} Mbps";
            return val;
        }

        /// <summary>GPS 海拔：exiftool 返回 "1944.8 m" 或 "1944.8 m Above Sea Level" → "1945 m"</summary>
        private static string FormatAltitude(string val)
        {
            // 取第一个数字部分："1944.8 m Above Sea Level" → "1944.8"
            var match = System.Text.RegularExpressions.Regex.Match(val.Trim(), @"^([\d.]+)");
            if (match.Success && double.TryParse(match.Groups[1].Value, out var d))
                return $"{d:F0} m";
            return val.Trim();
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

        private class ExifProperties
        {
            public int ImageWidth, ImageHeight;
            public string? MimeType;
            public string? Camera;
            public string? FocalLength, FNumber, ExposureTime;
            public int ISO;
            public string? DateTimeOriginal;
            public string? GpsLatitude, GpsLatitudeRef, GpsLongitude, GpsLongitudeRef, GpsAltitude;
            public string? MediaDuration, AvgBitrate, CompressorID;
            public string? ContentIdentifier;
        }
    }
}
