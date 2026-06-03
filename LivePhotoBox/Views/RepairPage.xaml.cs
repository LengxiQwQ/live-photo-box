using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace LivePhotoBox.Views
{
    public sealed partial class RepairPage : Page
    {
        public RepairViewModel ViewModel => AppViewModel.Instance.Repair;

        public RepairPage()
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

                await Task.Delay(100);

                if (ViewModel.ScanDirectoryCommand.CanExecute(null))
                {
                    ViewModel.ScanDirectoryCommand.Execute(null);
                }
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
    }
}
