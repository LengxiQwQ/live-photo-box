using Microsoft.UI.Dispatching;
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
using LogLevel = LivePhotoBox.Models.LogLevel;
using LogSource = LivePhotoBox.Models.LogSource;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 缩略图集中调度器。
    /// - 代际计数器：每次新的一批可见 item 入队时递增。worker 只处理当前代的 item，旧代自动丢弃。
    /// - 优先级队列（可见 > 预加载前向 > 预加载后向）
    /// - 4 并发 worker 持续消费（轮询模式）
    /// - 200ms 批量 UI 刷新（单次最多 20 张）
    /// - worker 崩溃自动重启
    /// - 损坏文件不重试
    /// </summary>
    public static class ThumbnailScheduler
    {
        private const int WorkerCount = 4;
        private const int MaxFlushBatch = 20;
        private const int IdlePollMs = 50;
        private const int MaxThumbnailAttempts = 2;
        private static readonly TimeSpan ThumbnailAttemptTimeout = TimeSpan.FromSeconds(55);
        // 视频 FFmpeg 抽帧更重，限制 2 并发防止 I/O 争抢
        private static readonly SemaphoreSlim _videoSem = new(2, 2);

        private sealed class ThumbnailRequest
        {
            public int Index;
            public string Path = null!;
            public int Priority;
            public int Generation; // Enqueue 时的代际
            public int Attempt = 1;
        }

        private sealed record BackgroundItem(int Index, string Path, Models.EditFileItem Item);

        private static readonly List<ThumbnailRequest> _queue = new();
        private static readonly object _queueLock = new();
        private static readonly HashSet<string> _inFlight = new(StringComparer.OrdinalIgnoreCase);
        private static readonly HashSet<string> _failedPaths = new(StringComparer.OrdinalIgnoreCase);
        private static CancellationTokenSource? _workerCts;
        private static readonly Task?[] _workerTasks = new Task?[WorkerCount];
        private static int _aliveWorkers;
        private static volatile int _currentGeneration;

        private static readonly ConcurrentQueue<(string path, byte[] data, int w, int h)> _pending = new();
        private static DispatcherQueueTimer? _flushTimer;
        private static DispatcherQueue? _dispatcher;
        private static readonly ConcurrentDictionary<string, Action<ImageSource?>> _callbacks =
            new(StringComparer.OrdinalIgnoreCase);

        // 后台预热：可见区加载完后，从 index 0 开始逐步预热全列表
        private static IReadOnlyList<BackgroundItem>? _bgItems;
        private static int _bgNextIndex;
        private static readonly object _backgroundLock = new();

        private static bool _started;

        public static void Initialize(DispatcherQueue dispatcher)
        {
            _dispatcher = dispatcher;

            if (_flushTimer == null)
            {
                _flushTimer = dispatcher.CreateTimer();
                _flushTimer.Interval = TimeSpan.FromMilliseconds(200);
                _flushTimer.Tick += (s, e) => FlushPendingToUI();
                _flushTimer.Start();
            }

            if (!_started)
            {
                _started = true;
                _workerCts = new CancellationTokenSource();
                for (int i = 0; i < WorkerCount; i++)
                {
                    var workerId = i;
                    _workerTasks[i] = Task.Run(() => SuperviseWorkerAsync(workerId, _workerCts.Token));
                }
            }
        }

        /// <summary>每次视图滚动产生新的可见 item 批次时，调用此方法淘汰上一代排队的旧 item。</summary>
        public static int NewGeneration()
        {
            int gen = Interlocked.Increment(ref _currentGeneration);
            lock (_queueLock)
            {
                // 移除旧代的所有 item，同时清理 _inFlight 和 _callbacks
                var toRemove = new List<ThumbnailRequest>();
                foreach (var r in _queue)
                    if (r.Generation < gen)
                        toRemove.Add(r);

                foreach (var r in toRemove)
                {
                    _queue.Remove(r);
                    _inFlight.Remove(r.Path);
                    _callbacks.TryRemove(r.Path, out _);
                }
            }
            LogService.Info($"[Scheduler] NewGeneration={gen}", LogSource.System);
            return gen;
        }

        public static void Enqueue(int index, string path, int priority, Action<ImageSource?> callback)
        {
            if (string.IsNullOrWhiteSpace(path)) return;

            if (ThumbnailService.GetCached(path) is { } cached)
            {
                _dispatcher?.TryEnqueue(() => callback(cached));
                return;
            }

            int gen = _currentGeneration; // volatile field — direct read has full acquire semantics
            lock (_queueLock)
            {
                if (_inFlight.Contains(path)) return;
                if (_failedPaths.Contains(path)) return;
                if (_queue.Any(r => r.Path == path)) return;

                _callbacks[path] = callback;
                _queue.Add(new ThumbnailRequest { Index = index, Path = path, Priority = priority, Generation = gen });
                _inFlight.Add(path);
                _queue.Sort((a, b) =>
                {
                    // 代际优先：新代在前面
                    int cmp = b.Generation.CompareTo(a.Generation);
                    if (cmp != 0) return cmp;
                    cmp = a.Priority.CompareTo(b.Priority);
                    return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
                });
            }
            int qCount = 0; lock (_queueLock) { qCount = _queue.Count; }
            if (qCount % 50 == 0 && qCount > 0)
                LogService.Info($"[Scheduler] Enqueue gen={gen} q={qCount} inFlight={_inFlight.Count} alive={_aliveWorkers}", LogSource.System);
        }

        public static void TrimQueue(int firstVisible, int lastVisible)
        {
            lock (_queueLock)
            {
                int removed = _queue.RemoveAll(r =>
                {
                    if (r.Index >= firstVisible && r.Index <= lastVisible) return false;
                    _inFlight.Remove(r.Path);
                    _callbacks.TryRemove(r.Path, out _);
                    return true;
                });
                if (removed > 0)
                    LogService.Info($"[Scheduler] TrimQueue removed={removed} remaining={_queue.Count}", LogSource.System);
            }
        }

        public static bool IsInFlight(string path)
        {
            if (string.IsNullOrWhiteSpace(path)) return false;
            lock (_queueLock) { return _inFlight.Contains(path); }
        }

        /// <summary>注册全量列表，用于可见区加载完后后台逐张预热剩余缩略图</summary>
        public static void StartBackgroundFill(System.Collections.IList allItems)
        {
            var snapshot = new List<BackgroundItem>(allItems.Count);
            for (int i = 0; i < allItems.Count; i++)
            {
                if (allItems[i] is Models.EditFileItem item
                    && item.NeedsThumbnail
                    && !string.IsNullOrWhiteSpace(item.FilePath))
                {
                    snapshot.Add(new BackgroundItem(i, item.FilePath, item));
                }
            }

            lock (_backgroundLock)
            {
                _bgItems = snapshot;
                _bgNextIndex = 0;
            }
        }

        public static void Reset()
        {
            Interlocked.Increment(ref _currentGeneration);
            lock (_backgroundLock)
            {
                _bgItems = null;
                _bgNextIndex = 0;
            }
            lock (_queueLock)
            {
                LogService.Info($"[Scheduler] Reset (q={_queue.Count} inFlight={_inFlight.Count})", LogSource.System);
                foreach (var r in _queue)
                {
                    _inFlight.Remove(r.Path);
                    _callbacks.TryRemove(r.Path, out _);
                }
                _queue.Clear();
                _failedPaths.Clear();
            }
            while (_pending.TryDequeue(out _)) { }
        }

        // ── 后台预热 ──

        private static void TryFillBackground()
        {
            int gen = _currentGeneration; // volatile field — direct read has full acquire semantics
            int added = 0;
            while (added < 5)
            {
                BackgroundItem backgroundItem;
                lock (_backgroundLock)
                {
                    if (_bgItems == null || _bgNextIndex >= _bgItems.Count)
                        return;
                    backgroundItem = _bgItems[_bgNextIndex++];
                }

                int i = backgroundItem.Index;
                string path = backgroundItem.Path;
                Models.EditFileItem item = backgroundItem.Item;

                lock (_queueLock)
                {
                    if (_inFlight.Contains(path)) continue;
                    if (_failedPaths.Contains(path)) continue;
                    if (_queue.Any(r => r.Path == path)) continue;
                    if (ThumbnailService.GetCached(path) != null) continue;

                    _callbacks[path] = source => item.Thumbnail = source;
                    _queue.Add(new ThumbnailRequest { Index = i, Path = path, Priority = 3, Generation = gen });
                    _inFlight.Add(path);
                    _queue.Sort((a, b) =>
                    {
                        int cmp = b.Generation.CompareTo(a.Generation);
                        if (cmp != 0) return cmp;
                        cmp = a.Priority.CompareTo(b.Priority);
                        return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
                    });
                }
                added++;
            }
        }

        // ── Worker ──

        private static async Task SuperviseWorkerAsync(int workerId, CancellationToken token)
        {
            for (int attempt = 1; attempt <= ExternalToolProcessGuard.MaxAttempts && !token.IsCancellationRequested; attempt++)
            {
                try
                {
                    await WorkerLoop(workerId, token).ConfigureAwait(false);
                    return;
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception ex)
                {
                    LogService.Merge(
                        $"[Scheduler] Worker#{workerId} crashed (attempt {attempt}/{ExternalToolProcessGuard.MaxAttempts}): {ex.Message}",
                        LogLevel.Error,
                        ex);
                    if (attempt < ExternalToolProcessGuard.MaxAttempts)
                        await Task.Delay(250, token).ConfigureAwait(false);
                }
            }

            LogService.Merge(
                $"[Scheduler] Worker#{workerId} disabled after {ExternalToolProcessGuard.MaxAttempts} crashes; remaining workers continue",
                LogLevel.Error);
        }

        private static async Task WorkerLoop(int workerId, CancellationToken token)
        {
            Interlocked.Increment(ref _aliveWorkers);
            LogService.Info($"[Scheduler] Worker#{workerId} started", LogSource.System);
            try
            {
                while (!token.IsCancellationRequested)
                {
                    ThumbnailRequest? request = null;
                    lock (_queueLock)
                    {
                        if (_queue.Count > 0)
                        {
                            request = _queue[0];
                            _queue.RemoveAt(0);
                        }
                    }

                    if (request == null)
                    {
                        // 空闲 → 从不可变快照领取下一批后台预热任务。
                        TryFillBackground();
                        await Task.Delay(IdlePollMs, token).ConfigureAwait(false);
                        continue;
                    }

                // 检查代际：已被淘汰的旧 item 直接丢弃
                    int currentGen = _currentGeneration; // volatile field — direct read has full acquire semantics
                    if (request.Generation < currentGen)
                    {
                        lock (_queueLock) { _inFlight.Remove(request.Path); }
                        _callbacks.TryRemove(request.Path, out _);
                        continue;
                    }

                    bool retainedForRetry = false;
                    try
                    {
                    using var attemptCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    attemptCts.CancelAfter(ThumbnailAttemptTimeout);
                    CancellationToken attemptToken = attemptCts.Token;
                    byte[]? data = null;
                    int width, height;
                    var sw = Stopwatch.StartNew();

                    if (HeicConverterService.IsHeicFile(request.Path))
                    {
                        (data, width, height) = await ThumbnailProviderFactory.Current
                            .LoadHeicThumbnailAsync(request.Path, 112)
                            .WaitAsync(attemptToken);
                    }
                    else if (!ThumbnailService.IsVideoFilePath(request.Path))
                    {
                        (data, width, height) = await LoadPhotoDataAsync(request.Path, 112)
                            .WaitAsync(attemptToken);
                    }
                    else if (ThumbnailService.IsVideoFilePath(request.Path))
                    {
                            await _videoSem.WaitAsync(attemptToken);
                            try { (data, width, height) = await ThumbnailService.LoadVideoThumbnailDataAsync(request.Path, 168, attemptToken); }
                        finally { _videoSem.Release(); }
                    }
                    else
                    {
                        lock (_queueLock) { _inFlight.Remove(request.Path); }
                        _callbacks.TryRemove(request.Path, out _);
                        continue;
                    }

                    sw.Stop();
                    if (data != null && data.Length > 0)
                    {
                        _pending.Enqueue((request.Path, data, width, height));
                        if (sw.ElapsedMilliseconds > 200)
                            LogService.Info($"[Scheduler] Worker#{workerId} slow: {request.Path} ({sw.ElapsedMilliseconds}ms)", LogSource.System);
                    }
                    }
                    catch (OperationCanceledException) when (token.IsCancellationRequested)
                    {
                        _callbacks.TryRemove(request.Path, out _);
                        throw;
                    }
                    catch (Exception ex)
                    {
                        lock (_queueLock)
                        {
                            _inFlight.Remove(request.Path);
                            if (request.Attempt < MaxThumbnailAttempts
                                && request.Generation >= _currentGeneration)
                            {
                                request.Attempt++;
                                _queue.Add(request);
                                _inFlight.Add(request.Path);
                                _queue.Sort((a, b) =>
                                {
                                    int cmp = b.Generation.CompareTo(a.Generation);
                                    if (cmp != 0) return cmp;
                                    cmp = a.Priority.CompareTo(b.Priority);
                                    return cmp != 0 ? cmp : a.Index.CompareTo(b.Index);
                                });
                                retainedForRetry = true;
                            }
                            else
                            {
                                _failedPaths.Add(request.Path);
                            }
                        }

                        LogService.Merge(
                            $"[Scheduler] Worker#{workerId} FAIL: {request.Path} — {ex.GetType().Name}: {ex.Message} " +
                            $"(attempt {request.Attempt}/{MaxThumbnailAttempts}, retry={retainedForRetry})",
                            LogLevel.Warning,
                            ex);
                        if (!retainedForRetry)
                            _callbacks.TryRemove(request.Path, out _);
                    }
                    finally
                    {
                        if (!retainedForRetry)
                        {
                            lock (_queueLock) { _inFlight.Remove(request.Path); }
                        }
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref _aliveWorkers);
                LogService.Merge($"[Scheduler] Worker#{workerId} EXITED (alive={_aliveWorkers})", LogLevel.Warning);
            }
        }

        private static async Task<(byte[] data, int width, int height)> LoadPhotoDataAsync(string path, uint targetSize)
        {
            return await ThumbnailService.LoadSystemThumbnailDataAsync(path, targetSize);
        }

        // ── 200ms 批量 UI 刷新 ──

        private static void FlushPendingToUI()
        {
            if (_dispatcher == null) return;

            var batch = new List<(string path, byte[] data, int w, int h)>();
            while (_pending.TryDequeue(out var item) && batch.Count < MaxFlushBatch)
                batch.Add(item);

            if (batch.Count == 0) return;

            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var deduped = new List<(string path, byte[] data, int w, int h)>();
            for (int i = batch.Count - 1; i >= 0; i--)
            {
                if (seen.Add(batch[i].path))
                    deduped.Add(batch[i]);
            }
            deduped.Reverse();

            LogService.Info($"[Scheduler] Flush batch={deduped.Count} pending={_pending.Count}", LogSource.System);

            _dispatcher.TryEnqueue(() =>
            {
                foreach (var (path, data, w, h) in deduped)
                {
                    try
                    {
                        var bitmapImage = new BitmapImage();
                        using var stream = new MemoryStream(data);
                        bitmapImage.SetSource(stream.AsRandomAccessStream());
                        ThumbnailService.WriteCache(path, bitmapImage);

                        if (_callbacks.TryRemove(path, out var cb))
                            cb(bitmapImage);
                    }
                    catch (Exception ex)
                    {
                        LogService.Merge($"[Scheduler] Flush UI error: {path} — {ex.GetType().Name}: {ex.Message}",
                            LogLevel.Warning);
                    }
                }

                if (deduped.Count > 10)
                    GC.Collect(0, GCCollectionMode.Optimized, false);
            });
        }
    }
}
