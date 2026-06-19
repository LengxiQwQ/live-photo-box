using LivePhotoBox.Helpers;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LivePhotoBox.Models;
using System;
using System.Diagnostics;

namespace LivePhotoBox.Views
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsViewModel ViewModel => AppViewModel.Instance.Settings;

        public AboutViewModel AboutViewModel => AppViewModel.Instance.About;

        public Visibility TestToolsVisibility => _isTestToolsVisible ? Visibility.Visible : Visibility.Collapsed;

        public Visibility CrashNoticeVisibility => _isTestToolsVisible && LogService.LastSessionCrashed
            ? Visibility.Visible : Visibility.Collapsed;

        public string CrashNoticeText => ResourceService.GetString("SettingsPage_CrashNotice_Text");

        public string TestToolsToggleButtonText => ResourceService.GetString(_isTestToolsVisible
            ? "SettingsPage_TestHide_Button_Text"
            : "SettingsPage_TestShow_Button_Text");

        private bool _isTestToolsVisible;

        public SettingsPage()
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

        /// <summary>
        /// 所有外观面板 ComboBox 共用：自动按最宽选项定宽
        /// </summary>
        private void AppearanceComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox comboBox)
                ComboBoxHelper.AutoFitWidth(comboBox);
        }

        /// <summary>
        /// 硬件 ComboBox 异步加载完成后再测量
        /// </summary>
        private void HardwareComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox comboBox)
                ComboBoxHelper.AutoFitWidthAsync(comboBox, ViewModel.AvailableHardware);
        }

        private void ToggleTestToolsButton_Click(object sender, RoutedEventArgs e)
        {
            _isTestToolsVisible = !_isTestToolsVisible;
            AboutViewModel.RefreshCrashLogs();
            Bindings.Update();
        }

        private async void RestartAppButton_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow?.Content?.XamlRoot == null) return;

            var dialog = new ContentDialog
            {
                Title = ResourceService.GetString("SettingsPage_Restart_Confirm_Title"),
                Content = new TextBlock
                {
                    Text = ResourceService.GetString("SettingsPage_Restart_Confirm_Message"),
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = ResourceService.GetString("Msg_Cancel"),
                SecondaryButtonText = ResourceService.GetString("Msg_Confirm"),
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = App.MainWindow.Content.XamlRoot,
                RequestedTheme = App.CurrentTheme
            };

            var result = await dialog.ShowAsync();
            if (result != ContentDialogResult.Secondary) return;

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

        private async void PreviewCrashDialogButton_Click(object sender, RoutedEventArgs e)
        {
            if (XamlRoot == null) return;
            string? logPath = LogService.PreviousLogPath;
            if (!string.IsNullOrWhiteSpace(logPath) && !System.IO.File.Exists(logPath))
                logPath = null;

            LogService.Info($"PreviewCrashDialog requested. File='{System.IO.Path.GetFileName(logPath)}'", LogSource.UI);
            await CrashHandler.ShowCrashDialogAsync(XamlRoot, logPath);
        }

        private void PrevBanner_Click(object sender, RoutedEventArgs e) => ViewModel.PrevBanner();
        private void NextBanner_Click(object sender, RoutedEventArgs e) => ViewModel.NextBanner();

        /// <summary>
        /// Resets the banner to the first (default) preset and turns off random mode.
        /// </summary>
        private void ResetBanner_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsBannerRandomEnabled = false;
            ViewModel.BannerPresetIndex = 0;
        }

        private async void RestoreDefaultSettings_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow?.Content?.XamlRoot == null) return;

            var dialog = new ContentDialog
            {
                Title = ResourceService.GetString("SettingsPage_Restore_Confirm_Title"),
                Content = new TextBlock
                {
                    Text = ResourceService.GetString("SettingsPage_Restore_Confirm_Message"),
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = ResourceService.GetString("Msg_Cancel"),
                SecondaryButtonText = ResourceService.GetString("Msg_Confirm"),
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = App.MainWindow.Content.XamlRoot,
                RequestedTheme = App.CurrentTheme
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Secondary)
            {
                ViewModel.RestoreDefaultSettingsCommand.Execute(null);
            }
        }
    }
}
