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

        private RoutedEventHandler? _scrollLoadedHandler;

        // 接收来自其他页面的导航参数，自动滚动到指定设置区域。
        // 分类标题使用顶部对齐，具体卡片使用居中对齐。
        // 滚动完成后会有短暂高亮闪烁，提示用户目标位置。
        // Loaded 处理器会在执行后自动移除，防止页面缓存导致重复触发。
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

        // 重启应用按钮点击：弹出确认对话框，确认后启动新进程并关闭当前应用
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

        // 切换到旧版设置页面（需重启生效）
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

        /// <summary>
        /// 执行版本检查并弹出对应的对话框。抽出为内部方法，方便 App.xaml.cs 启动检查调用。
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

            // 1. 调用 GitHub API
            var release = await Task.Run(() => UpdateService.FetchLatestReleaseAsync());
            UpdateService.RecordCheckTime();

            if (release == null)
            {
                LogService.Warn("Update UI: FetchLatestReleaseAsync returned null — showing 'check failed' dialog.",
                    source: LogSource.System);
                await ShowInfoDialogAsync(
                    xamlRoot,
                    ResourceService.GetString("Update_CheckFailed_Title"),
                    ResourceService.GetString("Update_CheckFailed_Message"),
                    ResourceService.GetString("Msg_GotIt"));
                return;
            }

            // 2. 检查是否有新版本
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

            // 3. 手动检查时清除之前的跳过记录（用户主动检查=不再忽略）
            if (UpdateService.IsVersionSkipped(release.TagName))
            {
                LogService.Info($"Update UI: Manually checking — clearing previous skip for {release.TagName}.", LogSource.System);
                UpdateService.ClearSkippedVersion();
            }

            // 4. 弹出版本选择对话框
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
            var contentStack = new StackPanel { Spacing = 12 };

            contentStack.Children.Add(new TextBlock
            {
                Text = ResourceService.GetString("Update_NewVersion_Message")
                    .Replace("\\n", Environment.NewLine)   // resw 中的 \n 是字面文本，需转为实际换行
                    .Replace("{0}", latestVersion)
                    .Replace("{1}", currentVersion),
                FontSize = 14,
                TextWrapping = TextWrapping.Wrap,
                FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Microsoft YaHei UI")
            });

            // 显示 Release 正文（截取前 2000 字符，避免弹窗过长）
            if (!string.IsNullOrWhiteSpace(release.Body))
            {
                var notesTitle = new TextBlock
                {
                    Text = ResourceService.GetString("Update_ReleaseNotes"),
                    FontSize = 13,
                    FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                    Margin = new Microsoft.UI.Xaml.Thickness(0, 4, 0, 0)
                };
                contentStack.Children.Add(notesTitle);

                // 使用 WebView2 渲染 Markdown（Markdig 转 HTML + CSS 样式）
                // 补上 base URL，让相对链接（如 changelogs/xxx.md）能正确跳转到 GitHub
                bool isDark = App.CurrentTheme == ElementTheme.Dark;
                var tag = release.TagName.TrimStart('v', 'V');
                var baseUrl = $"https://github.com/LengxiQwQ/live-photo-box/blob/v{tag}/";
                var html = MarkdownRenderService.RenderToHtml(release.Body, isDark, baseUrl);

                var webView = new Microsoft.UI.Xaml.Controls.WebView2
                {
                    Height = 260,
                    HorizontalAlignment = HorizontalAlignment.Stretch
                };

                // 异步初始化 WebView2 完成后注入 HTML，并拦截链接跳转到外部浏览器
                webView.Loaded += async (_, _) =>
                {
                    try
                    {
                        await webView.EnsureCoreWebView2Async();

                        // 禁用右键菜单/开发者工具（普通用户不需要）
                        webView.CoreWebView2.Settings.AreDefaultContextMenusEnabled = false;

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
                        // 降级：用纯文本显示（去掉 markdown 标记）
                        webView.NavigateToString(
                            MarkdownRenderService.RenderToHtml(
                                $"```\n{MarkdownToPlainText(release.Body)}\n```", isDark));
                    }
                };

                contentStack.Children.Add(webView);
            }

            var dialog = new ContentDialog
            {
                Title = ResourceService.GetString("Update_NewVersion_Title"),
                Content = contentStack,
                PrimaryButtonText = ResourceService.GetString("Update_Btn_DownloadInstall"),
                SecondaryButtonText = ResourceService.GetString("Update_Btn_SkipVersion"),
                CloseButtonText = ResourceService.GetString("Update_Btn_Later"),
                DefaultButton = ContentDialogButton.Primary,
                Width = 750,
                XamlRoot = xamlRoot,
                RequestedTheme = App.CurrentTheme
            };

            // 确保 ContentDialog Popup 能继承主窗口强调色，防止 PrimaryButton 按下变白
            // ContentDialog 使用 Popup 承载，部分主题资源不会自动继承，需手动复制
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

            var progressText = new TextBlock
            {
                Text = "0%",
                FontSize = 16,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
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

            // 进度报告器（UI 线程更新进度条和百分比文字）
            var progress = new Progress<double>(p =>
            {
                if (App.MainWindow?.DispatcherQueue != null)
                {
                    App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                    {
                        progressBar.Value = p;
                        progressText.Text = $"{p:F0}%";
                    });
                }
            });

            using var cts = new CancellationTokenSource(TimeSpan.FromMinutes(10));

            string? downloadedPath = null;
            Exception? downloadError = null;

            // 后台线程执行下载
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
                finally
                {
                    // 下载完成后，从 UI 线程关闭对话框（触发 ShowAsync 返回）
                    if (App.MainWindow?.DispatcherQueue != null)
                    {
                        App.MainWindow.DispatcherQueue.TryEnqueue(() =>
                        {
                            try { downloadDialog.Hide(); }
                            catch { /* 对话框可能已被用户取消 */ }
                        });
                    }
                }
            });

            // 显示下载进度对话框（阻塞 UI 线程，直到 Hide() 被调用）
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

            // 下载完成，提示即将安装
            bool isSetup = UpdateService.IsInnoSetupInstall();
            LogService.Info(
                $"Update UI: Download succeeded → {downloadedPath}. Install type: {(isSetup ? "Inno Setup" : "Portable")}",
                LogSource.System);
            var completeMsg = isSetup
                ? ResourceService.GetString("Update_DownloadComplete_Setup_Message")
                : ResourceService.GetString("Update_DownloadComplete_Portable_Message");

            var completeDialog = new ContentDialog
            {
                Title = ResourceService.GetString("Update_DownloadComplete_Title"),
                Content = new TextBlock
                {
                    Text = completeMsg,
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Microsoft YaHei UI")
                },
                PrimaryButtonText = ResourceService.GetString("Msg_Confirm"),
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
                RequestedTheme = App.CurrentTheme
            };

            await completeDialog.ShowAsync();

            // 启动安装器
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
            var dialog = new ContentDialog
            {
                Title = title,
                Content = new TextBlock
                {
                    Text = message,
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap,
                    FontFamily = new Microsoft.UI.Xaml.Media.FontFamily("Microsoft YaHei UI")
                },
                CloseButtonText = closeButtonText,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
                RequestedTheme = App.CurrentTheme
            };

            await dialog.ShowAsync();
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
            var resultDialog = new ContentDialog
            {
                Title = ResourceService.GetString("SettingsPage_CheckTools_Dialog_Title"),
                Content = new ScrollViewer
                {
                    MaxHeight = 450,
                    Content = resultPanel,
                    VerticalScrollBarVisibility = ScrollBarVisibility.Auto
                },
                CloseButtonText = ResourceService.GetString("Msg_Confirm"),
                XamlRoot = App.MainWindow.Content.XamlRoot,
                RequestedTheme = App.CurrentTheme
            };

            await resultDialog.ShowAsync();
        }
    }
}
