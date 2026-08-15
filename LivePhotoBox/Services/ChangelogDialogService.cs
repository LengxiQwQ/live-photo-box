/*
 * ChangelogDialogService.cs
 *
 * 关于页"更新日志"按钮的应用内弹窗：
 * 点击后立即弹出窗口（窗内转圈 + 提示文字），后台抓取 GitHub 上的 changelog
 * markdown（按 UI 语言选中/英文版），再用 MarkdownRenderService + WebView2 内联渲染。
 * 抓取失败时在窗口内显示错误提示，保留"在浏览器中打开"入口。
 */

using LivePhotoBox.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Net.Http;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 更新日志弹窗服务。点击关于页"更新日志"按钮时调用 <see cref="ShowAsync"/>，
    /// 在应用内弹窗渲染 GitHub 上的 changelog 文档。
    /// </summary>
    public static class ChangelogDialogService
    {
        // ── 常量：changelog 文档地址（master 分支） ──────────────────────
        // 应用内抓取走 raw 地址（HttpClient 秒开，不经网页版渲染）；
        // "在浏览器中打开"走 github.com blob 网页版，让普通用户看到渲染好的文档。
        private const string RawUrlEn = "https://raw.githubusercontent.com/LengxiQwQ/live-photo-box/master/changelogs/CHANGELOG.md";
        private const string RawUrlZh = "https://raw.githubusercontent.com/LengxiQwQ/live-photo-box/master/changelogs/CHANGELOG.zh-CN.md";
        private const string BlobUrlEn = "https://github.com/LengxiQwQ/live-photo-box/blob/master/changelogs/CHANGELOG.md";
        private const string BlobUrlZh = "https://github.com/LengxiQwQ/live-photo-box/blob/master/changelogs/CHANGELOG.zh-CN.md";

        // 抓取 changelog 用 HttpClient（raw.githubusercontent 无需 API 头）
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        static ChangelogDialogService()
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LivePhotoBox-Changelog/1.0");
        }

        /// <summary>
        /// 弹出更新日志大窗口。窗口立即出现（窗内先转圈），
        /// 后台抓取 Markdown 完成后动态替换为渲染内容。
        /// </summary>
        public static async Task ShowAsync(XamlRoot xamlRoot)
        {
            // 标题行放内容顶部：ContentDialog 标题区不拉伸宽度（链接无法右对齐到边缘），
            // 放内容里才能撑满弹窗宽度。下方内容区在 加载中/渲染/失败 之间切换
            var contentHost = new Grid();
            contentHost.Children.Add(BuildLoadingStack());

            var root = new StackPanel
            {
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            root.Children.Add(BuildChangelogHeaderRow());
            root.Children.Add(contentHost);

            var dialog = new ContentDialog
            {
                Content = root,
                CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
                RequestedTheme = App.CurrentTheme
            };

            // ContentDialog 宽度由内部资源键控制，不能通过 Width/MinWidth/MaxWidth 属性设置
            dialog.Resources["ContentDialogMaxWidth"] = 900.0;
            dialog.Resources["ContentDialogMinWidth"] = 680.0;

            // 继承主窗口强调色，防止按钮按下变白
            if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var accent))
                dialog.Resources["SystemAccentColor"] = accent;
            if (Application.Current.Resources.TryGetValue("SystemControlHighlightAccentBrush", out var highlightBrush))
                dialog.Resources["SystemControlHighlightAccentBrush"] = highlightBrush;

            // 先弹窗、后加载：窗口立即出现，避免用户以为卡住。
            // 后台加载与窗口生命周期解耦：窗口关闭后立即返回（调用方按钮马上恢复），
            // 加载若还没完成则继续在后台跑，结果只对还开着的窗口生效。
            var showTask = dialog.ShowAsync();
            _ = RunBackgroundLoadAsync(contentHost);
            await showTask;
        }

        /// <summary>
        /// 加载中的占位内容：转圈 + 提示文字。
        /// </summary>
        private static StackPanel BuildLoadingStack()
        {
            var stack = new StackPanel
            {
                Spacing = 12,
                Padding = new Thickness(0, 40, 0, 40),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            stack.Children.Add(new ProgressRing
            {
                IsActive = true,
                Width = 40,
                Height = 40,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            stack.Children.Add(new TextBlock
            {
                Text = ResourceService.GetString("AboutPage_ChangelogDialog_Loading"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return stack;
        }

        /// <summary>创建标题文字（18px SemiBold，比原生标题小一号，观感更紧凑）。</summary>
        private static TextBlock CreateTitleText(string text)
        {
            return new TextBlock
            {
                Text = text,
                FontSize = 18,
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                VerticalAlignment = VerticalAlignment.Center
            };
        }

        /// <summary>标题行（内容顶部）：左侧标题文字 + 右侧"在浏览器中打开"链接，链接右对齐到弹窗边缘。</summary>
        private static Grid BuildChangelogHeaderRow()
        {
            var titleText = CreateTitleText(ResourceService.GetString("AboutPage_ChangelogDialog_Title"));

            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };
            grid.Children.Add(titleText);

            var link = BuildOpenBrowserLink();
            grid.Children.Add(link);
            Grid.SetColumn(link, 1);
            return grid;
        }

        /// <summary>
        /// 后台抓取 changelog 并渲染进对话框内容区。
        /// 成功 → 替换为 WebView2 渲染；失败 → 替换为错误提示 + "在浏览器中打开"。
        /// </summary>
        private static async Task LoadAndRenderAsync(Grid contentHost)
        {
            string? markdown = await FetchChangelogAsync();

            if (string.IsNullOrWhiteSpace(markdown))
            {
                // 加载失败 → 内容区显示错误提示（右上角"在浏览器中打开"仍可见）
                contentHost.Children.Clear();
                contentHost.Children.Add(BuildErrorStack());
                return;
            }

            // 渲染成 HTML（适配亮/暗主题；baseUrl 让相对链接能跳到 GitHub）
            bool isDark = App.CurrentTheme == ElementTheme.Dark;
            var baseUrl = "https://github.com/LengxiQwQ/live-photo-box/blob/master/";
            var html = MarkdownRenderService.RenderToHtml(markdown, isDark, baseUrl);

            var webView = new Microsoft.UI.Xaml.Controls.WebView2
            {
                Height = 460,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };

            // WebView2 初始化完成后注入 HTML，并拦截用户点击的链接改为外部浏览器打开
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
                        // 用户点击链接 → 取消内部跳转，只用默认浏览器打开 http/https 链接
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
                    LogService.Warn($"WebView2 init failed in changelog dialog: {ex.Message}",
                        source: LogSource.System);
                }
            };

            contentHost.Children.Clear();
            contentHost.Children.Add(webView);
        }

        /// <summary>后台加载 changelog（独立任务，不阻塞弹窗生命周期；所有异常在此兜底）。</summary>
        private static async Task RunBackgroundLoadAsync(Grid contentHost)
        {
            try
            {
                await LoadAndRenderAsync(contentHost);
            }
            catch (Exception ex)
            {
                LogService.Warn($"Failed to load changelog: {ex.Message}", source: LogSource.System);
            }
        }

        /// <summary>
        /// 右上角"在浏览器中打开"入口。
        /// </summary>
        private static HyperlinkButton BuildOpenBrowserLink()
        {
            var button = new HyperlinkButton
            {
                Content = ResourceService.GetString("AboutPage_ChangelogDialog_OpenBrowser"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            button.Click += async (_, _) =>
                await FilePickerService.OpenUriAsync(new Uri(GetBrowserUrl()));
            return button;
        }

        /// <summary>
        /// 抓取失败时窗口内的错误内容：提示文字 + "在浏览器中打开"兜底。
        /// </summary>
        private static StackPanel BuildErrorStack()
        {
            var stack = new StackPanel
            {
                Spacing = 12,
                Padding = new Thickness(0, 40, 0, 40),
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            stack.Children.Add(new TextBlock
            {
                Text = ResourceService.GetString("AboutPage_ChangelogDialog_Error"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return stack;
        }

        /// <summary>
        /// 按当前 UI 语言抓取对应语言的 changelog markdown；失败返回 null。
        /// </summary>
        private static async Task<string?> FetchChangelogAsync()
        {
            try
            {
                string url = IsChineseUi() ? RawUrlZh : RawUrlEn;
                LogService.Debug($"Fetching changelog: {url}", source: LogSource.System);
                return await _httpClient.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                LogService.Warn($"Failed to fetch changelog: {ex.Message}", source: LogSource.System);
                return null;
            }
        }

        /// <summary>
        /// 按当前 UI 语言返回浏览器打开的 GitHub changelog 网页版地址。
        /// </summary>
        private static string GetBrowserUrl() => IsChineseUi() ? BlobUrlZh : BlobUrlEn;

        private static bool IsChineseUi() => LanguageService.IsChineseUi();
    }
}
