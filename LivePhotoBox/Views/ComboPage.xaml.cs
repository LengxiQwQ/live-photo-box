using LivePhotoBox.Helpers;
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

        private void ProtocolComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox comboBox)
                ComboBoxHelper.AutoFitWidth(comboBox);
        }

        private void ComboPage_Loaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = false;
            _taskListScrollViewer ??= FindDescendant<ScrollViewer>(ComboTaskListView);

            if (_eventsHooked) return;

            ViewModel.TaskStartedForScroll += ViewModel_TaskStartedForScroll;
            ViewModel.ProcessingCompletedForScroll += ViewModel_ProcessingCompletedForScroll;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            _eventsHooked = true;
        }

        private void ComboPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = true;
            _hasPendingAutoScroll = false;
            _pendingAutoScrollIndex = -1;
            _lastAutoScrollIndex = -1;

            if (!_eventsHooked) return;

            ViewModel.TaskStartedForScroll -= ViewModel_TaskStartedForScroll;
            ViewModel.ProcessingCompletedForScroll -= ViewModel_ProcessingCompletedForScroll;
            ViewModel.PropertyChanged -= ViewModel_PropertyChanged;
            _eventsHooked = false;
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.IsScanning) && !ViewModel.IsScanning && ViewModel.Tasks.Count > 0)
            {
                _ = FinalScanScrollAsync();
            }
        }

        private async Task FinalScanScrollAsync()
        {
            await Task.Delay(30).ConfigureAwait(false);
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (_isUnloaded || ViewModel.Tasks.Count == 0) return;
                ComboTaskListView.ScrollIntoView(ViewModel.Tasks[ViewModel.Tasks.Count - 1], ScrollIntoViewAlignment.Default);
            });
        }

        private void ViewModel_TaskStartedForScroll(object? sender, ComboTask task)
        {
            int taskIndex = task.Index - 1;

            // 新一轮开始时重置追踪
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

        // ── 防抖滚动系统（照搬 Split 页面） ──────────────

        private void ScheduleAutoScroll(int itemIndex)
        {
            if (_isUnloaded || itemIndex < 0 || itemIndex >= ViewModel.Tasks.Count) return;
            if (!ViewModel.IsProcessing && !ViewModel.IsScanning) return;

            // 始终追踪最远的目标索引（多线程瞬间并发时也不遗漏）
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
                    if (_isUnloaded || (!ViewModel.IsProcessing && !ViewModel.IsScanning) || targetIndex < 0 || targetIndex >= ViewModel.Tasks.Count || targetIndex == _lastAutoScrollIndex)
                    {
                        continue;
                    }

                    var dispatcher = DispatcherQueue;
                    if (dispatcher != null)
                    {
                        try { await EnqueueScrollIntoViewAsync(dispatcher, targetIndex).ConfigureAwait(false); }
                        catch (Exception ex) { LogService.Debug($"ComboPage auto-scroll failed: {ex.Message}", LogSource.UI); }
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
            try { await NudgeTaskListToBottomAsync(dispatcher).ConfigureAwait(false); }
            catch (Exception ex) { LogService.Debug($"ComboPage auto-scroll nudge failed: {ex.Message}", LogSource.UI); }
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
                catch (Exception ex) { LogService.Debug($"ComboPage scroll nudge dispatcher error: {ex.Message}", LogSource.UI); tcs.TrySetResult(); }
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
                    if (!_isUnloaded && (ViewModel.IsProcessing || ViewModel.IsScanning) && targetIndex >= 0 && targetIndex < ViewModel.Tasks.Count)
                    {
                        var targetTask = ViewModel.Tasks[targetIndex];

                        // Default 对齐：已在屏幕上则不滚，出了屏幕才滚到可见位置
                        ComboTaskListView.ScrollIntoView(targetTask, ScrollIntoViewAlignment.Default);
                        _lastAutoScrollIndex = targetIndex;
                    }
                    tcs.TrySetResult();
                }
                catch (Exception ex) { LogService.Debug($"ComboPage scroll-into-view dispatcher error: {ex.Message}", LogSource.UI); tcs.TrySetResult(); }
            }))
            {
                tcs.TrySetResult();
            }
            return tcs.Task;
        }

        // ── 工具方法 ────────────────────────────────────

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
            try { await FilePickerService.OpenFileAsync(path); }
            catch (Exception ex) { LogService.Debug($"ComboPage open file failed: {ex.Message}", LogSource.UI); }
        }

        private void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            try { FilePickerService.RevealInExplorer(path); }
            catch (Exception ex) { LogService.Debug($"ComboPage reveal in explorer failed: {ex.Message}", LogSource.UI); }
        }

        private void ComboTaskListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
        }

        private void StatusTextBlock_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not ComboTask task) return;
            if (task.Status != ProcessStatus.Failed || string.IsNullOrWhiteSpace(task.Details)) return;

            if (ErrorDetailTip.IsOpen && ErrorDetailTip.Target == element)
            {
                ErrorDetailTip.IsOpen = false;
                return;
            }

            ErrorDetailText.Text = task.Details;
            ErrorDetailTip.Target = element;
            ErrorDetailTip.IsOpen = true;
        }

        private void ErrorDetailTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args)
        {
            ErrorDetailTip.Target = null;
        }
    }
}
