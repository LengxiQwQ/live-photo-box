using LivePhotoBox.Helpers;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using LivePhotoBox.Models;
using System;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Windows.UI;

namespace LivePhotoBox.Views
{
    public sealed partial class SettingsPage : Page, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

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
                // 后台预加载 Banner
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

        private RoutedEventHandler? _scrollLoadedHandler;

        /// <summary>
        /// 接收来自其他页面的导航参数，自动滚动到指定设置区域。
        /// 分类标题使用顶部对齐，具体卡片使用居中对齐。
        /// 滚动完成后会有短暂高亮闪烁，提示用户目标位置。
        /// Loaded 处理器会在执行后自动移除，防止页面缓存导致重复触发。
        /// </summary>
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // 清理上一次的滚动处理器，防止缓存页切回时重复滚动
            if (_scrollLoadedHandler != null)
            {
                Loaded -= _scrollLoadedHandler;
                _scrollLoadedHandler = null;
            }

            if (e.Parameter is not string target)
                return;

            UIElement? scrollTarget = null;
            double alignment = 0.0;
            Border? highlightBorder = null;

            switch (target)
            {
                case "StrictLivePhotoScan":
                    scrollTarget = StrictLivePhotoScanRoot;
                    alignment = 0.5;
                    highlightBorder = StrictLivePhotoScanHighlight;
                    break;
                case "Merge":
                    scrollTarget = MergeSettingsHeader;
                    break;
                case "Split":
                    scrollTarget = SplitSettingsHeader;
                    break;
                case "Repair":
                    scrollTarget = RepairSettingsHeader;
                    break;
                default:
                    return;
            }

            _scrollLoadedHandler = (_, _) =>
            {
                // 一次性执行，用完即弃
                Loaded -= _scrollLoadedHandler;
                _scrollLoadedHandler = null;
                scrollTarget.StartBringIntoView(new BringIntoViewOptions
                {
                    AnimationDesired = true,
                    VerticalAlignmentRatio = alignment
                });
                _ = HighlightTargetAsync(highlightBorder);
            };
            Loaded += _scrollLoadedHandler;
        }

        /// <summary>
        /// 滚动到位后短暂高亮目标区域。
        /// highlightBorder 为 null 时仅滚动不闪烁（适用于分类标题跳转）。
        /// </summary>
        private async Task HighlightTargetAsync(Border? highlightBorder)
        {
            if (highlightBorder == null) return;

            try
            {
                await Task.Delay(550);

                var accentColor = (Color)Application.Current.Resources["SystemAccentColor"];
                var highlightFrom = Color.FromArgb(35, accentColor.R, accentColor.G, accentColor.B);

                highlightBorder.Background = new SolidColorBrush(highlightFrom);

                await Task.Delay(400);

                var storyboard = new Storyboard();
                var animation = new ColorAnimation
                {
                    From = highlightFrom,
                    To = Microsoft.UI.Colors.Transparent,
                    Duration = new Duration(TimeSpan.FromMilliseconds(800)),
                    EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
                };
                Storyboard.SetTarget(animation, highlightBorder);
                Storyboard.SetTargetProperty(animation, "(Border.Background).(SolidColorBrush.Color)");
                storyboard.Children.Add(animation);
                storyboard.Begin();
            }
            catch (Exception ex)
            {
                LogService.Debug($"HighlightTarget animation failed: {ex.Message}", LogSource.UI);
            }
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
            NotifyPropertyChanged(nameof(TestToolsVisibility));
            NotifyPropertyChanged(nameof(CrashNoticeVisibility));
            NotifyPropertyChanged(nameof(TestToolsToggleButtonText));
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

        private async void OpenHeifStoreLink_Click(object sender, RoutedEventArgs e)
        {
            await OpenStoreLinkAsync("9PMMSR1CGPWG");
        }

        private async void OpenHevcStoreLink_Click(object sender, RoutedEventArgs e)
        {
            await OpenStoreLinkAsync("9n4wgh0z6vhq");
        }

        private void PrevBanner_Click(object sender, RoutedEventArgs e) => ViewModel.PrevBanner();
        private void NextBanner_Click(object sender, RoutedEventArgs e) => ViewModel.NextBanner();

        private void OpenHistoryPage_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is MainWindow mainWin)
                mainWin.SwitchToPageByTag("History");
        }

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
                PageScrollViewer.ChangeView(null, 0, null, true);
            }
        }

        /// <summary>
        /// 切换到旧版设置页面（需重启生效）
        /// </summary>
        private async void SwitchToClassic_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow?.Content?.XamlRoot == null) return;

            var dialog = new ContentDialog
            {
                Title = ResourceService.GetString("SettingsPage_SwitchToClassic_Confirm_Title"),
                Content = new TextBlock
                {
                    Text = ResourceService.GetString("SettingsPage_SwitchToClassic_Confirm_Message"),
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

            // Save preference: switch back to classic
            AppSettingsService.SetValue("UseClassicSettingsPage", true);

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
