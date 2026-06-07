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
    public static class SplitThumbnailService
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
            // 完美移除：这里由于原有机制不带取消令牌，快速滑动时会产生大量的无法取消的僵尸任务。
        }

        private static async Task<ImageSource?> LoadCoreAsync(string imagePath, Microsoft.UI.Dispatching.DispatcherQueue dispatcher, int version, CancellationToken token)
        {
            StorageItemThumbnail thumbnail = null;
            try
            {
                await _loadLimiter.WaitAsync(token).ConfigureAwait(false);
                try
                {
                    if (token.IsCancellationRequested) return null;
                    if (_thumbnailCache.TryGetValue(imagePath, out var cached)) return cached;

                    // ✨ 修复 1：去掉 .AsTask(token)，避免强行中断 WinRT 导致 COM 异常
                    StorageFile file = await StorageFile.GetFileFromPathAsync(imagePath);
                    if (token.IsCancellationRequested) return null;

                    thumbnail = await file.GetThumbnailAsync(ThumbnailMode.ListView, 80, ThumbnailOptions.UseCurrentScale);
                }
                finally
                {
                    _loadLimiter.Release();
                }

                if (thumbnail == null || token.IsCancellationRequested)
                {
                    thumbnail?.Dispose();
                    return null;
                }

                var tcs = new TaskCompletionSource<ImageSource?>(TaskCreationOptions.RunContinuationsAsynchronously);

                bool enqueued = dispatcher.TryEnqueue(() =>
                {
                    async Task SafeRenderAsync()
                    {
                        try
                        {
                            using (thumbnail)
                            {
                                if (token.IsCancellationRequested)
                                {
                                    tcs.TrySetResult(null);
                                    return;
                                }

                                var bitmap = new BitmapImage();
                                // ✨ 修复 2：绝不可取消 SetSourceAsync！让 C++ 渲染引擎安全读完流，绝不会再报 0xc000027b
                                await bitmap.SetSourceAsync(thumbnail);

                                if (version == Volatile.Read(ref _cacheVersion) && !token.IsCancellationRequested)
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
                    }

                    _ = SafeRenderAsync();
                });

                if (!enqueued)
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