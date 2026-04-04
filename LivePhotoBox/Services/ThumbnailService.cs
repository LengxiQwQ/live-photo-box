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
                await _loadLimiter.WaitAsync().ConfigureAwait(false);
                try
                {
                    if (_thumbnailCache.TryGetValue(imagePath, out var cached))
                    {
                        return cached;
                    }

                    StorageFile file = await StorageFile.GetFileFromPathAsync(imagePath);
                    using var thumbnail = await file.GetThumbnailAsync(ThumbnailMode.ListView, 80, ThumbnailOptions.UseCurrentScale);

                    if (thumbnail == null)
                    {
                        return null;
                    }

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

        public static void ClearCache()
        {
            _thumbnailCache.Clear();
            _inflightLoads.Clear();
            Interlocked.Increment(ref _cacheVersion);
        }
    }
}