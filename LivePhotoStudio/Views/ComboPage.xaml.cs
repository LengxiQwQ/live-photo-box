using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;
using System.Diagnostics;
using Windows.Storage.Pickers;
using LivePhotoStudio.ViewModels;
using LivePhotoStudio.Models;

namespace LivePhotoStudio.Views
{
    /// <summary>
    /// 用于根据主题动态返回按钮悬停颜色的转换器
    /// </summary>
    public class ThemeAwareButtonHoverConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            var theme = Application.Current.RequestedTheme;
            // 浅色模式：深灰色 #E5E5E5
            // 深色模式：使用系统的 ControlFillColorSecondary
            if (theme == ApplicationTheme.Light)
            {
                return new SolidColorBrush(ColorHelper.FromArgb(255, 229, 229, 229));
            }
            else
            {
                return Application.Current.Resources["ControlFillColorSecondary"];
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// 用于将 ProcessStatus 状态智能转换为对应颜色的转换器
    /// </summary>
    public class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is ProcessStatus status)
            {
                return status switch
                {
                    // 处理中 -> 琥珀黄
                    ProcessStatus.Processing => new SolidColorBrush(ColorHelper.FromArgb(255, 245, 158, 11)),
                    // 成功 -> 翠绿色
                    ProcessStatus.Success => new SolidColorBrush(ColorHelper.FromArgb(255, 16, 185, 129)),
                    // 失败 -> 警告红
                    ProcessStatus.Failed => new SolidColorBrush(ColorHelper.FromArgb(255, 239, 68, 68)),
                    // 等待/默认 -> 根据主题返回合适的颜色
                    _ => GetDefaultColorBrush()
                };
            }
            return GetDefaultColorBrush();
        }

        private SolidColorBrush GetDefaultColorBrush()
        {
            var theme = Application.Current.RequestedTheme;
            // 浅色模式：深灰色 #666666
            // 深色模式：浅灰色 #E0E0E0
            if (theme == ApplicationTheme.Light)
            {
                return new SolidColorBrush(ColorHelper.FromArgb(255, 102, 102, 102));
            }
            else
            {
                return new SolidColorBrush(ColorHelper.FromArgb(255, 224, 224, 224));
            }
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

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

        // 文件名的点击事件：在 Windows 系统默认应用打开文件
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

        /// <summary>
        /// 点击缩略图：在文件资源管理器中打开并定位到该文件
        /// </summary>
        private void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is Button btn && btn.Tag is string path && !string.IsNullOrWhiteSpace(path))
            {
                try
                {
                    // 使用 explorer.exe /select 在文件管理器中打开并选中该文件
                    var psi = new ProcessStartInfo("explorer.exe")
                    {
                        Arguments = $"/select,\"{path}\"",
                        UseShellExecute = true
                    };
                    Process.Start(psi);
                }
                catch { } // 忽略无权限或文件不存在的错误
            }
        }
    }
}