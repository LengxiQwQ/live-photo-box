using LivePhotoBox.Services;
using LivePhotoBox.Models;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace LivePhotoBox.Views
{
    public sealed partial class ComboPage : Page
    {
        private const int BackwardPreloadRadius = 5;
        private const int ForwardPreloadRadius = 10;

        private int _lastRealizedItemIndex = -1;

        public AppViewModel ViewModel => AppViewModel.Instance;

        public ComboPage()
        {
            this.InitializeComponent();
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

        private async void FileGroupButton_Click(object sender, RoutedEventArgs e)
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

        private void ComboTaskListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue || args.Item is not LivePhotoMergeTask task)
            {
                return;
            }

            if (args.Phase == 0)
            {
                args.RegisterUpdateCallback(ComboTaskListView_ContainerContentChanging);
                return;
            }

            _ = task.EnsureThumbnailAsync(App.MainWindow?.DispatcherQueue);
            PreloadNeighborThumbnails(args.ItemIndex);
        }

        private void PreloadNeighborThumbnails(int centerIndex)
        {
            if (ViewModel.ComboTasks.Count == 0)
            {
                return;
            }

            bool isScrollingBackward = _lastRealizedItemIndex >= 0 && centerIndex < _lastRealizedItemIndex;

            int startIndex;
            int endIndex;

            if (isScrollingBackward)
            {
                startIndex = Math.Max(0, centerIndex - ForwardPreloadRadius);
                endIndex = Math.Min(ViewModel.ComboTasks.Count - 1, centerIndex + BackwardPreloadRadius);
            }
            else
            {
                startIndex = Math.Max(0, centerIndex - BackwardPreloadRadius);
                endIndex = Math.Min(ViewModel.ComboTasks.Count - 1, centerIndex + ForwardPreloadRadius);
            }

            _lastRealizedItemIndex = centerIndex;

            for (int index = startIndex; index <= endIndex; index++)
            {
                _ = ViewModel.ComboTasks[index].EnsureThumbnailAsync(App.MainWindow?.DispatcherQueue);
            }
        }
    }
}