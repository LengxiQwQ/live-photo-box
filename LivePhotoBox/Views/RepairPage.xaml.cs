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

                // 【核心修复】延迟 100 毫秒，防止底层 UI 状态消息丢失
                await Task.Delay(100);

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

        // 🎯 补上了这个缺失的点击打开文件方法
        // 点击打开文件
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
                // 忽略打开失败的异常
            }
        }

        // 点击打开文件夹所在位置
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
                // 忽略打开失败的异常
            }
        }
    }
}