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
using Windows.ApplicationModel.DataTransfer;

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

        private void PageRoot_Tapped(object sender, TappedRoutedEventArgs e)
        {
            // 点击页面空白处 → 关闭报错浮窗
            if (ErrorDetailTip.IsOpen)
            {
                ErrorDetailTip.IsOpen = false;
            }
        }

        private void StatusTextBlock_Tapped(object sender, TappedRoutedEventArgs e)
        {
            e.Handled = true; // 阻止冒泡到 PageRoot_Tapped

            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not RepairTask task) return;
            if (task.Status != ProcessStatus.Failed || string.IsNullOrWhiteSpace(task.Details)) return;

            // 点击同一个 → 关闭
            if (ErrorDetailTip.IsOpen && ErrorDetailTip.Target == element)
            {
                ErrorDetailTip.IsOpen = false;
                return;
            }

            // 更新内容
            ErrorDetailText.Text = task.Details;

            // 换 Target（不关浮窗，直接切过去）
            ErrorDetailTip.Target = element;
            ErrorDetailTip.IsOpen = true;

            // 阻止背景滚动
            SetBackgroundScrollEnabled(false);
        }

        private void SetBackgroundScrollEnabled(bool enabled)
        {
            var scrollViewer = FindDescendant<ScrollViewer>(RepairTaskListView);
            if (scrollViewer != null)
                scrollViewer.VerticalScrollMode = enabled ? ScrollMode.Auto : ScrollMode.Disabled;
        }

        private void ErrorDetailTip_Loaded(object sender, RoutedEventArgs e)
        {
            // 隐藏 TeachingTip 自带的关闭按钮
            var closeBtn = FindVisualChildByName<Button>(ErrorDetailTip, "CloseButton");
            if (closeBtn != null)
                closeBtn.Visibility = Visibility.Collapsed;
        }

        private static T? FindVisualChildByName<T>(DependencyObject parent, string name) where T : FrameworkElement
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T element && element.Name == name)
                    return element;
                var found = FindVisualChildByName<T>(child, name);
                if (found != null)
                    return found;
            }
            return null;
        }

        private void TipCopy_Click(object sender, RoutedEventArgs e)
        {
            var dataPackage = new DataPackage();
            dataPackage.SetText(ErrorDetailText.Text);
            Clipboard.SetContent(dataPackage);
        }

        private void TipClose_Click(object sender, RoutedEventArgs e)
        {
            ErrorDetailTip.IsOpen = false;
        }

        private void ErrorDetailTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args)
        {
            ErrorDetailTip.Target = null;
            SetBackgroundScrollEnabled(true);
        }

        private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                T? nested = FindDescendant<T>(child);
                if (nested is not null) return nested;
            }
            return null;
        }
    }
}
