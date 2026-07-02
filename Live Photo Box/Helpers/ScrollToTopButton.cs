using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

// <summary>
// File: ScrollToTopButton.cs
// 悬浮"回到顶部"按钮辅助类。
// 监听 ListView 内部 ScrollViewer 的垂直偏移，超过阈值时显示按钮，
// 点击后平滑滚动回顶部，回顶后自动隐藏。
// 供 RepairPage / SplitPage / MergePage 三页复用。
// </summary>

namespace LivePhotoBox.Helpers
{
    // ScrollToTopButton 辅助类。
    // 将悬浮按钮与 ListView 的滚动状态绑定，实现"滚远后出现、回顶后消失"的交互。
    public sealed class ScrollToTopButtonHelper
    {
        private readonly ListView _listView;
        private readonly Button _button;
        private readonly double _showThreshold;

        private ScrollViewer? _scrollViewer;
        private bool _isAttached;

        // listView: 目标列表
        // button: 悬浮按钮
        // showThreshold: 垂直滚动超过此像素数后显示按钮，默认 200
        public ScrollToTopButtonHelper(ListView listView, Button button, double showThreshold = 200)
        {
            _listView = listView ?? throw new ArgumentNullException(nameof(listView));
            _button = button ?? throw new ArgumentNullException(nameof(button));
            _showThreshold = showThreshold;
        }

        // 附加：查找 ScrollViewer，注册事件。
        public void Attach()
        {
            if (_isAttached) return;

            _button.Click += OnButtonClick;

            if (_listView.IsLoaded)
            {
                TryHookScrollViewer();
            }
            else
            {
                _listView.Loaded += OnListViewLoaded;
            }

            UpdateButtonVisibility(forceHide: true);
            _isAttached = true;
        }

        // 分离：解绑所有事件，防退出时访问违例。
        public void Detach()
        {
            if (!_isAttached) return;
            _isAttached = false; // ← 先置标志位，阻止后续事件处理

            _listView.Loaded -= OnListViewLoaded;

            if (_scrollViewer != null)
            {
                try { _scrollViewer.ViewChanged -= OnScrollViewChanged; }
                catch { /* ScrollViewer 可能在析构中，解绑访问可能抛异常 */ }
                _scrollViewer = null;
            }

            try { _button.Click -= OnButtonClick; }
            catch { /* Button 可能已释放 */ }
        }

        // ── 事件处理（全部守卫 _isAttached）──

        private void OnListViewLoaded(object sender, RoutedEventArgs e)
        {
            _listView.Loaded -= OnListViewLoaded;
            if (!_isAttached) return;
            TryHookScrollViewer();
        }

        private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            if (!_isAttached) return;
            try { UpdateButtonVisibility(); }
            catch { /* 布局更新期间可能暂时无效 */ }
        }

        private void OnButtonClick(object sender, RoutedEventArgs e)
        {
            if (!_isAttached) return;

            var sv = GetScrollViewer();
            if (sv == null) return;

            var dispatcher = _listView.DispatcherQueue;
            if (dispatcher != null)
            {
                dispatcher.TryEnqueue(() =>
                {
                    if (!_isAttached) return;
                    try { sv.ChangeView(null, 0, null, true); }
                    catch
                    {
                        try { sv.ChangeView(null, 0, null, false); }
                        catch { /* 静默失败 */ }
                    }
                });
            }
            else
            {
                try { sv.ChangeView(null, 0, null, true); }
                catch { /* 静默失败 */ }
            }
        }

        // ── 内部方法 ──────────────────────────────

        private void TryHookScrollViewer()
        {
            if (_scrollViewer != null) return;

            try
            {
                _scrollViewer = VisualTreeHelperExtensions.FindDescendant<ScrollViewer>(_listView);
                if (_scrollViewer != null)
                {
                    _scrollViewer.ViewChanged += OnScrollViewChanged;
                    UpdateButtonVisibility();
                }
            }
            catch { /* ListView 模板尚未就绪 */ }
        }

        // 安全获取 ScrollViewer（处理模板重应用导致的引用失效）
        private ScrollViewer? GetScrollViewer()
        {
            if (_scrollViewer != null)
            {
                try
                {
                    _ = _scrollViewer.VerticalOffset;
                    return _scrollViewer;
                }
                catch
                {
                    try { _scrollViewer.ViewChanged -= OnScrollViewChanged; }
                    catch { }
                    _scrollViewer = null;
                }
            }

            if (!_isAttached) return null;

            try
            {
                _scrollViewer = VisualTreeHelperExtensions.FindDescendant<ScrollViewer>(_listView);
                if (_scrollViewer != null)
                    _scrollViewer.ViewChanged += OnScrollViewChanged;
            }
            catch { }

            return _scrollViewer;
        }

        private void UpdateButtonVisibility(bool forceHide = false)
        {
            if (!_isAttached) return;

            var sv = GetScrollViewer();
            if (sv == null)
            {
                try { _button.Visibility = Visibility.Collapsed; }
                catch { }
                return;
            }

            bool shouldShow = !forceHide && sv.VerticalOffset > _showThreshold;

            try
            {
                if (shouldShow && _button.Visibility != Visibility.Visible)
                    _button.Visibility = Visibility.Visible;
                else if (!shouldShow && _button.Visibility == Visibility.Visible)
                    _button.Visibility = Visibility.Collapsed;
            }
            catch { }
        }
    }
}
