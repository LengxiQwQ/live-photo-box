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
    // 特性：LRU 内存缓存 + DecodePixelWidth 解码限制 + 相邻预加载。
    public sealed class ImagePreviewService
    {
        private readonly int _maxCacheSize;
        private readonly int _decodePixelWidth;
        private readonly int _preloadForward;
        private readonly int _preloadBackward;
        private readonly Dictionary<string, CachedEntry> _cache = new();
        private readonly LinkedList<string> _lruOrder = new();
        private readonly object _cacheLock = new();

        // HEIC 解码信号量：并发数从设置读取（默认 8），可动态调整
        private static int _heicConcurrencyCache;
        private static SemaphoreSlim _heicSemaphore = new(8, 8);

        // HEIC 解码并发信号量 — 从设置中读取最大并发数（默认 8），
        // 当设置变更时无锁替换信号量实例（Interlocked.Exchange），
        // 旧信号量会在等待中的操作完成后自然释放。
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

        // 优先槽信号量（容量 1）— 当前正在查看的图片走此通道，
        // 不参与预加载信号量的排队竞争，保证当前图片优先解码显示。
        private static readonly SemaphoreSlim _prioritySemaphore = new(1, 1);

        private record CachedEntry(ImageSource Image);

        // maxCacheSize: LRU 缓存最大条目数
        // decodePixelWidth: 解码时限制的最大像素宽度（0 表示不限制）
        // preloadForward: 预加载前方图片数
        // preloadBackward: 预加载后方图片数
        public ImagePreviewService(int maxCacheSize = 20, int decodePixelWidth = 1920,
            int preloadForward = 6, int preloadBackward = 2)
        {
            _maxCacheSize = maxCacheSize;
            _decodePixelWidth = decodePixelWidth;
            _preloadForward = preloadForward;
            _preloadBackward = preloadBackward;
        }

        // 加载一张图片（预加载用，可能排队等信号量）。
        public Task<ImageSource?> LoadAsync(string filePath) => LoadInternalAsync(filePath, usePriority: false);

        // 加载当前正在看的图片（走优先通道，不和预加载抢槽位）。
        public Task<ImageSource?> LoadCurrentAsync(string filePath) => LoadInternalAsync(filePath, usePriority: true);

        private async Task<ImageSource?> LoadInternalAsync(string filePath, bool usePriority)
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
                    image = await LoadHeicPreviewAsync(filePath, usePriority);
                }
                else
                {
                    var file = await StorageFile.GetFileFromPathAsync(filePath);
                    var bitmap = new BitmapImage();
                    if (_decodePixelWidth > 0)
                        bitmap.DecodePixelWidth = _decodePixelWidth;
                    using (var stream = await file.OpenReadAsync())
                        await bitmap.SetSourceAsync(stream);
                    image = bitmap;
                }

                if (image == null) return null;

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
            catch (Exception ex)
            {
                LogService.Debug($"ImagePreviewService load failed: {ex.Message}", LogSource.UI);
                return null;
            }
        }

        // HEIC 文件判断
        private static bool IsHeicFile(string path) =>
            path.EndsWith(".heic", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".heif", StringComparison.OrdinalIgnoreCase);

        // HEIC 预览：后台解码为临时 JPEG → BitmapImage 加载 → 删临时文件。
        // usePriority=true 走优先槽（当前图专享），false 走预加载槽。
        private async Task<ImageSource?> LoadHeicPreviewAsync(string filePath, bool usePriority = false)
        {
            var semaphore = usePriority ? _prioritySemaphore : HeicSemaphore;
            await semaphore.WaitAsync();
            try
            {
                // 后台：打开文件 → 解码 HEIC + 缩放 → 编码为临时 JPEG
                string? tempJpegPath = null;
                try
                {
                    tempJpegPath = await Task.Run(async () =>
                    {
                        var file = await StorageFile.GetFileFromPathAsync(filePath);
                        using var inputStream = await file.OpenReadAsync();
                        var decoder = await BitmapDecoder.CreateAsync(inputStream);

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

                        string tempPath = Path.Combine(Path.GetTempPath(), $"lpb_prev_{Guid.NewGuid():N}.jpg");
                        using (var fileStream = new FileStream(tempPath, FileMode.Create, FileAccess.Write))
                        {
                            var encoder = await BitmapEncoder.CreateAsync(
                                BitmapEncoder.JpegEncoderId, fileStream.AsRandomAccessStream());
                            encoder.SetSoftwareBitmap(softwareBitmap);
                            await encoder.FlushAsync();
                        }

                        return tempPath;
                    });

                    // UI 线程：从临时 JPEG 加载 BitmapImage
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

        // 后台预加载相邻图片（fire-and-forget）。
        // 按滚动方向预加载：前进时前多后少，后退时前少后多
        public void PreloadNeighbors(IReadOnlyList<string> allPaths, int centerIndex, int direction)
        {
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
                    _ = LoadAsync(path);
            }
        }

        // 清空缓存
        public void Clear()
        {
            _cache.Clear();
            _lruOrder.Clear();
        }
    }
}
