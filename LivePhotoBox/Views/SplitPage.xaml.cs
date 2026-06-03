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

        public SplitViewModel ViewModel => AppViewModel.Instance.Split;

        public SplitPage()
        {
            InitializeComponent();
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
            if (args.InRecycleQueue || args.Item is not SplitTask task)
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
            if (ViewModel.Tasks.Count == 0)
            {
                return;
            }

            int generation = ++_preloadGeneration;
            await Task.Delay(80);
            if (generation != _preloadGeneration || ViewModel.Tasks.Count == 0)
            {
                return;
            }

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
    }
}
