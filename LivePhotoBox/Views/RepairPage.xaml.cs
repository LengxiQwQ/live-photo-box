using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace LivePhotoBox.Views
{
    public sealed partial class RepairPage : Page
    {
        public AppViewModel ViewModel => AppViewModel.Instance;

        public RepairPage()
        {
            this.InitializeComponent();
        }

        private async void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null)
            {
                ViewModel.RepairInputDirectory = folder.Path;

                // 选择有效路径后，自动触发扫描操作
                if (ViewModel.ScanRepairDirectoryCommand.CanExecute(null))
                {
                    ViewModel.ScanRepairDirectoryCommand.Execute(null);
                }
            }
        }

        private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null)
            {
                ViewModel.RepairOutputDirectory = folder.Path;
            }
        }
    }
}