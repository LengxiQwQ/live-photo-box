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
    public sealed partial class ComboPage : Page
    {
        private static readonly TimeSpan FinalBottomNudgeDelay = TimeSpan.FromMilliseconds(80);

        private bool _isUnloaded;
        private bool _eventsHooked;
        private ScrollViewer? _taskListScrollViewer;
        private int _lastAutoScrollIndex = -1;

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
            _eventsHooked = true;
        }

        private void ComboPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = true;
            _lastAutoScrollIndex = -1;

            if (!_eventsHooked) return;

            ViewModel.TaskStartedForScroll -= ViewModel_TaskStartedForScroll;
            ViewModel.ProcessingCompletedForScroll -= ViewModel_ProcessingCompletedForScroll;
            _eventsHooked = false;
        }

        private void ViewModel_TaskStartedForScroll(object? sender, MergeTask task)
        {
            int taskIndex = task.Index - 1;
            int batchSize = 5;
            int batchStartIndex = (taskIndex / batchSize) * batchSize;
            int batchLastIndex = Math.Min(batchStartIndex + batchSize - 1, ViewModel.Tasks.Count - 1);
            ScrollToTask(batchLastIndex);
        }

        private void ViewModel_ProcessingCompletedForScroll(object? sender, EventArgs e)
        {
            var dispatcher = DispatcherQueue;
            if (dispatcher != null && !_isUnloaded)
            {
                _ = SafeNudgeTaskListToBottomAsync(dispatcher);
            }
        }

        private void ScrollToTask(int itemIndex)
        {
            if (_isUnloaded || itemIndex < 0 || itemIndex >= ViewModel.Tasks.Count || !ViewModel.IsProcessing || itemIndex == _lastAutoScrollIndex) return;

            var dispatcher = DispatcherQueue;
            if (dispatcher != null)
            {
                _ = EnqueueScrollIntoViewAsync(dispatcher, itemIndex);
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
            if (element.DataContext is not MergeTask task) return;
            if (task.Status != ProcessStatus.Failed || string.IsNullOrWhiteSpace(task.Details)) return;

            // 点击同一个 → 关闭
            if (ErrorDetailTip.IsOpen && ErrorDetailTip.Target == element)
            {
                ErrorDetailTip.IsOpen = false;
                return;
            }

            // 更新内容
            ErrorDetailText.Text = task.Details;

            // 换 Target（不关浮窗，直接切过去）
            ErrorDetailTip.Target = element;
            ErrorDetailTip.IsOpen = true;

            // 阻止背景滚动
            SetBackgroundScrollEnabled(false);
        }

        private void SetBackgroundScrollEnabled(bool enabled)
        {
            _taskListScrollViewer ??= FindDescendant<ScrollViewer>(ComboTaskListView);
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