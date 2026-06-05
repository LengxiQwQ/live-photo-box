using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LivePhotoBox.Views
{
    public sealed partial class ComboPage : Page
    {
        private const int BackwardPreloadRadius = 4;
        private const int ForwardPreloadRadius = 14;
        private static readonly TimeSpan AutoFollowDebounce = TimeSpan.FromMilliseconds(120);

        private int _lastRealizedItemIndex = -1;
        private int _preloadGeneration;
        private bool _isAutoScrollScheduled;
        private bool _hasPendingAutoScroll;
        private int _pendingAutoScrollIndex = -1;
        private int _lastAutoScrollIndex = -1;
        private bool _isUnloaded;

        public ComboViewModel ViewModel => AppViewModel.Instance.Combo;

        public ComboPage()
        {
            InitializeComponent();
            ViewModel.TaskStartedForScroll += ViewModel_TaskStartedForScroll;
            Unloaded += ComboPage_Unloaded;
        }

        private void ComboPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = true;
            ViewModel.TaskStartedForScroll -= ViewModel_TaskStartedForScroll;
            Unloaded -= ComboPage_Unloaded;
        }

        private void ViewModel_TaskStartedForScroll(object? sender, MergeTask task)
        {
            int processingIndex = task.Index - 1;
            _ = task.EnsureThumbnailAsync(App.MainWindow?.DispatcherQueue, forceLoad: true);
            ScheduleAutoScroll(processingIndex);
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

                    await EnqueueScrollIntoViewAsync(dispatcher, targetIndex).ConfigureAwait(false);
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
                    ComboTaskListView.ScrollIntoView(targetTask, ScrollIntoViewAlignment.Leading);
                    _lastAutoScrollIndex = targetIndex;
                    tcs.TrySetResult();
                }
                catch (Exception ex)
                {
                    tcs.TrySetException(ex);
                }
            }))
            {
                tcs.TrySetResult();
            }

            return tcs.Task;
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

        private void ComboTaskListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is not MergeTask task) return;

            if (args.InRecycleQueue)
            {
                task.CancelThumbnailLoad();
                return;
            }

            if (args.Phase == 0)
            {
                args.RegisterUpdateCallback(ComboTaskListView_ContainerContentChanging);
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

                ThumbnailService.Preload(
                    ViewModel.Tasks
                        .Skip(startIndex)
                        .Take(endIndex - startIndex + 1)
                        .Where(task => task.Index != centerIndex + 1)
                        .Where(task => task.Thumbnail is null)
                        .Select(task => task.ImagePath),
                    App.MainWindow?.DispatcherQueue);
            }
            catch
            {
            }
        }
    }
}
