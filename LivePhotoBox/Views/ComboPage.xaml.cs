using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;

namespace LivePhotoBox.Views
{
    public sealed partial class ComboPage : Page
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

        public ComboViewModel ViewModel => AppViewModel.Instance.Combo;

        public ComboPage()
        {
            InitializeComponent();
            Loaded += ComboPage_Loaded;
            Unloaded += ComboPage_Unloaded;
        }

        private void ComboPage_Loaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = false;
            _taskListScrollViewer ??= FindDescendant<ScrollViewer>(ComboTaskListView);

            if (_eventsHooked) return;

            ViewModel.TaskStartedForScroll += ViewModel_TaskStartedForScroll;
            ViewModel.ProcessingCompletedForScroll += ViewModel_ProcessingCompletedForScroll;
            _eventsHooked = true;
        }

        private void ComboPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = true;
            _hasPendingAutoScroll = false;
            _pendingAutoScrollIndex = -1;

            if (!_eventsHooked) return;

            ViewModel.TaskStartedForScroll -= ViewModel_TaskStartedForScroll;
            ViewModel.ProcessingCompletedForScroll -= ViewModel_ProcessingCompletedForScroll;
            _eventsHooked = false;
        }

        private void ViewModel_TaskStartedForScroll(object? sender, MergeTask task)
        {
            ScheduleAutoScroll(task.Index - 1);
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

            _pendingAutoScrollIndex = itemIndex;
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
                        _taskListScrollViewer ??= FindDescendant<ScrollViewer>(ComboTaskListView);
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
                        ComboTaskListView.ScrollIntoView(targetTask, ScrollIntoViewAlignment.Default);
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

        private async void FileGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            try { await FilePickerService.OpenFileAsync(path); } catch { }
        }

        private void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            try { FilePickerService.RevealInExplorer(path); } catch { }
        }

        // ==========================================
        // ✨ 老版本的精髓：UI 不干预，交给 XAML 自己绑定的 Getter
        // ==========================================
        private void ComboTaskListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            // 彻底掏空！不需要强行加载缩略图了。
            // 当滚动到这个列表项时，XAML 的 {x:Bind Thumbnail} 会自动触发 MergeTask 里的 get_Thumbnail 逻辑！
        }
    }
}