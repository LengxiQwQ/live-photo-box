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

        private async void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder != null)
            {
                ViewModel.InputDirectory = folder.Path;
                // [修改] 移除了自动扫描，改为用户手动点击“开始匹配”按钮
            }
        }

        private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await PickFolderAsync();
            if (folder != null)
            {
                ViewModel.OutputDirectory = folder.Path;
            }
        }

        private async System.Threading.Tasks.Task<Windows.Storage.StorageFolder?> PickFolderAsync()
        {
            var folderPicker = new FolderPicker();
            folderPicker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(folderPicker, hwnd);

            return await folderPicker.PickSingleFolderAsync();
        }
    }
}