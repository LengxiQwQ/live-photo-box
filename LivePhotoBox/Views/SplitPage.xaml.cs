using LivePhotoBox.Services;
using LivePhotoBox.Models;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Linq;
using System.Threading.Tasks;

namespace LivePhotoBox.Views
{
    public sealed partial class SplitPage : Page
    {
        private const int BackwardPreloadRadius = 4;
        private const int ForwardPreloadRadius = 14;

        private int _lastRealizedItemIndex = -1;
        private int _preloadGeneration;
        private bool _isUnloaded;
        private bool _eventsHooked;

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

            if (_eventsHooked)
            {
                return;
            }

            ViewModel.TaskCompletedForScroll += ViewModel_TaskCompletedForScroll;
            ViewModel.ProcessingCompletedForScroll += ViewModel_ProcessingCompletedForScroll;
            _eventsHooked = true;
        }

        private void SplitPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _isUnloaded = true;

            if (!_eventsHooked)
            {
                return;
            }

            ViewModel.TaskCompletedForScroll -= ViewModel_TaskCompletedForScroll;
            ViewModel.ProcessingCompletedForScroll -= ViewModel_ProcessingCompletedForScroll;
            _eventsHooked = false;
        }

        private void ViewModel_TaskCompletedForScroll(object? sender, SplitTask task)
        {
            if (_isUnloaded || !ViewModel.IsProcessing)
            {
                return;
            }

            try
            {
                SplitTaskListView.ScrollIntoView(task, ScrollIntoViewAlignment.Leading);
            }
            catch
            {
            }
        }

        private void ViewModel_ProcessingCompletedForScroll(object? sender, EventArgs e)
        {
            if (_isUnloaded)
            {
                return;
            }

            try
            {
                if (ViewModel.Tasks.Count > 0)
                {
                    SplitTaskListView.ScrollIntoView(ViewModel.Tasks[^1], ScrollIntoViewAlignment.Leading);
                }
            }
            catch
            {
            }
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
