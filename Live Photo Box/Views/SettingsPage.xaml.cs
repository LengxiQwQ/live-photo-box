/*
 * SettingsPage.xaml.cs
 *
 * 设置页面（现代版）的代码后置。
 * 应用的主要设置界面，包含外观、语言、功能开关、调试工具等配置项。
 * 支持从其他页面带参数导航并自动滚动到指定设置区域。
 *
 * 对应 ViewModel：SettingsViewModel / AboutViewModel
 *
 * 生命周期：
 *   - 构造函数 → 初始化组件 → Loaded 中预加载 Banner 和崩溃检测
 *   - OnNavigatedTo → 解析导航参数，注册滚动完成后的高亮动画
 *   - 各设置项通过事件处理器直接更新 ViewModel
 */

using LivePhotoBox.Helpers;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using LivePhotoBox.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
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

        // 关联的 SettingsViewModel
        public SettingsViewModel ViewModel => AppViewModel.Instance.Settings;

        // 关联的 AboutViewModel（用于崩溃日志等功能）
        public AboutViewModel AboutViewModel => AppViewModel.Instance.About;

        // 调试工具区域的可见性
        public Visibility TestToolsVisibility => IsTestToolsVisible ? Visibility.Visible : Visibility.Collapsed;

        // 崩溃通知横幅的可见性
        public Visibility CrashNoticeVisibility => IsTestToolsVisible && LogService.LastSessionCrashed
            ? Visibility.Visible : Visibility.Collapsed;

        // 崩溃通知文本
        public string CrashNoticeText => ResourceService.GetString("SettingsPage_CrashNotice_Text");

        // 调试工具开关按钮文本
        public string TestToolsToggleButtonText => ResourceService.GetString(IsTestToolsVisible
            ? "SettingsPage_TestHide_Button_Text"
            : "SettingsPage_TestShow_Button_Text");

        /// <summary>
        /// 调试工具区展开/折叠状态，绑定到 ToggleButton.IsChecked。
        /// ToggleButton 自带 Checked/Unchecked 视觉状态切换，自动变换颜色。
        /// </summary>
        public bool IsTestToolsVisible
        {
            get => _isTestToolsVisible;
            set
            {
                if (_isTestToolsVisible != value)
                {
                    _isTestToolsVisible = value;
                    AppSettingsService.SetValue(nameof(IsTestToolsVisible), value);
                    AboutViewModel.RefreshCrashLogs();
                    NotifyPropertyChanged(nameof(TestToolsVisibility));
                    NotifyPropertyChanged(nameof(CrashNoticeVisibility));
                    NotifyPropertyChanged(nameof(IsTestToolsVisible));
                    NotifyPropertyChanged(nameof(TestToolsToggleButtonText));
                    Bindings.Update();
                }
            }
        }

        private bool _isTestToolsVisible;

        // 构造函数：初始化组件，注册 Loaded 事件（预加载 Banner + 崩溃检测）
        public SettingsPage()
        {
            InitializeComponent();

            // 从设置中恢复调试工具展开状态（默认关闭）
            _isTestToolsVisible = AppSettingsService.GetValue(nameof(IsTestToolsVisible), false);

            Loaded += (_, _) =>
            {
                // 后台预加载 Banner，不阻塞页面打开（fire-and-forget）
                _ = ViewModel.EnsureBannersPreloadedAsync();

                // GitHub API Token 卡片：仅非打包模式显示（商店版不需要手动设置 Token）
                GitHubTokenCard.Visibility = App.IsPackaged ? Visibility.Collapsed : Visibility.Visible;

                // 刷新 GitHub API Token 状态显示
                GitHubTokenBox.PlaceholderText = ResourceService.GetString("SettingsPage_Debug_GitHubTokenBox_Placeholder");
                RefreshGitHubTokenStatus();

                // 如果上一次非正常退出，自动展开日志与调试工具区
                if (LogService.LastSessionCrashed && !IsTestToolsVisible)
                {
                    IsTestToolsVisible = true;
                }
            };
        }

        // 接收来自其他页面的导航参数，自动滚动到指定设置区域。
        // 分类标题使用顶部对齐，具体卡片使用居中对齐。
        // 滚动完成后会有短暂高亮闪烁，提示用户目标位置。
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (e.Parameter is not string target)
                return;

            UIElement? scrollTarget = null;
            Border? highlightBorder = null;

            switch (target)
            {
                case "StrictLivePhotoScan":
                    scrollTarget = StrictLivePhotoScanRoot;
                    highlightBorder = StrictLivePhotoScanHighlight;
                    break;
                case "Merge":
                    scrollTarget = MergeSettingsHeader;
                    break;
                // Split 设置已移至 SplitPage 页面本身，设置页中的对应区域已注释
                // case "Split":
                //     scrollTarget = SplitSettingsHeader;
                //     break;
                case "Repair":
                    scrollTarget = RepairSettingsHeader;
                    break;
                default:
                    return;
            }

            // 等待布局完成，然后滚动到目标位置
            _ = DispatcherQueue.TryEnqueue(Microsoft.UI.Dispatching.DispatcherQueuePriority.Low, async () =>
            {
                try
                {
                    // 强制布局更新，确保 TransformToVisual 结果准确
                    ContentPanel.UpdateLayout();
                    await Task.Delay(30);

                    GeneralTransform transform = scrollTarget.TransformToVisual(ContentPanel);
                    double targetY = Math.Max(0, transform.TransformPoint(new Point(0, 0)).Y);
                    PageScrollViewer.ChangeView(null, targetY, null, true);
                }
                catch { /* 页面已销毁时静默忽略 */ }
            });

            _ = HighlightTargetAsync(highlightBorder);
        }

        // 滚动到位后短暂高亮目标区域。
        // highlightBorder 为 null 时仅滚动不闪烁（适用于分类标题跳转）。
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

        // 打开历史页面
        private void OpenHistoryPage_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is MainWindow mainWin)
                mainWin.SwitchToPageByTag("History");
        }

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
                PageScrollViewer.ChangeView(null, 0, null, true);
            }
        }

        // 切换到旧版设置页面（需重启生效）
        private async void SwitchToClassic_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow?.Content?.XamlRoot == null) return;

            bool confirmed = await DialogService.ShowDualAsync(
                App.MainWindow.Content.XamlRoot,
                ResourceService.GetString("SettingsPage_SwitchToClassic_Confirm_Title"),
                ResourceService.GetString("SettingsPage_SwitchToClassic_Confirm_Message"),
                primaryText: ResourceService.GetString("Msg_Confirm"),
                closeText: ResourceService.GetString("Msg_Cancel"));
            if (!confirmed) return;

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

        // ── 自动更新 ────────────────────────────────────────────────

        /// <summary>
        /// 检查更新按钮点击：手动触发版本检测并展示更新对话框。
        /// 独立于 App.xaml.cs 中的启动检查，用户可在设置页面主动触发。
        /// </summary>
        private async void CheckUpdate_Click(object sender, RoutedEventArgs e)
        {
            if (XamlRoot == null) return;

            // 禁用按钮防止重复点击
            if (sender is Button btn) btn.IsEnabled = false;

            try
            {
                await PerformUpdateCheckAndShowDialogAsync(XamlRoot);
            }
            finally
            {
                if (sender is Button btn2) btn2.IsEnabled = true;
            }
        }

        // ── GitHub API Token 管理 ────────────────────────────────────

        /// <summary>
        /// 清空 Token 输入框内容。
        /// </summary>
        private void ClearTokenBox_Click(object sender, RoutedEventArgs e)
        {
            GitHubTokenBox.Password = "";
        }

        /// <summary>
        /// 打开 GitHub Personal Access Token 生成页面（默认浏览器）。
        /// </summary>
        private async void OpenGitHubTokenGuide_Click(object sender, RoutedEventArgs e)
        {
            await FilePickerService.OpenUriAsync(
                new Uri("https://github.com/settings/tokens/new?description=LivePhotoBox&scopes="));
        }

        /// <summary>
        /// 保存用户输入的 GitHub API Token。
        /// 空的 Token 会被忽略，提示用户输入。
        /// </summary>
        private void SaveGitHubToken_Click(object sender, RoutedEventArgs e)
        {
            var token = GitHubTokenBox.Password?.Trim();
            if (string.IsNullOrWhiteSpace(token))
            {
                GitHubTokenStatus.Text = ResourceService.GetString("SettingsPage_Debug_GitHubTokenEmpty");
                return;
            }

            // 基本校验：GitHub PAT 以 ghp_ / github_pat_ / gho_ 开头
            if (!token.StartsWith("ghp_") && !token.StartsWith("github_pat_") && !token.StartsWith("gho_"))
            {
                GitHubTokenStatus.Text = ResourceService.GetString("SettingsPage_Debug_GitHubTokenInvalidFormat");
                return;
            }

            UpdateService.SetApiToken(token);
            GitHubTokenBox.Password = "";
            RefreshGitHubTokenStatus();
            LogService.Info("Settings: GitHub API token saved by user.", LogSource.Settings);
        }

        /// <summary>
        /// 清除已保存的 GitHub API Token，恢复未认证模式（60次/小时限流）。
        /// </summary>
        private void ClearGitHubToken_Click(object sender, RoutedEventArgs e)
        {
            UpdateService.SetApiToken(null);
            RefreshGitHubTokenStatus();
            LogService.Info("Settings: GitHub API token cleared by user.", LogSource.Settings);
        }

        /// <summary>
        /// 刷新 GitHub API Token 的状态显示和按钮可见性。
        /// Token 已设置时显示前缀（如 "ghp_ab…"）和清除按钮；
        /// 未设置时显示 "未设置" 提示，隐藏清除按钮。
        /// </summary>
        private void RefreshGitHubTokenStatus()
        {
            if (UpdateService.HasApiToken)
            {
                GitHubTokenStatus.Text = string.Format(
                    ResourceService.GetString("SettingsPage_Debug_GitHubTokenSet"),
                    UpdateService.TokenDisplayText);
                ClearGitHubTokenBtn.Visibility = Visibility.Visible;
            }
            else
            {
                GitHubTokenStatus.Text = ResourceService.GetString("SettingsPage_Debug_GitHubTokenNotSet");
                ClearGitHubTokenBtn.Visibility = Visibility.Collapsed;
            }
        }

        // ── 自动更新 ────────────────────────────────────────────────

        /// <summary>
        /// 执行版本检查并弹出对应的对话框。手动检查入口（调用 GitHub API）。
        /// 流程：请求 API → 无新版弹提示 / 有新版弹选择 → 下载 → 安装。
        /// </summary>
        internal static async Task PerformUpdateCheckAndShowDialogAsync(Microsoft.UI.Xaml.XamlRoot xamlRoot)
        {
            LogService.Info("Update UI: Manual check triggered by user.", LogSource.System);

            // 打包模式（MSIX）由 Windows Store 负责更新，自动更新功能不可用
            if (!UpdateService.IsUpdateEnabled)
            {
                LogService.Info("Update UI: Packaged mode detected — showing disabled message.", LogSource.System);
                await ShowInfoDialogAsync(
                    xamlRoot,
                    ResourceService.GetString("Update_CheckFailed_Title"),
                    ResourceService.GetString("Update_PackagedMode_Disabled"),
                    ResourceService.GetString("Msg_GotIt"));
                return;
            }

            // 调用 GitHub API（仅此一次）
            var release = await Task.Run(() => UpdateService.FetchLatestReleaseAsync());
            // 注意：手动检查不记录 CheckTime，避免阻塞启动自动检查的 3 天间隔

            await HandleUpdateCheckResultAsync(xamlRoot, release, isManualCheck: true);
        }

        /// <summary>
        /// 启动检查入口：使用已获取的 release 信息，不再重复请求 API。
        /// 避免启动路径中 FetchLatestReleaseAsync 被调用两次（一次在 App、一次在此方法），
        /// 减少不必要的 API 消耗，降低被 GitHub 限流的风险。
        /// </summary>
        internal static async Task HandleUpdateCheckResultAsync(
            Microsoft.UI.Xaml.XamlRoot xamlRoot,
            GitHubReleaseResponse? release,
            bool isManualCheck = false)
        {
            if (release == null)
            {
                // 区分"没网"和"GitHub API 不可用"，给用户精准提示
                bool hasInternet = await UpdateService.CheckInternetConnectivityAsync();
                string titleKey, messageKey;
                if (!hasInternet)
                {
                    LogService.Warn("Update UI: No internet connectivity — showing network error dialog.",
                        source: LogSource.System);
                    titleKey = "Update_NetworkError_Title";
                    messageKey = "Update_NetworkError_Message";
                }
                else
                {
                    LogService.Warn("Update UI: Internet OK but GitHub API failed — showing retry dialog.",
                        source: LogSource.System);
                    titleKey = "Update_CheckFailed_Title";
                    messageKey = "Update_CheckFailed_Message";
                }

                // 错误弹窗：显示错误信息 + 关闭按钮 + 前往 GitHub 手动下载按钮
                var releasesUrl = "https://github.com/LengxiQwQ/live-photo-box/releases";
                bool clickedDownload = await DialogService.ShowDualAsync(
                    xamlRoot,
                    ResourceService.GetString(titleKey),
                    ResourceService.GetString(messageKey),
                    primaryText: ResourceService.GetString("Update_Btn_ManualDownload"),
                    closeText: ResourceService.GetString("Msg_GotIt"));
                if (clickedDownload)
                    await FilePickerService.OpenUriAsync(new Uri(releasesUrl));
                return;
            }

            // 检查是否有新版本
            if (!UpdateService.IsNewerVersion(release))
            {
                LogService.Info(
                    $"Update UI: No new version. Current={App.AppVersion}, Latest={release.TagName}",
                    LogSource.System);
                var currentVersionFull = App.AppVersion;
                var msg = string.Format(
                    ResourceService.GetString("Update_NoNewVersion_Message"), currentVersionFull);
                await ShowInfoDialogAsync(
                    xamlRoot,
                    ResourceService.GetString("Update_NoNewVersion_Title"),
                    msg,
                    ResourceService.GetString("Msg_GotIt"));
                return;
            }

            // 手动检查时清除之前的跳过记录（用户主动检查=不再忽略）
            if (isManualCheck && UpdateService.IsVersionSkipped(release.TagName))
            {
                LogService.Info($"Update UI: Manually checking — clearing previous skip for {release.TagName}.", LogSource.System);
                UpdateService.ClearSkippedVersion();
            }

            // 弹出版本选择对话框
            LogService.Info(
                $"Update UI: New version detected! Showing update choice dialog. " +
                $"Latest={release.TagName}, Current={App.AppVersion}",
                LogSource.System);
            await ShowUpdateChoiceDialogAsync(xamlRoot, release);
        }

        /// <summary>
        /// 弹出「发现新版本」三按钮选择对话框。
        /// 按钮：下载并安装(主按钮) / 忽略此版本 / 下次再说(关闭按钮)。
        /// </summary>
        private static async Task ShowUpdateChoiceDialogAsync(
            Microsoft.UI.Xaml.XamlRoot xamlRoot,
            GitHubReleaseResponse release)
        {
            // 格式化版本号显示：去掉 tag 前缀 v
            var latestVersion = release.TagName.TrimStart('v', 'V');
            var currentVersion = App.AppVersion;

            // 构建内容：新版本号 + 当前版本号 + 更新日志
            var contentStack = new StackPanel { Spacing = 12, HorizontalAlignment = HorizontalAlignment.Stretch };

            contentStack.Children.Add(new TextBlock
            {
                Text = ResourceService.GetString("Update_NewVersion_Message")
                    .Replace("{0}", latestVersion)
                    .Replace("{1}", currentVersion),
                FontSize = 14,
                TextWrapping = TextWrapping.NoWrap,
                HorizontalAlignment = HorizontalAlignment.Stretch,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Microsoft YaHei UI")
            });

            // 显示 Release 正文（Markdown 渲染）
            if (!string.IsNullOrWhiteSpace(release.Body))
            {
                // 补上 base URL，让相对链接（如 changelogs/xxx.md）能正确跳转到 GitHub
                bool isDark = App.CurrentTheme == ElementTheme.Dark;
                var tag = release.TagName.TrimStart('v', 'V');
                var baseUrl = $"https://github.com/LengxiQwQ/live-photo-box/blob/v{tag}/";
                var html = MarkdownRenderService.RenderToHtml(release.Body, isDark, baseUrl);

                var webView = new Microsoft.UI.Xaml.Controls.WebView2
                {
                    Height = 300,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                // WebView2 初始化失败的降级方案：纯文本 ScrollViewer。
                // 注意：不能对未初始化的 WebView2 调用 NavigateToString（同样会失败）。
                var fallbackTextBlock = new TextBlock
                {
                    Text = MarkdownToPlainText(release.Body),
                    FontSize = 12,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Consolas, Microsoft YaHei UI"),
                    MaxHeight = 260
                };
                var fallbackScrollViewer = new ScrollViewer
                {
                    Content = fallbackTextBlock,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
                    MaxHeight = 300,
                    Visibility = Visibility.Collapsed
                };

                // 异步初始化 WebView2 完成后注入 HTML，并拦截链接跳转到外部浏览器
                webView.Loaded += async (_, _) =>
                {
                    try
                    {
                        await webView.EnsureCoreWebView2Async();

                        // 禁用右键菜单/开发者工具/状态栏链接提示
                        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;
                        webView.CoreWebView2.Settings.IsStatusBarEnabled = false;

                        // 用标志位放行 NavigateToString 的首屏加载，只拦截用户点击的链接
                        var isInitialLoad = true;
                        webView.CoreWebView2.NavigationStarting += (_, args) =>
                        {
                            if (isInitialLoad)
                            {
                                isInitialLoad = false;
                                return;
                            }
                            // 用户点击了链接 → 取消内部跳转，只用默认浏览器打开 http/https 链接
                            args.Cancel = true;
                            if (args.Uri != null &&
                                (args.Uri.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
                                 args.Uri.StartsWith("https://", StringComparison.OrdinalIgnoreCase)))
                            {
#pragma warning disable CS4014
                                FilePickerService.OpenUriAsync(new Uri(args.Uri));
#pragma warning restore CS4014
                            }
                        };

                        webView.NavigateToString(html);
                    }
                    catch (Exception ex)
                    {
                        LogService.Warn($"WebView2 init failed in update dialog: {ex.Message}",
                            source: LogSource.System);
                        // 降级：隐藏 WebView2，改为纯文本 ScrollViewer 显示。
                        // 不能用 NavigateToString，因为 WebView2 还没初始化成功。
                        webView.Visibility = Visibility.Collapsed;
                        fallbackScrollViewer.Visibility = Visibility.Visible;
                    }
                };

                contentStack.Children.Add(webView);
                contentStack.Children.Add(fallbackScrollViewer);
            }

            var dialog = new ContentDialog
            {
                Title = ResourceService.GetString("Update_NewVersion_Title"),
                Content = contentStack,
                PrimaryButtonText = ResourceService.GetString("Update_Btn_DownloadInstall"),
                SecondaryButtonText = ResourceService.GetString("Update_Btn_SkipVersion"),
                CloseButtonText = ResourceService.GetString("Update_Btn_Later"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
                RequestedTheme = App.CurrentTheme
            };

            // ContentDialog 宽度由内部资源键控制，不能通过 Width/MinWidth/MaxWidth 属性设置
            dialog.Resources["ContentDialogMaxWidth"] = 900.0;
            dialog.Resources["ContentDialogMinWidth"] = 680.0;

            // 确保 ContentDialog Popup 能继承主窗口强调色，防止 PrimaryButton 按下变白
            if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var accent))
                dialog.Resources["SystemAccentColor"] = accent;
            if (Application.Current.Resources.TryGetValue("SystemControlHighlightAccentBrush", out var highlightBrush))
                dialog.Resources["SystemControlHighlightAccentBrush"] = highlightBrush;


            var result = await dialog.ShowAsync();

            switch (result)
            {
                case ContentDialogResult.Primary:
                    // 下载并安装
                    LogService.Info($"Update UI: User chose 'Download & Install' for {release.TagName}", LogSource.System);
                    await DownloadAndInstallUpdateAsync(xamlRoot, release);
                    break;

                case ContentDialogResult.Secondary:
                    // 忽略此版本
                    LogService.Info($"Update UI: User chose 'Skip' for {release.TagName}", LogSource.System);
                    UpdateService.SkipVersion(release.TagName);
                    break;

                default:
                    // 下次再说 / 关闭 → 什么都不做
                    LogService.Info("Update UI: User chose 'Remind Me Later' — dialog dismissed.", LogSource.System);
                    break;
            }
        }

        /// <summary>
        /// 下载更新文件并启动安装流程。
        /// 先弹出下载进度对话框 → 下载 → 完成/失败提示 → 启动安装 → 退出应用。
        /// </summary>
        private static async Task DownloadAndInstallUpdateAsync(
            Microsoft.UI.Xaml.XamlRoot xamlRoot,
            GitHubReleaseResponse release)
        {
            // 构建下载进度对话框内容
            var progressBar = new ProgressBar
            {
                Minimum = 0,
                Maximum = 100,
                Value = 0,
                IsIndeterminate = false,
                Height = 6,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 8, 0, 0)
            };

            var statusText = new TextBlock
            {
                Text = ResourceService.GetString("Update_Downloading_Message"),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Microsoft YaHei UI"),
                Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 4)
            };

            // 总大小（MB），用于显示下载进度
            var totalMb = UpdateService.GetAssetSize(release) / 1024.0 / 1024.0;

            var progressText = new TextBlock
            {
                Text = totalMb > 0 ? $"0 / {totalMb:F1} MB" : "0%",
                FontSize = 14,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 8)
            };

            var downloadContent = new StackPanel { Spacing = 4 };
            downloadContent.Children.Add(statusText);
            downloadContent.Children.Add(progressBar);
            downloadContent.Children.Add(progressText);

            var downloadDialog = new ContentDialog
            {
                Title = ResourceService.GetString("Update_Downloading_Title"),
                Content = downloadContent,
                SecondaryButtonText = ResourceService.GetString("Msg_Cancel"),
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = xamlRoot,
                RequestedTheme = App.CurrentTheme
            };

            // 进度报告器：进度条按 1% 变，MB 文字 ~50ms 刷新（先节流再入队，防洪水）
            var lastPercent = -1;
            var lastMbTick = DateTime.MinValue;
            var progress = new Progress<double>(p =>
            {
                // 节流：两次入队间隔 < 40ms 直接跳过
                var now = DateTime.UtcNow;
                if ((now - lastMbTick).TotalMilliseconds < 40) return;
                lastMbTick = now;

                var percent = (int)p;

                if (App.MainWindow?.DispatcherQueue != null)
                {
                    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                    {
                        if (percent != lastPercent)
                        {
                            lastPercent = percent;
                            progressBar.Value = p;
                        }

                        if (totalMb > 0)
                        {
                            var downloadedMb = p / 100.0 * totalMb;
                            progressText.Text = $"{downloadedMb:F1} / {totalMb:F1} MB";
                        }
                        else
                        {
                            progressText.Text = $"{percent}%";
                        }
                    });
                }
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            string? downloadedPath = null;
            Exception? downloadError = null;

            // 后台线程执行下载（完成后自动关对话框，不需要 Hide()）
            var downloadTask = Task.Run(async () =>
            {
                try
                {
                    downloadedPath = await UpdateService.DownloadAssetAsync(release, progress, cts.Token);
                }
                catch (Exception ex)
                {
                    downloadError = ex;
                }

                // 下载完成（成功或失败）→ UI 线程：展示结果 → 短暂停留 → 关对话框
                if (App.MainWindow?.DispatcherQueue != null)
                {
                    App.MainWindow.DispatcherQueue.TryEnqueue(async () =>
                    {
                        if (downloadedPath != null)
                        {
                            progressBar.Value = 100;
                            progressText.Text = totalMb > 0 ? $"{totalMb:F1} / {totalMb:F1} MB" : "100%";
                            statusText.Text = ResourceService.GetString("Update_DownloadComplete_Title");
                        }
                        await Task.Delay(800);
                        try { downloadDialog.Hide(); }
                        catch { /* 对话框可能已被用户取消 */ }
                    });
                }
            });

            // 显示下载进度对话框（ShowAsync 不阻塞 UI 消息循环，进度条正常刷新）
            var dialogResult = await downloadDialog.ShowAsync();

            // 用户点击了取消按钮
            if (dialogResult == ContentDialogResult.Secondary)
            {
                cts.Cancel();
                LogService.Info("Update UI: User cancelled download.", LogSource.System);
                return;
            }

            // 等下载任务完全结束（Hide 只是关对话框，下载可能还在收尾）
            try { await downloadTask; }
            catch { /* 异常已记录到 downloadError */ }

            if (downloadedPath == null)
            {
                string errorMsg = downloadError?.Message ?? ResourceService.GetString("Update_CheckFailed_Message");
                LogService.Error(
                    $"Update UI: Download FAILED! Error: {errorMsg}",
                    exception: downloadError,
                    source: LogSource.System);
                await ShowInfoDialogAsync(
                    xamlRoot,
                    ResourceService.GetString("Update_DownloadFailed_Title"),
                    string.Format(ResourceService.GetString("Update_DownloadFailed_Message"), errorMsg),
                    ResourceService.GetString("Msg_GotIt"));
                return;
            }

            // 下载完成 → 询问用户是否立即重启以完成更新
            bool isSetup = UpdateService.IsInnoSetupInstall();
            LogService.Info(
                $"Update UI: Download succeeded → {downloadedPath}. Install type: {(isSetup ? "Inno Setup" : "Portable")}",
                LogSource.System);

            var restartMsg = isSetup
                ? ResourceService.GetString("Update_DownloadComplete_Setup_Message")
                : ResourceService.GetString("Update_DownloadComplete_Portable_Message");

            var restartDialog = new ContentDialog
            {
                Title = ResourceService.GetString("Update_DownloadComplete_Title"),
                Content = new TextBlock
                {
                    Text = restartMsg,
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Microsoft YaHei UI")
                },
                PrimaryButtonText = ResourceService.GetString("Update_Btn_RestartNow"),
                CloseButtonText = ResourceService.GetString("Update_Btn_UpdateOnClose"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
                RequestedTheme = App.CurrentTheme
            };

            var restartResult = await restartDialog.ShowAsync();

            if (restartResult != ContentDialogResult.Primary)
            {
                // 「关闭时更新」→ 后台解压+写.bat，不阻塞 UI。关窗口时如果准备完了就秒启，没完就等一下
                LogService.Info("Update UI: User chose 'Update on close'. Preparing in background...", LogSource.System);
                var capturedIsSetup = isSetup;
                string? preparedPath = null;
                var prepareTask = Task.Run(() =>
                {
                    preparedPath = UpdateService.PrepareInstaller(downloadedPath, capturedIsSetup, restartAfterUpdate: false);
                    LogService.Info("Update UI: Background preparation complete.", LogSource.System);
                });

                if (App.MainWindow != null)
                {
                    App.MainWindow.Closed += async (_, _) =>
                    {
                        LogService.Info("Update UI: Window closed — executing pending update.", LogSource.System);
                        await prepareTask;
                        if (preparedPath != null)
                            UpdateService.ExecutePreparedInstaller(preparedPath, capturedIsSetup);
                    };
                }
                return;
            }

            // 「立即重启并更新」
            LogService.Info("Update UI: User chose 'Restart now'.", LogSource.System);
            UpdateService.LaunchInstaller(downloadedPath, isSetup);

            // 退出应用（给安装/替换脚本让路）
            LogService.MarkCleanShutdown();
            Application.Current.Exit();
        }

        /// <summary>
        /// 将 GitHub Release 的 Markdown 正文转为可读纯文本。
        /// 去掉 ##、**、`、-、[url] 等标记，保留段落结构和链接文字。
        /// 这只是一个轻量级"预览"转换，不追求完美的 Markdown 渲染。
        /// </summary>
        private static string MarkdownToPlainText(string markdown)
        {
            if (string.IsNullOrWhiteSpace(markdown))
                return markdown;

            var text = markdown;

            // 1. 去掉 Markdown 链接 → 只保留文字 [text](url) → text
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"\[([^\]]*)\]\([^)]*\)", "$1");

            // 2. 图片 → 去掉 ![alt](url)
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"!\[[^\]]*\]\([^)]*\)", "");

            // 3. 去掉加粗/斜体标记 **text** → text, *text* → text
            text = System.Text.RegularExpressions.Regex.Replace(text, @"\*\*(.+?)\*\*", "$1");
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(?<!\*)\*([^*\n]+?)\*(?!\*)", "$1");

            // 4. 去掉行内代码 `code` → code
            text = System.Text.RegularExpressions.Regex.Replace(text, @"`([^`]+)`", "$1");

            // 5. 将 ## 标题转为带冒号的独立行 "Heading:"（保留换行）
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"^#{1,3}\s+(.+)$", "$1：", System.Text.RegularExpressions.RegexOptions.Multiline);

            // 6. 无序列表 - item 或 * item → • item
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"^[\-\*]\s+", "• ", System.Text.RegularExpressions.RegexOptions.Multiline);

            // 7. 有序列表 1. item → 1) item
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"^(\d+)\.\s+", "$1) ", System.Text.RegularExpressions.RegexOptions.Multiline);

            // 8. 合并连续空白行（保留单个空行作为段落分隔）
            text = System.Text.RegularExpressions.Regex.Replace(text, @"(\r?\n){3,}", "\n\n");

            // 9. 去掉 Markdown 水平线 --- ***
            text = System.Text.RegularExpressions.Regex.Replace(
                text, @"^[\-\*_]{3,}\s*$", "", System.Text.RegularExpressions.RegexOptions.Multiline);

            // 10. 去掉 HTML 标签（如有）
            text = System.Text.RegularExpressions.Regex.Replace(text, @"<[^>]+>", "");

            // 11. 去除首尾多余的空白行
            text = text.Trim();

            return text;
        }

        /// <summary>
        /// 显示仅带一个确认按钮的信息对话框。
        /// </summary>
        private static async Task ShowInfoDialogAsync(
            Microsoft.UI.Xaml.XamlRoot xamlRoot,
            string title,
            string message,
            string closeButtonText)
        {
            await DialogService.ShowSingleAsync(xamlRoot, title, message, closeButtonText);
        }

        // 检测外部工具按钮点击：异步检测所有外部工具，以 ContentDialog 弹窗展示结果。
        private async void CheckExternalTools_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow?.Content?.XamlRoot == null) return;

            // 禁用按钮防止重复点击
            if (sender is Button btn) btn.IsEnabled = false;

            List<SettingsViewModel.ToolCheckResult> results;
            try
            {
                results = await SettingsViewModel.CheckAllExternalToolsAsync();
            }
            finally
            {
                if (sender is Button btn2) btn2.IsEnabled = true;
            }

            // 构建结果内容
            var resultPanel = new StackPanel { Spacing = 16 };

            // 顶部汇总
            int availableCount = 0;
            foreach (var r in results)
                if (r.Found && string.IsNullOrEmpty(r.Error))
                    availableCount++;
            string summaryKey = availableCount == results.Count
                ? "SettingsPage_CheckTools_AllOk"
                : "SettingsPage_CheckTools_SomeFailed";
            string summaryText = string.Format(ResourceService.GetString(summaryKey), availableCount, results.Count);

            resultPanel.Children.Add(new TextBlock
            {
                Text = summaryText,
                FontSize = 14,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                TextWrapping = TextWrapping.Wrap,
                IsTextSelectionEnabled = true,
                Margin = new Microsoft.UI.Xaml.Thickness(0, 0, 0, 4)
            });

            // 逐条工具结果
            for (int i = 0; i < results.Count; i++)
            {
                var r = results[i];
                bool isOk = r.Found && string.IsNullOrEmpty(r.Error);
                string statusIcon = isOk ? "✅" : "❌";
                string statusText = isOk
                    ? ResourceService.GetString("SettingsPage_CheckTools_Available")
                    : ResourceService.GetString("SettingsPage_CheckTools_Unavailable");

                var headerText = new TextBlock
                {
                    Text = $"{statusIcon}  {r.DisplayName}  —  {statusText}",
                    FontSize = 14,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    TextWrapping = TextWrapping.Wrap,
                    IsTextSelectionEnabled = true
                };

                var detailStack = new StackPanel { Spacing = 2, Margin = new Microsoft.UI.Xaml.Thickness(24, 2, 0, 0) };

                if (r.Path != null)
                {
                    detailStack.Children.Add(new TextBlock
                    {
                        Text = $"{ResourceService.GetString("SettingsPage_CheckTools_Path")}: {r.Path}",
                        FontSize = 11,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorTertiaryBrush"],
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    });
                }

                if (r.Version != null)
                {
                    detailStack.Children.Add(new TextBlock
                    {
                        Text = $"{ResourceService.GetString("SettingsPage_CheckTools_Version")}: {r.Version}",
                        FontSize = 11,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    });
                }

                if (r.Error != null)
                {
                    detailStack.Children.Add(new TextBlock
                    {
                        Text = $"{ResourceService.GetString("SettingsPage_CheckTools_Error")}: {r.Error}",
                        FontSize = 11,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["SystemErrorTextColor"],
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true
                    });
                }

                var toolCard = new StackPanel { Spacing = 4 };
                toolCard.Children.Add(headerText);
                toolCard.Children.Add(detailStack);
                resultPanel.Children.Add(toolCard);

                // 分隔线（最后一项不加）
                if (i < results.Count - 1)
                {
                    resultPanel.Children.Add(new Microsoft.UI.Xaml.Controls.Border
                    {
                        Height = 1,
                        Background = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["CardStrokeColorDefaultBrush"]
                    });
                }
            }

            // 显示结果弹窗
            await DialogService.ShowSingleAsync(
                App.MainWindow.Content.XamlRoot,
                ResourceService.GetString("SettingsPage_CheckTools_Dialog_Title"),
                new ScrollViewer
                {
                    MaxHeight = 450,
                    Content = resultPanel,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                },
                ResourceService.GetString("Msg_Confirm"));
        }

        // ════════════════════════════════════════════════════════════
        //  关键帧时间轴模式切换
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 用户切换时间轴模式时通知 KeyPhotoPage 刷新。
        /// 因为 KeyPhotoPage 使用 NavigationCacheMode="Required"，
        /// 切换页面时不会重新构造，需要通过 NotifyTimelineModeChanged
        /// 让 Visibility 绑定重新求值。
        /// </summary>
        private void TimelineMode_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            AppViewModel.Instance.KeyPhoto.NotifyTimelineModeChanged();
        }
    }
}
