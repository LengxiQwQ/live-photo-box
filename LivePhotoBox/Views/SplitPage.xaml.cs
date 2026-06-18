using LivePhotoBox.Behaviors;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;
using Windows.ApplicationModel.DataTransfer;

namespace LivePhotoBox.Views
{
    public sealed partial class SplitPage : Page
    {
        private static readonly TimeSpan AutoFollowDebounce = TimeSpan.FromMilliseconds(120);
        private static readonly TimeSpan FinalBottomNudgeDelay = TimeSpan.FromMilliseconds(80);

        private bool _isAutoScrollScheduled;
        private bool _hasPendingAutoScroll;
        private int _pendingAutoScrollIndex = -1;
        private int _lastAutoScrollIndex = -1;
        private bool _isUnloaded;
        private bool _eventsHooked;
        private ScrollViewer? _taskListScrollViewer;

        public SplitViewModel ViewModel => AppViewModel.Instance.Split;

        public SplitPage()
        {
            InitializeComponent();
            Loaded += SplitPage_Loaded;
            Unloaded += SplitPage_Unloaded;
        }

        private void FormatComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox comboBox)
                ComboBoxHelper.AutoFitWidth(comboBox);
        }

        private void SplitPage_Loaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = false;
            _taskListScrollViewer ??= FindDescendant<ScrollViewer>(SplitTaskListView);

            if (_eventsHooked) return;

            ViewModel.TaskStartedForScroll += ViewModel_TaskStartedForScroll;
            ViewModel.ProcessingCompletedForScroll += ViewModel_ProcessingCompletedForScroll;
            _eventsHooked = true;
        }

        private void SplitPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = true;
            _hasPendingAutoScroll = false;
            _pendingAutoScrollIndex = -1;

            if (!_eventsHooked) return;

            ViewModel.TaskStartedForScroll -= ViewModel_TaskStartedForScroll;
            ViewModel.ProcessingCompletedForScroll -= ViewModel_ProcessingCompletedForScroll;
            _eventsHooked = false;
        }

        private void ViewModel_TaskStartedForScroll(object? sender, SplitTask task)
        {
            int taskIndex = task.Index - 1;

            // 【核心修复 1】：如果遇到了队列开头的任务（通常是开启了新的一轮），强制重置历史追踪记录
            if (taskIndex == 0)
            {
                _pendingAutoScrollIndex = -1;
                _lastAutoScrollIndex = -1;
            }

            ScheduleAutoScroll(taskIndex);
        }

        private void ViewModel_ProcessingCompletedForScroll(object? sender, EventArgs e)
        {
            var dispatcher = DispatcherQueue;
            if (dispatcher != null && !_isUnloaded)
            {
                _ = SafeNudgeTaskListToBottomAsync(dispatcher);
            }
        }

        private void ScheduleAutoScroll(int itemIndex)
        {
            if (_isUnloaded || itemIndex < 0 || itemIndex >= ViewModel.Tasks.Count || !ViewModel.IsProcessing) return;

            // 总是记录最远的目标索引（保证多线程瞬间并发时，向下追踪最末尾的任务）
            _pendingAutoScrollIndex = Math.Max(_pendingAutoScrollIndex, itemIndex);
            _hasPendingAutoScroll = true;

            if (!_isAutoScrollScheduled)
            {
                _isAutoScrollScheduled = true;
                _ = RunAutoScrollAsync();
            }
        }

        private async Task RunAutoScrollAsync()
        {
            try
            {
                while (_hasPendingAutoScroll && !_isUnloaded)
                {
                    _hasPendingAutoScroll = false;
                    await Task.Delay(AutoFollowDebounce).ConfigureAwait(false);

                    int targetIndex = _pendingAutoScrollIndex;
                    if (_isUnloaded || !ViewModel.IsProcessing || targetIndex < 0 || targetIndex >= ViewModel.Tasks.Count || targetIndex == _lastAutoScrollIndex)
                    {
                        continue;
                    }

                    var dispatcher = DispatcherQueue;
                    if (dispatcher != null)
                    {
                        try { await EnqueueScrollIntoViewAsync(dispatcher, targetIndex).ConfigureAwait(false); } catch { }
                    }
                }
            }
            finally
            {
                _isAutoScrollScheduled = false;
                if (_hasPendingAutoScroll && !_isUnloaded)
                {
                    _isAutoScrollScheduled = true;
                    _ = RunAutoScrollAsync();
                }
            }
        }

        private async Task SafeNudgeTaskListToBottomAsync(DispatcherQueue dispatcher)
        {
            try { await NudgeTaskListToBottomAsync(dispatcher).ConfigureAwait(false); } catch { }
        }

        private async Task NudgeTaskListToBottomAsync(DispatcherQueue dispatcher)
        {
            await Task.Delay(FinalBottomNudgeDelay).ConfigureAwait(false);
            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    if (!_isUnloaded)
                    {
                        _taskListScrollViewer ??= FindDescendant<ScrollViewer>(SplitTaskListView);
                        _taskListScrollViewer?.ChangeView(null, _taskListScrollViewer.ScrollableHeight, null, true);
                    }
                    tcs.TrySetResult();
                }
                catch { tcs.TrySetResult(); }
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
                    if (!_isUnloaded && ViewModel.IsProcessing && targetIndex >= 0 && targetIndex < ViewModel.Tasks.Count)
                    {
                        var targetTask = ViewModel.Tasks[targetIndex];

                        // 【核心修复 2】：剔除多余的判断，单纯依赖 Default。
                        // Default 的特性是：如果任务在第一页（已可见），它不会触发任何滚动；
                        // 一旦并发任务跑到了屏幕外面，它会自动向下滚动，使其恰好对齐到 ListView 底部。
                        SplitTaskListView.ScrollIntoView(targetTask, ScrollIntoViewAlignment.Default);
                        _lastAutoScrollIndex = targetIndex;
                    }
                    tcs.TrySetResult();
                }
                catch { tcs.TrySetResult(); }
            }))
            {
                tcs.TrySetResult();
            }
            return tcs.Task;
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                T? nested = FindDescendant<T>(child);
                if (nested is not null) return nested;
            }
            return null;
        }

        private void DirectoryBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox) textBox.Text = string.Empty;
        }

        private async void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null) ViewModel.InputDirectory = folder.Path;
        }

        private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null) ViewModel.OutputDirectory = folder.Path;
        }

        private async void FileButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            try { await FilePickerService.OpenFileAsync(path); } catch { }
        }

        private void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            try { FilePickerService.RevealInExplorer(path); } catch { }
        }

        private void SplitTaskListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
        }

        private void PageRoot_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // 点击页面空白处 → 关闭报错浮窗
            if (ErrorDetailTip.IsOpen)
            {
                ErrorDetailTip.IsOpen = false;
            }
        }

        private void StatusTextBlock_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true; // 阻止冒泡到 PageRoot_Tapped

            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not SplitTask task) return;
            if (task.Status != ProcessStatus.Failed || string.IsNullOrWhiteSpace(task.DisplayStatus)) return;

            // 点击同一个 → 关闭
            if (ErrorDetailTip.IsOpen && ErrorDetailTip.Target == element)
            {
                ErrorDetailTip.IsOpen = false;
                return;
            }

            // 更新内容
            ErrorDetailText.Text = task.DisplayStatus;

            // 换 Target（不关浮窗，直接切过去）
            ErrorDetailTip.Target = element;
            ErrorDetailTip.IsOpen = true;

            // 阻止背景滚动
            SetBackgroundScrollEnabled(false);
        }

        private void SetBackgroundScrollEnabled(bool enabled)
        {
            _taskListScrollViewer ??= FindDescendant<ScrollViewer>(SplitTaskListView);
            if (_taskListScrollViewer != null)
                _taskListScrollViewer.VerticalScrollMode = enabled ? ScrollMode.Auto : ScrollMode.Disabled;
        }

        private void ErrorDetailTip_Loaded(object sender, RoutedEventArgs e)
        {
            // 隐藏 TeachingTip 自带的关闭按钮
            var closeBtn = FindVisualChildByName<Button>(ErrorDetailTip, "CloseButton");
            if (closeBtn != null)
                closeBtn.Visibility = Visibility.Collapsed;
        }

        private static T? FindVisualChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T element && element.Name == name)
                    return element;
                var found = FindVisualChildByName<T>(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }

        private void TipCopy_Click(object sender, RoutedEventArgs e)
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(ErrorDetailText.Text);
            Clipboard.SetContent(dataPackage);
        }

        private void TipClose_Click(object sender, RoutedEventArgs e)
        {
            ErrorDetailTip.IsOpen = false;
        }

        private void ErrorDetailTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args)
        {
            ErrorDetailTip.Target = null;
            SetBackgroundScrollEnabled(true);
        }
    }
}