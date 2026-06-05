using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;
using Windows.Storage.FileProperties;

namespace LivePhotoBox.Services
{
    public static class ThumbnailService
    {
        private static readonly ConcurrentDictionary<string, ImageSource> _thumbnailCache = new(StringComparer.OrdinalIgnoreCase);
        private static readonly ConcurrentDictionary<string, Task<ImageSource?>> _inflightLoads = new(StringComparer.OrdinalIgnoreCase);
        private static readonly SemaphoreSlim _loadLimiter = new(4, 4);
        private static int _cacheVersion;

        public static ImageSource? GetCached(string imagePath)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return null;
            return _thumbnailCache.TryGetValue(imagePath, out var cached) ? cached : null;
        }

        public static Task<ImageSource?> LoadAsync(string imagePath, Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null, CancellationToken token = default)
        {
            if (string.IsNullOrWhiteSpace(imagePath)) return Task.FromResult<ImageSource?>(null);

            dispatcher ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher == null) return Task.FromResult<ImageSource?>(null);

            if (_thumbnailCache.TryGetValue(imagePath, out var cached))
            {
                return Task.FromResult<ImageSource?>(cached);
            }

            int version = Volatile.Read(ref _cacheVersion);

            return _inflightLoads.GetOrAdd(imagePath, path => LoadCoreAsync(path, dispatcher, version, token));
        }

        public static void Preload(IEnumerable<string> imagePaths, Microsoft.UI.Dispatching.DispatcherQueue? dispatcher = null)
        {
            dispatcher ??= Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();
            if (dispatcher == null) return;

            foreach (var imagePath in imagePaths.Where(static path => !string.IsNullOrWhiteSpace(path)).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                _ = LoadAsync(imagePath, dispatcher);
            }
        }

        private static async Task<ImageSource?> LoadCoreAsync(string imagePath, Microsoft.UI.Dispatching.DispatcherQueue dispatcher, int version, CancellationToken token)
        {
            StorageItemThumbnail thumbnail = null;
            try
            {
                // 第一阶段：纯后台占用名额，读取磁盘
                await _loadLimiter.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (token.IsCancellationRequested) return null;
                    if (_thumbnailCache.TryGetValue(imagePath, out var cached)) return cached;

                    StorageFile file = await StorageFile.GetFileFromPathAsync(imagePath);
                    thumbnail = await file.GetThumbnailAsync(ThumbnailMode.ListView, 80, ThumbnailOptions.UseCurrentScale);
                }
                finally
                {
                    // 【绝对破局点】：只要硬盘读完，立刻释放名额给下一张图！绝不等待UI线程！死锁根除！
                    _loadLimiter.Release();
                }

                if (thumbnail == null) return null;

                var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);

                // 第二阶段：交给 UI 线程慢慢画，画多慢都不会卡死硬盘读取
                if (!dispatcher.TryEnqueue(async () =>
                {
                    try
                    {
                        using (thumbnail) // 在 UI 线程使用完毕后自动释放内存
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
                    }
                    catch
                    {
                        tcs.TrySetResult(null);
                    }
                }))
                {
                    thumbnail.Dispose();
                    tcs.TrySetResult(null);
                }

                return await tcs.Task.ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                thumbnail?.Dispose();
                return null;
            }
            catch
            {
                thumbnail?.Dispose();
                return null;
            }
            finally
            {
                _inflightLoads.TryRemove(imagePath, out _);
            }
        }

        public static void ClearCache()
        {
            _thumbnailCache.Clear();
            _inflightLoads.Clear();
            Interlocked.Increment(ref _cacheVersion);
        }
    }
}