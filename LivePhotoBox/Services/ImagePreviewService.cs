using LivePhotoBox.Models;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Windows.Graphics.Imaging;
using Windows.Storage;

namespace LivePhotoBox.Services
{
    // 统一图片预览服务 — 所有预览样式共用同一套优化加载逻辑。
    // 特性：LRU 内存缓存 + DecodePixelWidth 解码限制 + 相邻预加载 + 令牌取消防拥堵。
    public sealed class ImagePreviewService
    {
        private readonly int _maxCacheSize;
        private readonly int _decodePixelWidth;
        private readonly int _preloadForward;
        private readonly int _preloadBackward;
        private readonly Dictionary<string, CachedEntry> _cache = new();
        private readonly LinkedList<string> _lruOrder = new();
        private readonly object _cacheLock = new();

        // 🔴 新增：用于随时掐断旧的预加载任务，防止后台拥堵
        private CancellationTokenSource? _preloadCts;

        private static int _heicConcurrencyCache;
        private static SemaphoreSlim _heicSemaphore = new(8, 8);

        private static SemaphoreSlim HeicSemaphore
        {
            get
            {
                int setting = AppSettingsService.GetValue("HeicConcurrency", 8);
                if (setting != Volatile.Read(ref _heicConcurrencyCache))
                {
                    var newSem = new SemaphoreSlim(setting, setting);
                    Interlocked.Exchange(ref _heicSemaphore, newSem);
                    Volatile.Write(ref _heicConcurrencyCache, setting);
                }
                return _heicSemaphore;
            }
        }

        private static readonly SemaphoreSlim _prioritySemaphore = new(1, 1);

        private record CachedEntry(ImageSource Image);

        public ImagePreviewService(int maxCacheSize = 20, int decodePixelWidth = 1920,
            int preloadForward = 6, int preloadBackward = 2)
        {
            _maxCacheSize = maxCacheSize;
            _decodePixelWidth = decodePixelWidth;
            _preloadForward = preloadForward;
            _preloadBackward = preloadBackward;
        }

        // 🔴 增加了 CancellationToken 参数
        public Task<ImageSource?> LoadAsync(string filePath, CancellationToken token = default)
            => LoadInternalAsync(filePath, false, token);

        public Task<ImageSource?> LoadCurrentAsync(string filePath, CancellationToken token = default)
            => LoadInternalAsync(filePath, true, token);

        private async Task<ImageSource?> LoadInternalAsync(string filePath, bool usePriority, CancellationToken token)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            lock (_cacheLock)
            {
                if (_cache.TryGetValue(filePath, out var cached))
                {
                    if (_lruOrder.First?.Value != filePath)
                    {
                        _lruOrder.Remove(filePath);
                        _lruOrder.AddFirst(filePath);
                    }
                    return cached.Image;
                }
            }

            try
            {
                ImageSource? image;

                if (IsHeicFile(filePath))
                {
                    image = await LoadHeicPreviewAsync(filePath, usePriority, token);
                }
                else
                {
                    // 标准图片加载，加入对 token 的敏感响应
                    var file = await StorageFile.GetFileFromPathAsync(filePath).AsTask(token);
                    if (token.IsCancellationRequested) return null;

                    var bitmap = new BitmapImage();
                    if (_decodePixelWidth > 0)
                        bitmap.DecodePixelWidth = _decodePixelWidth;

                    using (var stream = await file.OpenReadAsync().AsTask(token))
                    {
                        if (token.IsCancellationRequested) return null;
                        await bitmap.SetSourceAsync(stream);
                    }
                    image = bitmap;
                }

                if (image == null || token.IsCancellationRequested) return null;

                lock (_cacheLock)
                {
                    _cache[filePath] = new CachedEntry(image);
                    _lruOrder.AddFirst(filePath);
                    while (_cache.Count > _maxCacheSize)
                    {
                        var last = _lruOrder.Last;
                        if (last == null) break;
                        _cache.Remove(last.Value);
                        _lruOrder.RemoveLast();
                    }
                }

                LogService.Debug($"ImagePreviewService loaded: {Path.GetFileName(filePath)} (cache={_cache.Count})", LogSource.UI);
                return image;
            }
            catch (OperationCanceledException)
            {
                // 🔴 任务被成功打断，静默退出
                return null;
            }
            catch (Exception ex)
            {
                LogService.Debug($"ImagePreviewService load failed: {ex.Message}", LogSource.UI);
                return null;
            }
        }

        private static bool IsHeicFile(string path) =>
            path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);

        private async Task<ImageSource?> LoadHeicPreviewAsync(string filePath, bool usePriority, CancellationToken token)
        {
            var semaphore = usePriority ? _prioritySemaphore : HeicSemaphore;

            // 🔴 核心奥义：如果在排队等候通道期间，用户划走了，这里会瞬间抛出异常离开队伍，绝不干占茅坑！
            await semaphore.WaitAsync(token);
            try
            {
                string? tempJpegPath = null;
                try
                {
                    tempJpegPath = await Task.Run(async () =>
                    {
                        // 任务开始前检查一次
                        if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();

                        var file = await StorageFile.GetFileFromPathAsync(filePath).AsTask(token);
                        using var inputStream = await file.OpenReadAsync().AsTask(token);
                        var decoder = await BitmapDecoder.CreateAsync(inputStream);

                        // 耗时操作前检查一次
                        if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();

                        var transform = new BitmapTransform
                        {
                            InterpolationMode = BitmapInterpolationMode.Fant
                        };

                        if (_decodePixelWidth > 0 && decoder.PixelWidth > _decodePixelWidth)
                        {
                            double scale = (double)_decodePixelWidth / decoder.PixelWidth;
                            transform.ScaledWidth = (uint)_decodePixelWidth;
                            transform.ScaledHeight = (uint)Math.Max(1, decoder.PixelHeight * scale);
                        }

                        var softwareBitmap = await decoder.GetSoftwareBitmapAsync(
                            BitmapPixelFormat.Bgra8,
                            BitmapAlphaMode.Premultiplied,
                            transform,
                            ExifOrientationMode.RespectExifOrientation,
                            ColorManagementMode.ColorManageToSRgb);

                        // 写入文件前检查一次
                        if (token.IsCancellationRequested) token.ThrowIfCancellationRequested();

                        string tempPath = Path.Combine(Path.GetTempPath(), $"lpb_prev_{Guid.NewGuid():N}.jpg");
                        using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                        {
                            var encoder = await BitmapEncoder.CreateAsync(
                                BitmapEncoder.JpegEncoderId, fileStream.AsRandomAccessStream());
                            encoder.SetSoftwareBitmap(softwareBitmap);
                            await encoder.FlushAsync();
                        }

                        return tempPath;
                    }, token); // 传入 token 供后台 Task 调度器使用

                    if (token.IsCancellationRequested) return null;

                    var bitmap = new BitmapImage();
                    using (var fileStream = new FileStream(tempJpegPath, FileMode.Open, FileAccess.Read))
                    {
                        await bitmap.SetSourceAsync(fileStream.AsRandomAccessStream());
                    }

                    return bitmap;
                }
                finally
                {
                    if (tempJpegPath != null)
                    {
                        try { File.Delete(tempJpegPath); } catch { }
                    }
                }
            }
            finally
            {
                semaphore.Release();
            }
        }

        public void PreloadNeighbors(IReadOnlyList<string> allPaths, int centerIndex, int direction)
        {
            // 🔴 核心优化：每次预加载前，直接掐断上一次没完成的预加载。
            // 保证你快速滚动时，那些被错过的甜点区图片立马停止下载/解码，为新图片让出算力
            _preloadCts?.Cancel();
            _preloadCts = new CancellationTokenSource();
            var token = _preloadCts.Token;

            int forward = direction > 0 ? _preloadForward : _preloadBackward;
            int backward = direction > 0 ? _preloadBackward : _preloadForward;

            int start = Math.Max(0, centerIndex - backward);
            int end = Math.Min(allPaths.Count - 1, centerIndex + forward);

            for (int i = start; i <= end; i++)
            {
                if (i == centerIndex) continue;
                var path = allPaths[i];

                bool shouldLoad;
                lock (_cacheLock) { shouldLoad = !_cache.ContainsKey(path); }
                if (shouldLoad)
                    _ = LoadAsync(path, token); // 把预加载专属令牌传进去
            }
        }

        public void Clear()
        {
            _preloadCts?.Cancel();
            _cache.Clear();
            _lruOrder.Clear();
        }
    }
}