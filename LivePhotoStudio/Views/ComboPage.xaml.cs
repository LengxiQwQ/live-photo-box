using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Storage.Pickers;
using LivePhotoStudio.ViewModels;

namespace LivePhotoStudio.Views
{
    public sealed partial class ComboPage : Page
    {
        public SharedViewModel ViewModel => SharedViewModel.Instance;

        public ComboPage()
        {
            this.InitializeComponent();
        }

        public Visibility BoolToVisibility(bool val) => val ? Visibility.Visible : Visibility.Collapsed;
        public Visibility InverseBoolToVisibility(bool val) => val ? Visibility.Collapsed : Visibility.Visible;

        private async void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder != null) ViewModel.InputDirectory = folder.Path;
        }

        private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder != null) ViewModel.OutputDirectory = folder.Path;
        }

        private async System.Threading.Tasks.Task<Windows.Storage.StorageFolder?> PickFolderAsync()
        {
            var folderPicker = new FolderPicker();
            folderPicker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

            return await folderPicker.PickSingleFolderAsync();
        }

        // 新增：文件组的点击事件，使用 Windows 系统默认应用打开文件
        private async void FileGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path && !string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    var file = await Windows.Storage.StorageFile.GetFileFromPathAsync(path);
                    await Windows.System.Launcher.LaunchFileAsync(file);
                }
                catch { } // 忽略无权限或文件不存在的错误
            }
        }
    }
}