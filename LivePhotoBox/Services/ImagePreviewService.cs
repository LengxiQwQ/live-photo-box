using LivePhotoBox.Models;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using Windows.Storage;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 统一图片预览服务 — 所有预览样式共用同一套优化加载逻辑。
    /// 特性：LRU 内存缓存 + DecodePixelWidth 解码限制 + 相邻预加载。
    /// </summary>
    public sealed class ImagePreviewService
    {
        private readonly int _maxCacheSize;
        private readonly int _decodePixelWidth;
        private readonly int _preloadCount;
        private readonly Dictionary<string, CachedEntry> _cache = new();
        private readonly LinkedList<string> _lruOrder = new();

        private record CachedEntry(BitmapImage Image, long FileSize);

        /// <param name="maxCacheSize">最多缓存多少张图片</param>
        /// <param name="decodePixelWidth">解码像素宽度（0=不限制，推荐 1920）</param>
        /// <param name="preloadCount">预加载相邻图片数（每侧）</param>
        public ImagePreviewService(int maxCacheSize = 20, int decodePixelWidth = 1920, int preloadCount = 2)
        {
            _maxCacheSize = maxCacheSize;
            _decodePixelWidth = decodePixelWidth;
            _preloadCount = preloadCount;
        }

        /// <summary>
        /// 加载一张图片（优先从缓存取）。调用方负责设置到 UI 控件。
        /// </summary>
        public async Task<BitmapImage?> LoadAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath)) return null;

            // 命中缓存 → 移到 LRU 最前面
            if (_cache.TryGetValue(filePath, out var cached))
            {
                if (_lruOrder.First?.Value != filePath)
                {
                    _lruOrder.Remove(filePath);
                    _lruOrder.AddFirst(filePath);
                }
                return cached.Image;
            }

            // 加载新图
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(filePath);
                var fileSize = (await file.GetBasicPropertiesAsync()).Size;
                var bitmap = new BitmapImage();

                if (_decodePixelWidth > 0)
                    bitmap.DecodePixelWidth = _decodePixelWidth;

                using (var stream = await file.OpenReadAsync())
                {
                    await bitmap.SetSourceAsync(stream);
                }

                // 写入缓存
                var entry = new CachedEntry(bitmap, (long)fileSize);
                _cache[filePath] = entry;
                _lruOrder.AddFirst(filePath);

                // 淘汰最久未使用的
                while (_cache.Count > _maxCacheSize)
                {
                    var last = _lruOrder.Last;
                    if (last == null) break;
                    _cache.Remove(last.Value);
                    _lruOrder.RemoveLast();
                }

                LogService.Debug($"ImagePreviewService loaded: {Path.GetFileName(filePath)} (cache={_cache.Count})", LogSource.UI);
                return bitmap;
            }
            catch (Exception ex)
            {
                LogService.Debug($"ImagePreviewService load failed: {ex.Message}", LogSource.UI);
                return null;
            }
        }

        /// <summary>
        /// 后台预加载相邻图片（fire-and-forget）。
        /// </summary>
        public void PreloadNeighbors(IReadOnlyList<string> allPaths, int centerIndex)
        {
            int start = Math.Max(0, centerIndex - _preloadCount);
            int end = Math.Min(allPaths.Count - 1, centerIndex + _preloadCount);

            for (int i = start; i <= end; i++)
            {
                if (i == centerIndex) continue; // 当前图由主加载路径处理
                var path = allPaths[i];
                if (!_cache.ContainsKey(path))
                {
                    _ = LoadAsync(path); // fire-and-forget
                }
            }
        }

        /// <summary>
        /// 清空缓存
        /// </summary>
        public void Clear()
        {
            _cache.Clear();
            _lruOrder.Clear();
        }
    }
}
