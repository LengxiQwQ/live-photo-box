using LivePhotoBox.Services;
using LivePhotoBox.Models;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace LivePhotoBox.Views
{
    public sealed partial class SplitPage : Page
    {
        private const int BackwardPreloadRadius = 2;
        private const int ForwardPreloadRadius = 5;

        private static readonly TimeSpan AutoFollowDebounce = TimeSpan.FromMilliseconds(250);
        private static readonly TimeSpan FinalBottomNudgeDelay = TimeSpan.FromMilliseconds(80);

        private int _lastRealizedItemIndex = -1;
        private int _preloadGeneration;
        private bool _isUnloaded;
        private bool _eventsHooked;

        private bool _isAutoScrollScheduled;
        private bool _hasPendingAutoScroll;
        private int _pendingAutoScrollIndex = -1;
        private int _lastAutoScrollIndex = -1;
        private ScrollViewer? _taskListScrollViewer;

        public SplitViewModel ViewModel => AppViewModel.Instance.Split;

        public SplitPage()
        {
            InitializeComponent();
            Loaded += SplitPage_Loaded;
            Unloaded += SplitPage_Unloaded;
        }

        private void SplitPage_Loaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = false;
            _taskListScrollViewer ??= FindDescendant<ScrollViewer>(SplitTaskListView);

            if (_eventsHooked)
            {
                return;
            }

            ViewModel.TaskStartedForScroll += ViewModel_TaskStartedForScroll;
            ViewModel.ProcessingCompletedForScroll += ViewModel_ProcessingCompletedForScroll;
            _eventsHooked = true;
        }

        private void SplitPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = true;
            _hasPendingAutoScroll = false;
            _pendingAutoScrollIndex = -1;

            if (!_eventsHooked)
            {
                return;
            }

            ViewModel.TaskStartedForScroll -= ViewModel_TaskStartedForScroll;
            ViewModel.ProcessingCompletedForScroll -= ViewModel_ProcessingCompletedForScroll;
            _eventsHooked = false;
        }

        private void ViewModel_TaskStartedForScroll(object? sender, SplitTask task)
        {
            int processingIndex = task.Index - 1;
            _ = task.EnsureThumbnailAsync(App.MainWindow?.DispatcherQueue, forceLoad: true);
            ScheduleAutoScroll(processingIndex);
        }

        private void ViewModel_ProcessingCompletedForScroll(object? sender, EventArgs e)
        {
            var dispatcher = DispatcherQueue;
            if (dispatcher == null || _isUnloaded)
            {
                return;
            }

            _ = SafeNudgeTaskListToBottomAsync(dispatcher);
        }

        private void ScheduleAutoScroll(int itemIndex)
        {
            if (_isUnloaded || itemIndex < 0 || itemIndex >= ViewModel.Tasks.Count || !ViewModel.IsProcessing)
            {
                return;
            }

            _pendingAutoScrollIndex = itemIndex;
            _hasPendingAutoScroll = true;

            if (_isAutoScrollScheduled)
            {
                return;
            }

            _isAutoScrollScheduled = true;
            _ = RunAutoScrollAsync();
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
                    if (_isUnloaded || !ViewModel.IsProcessing || targetIndex < 0 || targetIndex >= ViewModel.Tasks.Count)
                    {
                        continue;
                    }

                    if (targetIndex == _lastAutoScrollIndex)
                    {
                        continue;
                    }

                    var dispatcher = DispatcherQueue;
                    if (dispatcher == null)
                    {
                        continue;
                    }

                    try
                    {
                        await EnqueueScrollIntoViewAsync(dispatcher, targetIndex).ConfigureAwait(false);
                    }
                    catch
                    {
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
            try
            {
                await NudgeTaskListToBottomAsync(dispatcher).ConfigureAwait(false);
            }
            catch
            {
            }
        }

        private async Task NudgeTaskListToBottomAsync(DispatcherQueue dispatcher)
        {
            await Task.Delay(FinalBottomNudgeDelay).ConfigureAwait(false);

            var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);

            if (!dispatcher.TryEnqueue(() =>
            {
                try
                {
                    if (_isUnloaded)
                    {
                        tcs.TrySetResult();
                        return;
                    }

                    _taskListScrollViewer ??= FindDescendant<ScrollViewer>(SplitTaskListView);
                    _taskListScrollViewer?.ChangeView(null, _taskListScrollViewer.ScrollableHeight, null, true);
                    tcs.TrySetResult();
                }
                catch
                {
                    tcs.TrySetResult();
                }
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
                    if (_isUnloaded || !ViewModel.IsProcessing || targetIndex < 0 || targetIndex >= ViewModel.Tasks.Count)
                    {
                        tcs.TrySetResult();
                        return;
                    }

                    var targetTask = ViewModel.Tasks[targetIndex];

                    SplitTaskListView.ScrollIntoView(targetTask, ScrollIntoViewAlignment.Default);

                    _lastAutoScrollIndex = targetIndex;
                    tcs.TrySetResult();
                }
                catch
                {
                    tcs.TrySetResult();
                }
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
                if (child is T match)
                {
                    return match;
                }

                T? nested = FindDescendant<T>(child);
                if (nested is not null)
                {
                    return nested;
                }
            }

            return null;
        }

        private void DirectoryBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox)
            {
                textBox.Text = string.Empty;
            }
        }

        private async void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null)
            {
                ViewModel.InputDirectory = folder.Path;
            }
        }

        private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null)
            {
                ViewModel.OutputDirectory = folder.Path;
            }
        }

        private async void FileButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                await FilePickerService.OpenFileAsync(path);
            }
            catch
            {
            }
        }

        private void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path))
            {
                return;
            }

            try
            {
                FilePickerService.RevealInExplorer(path);
            }
            catch
            {
            }
        }

        private void SplitTaskListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is not SplitTask task) return;

            if (args.InRecycleQueue)
            {
                task.CancelThumbnailLoad();
                return;
            }

            if (args.Phase == 0)
            {
                args.RegisterUpdateCallback(SplitTaskListView_ContainerContentChanging);
                args.Handled = true;
                return;
            }

            _ = task.EnsureThumbnailAsync(App.MainWindow?.DispatcherQueue, forceLoad: ViewModel.IsProcessing);

            if (!ViewModel.IsProcessing)
            {
                _ = PreloadNeighborThumbnailsSafeAsync(args.ItemIndex);
            }
        }

        private async Task PreloadNeighborThumbnailsSafeAsync(int centerIndex)
        {
            try
            {
                if (ViewModel.Tasks.Count == 0 || ViewModel.IsProcessing) return;

                int generation = ++_preloadGeneration;
                await Task.Delay(40).ConfigureAwait(false);
                if (generation != _preloadGeneration || ViewModel.Tasks.Count == 0 || _isUnloaded || ViewModel.IsProcessing) return;

                bool isScrollingBackward = _lastRealizedItemIndex >= 0 && centerIndex < _lastRealizedItemIndex;
                int startIndex;
                int endIndex;

                if (isScrollingBackward)
                {
                    startIndex = Math.Max(0, centerIndex - ForwardPreloadRadius);
                    endIndex = Math.Min(ViewModel.Tasks.Count - 1, centerIndex + BackwardPreloadRadius);
                }
                else
                {
                    startIndex = Math.Max(0, centerIndex - BackwardPreloadRadius);
                    endIndex = Math.Min(ViewModel.Tasks.Count - 1, centerIndex + ForwardPreloadRadius);
                }

                _lastRealizedItemIndex = centerIndex;

                SplitThumbnailService.Preload(
                    ViewModel.Tasks
                        .Skip(startIndex)
                        .Take(endIndex - startIndex + 1)
                        .Where(task => task.Index != centerIndex + 1)
                        .Where(task => task.Thumbnail is null)
                        .Select(task => task.SourcePath),
                    App.MainWindow?.DispatcherQueue);
            }
            catch
            {
            }
        }
    }
}