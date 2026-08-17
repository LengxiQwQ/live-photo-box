/*
 * AboutPage.xaml.cs
 *
 * 关于页面的代码后置。绑定 AboutViewModel，处理页面事件：
 *   - 检查更新按钮（复用 SettingsPage 的更新检测对话框逻辑）
 *   - 打开应用安装目录
 *   - 复制作者邮箱 / QQ 联系信息到剪贴板
 */

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
using LivePhotoBox.Models;
using LivePhotoBox.Services;
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
        /// 打开安装位置按钮点击：在资源管理器中打开应用安装目录。
        /// 复用 FilePickerService.OpenFolderInExplorer（已封装 explorer.exe 调用与错误日志）。
        /// </summary>
        private void OpenInstallFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                FilePickerService.OpenFolderInExplorer(AppContext.BaseDirectory);
            }
            catch (Exception ex)
            {
                LogService.Debug($"AboutPage open install folder failed: {ex.Message}", LogSource.UI);
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