/*
 * TaskListAutoScroller.cs
 *
 * 任务列表自动滚动控制器。统一管理列表的"跟随最新任务"行为：
 * 扫描/处理阶段自动滚动到最新任务（120ms 防抖），
 * 用户手动上滚时暂停跟随，2 秒空闲后自动恢复，手动滚到底部立即恢复。
 */

using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Helpers
{
    // 任务列表自动滚动控制器。
    // 统一管理列表的"跟随最新任务"行为：
    // - 扫描/处理阶段自动滚动到最新任务（120ms 防抖）
    // - 用户手动上滚时暂停跟随，2 秒空闲后自动恢复
    // - 用户手动滚到底部立即恢复
    // - 区分程序自动滚动与用户手动滚动
    // 使用方法：
    // 1. 在 Page.Loaded 中调用 Attach(listView)
    // 2. 在 Page.Unloaded 中调用 Detach()
    // 3. 将 ViewModel 事件转发给对应的 NotifyXxx 方法
    // 4. 当扫描/处理开始时调用 NotifyScanStarting() / NotifyProcessingStarting()
    public class TaskListAutoScroller
    {
        private static readonly TimeSpan AutoFollowDebounce = TimeSpan.FromMilliseconds(120);
        private static readonly TimeSpan FinalBottomNudgeDelay = TimeSpan.FromMilliseconds(80);
        private static readonly double ResumeThresholdPx = 20.0;
        private static readonly TimeSpan UserIdleTimeout = TimeSpan.FromSeconds(2);

        private readonly string _ownerName; // 用于日志区分
        private readonly Func<bool> _isActive; // 页面是否在活跃状态 (IsProcessing || IsScanning)
        private readonly Func<int> _getTaskCount;
        private readonly Func<int, object> _getTaskAt;
        private readonly DispatcherQueue _dispatcher;

        private ListView? _listView;
        private ScrollViewer? _scrollViewer;
        private bool _isAttached;

        private bool _isLoopScheduled;
        private bool _hasPending;
        private int _pendingIndex = -1;
        private int _lastScrolledIndex = -1;
        private bool _isProgrammatic;
        private bool _pausedByUser;
        private CancellationTokenSource? _idleTimerCts;

        public TaskListAutoScroller(string ownerName, Func<bool> isActive, Func<int> getTaskCount, Func<int, object> getTaskAt)
        {
            _ownerName = ownerName;
            _isActive = isActive;
            _getTaskCount = getTaskCount;
            _getTaskAt = getTaskAt;
            _dispatcher = DispatcherQueue.GetForCurrentThread()
                ?? throw new InvalidOperationException("TaskListAutoScroller must be created on a UI thread with a DispatcherQueue.");
        }

        // ── 公开 API ──────────────────────────────────────

        // 挂载到 ListView，开始监听滚动事件。
        public void Attach(ListView listView)
        {
            if (_isAttached) return;
            _listView = listView;
            _scrollViewer = FindDescendant<ScrollViewer>(listView);
            if (_scrollViewer != null)
                _scrollViewer.ViewChanged += OnScrollViewerViewChanged;
            _isAttached = true;
        }

        // 卸载，释放资源。
        public void Detach()
        {
            _isAttached = false;
            CancelIdleTimer();
            if (_scrollViewer != null)
            {
                _scrollViewer.ViewChanged -= OnScrollViewerViewChanged;
                _scrollViewer = null;
            }
            _listView = null;
            _hasPending = false;
            _pendingIndex = -1;
            _lastScrolledIndex = -1;
            _pausedByUser = false;
        }

        // 某个任务开始处理 — 触发自动滚动。
        public void NotifyTaskStarted(int zeroBasedIndex)
        {
            if (zeroBasedIndex == 0)
            {
                _pendingIndex = -1;
                _lastScrolledIndex = -1;
            }
            ScheduleAutoScroll(zeroBasedIndex);
        }

        // 全部处理完毕。
        // 自然完成时滚到底部展示结果；用户手动停止时保持当前位置。
        public void NotifyAllCompleted(bool wasCancelled = false)
        {
            if (!wasCancelled)
                _ = SafeNudgeToBottomAsync();
        }

        // 扫描阶段批量刷入项目 — 滚动到最新。
        public void NotifyItemsFlushed()
        {
            int idx = _getTaskCount() - 1;
            if (idx >= 0) ScheduleAutoScroll(idx);
        }

        // 新一轮扫描开始 — 重置所有状态。
        public void NotifyScanStarting()
        {
            _pendingIndex = -1;
            _lastScrolledIndex = -1;
            _hasPending = false;
            _pausedByUser = false;
            CancelIdleTimer();
        }

        // 处理阶段开始 — 清除扫描阶段残留的暂停状态。
        public void NotifyProcessingStarting()
        {
            _pendingIndex = -1;
            _lastScrolledIndex = -1;
            _hasPending = false;
            _pausedByUser = false;
            CancelIdleTimer();
        }

        // 暂停后恢复 — 重置追踪索引，避免旧索引导致跳过后续滚动。
        public void NotifyProcessingResumed()
        {
            _pendingIndex = -1;
            _lastScrolledIndex = -1;
            _hasPending = false;
            _pausedByUser = false;
            CancelIdleTimer();
        }

        public void NotifyScanFinished()
        {
            if (_getTaskCount() > 0)
                _ = FinalScanScrollAsync();
        }

        public void NotifyPageUnloading()
        {
            _hasPending = false;
            _pendingIndex = -1;
            _lastScrolledIndex = -1;
            _pausedByUser = false;
            CancelIdleTimer();
        }

        // ── 内部实现 ──────────────────────────────────────

        private void ScheduleAutoScroll(int itemIndex)
        {
            if (!_isAttached || itemIndex < 0 || itemIndex >= _getTaskCount()) return;
            if (!_isActive()) return;
            if (_pausedByUser) return;

            _pendingIndex = Math.Max(_pendingIndex, itemIndex);
            _hasPending = true;

            if (!_isLoopScheduled)
            {
                _isLoopScheduled = true;
                _ = RunAutoScrollLoopAsync();
            }
        }

        private async Task RunAutoScrollLoopAsync()
        {
            try
            {
                while (_hasPending && _isAttached)
                {
                    _hasPending = false;
                    await Task.Delay(AutoFollowDebounce).ConfigureAwait(false);

                    int target = _pendingIndex;
                    if (!_isAttached || !_isActive() || target < 0 || target >= _getTaskCount() || target == _lastScrolledIndex)
                        continue;

                    await DispatchAsync(() =>
                    {
                        if (!_isAttached || !_isActive() || target < 0 || target >= _getTaskCount())
                            return;
                        _isProgrammatic = true;
                        _listView!.ScrollIntoView(_getTaskAt(target), ScrollIntoViewAlignment.Default);
                        _lastScrolledIndex = target;
                    }).ConfigureAwait(false);
                }
            }
            finally
            {
                _isLoopScheduled = false;
                if (_hasPending && _isAttached && !_isLoopScheduled)
                {
                    _isLoopScheduled = true;
                    _ = RunAutoScrollLoopAsync();
                }
            }
        }

        private async Task SafeNudgeToBottomAsync()
        {
            if (!_isAttached) return;
            await Task.Delay(FinalBottomNudgeDelay).ConfigureAwait(false);
            await DispatchAsync(() =>
            {
                if (!_isAttached) return;
                _scrollViewer ??= FindDescendant<ScrollViewer>(_listView!);
                _scrollViewer?.ChangeView(null, _scrollViewer.ScrollableHeight, null, true);
            }).ConfigureAwait(false);
        }

        private async Task FinalScanScrollAsync()
        {
            if (!_isAttached) return;
            await Task.Delay(30).ConfigureAwait(false);
            await DispatchAsync(() =>
            {
                if (!_isAttached || _getTaskCount() == 0) return;
                _isProgrammatic = true;
                _listView!.ScrollIntoView(_getTaskAt(_getTaskCount() - 1), ScrollIntoViewAlignment.Default);
            }).ConfigureAwait(false);
        }

        private void OnScrollViewerViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (_isProgrammatic)
            {
                _isProgrammatic = false;
                return;
            }
            var sv = _scrollViewer;
            if (sv == null || !_isAttached) return;

            double dist = sv.ScrollableHeight - sv.VerticalOffset;
            if (dist <= ResumeThresholdPx)
            {
                CancelIdleTimer();
                _pausedByUser = false;
            }
            else if (!e.IsIntermediate && dist > ResumeThresholdPx)
            {
                _pausedByUser = true;
                StartIdleTimer();
            }
        }

        private void StartIdleTimer()
        {
            CancelIdleTimer();
            _idleTimerCts = new CancellationTokenSource();
            var token = _idleTimerCts.Token;
            _ = Task.Run(async () =>
            {
                try
                {
                    await Task.Delay(UserIdleTimeout, token).ConfigureAwait(false);
                    if (token.IsCancellationRequested) return;
                    await DispatchAsync(() =>
                    {
                        if (!_isAttached || !_pausedByUser) return;
                        _pausedByUser = false;
                        // 只解除暂停，不主动滚动。
                        // 如果滚到底部再等下一个任务把它拉回来，会闪一下，体验很差。
                        // 直接等下一个 NotifyTaskStarted 自然地滚到当前正在处理的任务。
                        _lastScrolledIndex = -1;
                        _pendingIndex = -1;
                        _hasPending = false;
                    }).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { }
            }, token);
        }

        private void CancelIdleTimer()
        {
            try { _idleTimerCts?.Cancel(); _idleTimerCts?.Dispose(); }
            catch { }
            finally { _idleTimerCts = null; }
        }

        // ── 可靠的 UI 线程调度 ────────────────────────────

        // 将操作可靠地排入 UI 线程。TryEnqueue 在队列满时返回 false，
        // 这里用轮询重试 + 递增退避，最多 30 次（约 500ms）。
        private async Task DispatchAsync(Action action)
        {
            for (int attempt = 0; attempt < 30; attempt++)
            {
                var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                if (_dispatcher.TryEnqueue(() =>
                {
                    try { action(); }
                    catch (Exception ex) { LogService.Debug($"[{_ownerName}] Dispatch action failed: {ex.Message}", LogSource.UI); }
                    finally { tcs.TrySetResult(true); }
                }))
                {
                    await tcs.Task.ConfigureAwait(false);
                    return;
                }
                // 递增退避：1ms → 2ms → 4ms → 8ms → … → 最大 32ms
                int delay = Math.Min(1 << Math.Min(attempt, 5), 32);
                await Task.Delay(delay).ConfigureAwait(false);
            }
            LogService.Warn($"[{_ownerName}] DispatchAsync failed after 30 retries — scroll operation dropped", source: LogSource.UI);
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                var nested = FindDescendant<T>(child);
                if (nested is not null) return nested;
            }
            return default;
        }
    }
}
