/*
 * SettingsPageOld.xaml.cs
 *
 * 设置页面（经典版）的代码后置。
 * 旧版设置布局，与 SettingsPage（现代版）功能等价但使用不同的 XAML 布局。
 * 通过 AppSettingsService("UseClassicSettingsPage") 控制使用哪个版本。
 *
 * 对应 ViewModel：SettingsViewModel / AboutViewModel
 *
 * 生命周期：
 *   - 构造函数 → 初始化组件 → Loaded 中预加载 Banner 和崩溃检测
 *   - 各设置项通过事件处理器直接更新 ViewModel
 */

using LivePhotoBox.Helpers;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LivePhotoBox.Models;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;

namespace LivePhotoBox.Views
{
    public sealed partial class SettingsPageOld : Page, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // 关联的 SettingsViewModel
        public SettingsViewModel ViewModel => AppViewModel.Instance.Settings;

        // 关联的 AboutViewModel（用于崩溃日志等功能）
        public AboutViewModel AboutViewModel => AppViewModel.Instance.About;

        // 调试工具区域的可见性
        public Visibility TestToolsVisibility => _isTestToolsVisible ? Visibility.Visible : Visibility.Collapsed;

        // 崩溃通知横幅的可见性
        public Visibility CrashNoticeVisibility => _isTestToolsVisible && LogService.LastSessionCrashed
            ? Visibility.Visible : Visibility.Collapsed;

        // 崩溃通知文本
        public string CrashNoticeText => ResourceService.GetString("SettingsPage_CrashNotice_Text");

        // 调试工具开关按钮文本
        public string TestToolsToggleButtonText => ResourceService.GetString(_isTestToolsVisible
            ? "SettingsPage_TestHide_Button_Text"
            : "SettingsPage_TestShow_Button_Text");

        private bool _isTestToolsVisible;

        // 构造函数：初始化组件，注册 Loaded 事件（预加载 Banner + 崩溃检测）
        public SettingsPageOld()
        {
            InitializeComponent();
            Loaded += (_, _) =>
            {
                // 后台预加载 Banner，不阻塞页面打开（fire-and-forget）
                _ = ViewModel.EnsureBannersPreloadedAsync();

                // 如果上一次非正常退出，自动展开日志与调试工具区
                if (LogService.LastSessionCrashed && !_isTestToolsVisible)
                {
                    _isTestToolsVisible = true;
                    AboutViewModel.RefreshCrashLogs();
                    Bindings.Update();
                }
            };
        }

        // 所有外观面板 ComboBox 共用：自动按最宽选项定宽
        private void AppearanceComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox comboBox)
                ComboBoxHelper.AutoFitWidth(comboBox);
        }

        // 硬件 ComboBox 异步加载完成后再测量
        private void HardwareComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox comboBox)
                ComboBoxHelper.AutoFitWidthAsync(comboBox, ViewModel.AvailableHardware);
        }

        // 切换调试工具区域的显示/隐藏
        private void ToggleTestToolsButton_Click(object sender, RoutedEventArgs e)
        {
            _isTestToolsVisible = !_isTestToolsVisible;
            AboutViewModel.RefreshCrashLogs();
            NotifyPropertyChanged(nameof(TestToolsVisibility));
            NotifyPropertyChanged(nameof(CrashNoticeVisibility));
            NotifyPropertyChanged(nameof(TestToolsToggleButtonText));
            Bindings.Update();
        }

        // 点击扫描缩略图标签切换设置
        private void ScanThumbnailLabel_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            ViewModel.IsRepairScanLoadThumbnail = !ViewModel.IsRepairScanLoadThumbnail;
        }

        // 点击 HEIC 修复标签切换设置
        private void HeicRepairLabel_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            ViewModel.IsHeicRepairEnabled = !ViewModel.IsHeicRepairEnabled;
        }

        // 点击非 Live Photo 视频修复标签切换设置
        private void NonLivePhotoVideoRepairLabel_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            ViewModel.IsNonLivePhotoVideoRepairEnabled = !ViewModel.IsNonLivePhotoVideoRepairEnabled;
        }

        // 点击严格 Live Photo 扫描标签切换设置
        private void StrictLivePhotoScanLabel_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            ViewModel.IsStrictLivePhotoScanEnabled = !ViewModel.IsStrictLivePhotoScanEnabled;
        }

        // 重启应用按钮点击：弹出确认对话框，确认后启动新进程并关闭当前应用
        private async void RestartAppButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow?.Content?.XamlRoot == null) return;

            bool confirmed = await DialogService.ShowDualAsync(
                App.MainWindow.Content.XamlRoot,
                ResourceService.GetString("SettingsPage_Restart_Confirm_Title"),
                ResourceService.GetString("SettingsPage_Restart_Confirm_Message"),
                primaryText: ResourceService.GetString("Msg_Confirm"),
                closeText: ResourceService.GetString("Msg_Cancel"));
            if (!confirmed) return;

            // 启动新实例后关闭当前应用
            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                try
                {
                    Process.Start(new ProcessStartInfo(processPath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    LogService.Error($"Failed to restart app: {ex.Message}", ex, LogSource.UI);
                    return;
                }
            }

            Application.Current.Exit();
        }

        // 预览崩溃对话框按钮点击：模拟显示崩溃报告弹窗
        private async void PreviewCrashDialogButton_Click(object sender, RoutedEventArgs e)
        {
            if (XamlRoot == null) return;
            string? logPath = LogService.PreviousLogPath;
            if (!string.IsNullOrWhiteSpace(logPath) && !System.IO.File.Exists(logPath))
                logPath = null;

            LogService.Info($"PreviewCrashDialog requested. File='{System.IO.Path.GetFileName(logPath)}'", LogSource.UI);
            await CrashHandler.ShowCrashDialogAsync(XamlRoot, logPath);
        }

        // 打开 Microsoft Store 应用页面，优先唤起 Store 应用，降级到浏览器
        private static async Task OpenStoreLinkAsync(string productId)
        {
            // 优先唤起 Microsoft Store 应用
            var storeUri = new Uri($"ms-windows-store://pdp/?ProductId={productId}");
            if (await Windows.System.Launcher.LaunchUriAsync(storeUri))
                return;

            // Store 不可用时降级到浏览器
            var webUri = new Uri($"https://apps.microsoft.com/detail/{productId}");
            await Windows.System.Launcher.LaunchUriAsync(webUri);
        }

        // 打开 HEIF 图像扩展的 Store 页面
        private async void OpenHeifStoreLink_Click(object sender, RoutedEventArgs e)
        {
            await OpenStoreLinkAsync("9PMMSR1CGPWG");
        }

        // 打开 HEVC 视频扩展的 Store 页面
        private async void OpenHevcStoreLink_Click(object sender, RoutedEventArgs e)
        {
            await OpenStoreLinkAsync("9n4wgh0z6vhq");
        }

        // 上一个横幅预设
        private void PrevBanner_Click(object sender, RoutedEventArgs e) => ViewModel.PrevBanner();

        // 下一个横幅预设
        private void NextBanner_Click(object sender, RoutedEventArgs e) => ViewModel.NextBanner();

        // Resets the banner to the first (default) preset and turns off random mode.
        private void ResetBanner_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsBannerRandomEnabled = false;
            ViewModel.BannerPresetIndex = 0;
        }

        // 恢复默认设置按钮点击：弹出确认对话框，确认后执行恢复并滚动到顶部
        private async void RestoreDefaultSettings_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow?.Content?.XamlRoot == null) return;

            bool confirmed = await DialogService.ShowDualAsync(
                App.MainWindow.Content.XamlRoot,
                ResourceService.GetString("SettingsPage_Restore_Confirm_Title"),
                ResourceService.GetString("SettingsPage_Restore_Confirm_Message"),
                primaryText: ResourceService.GetString("Msg_Confirm"),
                closeText: ResourceService.GetString("Msg_Cancel"));
            if (confirmed)
            {
                ViewModel.RestoreDefaultSettingsCommand.Execute(null);
                // 立即跳到顶部（无动画），让用户感知已重置
                PageScrollViewer.ChangeView(null, 0, null, true);
            }
        }

        // 切换到新版设置页面（需重启生效）
        private async void SwitchToModern_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow?.Content?.XamlRoot == null) return;

            bool confirmed = await DialogService.ShowDualAsync(
                App.MainWindow.Content.XamlRoot,
                ResourceService.GetString("SettingsPage_SwitchToModern_Confirm_Title"),
                ResourceService.GetString("SettingsPage_SwitchToModern_Confirm_Message"),
                primaryText: ResourceService.GetString("Msg_Confirm"),
                closeText: ResourceService.GetString("Msg_Cancel"));
            if (!confirmed) return;

            // Save preference: switch to modern
            AppSettingsService.SetValue("UseClassicSettingsPage", false);

            string? processPath = Environment.ProcessPath;
            if (!string.IsNullOrWhiteSpace(processPath))
            {
                try
                {
                    LogService.MarkCleanShutdown();
                    Process.Start(new ProcessStartInfo(processPath) { UseShellExecute = true });
                }
                catch (Exception ex)
                {
                    LogService.Error($"Failed to restart app: {ex.Message}", ex, LogSource.UI);
                    return;
                }
            }

            Application.Current.Exit();
        }
    }
}
