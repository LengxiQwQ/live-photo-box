using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
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
            catch (Exception ex)
            {
                LogService.Debug($"RepairPage open file failed: {ex.Message}", LogSource.UI);
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
            catch (Exception ex)
            {
                LogService.Debug($"RepairPage reveal in explorer failed: {ex.Message}", LogSource.UI);
            }
        }

        private void StatusTextBlock_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not RepairTask task) return;
            if (task.Status != ProcessStatus.Failed || string.IsNullOrWhiteSpace(task.Details)) return;

            // 点击同一个 → 关闭
            if (ErrorDetailTip.IsOpen && ErrorDetailTip.Target == element)
            {
                ErrorDetailTip.IsOpen = false;
                return;
            }

            // 更新内容，换 Target
            ErrorDetailText.Text = task.Details;
            ErrorDetailTip.Target = element;
            ErrorDetailTip.IsOpen = true;
        }

        private void ErrorDetailTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args)
        {
            // 确保关闭后清理目标引用，避免悬空导致卡死
            ErrorDetailTip.Target = null;
        }
    }
}
