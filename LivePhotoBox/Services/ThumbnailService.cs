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
    // 缩略图服务 — 为文件列表提供异步缩略图加载与缓存。
    // 支持三种来源：
    // - 普通图片（JPG/PNG）：Windows Shell API (StorageFile.GetThumbnailAsync)
    // - HEIC 图片：通过 BitmapDecoder 解码并缩放到 80px
    // - 视频（MOV/MP4）：FFmpeg 抽第一帧，支持硬件加速解码
    // 使用两级缓存（_thumbnailCache + _inflightLoads）防止重复加载，
    // 并用 SemaphoreSlim 限制并发数（照片 4 路，视频根据硬件自动调整）。
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
        private static int _cacheVersion;

        // 从缓存中直接获取已加载的缩略图（同步，非阻塞）。
        // imagePath: 文件路径。
        // è¿å: 缓存的 ImageSource，若尚未加载则返回 null。
        public static ImageSource? GetCached(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            return _thumbnailCache.TryGetValue(imagePath, out var cached) ? cached : null;
        }

        // 异步加载指定文件的缩略图（走限量的并发信号量）。
        // 照片（JPG/HEIC）和视频分别走独立的信号量，互不阻塞。
        // 已缓存的直接返回，正在加载中的复用同一个 Task。
        // imagePath: 文件路径。
        // dispatcher: UI 线程调度器，用于在 UI 线程创建 BitmapImage。若为 null 则自动获取当前线程的。
        // è¿å: 加载完成的 ImageSource，失败或取消返回 null。
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
                        // 普通照片（JPG/PNG 等）— 保持原有内联逻辑不变
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

                    using var softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

                    using (var fileStream = new FileStream(tempJpegPath, FileMode.Create, FileAccess.Write))
                    using (var randomAccessStream = fileStream.AsRandomAccessStream())
                    {
                        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, randomAccessStream);
                        encoder.SetSoftwareBitmap(softwareBitmap);
                        if (targetWidth != originalWidth || targetHeight != originalHeight)
                        {
                            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
                            encoder.BitmapTransform.ScaledWidth = targetWidth;
                            encoder.BitmapTransform.ScaledHeight = targetHeight;
                        }
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

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                // 等待 FFmpeg 完成，带超时保护（大视频/慢速解码放宽到 30 秒）
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch { }
                    return null;
                }

                if (process.ExitCode != 0 || !File.Exists(tempJpeg) || new FileInfo(tempJpeg).Length == 0)
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
            Interlocked.Increment(ref _cacheVersion);
        }

        private static bool IsVideoFile(string path) =>
            path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase);

        // 公开给外部判断视频文件
        public static bool IsVideoFilePath(string path) => IsVideoFile(path);

        //  ------  x:Bind property-getter support  ------

        // For x:Bind property getter usage. Non-async, returns cached or triggers background load.
        public static ImageSource? TryGetOrLoad(
            ref ImageSource? thumbnail,
            ref bool isLoading,
            string? imagePath,
            Action<ImageSource?> assignThumbnail)
        {
            if (thumbnail == null && !isLoading && !string.IsNullOrWhiteSpace(imagePath))
            {
                isLoading = true;
                var dispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
                var path = imagePath;

                _ = System.Threading.Tasks.Task.Run(async () =>
                {
                    // 视频走独立慢速信号量，不阻塞照片加载
                    if (IsVideoFile(path))
                    {
                        await _videoLoadLimiter.WaitAsync();
                        try
                        {
                            var (data, w, h) = await LoadVideoThumbnailDataAsync(path);
                            if (data is { Length: > 0 } && dispatcher != null)
                            {
                                dispatcher.TryEnqueue(() =>
                                {
                                    try
                                    {
                                        var bmp = new BitmapImage();
                                        bmp.DecodePixelWidth = 80;
                                        using var ms = new MemoryStream(data);
                                        bmp.SetSource(ms.AsRandomAccessStream());
                                        assignThumbnail(bmp);
                                    }
                                    catch { }
                                });
                            }
                        }
                        finally
                        {
                            _videoLoadLimiter.Release();
                        }
                        return;
                    }

                    await _loadLimiter.WaitAsync();

                    try
                    {
                        byte[]? imageData = null;
                        int width = 80;
                        int height = 80;

                        try
                        {
                            if (HeicConverterService.IsHeicFile(path))
                            {
                                (imageData, width, height) = await LoadHeicThumbnailDataAsync(path);
                            }
                            else
                            {
                                (imageData, width, height) = await LoadSystemThumbnailDataAsync(path);
                            }
                        }
                        catch
                        {
                        }

                        if (imageData != null && imageData.Length > 0 && dispatcher != null)
                        {
                            dispatcher.TryEnqueue(() =>
                            {
                                try
                                {
                                    var bitmapImage = new BitmapImage();
                                    var stream = new MemoryStream(imageData);
                                    bitmapImage.SetSource(stream.AsRandomAccessStream());
                                    assignThumbnail(bitmapImage);
                                }
                                catch
                                {
                                }
                            });
                        }
                    }
                    finally
                    {
                        _loadLimiter.Release();
                    }
                });
            }

            return thumbnail;
        }

        // 判断缩略图占位符的可见性：缩略图未加载时显示占位符，加载后隐藏。
        // 用于 x:Bind 绑定。
        public static Visibility GetPlaceholderVisibility(ImageSource? thumbnail)
            => thumbnail == null ? Visibility.Visible : Visibility.Collapsed;

        private static async Task<(byte[] data, int width, int height)> LoadHeicThumbnailDataAsync(string imagePath)
        {
            var file = await StorageFile.GetFileFromPathAsync(imagePath);
            using var inputStream = await file.OpenAsync(FileAccessMode.Read);

            var decoder = await BitmapDecoder.CreateAsync(inputStream);
            uint w = decoder.PixelWidth;
            uint h = decoder.PixelHeight;

            uint targetSize = 80;
            double scale = Math.Min((double)targetSize / w, (double)targetSize / h);
            uint targetWidth = Math.Max(1, (uint)(w * scale));
            uint targetHeight = Math.Max(1, (uint)(h * scale));

            var softwareBitmap = await decoder.GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Premultiplied);

            var outputStream = new Windows.Storage.Streams.InMemoryRandomAccessStream();
            var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.JpegEncoderId, outputStream);
            encoder.SetSoftwareBitmap(softwareBitmap);
            encoder.BitmapTransform.ScaledWidth = targetWidth;
            encoder.BitmapTransform.ScaledHeight = targetHeight;
            encoder.BitmapTransform.InterpolationMode = BitmapInterpolationMode.Fant;
            await encoder.FlushAsync();

            outputStream.Seek(0);
            using var reader = new Windows.Storage.Streams.DataReader(outputStream);
            var buffer = new byte[outputStream.Size];
            await reader.LoadAsync((uint)outputStream.Size);
            reader.ReadBytes(buffer);

            softwareBitmap.Dispose();

            return (buffer, (int)targetWidth, (int)targetHeight);
        }

        private static async Task<(byte[] data, int width, int height)> LoadSystemThumbnailDataAsync(string imagePath)
        {
            var file = await StorageFile.GetFileFromPathAsync(imagePath);
            using var thumb = await file.GetThumbnailAsync(ThumbnailMode.ListView, 80, ThumbnailOptions.UseCurrentScale);

            if (thumb != null && thumb.Size > 0)
            {
                var thumbCopy = new MemoryStream();
                await thumb.AsStream().CopyToAsync(thumbCopy);
                return (thumbCopy.ToArray(), 80, 80);
            }

            return (Array.Empty<byte>(), 0, 0);
        }

        // 视频缩略图数据提取（用于 x:Bind 路径）：使用 FFmpeg 抽取第一帧。
        private static async Task<(byte[] data, int width, int height)> LoadVideoThumbnailDataAsync(string videoPath)
        {
            string? ffmpegPath = ExternalToolLocator.FindFFmpeg();
            if (string.IsNullOrEmpty(ffmpegPath) || !File.Exists(ffmpegPath))
                return (Array.Empty<byte>(), 0, 0);

            string tempJpeg = Path.Combine(Path.GetTempPath(), $"lpb_vthumb_{Guid.NewGuid():N}.jpg");

            try
            {
                string hwaccel = GetVideoHwAccelFlag();
                string args = string.IsNullOrEmpty(hwaccel)
                    ? $"-i \"{videoPath}\" -vframes 1 -vf \"scale=80:-1:force_original_aspect_ratio=decrease\" -q:v 2 \"{tempJpeg}\" -y -loglevel error"
                    : $"{hwaccel} -i \"{videoPath}\" -vframes 1 -vf \"scale=80:-1:force_original_aspect_ratio=decrease\" -q:v 2 \"{tempJpeg}\" -y -loglevel error";

                var psi = new ProcessStartInfo
                {
                    FileName = ffmpegPath,
                    Arguments = args,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardError = true
                };

                using var process = new Process { StartInfo = psi };
                process.Start();

                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(30));
                try
                {
                    await process.WaitForExitAsync(cts.Token);
                }
                catch (OperationCanceledException)
                {
                    try { process.Kill(); } catch { }
                    return (Array.Empty<byte>(), 0, 0);
                }

                if (process.ExitCode != 0 || !File.Exists(tempJpeg))
                    return (Array.Empty<byte>(), 0, 0);

                var fileInfo = new FileInfo(tempJpeg);
                if (fileInfo.Length == 0)
                    return (Array.Empty<byte>(), 0, 0);

                byte[] imageData = await File.ReadAllBytesAsync(tempJpeg);
                return (imageData, 80, 80);
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