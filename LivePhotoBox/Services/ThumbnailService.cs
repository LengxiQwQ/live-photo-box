using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;
using Windows.Storage.FileProperties;
using Windows.Storage.Streams;
using LogLevel = LivePhotoBox.Models.LogLevel;

namespace LivePhotoBox.Services
{
    /*
     * ThumbnailService.cs
     *
     * 缩略图服务。为文件列表提供异步缩略图加载与缓存，支持三种来源：
     * 普通图片（JPG/PNG，Shell API）、HEIC（BitmapDecoder 解码缩放）、
     * 视频（FFmpeg 抽第一帧，支持硬件加速）。
     * 使用两级缓存防止重复加载，SemaphoreSlim 限制并发（照片 4 路，视频按硬件调整）。
     */
    public static class ThumbnailService
    {
        private static readonly ConcurrentDictionary<string, ImageSource> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Task<ImageSource?>> _inflightLoads = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim _loadLimiter = new(4, 4);
        // 视频 FFmpeg 抽帧并发数：根据硬件自动调整（CPU→4路，NVIDIA→16路，QSV→10路，AMF→8路）
        private static readonly Lazy<SemaphoreSlim> _videoLoadLimiterLazy = new(() =>
        {
            int c = 4;
            try
            {
                string enc = AppSettingsService.GetValue("SplitHardwareEncoder", "") ?? "";
                if (enc.Contains("nvenc") || enc.Contains("cuda")) c = 16;
                else if (enc.Contains("qsv")) c = 10;
                else if (enc.Contains("amf")) c = 8;
            }
            catch { }
            return new SemaphoreSlim(c, c);
        });
        private static SemaphoreSlim _videoLoadLimiter => _videoLoadLimiterLazy.Value;
        // 追踪可取消的视频缩略图加载（用于滚动时取消队列中等待的）
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _videoLoadCts = new(StringComparer.OrdinalIgnoreCase);
        // 追踪可取消的照片加载（TryGetOrLoad 旧路径用，滚动时新请求取消旧请求）
        private static readonly ConcurrentDictionary<string, CancellationTokenSource> _photoLoadCts = new(StringComparer.OrdinalIgnoreCase);
        private static readonly object _photoCtsLock = new();
        // 追踪加载失败次数，防止损坏文件无限重试
        private static readonly ConcurrentDictionary<string, int> _failCounts = new(StringComparer.OrdinalIgnoreCase);
        private const int MaxFailRetries = 3;
        // 追踪 TryGetOrLoad 发起的加载（与 _photoLoadCts 分开，存在于整个加载周期而非仅信号量等待期）
        private static readonly ConcurrentDictionary<string, byte> _tryGetOrLoadInFlight = new(StringComparer.OrdinalIgnoreCase);
        private static int _cacheVersion;

        // 从缓存中直接获取已加载的缩略图（同步，非阻塞）。
        // imagePath: 文件路径。
        // 返回: 缓存的 ImageSource，若尚未加载则返回 null。
        public static ImageSource? GetCached(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            return _thumbnailCache.TryGetValue(imagePath, out var cached) ? cached : null;
        }

        /// <summary>由 ThumbnailScheduler 写入缓存（不触发 UI 回调）</summary>
        public static void WriteCache(string imagePath, ImageSource source)
        {
            if (string.IsNullOrWhiteSpace(imagePath) || source == null) return;
            _thumbnailCache[imagePath] = source;
        }

        // 异步加载指定文件的缩略图（走限量的并发信号量）。
        // 照片（JPG/HEIC）和视频分别走独立的信号量，互不阻塞。
        // 已缓存的直接返回，正在加载中的复用同一个 Task。
        // imagePath: 文件路径。
        // dispatcher: UI 线程调度器，用于在 UI 线程创建 BitmapImage。若为 null 则自动获取当前线程的。
        // 返回: 加载完成的 ImageSource，失败或取消返回 null。
        public static Task<ImageSource?> LoadAsync(string imagePath, Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return Task.FromResult<ImageSource?>(null);

            dispatcher ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher == null) return Task.FromResult<ImageSource?>(null);

            if (_thumbnailCache.TryGetValue(imagePath, out var cached))
            {
                return Task.FromResult<ImageSource?>(cached);
            }

            int version = Volatile.Read(ref _cacheVersion);

            return _inflightLoads.GetOrAdd(imagePath, path => LoadCoreAsync(path, dispatcher, version));
        }

        // 取消最早的排队中照片加载——信号量满时为新请求让位，保证可见区域优先。
        // excludePath: 调用者自身的路径，排除以免疫消自己。
        private static void CancelOldestPhotoLoad(string? excludePath = null)
        {
            lock (_photoCtsLock)
            {
                foreach (var kvp in _photoLoadCts)
                {
                    if (kvp.Key == excludePath) continue;
                    if (_photoLoadCts.TryRemove(kvp.Key, out var cts))
                    {
                        try { cts.Cancel(); } catch { }
                        // 不 Dispose —— 由持有此 CTS 的 Task 负责清理
                        return;
                    }
                }
                // dict 里只有自身 → 没有任何可取消的 → 调用者正常排队等待
            }
        }

        // 取消队列中等待的视频缩略图加载（已开始的 FFmpeg 不受影响）
        public static void CancelPendingVideoLoad(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return;
            if (_videoLoadCts.TryRemove(filePath, out var cts))
            {
                cts.Cancel();
                cts.Dispose();
            }
        }

        // 扫描阶段视频背景加载（简单 FIFO，无优先/取消逻辑）。
        // 与 UI 可见路径（LoadCoreAsync）共享 _videoLoadLimiter 和 _inflightLoads，不重复加载。
        public static void BackgroundVideoLoad(string videoPath, Microsoft.UI.Dispatching.DispatcherQueue? dispatcher)
        {
            if (string.IsNullOrWhiteSpace(videoPath)) return;
            if (_thumbnailCache.ContainsKey(videoPath)) return;
            dispatcher ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher == null) return;

            int version = Volatile.Read(ref _cacheVersion);
            _ = _inflightLoads.GetOrAdd(videoPath, path => RunBackgroundVideoLoadAsync(path, dispatcher, version));
        }

        private static async Task<ImageSource?> RunBackgroundVideoLoadAsync(string videoPath, Microsoft.UI.Dispatching.DispatcherQueue dispatcher, int version)
        {
            try
            {
                await _videoLoadLimiter.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_thumbnailCache.TryGetValue(videoPath, out var cached))
                        return cached;
                    return await LoadVideoThumbnailAsync(videoPath, dispatcher, version);
                }
                finally
                {
                    _videoLoadLimiter.Release();
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                _inflightLoads.TryRemove(videoPath, out _);
            }
        }

        public static void Preload(IEnumerable<string> imagePaths, Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
        {
            dispatcher ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher == null)
            {
                return;
            }

            foreach (var imagePath in imagePaths.Where(static path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _ = LoadAsync(imagePath, dispatcher);
            }
        }

        private static async Task<ImageSource?> LoadCoreAsync(string imagePath, Microsoft.UI.Dispatching.DispatcherQueue dispatcher, int version)
        {
            try
            {
                // 视频走独立信号量（不抢照片通道），支持取消排队
                if (IsVideoFile(imagePath))
                {
                    var cts = new CancellationTokenSource();
                    _videoLoadCts[imagePath] = cts;
                    bool acquired = false;

                    try
                    {
                        await _videoLoadLimiter.WaitAsync(cts.Token).ConfigureAwait(false);
                        acquired = true;
                        cts.Token.ThrowIfCancellationRequested();

                        if (_thumbnailCache.TryGetValue(imagePath, out var cached))
                            return cached;
                        return await LoadVideoThumbnailAsync(imagePath, dispatcher, version);
                    }
                    catch (OperationCanceledException)
                    {
                        return null;
                    }
                    finally
                    {
                        if (acquired) _videoLoadLimiter.Release();
                        _videoLoadCts.TryRemove(imagePath, out _);
                    }
                }

                // 照片 / HEIC 走共享快速信号量
                await _loadLimiter.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_thumbnailCache.TryGetValue(imagePath, out var cached))
                    {
                        return cached;
                    }

                    ImageSource? result = null;

                    if (HeicConverterService.IsHeicFile(imagePath))
                    {
                        result = await LoadHeicThumbnailAsync(imagePath, dispatcher, version);
                    }
                    else
                    {
                        // 普通照片：JPG/HEIC 走 Shell API（快），PNG/BMP 等跳过（Shell 返回白板）
                        if (HasReliableShellThumbnail(imagePath))
                        {
                            try
                            {
                                StorageFile file = await StorageFile.GetFileFromPathAsync(imagePath);
                                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.ListView, 80, ThumbnailOptions.UseCurrentScale);

                                if (thumbnail != null && thumbnail.Size > 0)
                                {
                                    var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);

                                    if (!dispatcher.TryEnqueue(async () =>
                                    {
                                        try
                                        {
                                            var bitmap = new BitmapImage();
                                            await bitmap.SetSourceAsync(thumbnail);

                                            if (version == Volatile.Read(ref _cacheVersion))
                                            {
                                                _thumbnailCache[imagePath] = bitmap;
                                                tcs.TrySetResult(bitmap);
                                            }
                                            else
                                            {
                                                tcs.TrySetResult(null);
                                            }
                                        }
                                        catch
                                        {
                                            tcs.TrySetResult(null);
                                        }
                                    }))
                                    {
                                        tcs.TrySetResult(null);
                                    }

                                    result = await tcs.Task.ConfigureAwait(false);
                                }
                            }
                            catch { }
                        }

                        // Shell 不可靠 / Shell 失败 → BitmapDecoder 兜底
                        if (result == null)
                        {
                            var (data, w, h) = await LoadBitmapDecoderThumbnailDataAsync(imagePath);
                            if (data is { Length: > 0 })
                            {
                                var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);
                                if (dispatcher.TryEnqueue(() =>
                                {
                                    try
                                    {
                                        var bitmap = new BitmapImage();
                                        using var ms = new MemoryStream(data);
                                        bitmap.SetSource(ms.AsRandomAccessStream());
                                        if (version == Volatile.Read(ref _cacheVersion))
                                        {
                                            _thumbnailCache[imagePath] = bitmap;
                                            tcs.TrySetResult(bitmap);
                                        }
                                        else
                                        {
                                            tcs.TrySetResult(null);
                                        }
                                    }
                                    catch
                                    {
                                        tcs.TrySetResult(null);
                                    }
                                }))
                                {
                                    result = await tcs.Task.ConfigureAwait(false);
                                }
                            }
                        }
                    }

                    return result;
                }
                finally
                {
                    _loadLimiter.Release();
                }
            }
            catch
            {
                return null;
            }
            finally
            {
                _inflightLoads.TryRemove(imagePath, out _);
            }
        }

        // 普通照片缩略图（JPG/PNG 等）：使用 Windows Shell API。
        private static async Task<ImageSource?> LoadPhotoThumbnailAsync(string imagePath, Microsoft.UI.Dispatching.DispatcherQueue dispatcher, int version)
        {
            try
            {
                StorageFile file = await StorageFile.GetFileFromPathAsync(imagePath);
                using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.ListView, 80, ThumbnailOptions.UseCurrentScale);

                if (thumbnail != null && thumbnail.Size > 0)
                {
                    var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);

                    if (!dispatcher.TryEnqueue(async () =>
                    {
                        try
                        {
                            var bitmap = new BitmapImage();
                            await bitmap.SetSourceAsync(thumbnail);

                            if (version == Volatile.Read(ref _cacheVersion))
                            {
                                _thumbnailCache[imagePath] = bitmap;
                                tcs.TrySetResult(bitmap);
                            }
                            else
                            {
                                tcs.TrySetResult(null);
                            }
                        }
                        catch
                        {
                            tcs.TrySetResult(null);
                        }
                    }))
                    {
                        tcs.TrySetResult(null);
                    }

                    return await tcs.Task.ConfigureAwait(false);
                }
            }
            catch
            {
            }

            return null;
        }

        private static async Task<ImageSource?> LoadHeicThumbnailAsync(string imagePath, Microsoft.UI.Dispatching.DispatcherQueue dispatcher, int version)
        {
            try
            {
                string tempJpegPath = Path.Combine(
                    Path.GetTempPath(),
                    $"thumb_{Guid.NewGuid():N}.jpg"
                );

                try
                {
                    StorageFile sourceFile = await StorageFile.GetFileFromPathAsync(imagePath);
                    using var inputStream = await sourceFile.OpenAsync(FileAccessMode.Read);
                    var decoder = await BitmapDecoder.CreateAsync(inputStream);

                    uint originalWidth = decoder.PixelWidth;
                    uint originalHeight = decoder.PixelHeight;

                    double scale = Math.Min(80.0 / originalWidth, 80.0 / originalHeight);
                    uint targetWidth, targetHeight;

                    if (scale >= 1.0)
                    {
                        targetWidth = originalWidth;
                        targetHeight = originalHeight;
                    }
                    else
                    {
                        targetWidth = (uint)Math.Max(1, originalWidth * scale);
                        targetHeight = (uint)Math.Max(1, originalHeight * scale);
                    }

                    // ▸▸▸ 解码阶段直接缩放到目标尺寸，不解码全分辨率（省几十 MB 内存）
                    var decodeTransform = new BitmapTransform
                    {
                        ScaledWidth = targetWidth,
                        ScaledHeight = targetHeight,
                        InterpolationMode = BitmapInterpolationMode.Fant
                    };
                    using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                        BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied,
                        decodeTransform, ExifOrientationMode.IgnoreExifOrientation,
                        ColorManagementMode.DoNotColorManage);

                    using (var fileStream = new FileStream(tempJpegPath, FileMode.Create, FileAccess.Write))
                    using (var randomAccessStream = fileStream.AsRandomAccessStream())
                    {
                        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, randomAccessStream);
                        encoder.SetSoftwareBitmap(softwareBitmap);
                        // 不用再设 BitmapTransform——已在解码阶段缩放过
                        await encoder.FlushAsync();
                    }

                    var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);

                    if (!dispatcher.TryEnqueue(() =>
                    {
                        try
                        {
                            var bitmapImage = new BitmapImage();
                            bitmapImage.DecodePixelWidth = (int)targetWidth;
                            bitmapImage.DecodePixelHeight = (int)targetHeight;

                            using var fileStream = new FileStream(tempJpegPath, FileMode.Open, FileAccess.Read);
                            bitmapImage.SetSource(fileStream.AsRandomAccessStream());

                            if (version == Volatile.Read(ref _cacheVersion))
                            {
                                _thumbnailCache[imagePath] = bitmapImage;
                                tcs.TrySetResult(bitmapImage);
                            }
                            else
                            {
                                tcs.TrySetResult(null);
                            }
                        }
                        catch (Exception ex)
                        {
                            LogService.Merge($"HEIC thumbnail load error: {ex.Message}", LogLevel.Warning, ex);
                            tcs.TrySetResult(null);
                        }
                    }))
                    {
                        tcs.TrySetResult(null);
                    }

                    return await tcs.Task.ConfigureAwait(false);
                }
                finally
                {
                    try { File.Delete(tempJpegPath); } catch { }
                }
            }
            catch (Exception ex)
            {
                LogService.Merge($"HEIC thumbnail decode error: {ex.Message}", LogLevel.Warning, ex);
                return null;
            }
        }

        // 视频缩略图提取：使用 FFmpeg 抽取第一帧作为缩略图，
        // 避免 Windows Shell API 返回应用图标的问题。
        // 根据用户设置中选中的显卡自动添加硬件加速。
        private static async Task<ImageSource?> LoadVideoThumbnailAsync(string videoPath, Microsoft.UI.Dispatching.DispatcherQueue dispatcher, int version)
        {
            string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
                return null;

            string tempJpeg = Path.Combine(Path.GetTempPath(), $"lpb_vthumb_{Guid.NewGuid():N}.jpg");

            try
            {
                string hwaccel = GetVideoHwAccelFlag();
                string args = string.IsNullOrEmpty(hwaccel)
                    ? $"-i \"{videoPath}\" -vframes 1 -vf \"scale=80:-1:force_original_aspect_ratio=decrease\" -q:v 2 \"{tempJpeg}\" -y -loglevel error"
                    : $"{hwaccel} -i \"{videoPath}\" -vframes 1 -vf \"scale=80:-1:force_original_aspect_ratio=decrease\" -q:v 2 \"{tempJpeg}\" -y -loglevel error";

                var run = await ExternalToolProcessGuard.RunAsync(
                    () => new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true
                    },
                    timeout: TimeSpan.FromSeconds(20),
                    operation: $"legacy video thumbnail: {Path.GetFileName(videoPath)}",
                    prepareAttempt: _ =>
                    {
                        try { File.Delete(tempJpeg); } catch { }
                    }).ConfigureAwait(false);

                if (!run.IsSuccess || !File.Exists(tempJpeg) || new FileInfo(tempJpeg).Length == 0)
                    return null;

                var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);

                if (!dispatcher.TryEnqueue(() =>
                {
                    try
                    {
                        var bitmap = new BitmapImage();
                        bitmap.DecodePixelWidth = 80;
                        using var fs = new FileStream(tempJpeg, FileMode.Open, FileAccess.Read);
                        bitmap.SetSource(fs.AsRandomAccessStream());

                        if (version == Volatile.Read(ref _cacheVersion))
                        {
                            _thumbnailCache[videoPath] = bitmap;
                            tcs.TrySetResult(bitmap);
                        }
                        else
                        {
                            tcs.TrySetResult(null);
                        }
                    }
                    catch
                    {
                        tcs.TrySetResult(null);
                    }
                }))
                {
                    tcs.TrySetResult(null);
                }

                return await tcs.Task.ConfigureAwait(false);
            }
            catch
            {
                return null;
            }
            finally
            {
                try { File.Delete(tempJpeg); } catch { }
            }
        }

        // 根据用户设置中的硬件编码器获取 FFmpeg 硬件加速解码标志。
        // 抽帧是解码操作，用对应的 hwaccel 可大幅提升 HEVC/高码率视频速度。
        private static string GetVideoHwAccelFlag()
        {
            try
            {
                string encoder = AppSettingsService.GetValue("SplitHardwareEncoder", "") ?? "";
                if (string.IsNullOrEmpty(encoder)) return "";

                // 从编码器名推断硬件加速类型
                if (encoder.Contains("nvenc", StringComparison.OrdinalIgnoreCase))
                    return "-hwaccel cuda";
                if (encoder.Contains("qsv", StringComparison.OrdinalIgnoreCase))
                    return "-hwaccel qsv";
                if (encoder.Contains("amf", StringComparison.OrdinalIgnoreCase))
                    return "-hwaccel d3d11va";
                if (encoder.Contains("vaapi", StringComparison.OrdinalIgnoreCase))
                    return "-hwaccel vaapi";
                return "";
            }
            catch
            {
                return "";
            }
        }

        // 清空所有缩略图缓存并递增版本号，使进行中的旧版本加载结果被丢弃。
        public static void ClearCache()
        {
            _thumbnailCache.Clear();
            _inflightLoads.Clear();
            _failCounts.Clear();
            _tryGetOrLoadInFlight.Clear();
            Interlocked.Increment(ref _cacheVersion);
        }

        private static bool IsVideoFile(string path) =>
            path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase);

        // 公开给外部判断视频文件
        public static bool IsVideoFilePath(string path) => IsVideoFile(path);

        /// <summary>
        /// 判断该图片格式的 Windows Shell 缩略图是否可靠。
        /// JPG/HEIC 有系统级缩略图缓存，可靠；PNG/BMP/GIF 等可能返回白板图标，需跳过。
        /// </summary>
        private static bool HasReliableShellThumbnail(string path) =>
            !(path.EndsWith(".png", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".bmp", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".gif", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".tiff", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".tif", StringComparison.OrdinalIgnoreCase)
           || path.EndsWith(".webp", StringComparison.OrdinalIgnoreCase));

        //  ------  x:Bind property-getter support  ------

        /// <summary>获取当前窗口的 DPI 缩放比例（线程安全：仅 UI 线程调用）</summary>
        private static double GetDpiScale()
        {
            try
            {
                var xamlRoot = App.MainWindow?.Content?.XamlRoot;
                if (xamlRoot != null)
                    return xamlRoot.RasterizationScale;
            }
            catch { }
            return 1.0;
        }

        /// <summary>
        /// 检查指定路径是否已有进行中的加载（在 _photoLoadCts / _videoLoadCts / _inflightLoads /
        /// _tryGetOrLoadInFlight 中）。替代之前由调用方管理的 ref bool isLoading，
        /// 因为内部字典有 finally 保证清理，不会被卡住。
        /// </summary>
        private static bool IsBeingLoaded(string path) =>
            _photoLoadCts.ContainsKey(path) ||
            _videoLoadCts.ContainsKey(path) ||
            _inflightLoads.ContainsKey(path) ||
            _tryGetOrLoadInFlight.ContainsKey(path);

        // 供 x:Bind 属性 getter 使用，非异步：返回缓存或触发后台加载。
        // targetSize：缩略图逻辑像素（长边），默认 80，会和 DPI 缩放相乘得到实际解码像素。
        // 例如 KeyPhoto 框 56×56，200% DPI → 实际解码 112px，保证高清不糊。
        public static ImageSource? TryGetOrLoad(
            string? imagePath,
            Action<ImageSource?> assignThumbnail,
            uint targetSize = 80)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return null;

            // 先查全局缓存——避免重复解码（尤其 HEIC，单次解码耗时 100-300ms）
            if (_thumbnailCache.TryGetValue(imagePath, out var cached))
            {
                assignThumbnail(cached);
                return cached;
            }

            // 已有进行中的加载 → 不重复触发。内部字典在 finally 块中必然清理，不会永久卡住。
            if (IsBeingLoaded(imagePath)) return null;

            var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            var path = imagePath;
            double dpiScale = GetDpiScale();
            uint decodeSize = (uint)Math.Max(1, targetSize * dpiScale);

            // 视频：无法提前知道宽高比，1.5× 兜底，后续可加 ffprobe 取真实尺寸
            uint videoDecodeSize = (uint)(decodeSize * 1.5);

            _ = System.Threading.Tasks.Task.Run(async () =>
            {
                // 视频走独立慢速信号量，不阻塞照片加载
                if (IsVideoFile(path))
                {
                    // 注册到 _tryGetOrLoadInFlight（独立于 _videoLoadCts 的取消体系），防止重复加载
                    _tryGetOrLoadInFlight[path] = 0;
                    try
                    {
                        await _videoLoadLimiter.WaitAsync();
                        try
                        {
                            if (_thumbnailCache.TryGetValue(path, out var vidCached))
                            {
                                _ = dispatcher?.TryEnqueue(() => assignThumbnail(vidCached));
                                return;
                            }
                            var (data, w, h) = await LoadVideoThumbnailDataAsync(path, videoDecodeSize);
                            if (data is { Length: > 0 } && dispatcher != null)
                            {
                                _ = dispatcher.TryEnqueue(() =>
                                {
                                    try
                                    {
                                        var bmp = new BitmapImage();
                                        bmp.DecodePixelWidth = (int)videoDecodeSize;
                                        using var ms = new MemoryStream(data);
                                        bmp.SetSource(ms.AsRandomAccessStream());
                                        _thumbnailCache[path] = bmp;
                                        assignThumbnail(bmp);
                                    }
                                    catch
                                    {
                                        // SetSource 失败 → 回调 null 以触发 UI 刷新，后续 getter 会自动重试
                                        assignThumbnail(null);
                                    }
                                });
                            }
                            else
                            {
                                _ = dispatcher?.TryEnqueue(() => assignThumbnail(null));
                            }
                        }
                        finally { _videoLoadLimiter.Release(); }
                    }
                    finally
                    {
                        _tryGetOrLoadInFlight.TryRemove(path, out _);
                    }
                    return;
                }

                // ── 照片路径 ──
                // 注册可取消令牌：快速滚动时新请求会取消最旧的排队请求
                _tryGetOrLoadInFlight[path] = 0;
                var photoCts = new CancellationTokenSource();
                _photoLoadCts[path] = photoCts;

                try // 外层 finally — 无论成功/失败/取消，都清理追踪
                {
                    // ── 阶段 1：获取信号量 ──
                    try
                    {
                        // 信号量满 → 取消最早排队项，保证可见区域优先（排除自身）
                        if (_loadLimiter.CurrentCount == 0)
                            CancelOldestPhotoLoad(path);
                        await _loadLimiter.WaitAsync(photoCts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        _ = dispatcher?.TryEnqueue(() => assignThumbnail(null));
                        return; // 外层 finally 仍然执行
                    }

                    // ── 阶段 2：加载和解码 ──
                    try
                    {
                        // 双重检查：等信号量期间可能已被其他请求加载
                        if (_thumbnailCache.TryGetValue(path, out var photoCached))
                        {
                            _ = dispatcher?.TryEnqueue(() => assignThumbnail(photoCached));
                            return;
                        }

                        byte[]? imageData = null;
                        int width = (int)decodeSize;
                        int height = (int)decodeSize;

                        try
                        {
                            if (HeicConverterService.IsHeicFile(path))
                                (imageData, width, height) = await LoadHeicMagickThumbnailAsync(path, decodeSize);
                            else
                                (imageData, width, height) = await LoadSystemThumbnailDataAsync(path, decodeSize);
                        }
                        catch
                        {
                        }

                        if (imageData != null && imageData.Length > 0 && dispatcher != null)
                        {
                            _ = dispatcher.TryEnqueue(() =>
                            {
                                try
                                {
                                    var bitmapImage = new BitmapImage();
                                    var stream = new MemoryStream(imageData);
                                    bitmapImage.SetSource(stream.AsRandomAccessStream());
                                    _thumbnailCache[path] = bitmapImage;
                                    _failCounts.TryRemove(path, out _); // 加载成功 → 清零失败计数
                                    assignThumbnail(bitmapImage);
                                }
                                catch
                                {
                                    // SetSource 失败 → 回调 null 触发重试
                                    assignThumbnail(null);
                                }
                            });
                        }
                        else
                        {
                            // 加载失败 → 检查重试次数，防止损坏文件无限循环
                            int fails = _failCounts.AddOrUpdate(path, 1, (_, c) => c + 1);
                            if (fails <= MaxFailRetries)
                            {
                                _ = dispatcher?.TryEnqueue(() => assignThumbnail(null));
                            }
                            // 超过 MaxFailRetries 次 → 静默放弃，不再重试
                        }
                    }
                    finally
                    {
                        _loadLimiter.Release();
                    }
                }
                finally
                {
                    _photoLoadCts.TryRemove(path, out _);
                    photoCts.Dispose();
                    _tryGetOrLoadInFlight.TryRemove(path, out _);
                }
            });

            return null;
        }

        // 判断缩略图占位符的可见性：缩略图未加载时显示占位符，加载后隐藏。
        // 用于 x:Bind 绑定。
        public static Visibility GetPlaceholderVisibility(ImageSource? thumbnail)
            => thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        private static async Task<(byte[] data, int width, int height)> LoadHeicThumbnailDataAsync(string imagePath, uint targetSize = 80)
        {
            // HEIC 缩略图优先走 Windows Shell API（Explorer 缓存，已正确处理旋转，瞬时返回）
            // 回退：BitmapDecoder 全解码 + 缩放（Shell 缓存未命中时）
            return await LoadSystemThumbnailDataAsync(imagePath, targetSize);
        }

        /// <summary>
        /// HEIC 缩略图解码。委托给 ThumbnailProviderFactory 当前选中的提供者，
        /// 支持在 Magick.NET / MagicScaler 之间切换对比。
        /// </summary>
        private static Task<(byte[] data, int width, int height)> LoadHeicMagickThumbnailAsync(
            string imagePath, uint targetSize)
            => ThumbnailProviderFactory.Current.LoadHeicThumbnailAsync(imagePath, targetSize);

        internal static async Task<(byte[] data, int width, int height)> LoadSystemThumbnailDataAsync(string imagePath, uint targetSize = 80)
        {
            // PNG/BMP 等格式的 Shell 缩略图会返回白板图标 → 直接走自解码
            if (HasReliableShellThumbnail(imagePath))
            {
                try
                {
                    var file = await StorageFile.GetFileFromPathAsync(imagePath);
                    using var thumb = await file.GetThumbnailAsync(ThumbnailMode.ListView, targetSize, ThumbnailOptions.UseCurrentScale);

                    if (thumb != null && thumb.Size > 0)
                    {
                        var thumbCopy = new MemoryStream();
                        await thumb.AsStream().CopyToAsync(thumbCopy);
                        var data = thumbCopy.ToArray();
                        if (data.Length > 0)
                            return (data, (int)targetSize, (int)targetSize);
                    }
                }
                catch
                {
                    // Shell 缩略图 API 不可用 → 走 BitmapDecoder 兜底
                }
            }

            // 兜底：直接用 BitmapDecoder 解码并缩放（PNG/BMP/GIF/TIFF/WebP 等）
            return await LoadBitmapDecoderThumbnailDataAsync(imagePath, targetSize);
        }

        /// <summary>
        /// BitmapDecoder 兜底缩略图：适用于 Shell API 无法提供缩略图的格式（PNG/BMP/GIF/TIFF 等）。
        /// 使用 GetPixelDataAsync 直接解码到目标尺寸（老 API，兼容性好）。
        /// </summary>
        private static async Task<(byte[] data, int width, int height)> LoadBitmapDecoderThumbnailDataAsync(string imagePath, uint targetSize = 80)
        {
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(imagePath);
                using var inputStream = await file.OpenAsync(FileAccessMode.Read);
                var decoder = await BitmapDecoder.CreateAsync(inputStream);

                uint w = decoder.PixelWidth;
                uint h = decoder.PixelHeight;

                // 以短边为准缩放：正方形 UniformToFill 框里短边决定锐度
                uint shorterEdge = Math.Min(w, h);
                double scale = (double)targetSize / shorterEdge;
                uint targetWidth = Math.Max(1, (uint)(w * scale));
                uint targetHeight = Math.Max(1, (uint)(h * scale));

                // 用 GetPixelDataAsync（老 API）直接获取缩放后的像素，避开 GetSoftwareBitmapAsync
                // 5 参数重载在某些格式（PNG）上不稳定
                var decodeTransform = new BitmapTransform
                {
                    ScaledWidth = targetWidth,
                    ScaledHeight = targetHeight,
                    InterpolationMode = BitmapInterpolationMode.Fant
                };
                var pixelData = await decoder.GetPixelDataAsync(
                    BitmapPixelFormat.Bgra8,
                    BitmapAlphaMode.Straight,
                    decodeTransform,
                    ExifOrientationMode.IgnoreExifOrientation,
                    ColorManagementMode.DoNotColorManage);

                // 用 SetPixelData 直接写入已缩放的像素（无需 SoftwareBitmap 中转）
                var outputStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
                var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream);
                encoder.SetPixelData(
                    BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore,
                    targetWidth, targetHeight, 96, 96,
                    pixelData.DetachPixelData());
                await encoder.FlushAsync();

                outputStream.Seek(0);
                using var reader = new Windows.Storage.Streams.DataReader(outputStream);
                var buffer = new byte[outputStream.Size];
                await reader.LoadAsync((uint)outputStream.Size);
                reader.ReadBytes(buffer);

                return (buffer, (int)targetWidth, (int)targetHeight);
            }
            catch
            {
                return (Array.Empty<byte>(), 0, 0);
            }
        }

        // 视频缩略图数据提取（用于 x:Bind 路径）：使用 FFmpeg 抽取第一帧。
        internal static async Task<(byte[] data, int width, int height)> LoadVideoThumbnailDataAsync(
            string videoPath,
            uint targetSize = 80,
            CancellationToken cancellationToken = default)
        {
            string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
                return (Array.Empty<byte>(), 0, 0);

            string tempJpeg = Path.Combine(Path.GetTempPath(), $"lpb_vthumb_{Guid.NewGuid():N}.jpg");

            try
            {
                // 缩略图只抽一帧，CPU 解码快且稳定；GPU hwaccel 可能触发 nvcuda64.dll 访问冲突
                string args = $"-i \"{videoPath}\" -vframes 1 -vf \"scale={targetSize}:-1:force_original_aspect_ratio=decrease\" -q:v 2 \"{tempJpeg}\" -y -loglevel error";

                var run = await ExternalToolProcessGuard.RunAsync(
                    () => new ProcessStartInfo
                    {
                        FileName = ffmpegPath,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        ErrorDialog = false
                    },
                    timeout: TimeSpan.FromSeconds(20),
                    operation: $"video thumbnail: {Path.GetFileName(videoPath)}",
                    cancellationToken,
                    prepareAttempt: _ =>
                    {
                        try { File.Delete(tempJpeg); } catch { }
                    }).ConfigureAwait(false);

                if (!run.IsSuccess || !File.Exists(tempJpeg))
                    return (Array.Empty<byte>(), 0, 0);

                var fileInfo = new FileInfo(tempJpeg);
                if (fileInfo.Length == 0)
                    return (Array.Empty<byte>(), 0, 0);

                byte[] imageData = await File.ReadAllBytesAsync(tempJpeg);
                return (imageData, 80, 80);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch
            {
                return (Array.Empty<byte>(), 0, 0);
            }
            finally
            {
                try { File.Delete(tempJpeg); } catch { }
            }
        }
    }
}
