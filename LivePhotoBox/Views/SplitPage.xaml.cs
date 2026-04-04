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
        private const int BackwardPreloadRadius = 5;
        private const int ForwardPreloadRadius = 10;

        private int _lastRealizedItemIndex = -1;
        private int _preloadGeneration;

        public AppViewModel ViewModel => AppViewModel.Instance;

        public SplitPage()
        {
            InitializeComponent();
        }

        private async void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null)
            {
                ViewModel.SplitInputDirectory = folder.Path;
            }
        }

        private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null)
            {
                ViewModel.SplitOutputDirectory = folder.Path;
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
            if (args.InRecycleQueue || args.Item is not LivePhotoSplitTask task)
            {
                return;
            }

            if (args.Phase == 0)
            {
                args.RegisterUpdateCallback(SplitTaskListView_ContainerContentChanging);
                return;
            }

            _ = task.EnsureThumbnailAsync(App.MainWindow?.DispatcherQueue);
            _ = PreloadNeighborThumbnailsAsync(args.ItemIndex);
        }

        private async Task PreloadNeighborThumbnailsAsync(int centerIndex)
        {
            if (ViewModel.SplitTasks.Count == 0)
            {
                return;
            }

            int generation = ++_preloadGeneration;
            await Task.Delay(80);
            if (generation != _preloadGeneration || ViewModel.SplitTasks.Count == 0)
            {
                return;
            }

            bool isScrollingBackward = _lastRealizedItemIndex >= 0 && centerIndex < _lastRealizedItemIndex;

            int startIndex;
            int endIndex;

            if (isScrollingBackward)
            {
                startIndex = Math.Max(0, centerIndex - ForwardPreloadRadius);
                endIndex = Math.Min(ViewModel.SplitTasks.Count - 1, centerIndex + BackwardPreloadRadius);
            }
            else
            {
                startIndex = Math.Max(0, centerIndex - BackwardPreloadRadius);
                endIndex = Math.Min(ViewModel.SplitTasks.Count - 1, centerIndex + ForwardPreloadRadius);
            }

            _lastRealizedItemIndex = centerIndex;

            SplitThumbnailService.Preload(
                ViewModel.SplitTasks
                    .Skip(startIndex)
                    .Take(endIndex - startIndex + 1)
                    .Where(task => task.Index != centerIndex + 1)
                    .Where(task => task.Thumbnail is null)
                    .Select(task => task.SourcePath),
                App.MainWindow?.DispatcherQueue);
        }
    }
}
