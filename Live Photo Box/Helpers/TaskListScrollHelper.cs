using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

// <summary>
// File: TaskListScrollHelper.cs
// 任务列表自动滚动辅助类，封装 MergePage / SplitPage / RepairPage 的公共滚动逻辑。
// 提供处理中自动跟随（带防抖）、扫描结束滚到底部、处理完成后推到底部等功能。
// Merge 页面不需要扫描滚动（任务在扫描完成后一次性装入），但仍可使用 Nudge / ScrollIntoView。
// </summary>

namespace LivePhotoBox.Helpers
{
    // 任务列表自动滚动辅助类。
    // 封装 MergePage / SplitPage / RepairPage 的公共滚动逻辑。
    // Merge 页面不需要扫描滚动（任务在扫描完成后一次性装入），
    // 也不需要防抖（批次回调），但仍可借助 Nudge / ScrollIntoView。
    public sealed class TaskListScrollHelper
    {
        private static readonly TimeSpan AutoScrollDebounce = TimeSpan.FromMilliseconds(120);
        private static readonly TimeSpan FinalNudgeDelay = TimeSpan.FromMilliseconds(80);
        private static readonly TimeSpan ScanEndDelay = TimeSpan.FromMilliseconds(30);

        private readonly ListView _listView;
        private readonly string _logPrefix;
        private readonly Func<bool> _isActive;

        private bool _isScheduled;
        private bool _hasPending;
        private int _pendingIndex = -1;
        private int _lastIndex = -1;
        private ScrollViewer? _scrollViewer;

        public bool IsUnloaded { get; set; }

        // listView: 要滚动的 ListView
        // logPrefix: 日志前缀，例如 "MergePage" / "SplitPage" / "RepairPage"
        // isActive: 当前是否处于"活跃滚动"状态，Split/Repair 用 () => IsProcessing || IsScanning，Merge 用 () => IsProcessing
        public TaskListScrollHelper(ListView listView, string logPrefix, Func<bool> isActive)
        {
            _listView = listView ?? throw new ArgumentNullException(nameof(listView));
            _logPrefix = logPrefix;
            _isActive = isActive ?? throw new ArgumentNullException(nameof(isActive));
        }

        // ─────────────────────────────────────
        //  公开方法：页面事件 → 调用这些
        // ─────────────────────────────────────

        // TaskStartedForScroll 的回调。Merge 不需要 resetOnFirst。
        // taskIndex: 当前任务在列表中的索引（从 0 开始）。
        // totalCount: 任务总数，用于边界检查。
        // resetOnFirst: 当 taskIndex 为 0 时是否重置追踪索引，默认 true。Merge 页面应传 false。
        public void OnTaskStarted(int taskIndex, int totalCount, bool resetOnFirst = true)
        {
            if (IsUnloaded || taskIndex < 0 || taskIndex >= totalCount) return;
            if (!_isActive()) return;

            if (resetOnFirst && taskIndex == 0)
            {
                _pendingIndex = -1;
                _lastIndex = -1;
            }

            _pendingIndex = Math.Max(_pendingIndex, taskIndex);
            _hasPending = true;

            if (!_isScheduled)
            {
                _isScheduled = true;
                _ = RunAutoScrollLoopAsync();
            }
        }

        // 批量滚动到指定索引，Merge 专用（无防抖，直接滚）。
        // itemIndex: 要滚动到的目标项索引。
        // totalCount: 列表总项数，用于边界检查。
        // dispatcher: 用于在 UI 线程执行滚动的 DispatcherQueue。
        public void ScrollToTask(int itemIndex, int totalCount, DispatcherQueue dispatcher)
        {
            if (IsUnloaded || itemIndex < 0 || itemIndex >= totalCount || !_isActive() || itemIndex == _lastIndex) return;

            if (dispatcher != null)
            {
                _ = EnqueueScrollIntoViewAsync(dispatcher, itemIndex);
            }
        }

        // 扫描过程中新增项目时滚动到底部。Split/Repair 用。
        // dispatcher: 用于在 UI 线程执行滚动的 DispatcherQueue。
        // totalCount: 刷新后的列表总项数。
        public void OnScanItemsFlushed(DispatcherQueue dispatcher, int totalCount)
        {
            if (IsUnloaded || !_isActive()) return;
            int lastIndex = totalCount - 1;
            if (lastIndex < 0) return;

            dispatcher.TryEnqueue(() =>
            {
                if (IsUnloaded) return;
                if (lastIndex >= 0 && lastIndex < _listView.Items.Count)
                    _listView.ScrollIntoView(_listView.Items[lastIndex], ScrollIntoViewAlignment.Default);
            });
        }

        // 扫描结束后滚到底部。Split/Repair 用。
        // dispatcher: 用于在 UI 线程执行滚动的 DispatcherQueue。
        public async Task FinalScanScrollAsync(DispatcherQueue dispatcher)
        {
            await Task.Delay(ScanEndDelay).ConfigureAwait(false);
            dispatcher.TryEnqueue(() =>
            {
                if (IsUnloaded) return;
                int count = _listView.Items.Count;
                if (count == 0) return;
                _listView.ScrollIntoView(_listView.Items[count - 1], ScrollIntoViewAlignment.Default);
            });
        }

        // 处理完成后缓慢推到底部。
        // dispatcher: 用于在 UI 线程执行滚动的 DispatcherQueue。
        public void OnProcessingCompleted(DispatcherQueue dispatcher)
        {
            if (dispatcher != null && !IsUnloaded)
                _ = SafeNudgeToBottomAsync(dispatcher);
        }

        // 重置所有滚动追踪状态（切换页面、清空队列时调用）。
        // 将 _pendingIndex、_lastIndex 和 _hasPending 全部清空，下次滚动操作将重新追踪。
        public void Reset()
        {
            _pendingIndex = -1;
            _lastIndex = -1;
            _hasPending = false;
        }

        // ─────────────────────────────────────
        //  内部实现
        // ─────────────────────────────────────

        private async Task RunAutoScrollLoopAsync()
        {
            try
            {
                while (_hasPending && !IsUnloaded)
                {
                    _hasPending = false;
                    await Task.Delay(AutoScrollDebounce).ConfigureAwait(false);

                    int targetIndex = _pendingIndex;
                    if (IsUnloaded || !_isActive() || targetIndex < 0 || targetIndex >= _listView.Items.Count || targetIndex == _lastIndex)
                    {
                        continue;
                    }

                    var dispatcher = _listView.DispatcherQueue;
                    if (dispatcher != null)
                    {
                        try { await EnqueueScrollIntoViewAsync(dispatcher, targetIndex).ConfigureAwait(false); }
                        catch (Exception ex) { LogService.Debug($"{_logPrefix} auto-scroll failed: {ex.Message}", LogSource.UI); }
                    }
                }
            }
            finally
            {
                _isScheduled = false;
                if (_hasPending && !IsUnloaded)
                {
                    _isScheduled = true;
                    _ = RunAutoScrollLoopAsync();
                }
            }
        }

        private async Task SafeNudgeToBottomAsync(DispatcherQueue dispatcher)
        {
            try { await NudgeToBottomAsync(dispatcher).ConfigureAwait(false); }
            catch (Exception ex) { LogService.Debug($"{_logPrefix} auto-scroll nudge failed: {ex.Message}", LogSource.UI); }
        }

        private async Task NudgeToBottomAsync(DispatcherQueue dispatcher)
        {
            await Task.Delay(FinalNudgeDelay).ConfigureAwait(false);
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    if (!IsUnloaded)
                    {
                        _scrollViewer ??= VisualTreeHelperExtensions.FindDescendant<ScrollViewer>(_listView);
                        _scrollViewer?.ChangeView(null, _scrollViewer.ScrollableHeight, null, true);
                    }
                    tcs.TrySetResult();
                }
                catch (Exception ex) { LogService.Debug($"{_logPrefix} scroll nudge dispatcher error: {ex.Message}", LogSource.UI); tcs.TrySetResult(); }
            }))
            {
                tcs.TrySetResult();
            }
            await tcs.Task.ConfigureAwait(false);
        }

        private Task EnqueueScrollIntoViewAsync(DispatcherQueue dispatcher, int targetIndex)
        {
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    if (!IsUnloaded && _isActive() && targetIndex >= 0 && targetIndex < _listView.Items.Count)
                    {
                        _listView.ScrollIntoView(_listView.Items[targetIndex], ScrollIntoViewAlignment.Default);
                        _lastIndex = targetIndex;
                    }
                    tcs.TrySetResult();
                }
                catch (Exception ex) { LogService.Debug($"{_logPrefix} scroll-into-view dispatcher error: {ex.Message}", LogSource.UI); tcs.TrySetResult(); }
            }))
            {
                tcs.TrySetResult();
            }
            return tcs.Task;
        }
    }
}
