// ******************************************************************
// 文件名: AboutPage.xaml.cs
// 作者: LengxiQwQ
// 描述: AboutPage 的后台代码，负责绑定 ViewModel
// ******************************************************************

using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices.WindowsRuntime;
using System.Threading.Tasks;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using Windows.Foundation;
using Windows.Foundation.Collections;
using LivePhotoBox.ViewModels;

namespace LivePhotoBox.Views
{
    /// <summary>
    /// 可用于自身或导航至 Frame 内部的关于页面。
    /// </summary>
    public sealed partial class AboutPage : Page
    {
        public AboutViewModel ViewModel { get; }

        public AboutPage()
        {
            this.InitializeComponent();
            ViewModel = new AboutViewModel();
        }

        /// <summary>
        /// 检查更新按钮点击：手动触发版本检测并展示更新对话框。
        /// 复用 SettingsPage 中的共享更新对话框逻辑（启动自动检查也走同一套方法）。
        /// </summary>
        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (XamlRoot == null) return;

            // 禁用按钮防止重复点击
            if (sender is Button btn) btn.IsEnabled = false;

            try
            {
                await SettingsPage.PerformUpdateCheckAndShowDialogAsync(XamlRoot);
            }
            finally
            {
                if (sender is Button btn2) btn2.IsEnabled = true;
            }
        }

        /// <summary>
        /// 复制邮箱地址到剪贴板，并显示反馈。
        /// </summary>
        private async void OnCopyEmailClick(object sender, RoutedEventArgs e)
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText("lengxiowo@gmail.com");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

            // 显示"已复制"反馈
            CopiedFeedback.Visibility = Visibility.Visible;
            await Task.Delay(2000);
            CopiedFeedback.Visibility = Visibility.Collapsed;
        }

        /// <summary>
        /// 复制 QQ 号到剪贴板，并显示反馈。
        /// </summary>
        private async void OnCopyQQClick(object sender, RoutedEventArgs e)
        {
            var dataPackage = new Windows.ApplicationModel.DataTransfer.DataPackage();
            dataPackage.SetText("3197635836");
            Windows.ApplicationModel.DataTransfer.Clipboard.SetContent(dataPackage);

            CopiedFeedback.Visibility = Visibility.Visible;
            await Task.Delay(2000);
            CopiedFeedback.Visibility = Visibility.Collapsed;
        }
    }
}