/*
 * CliManualDialogService.cs
 *
 * 在应用内弹窗展示 CLI 使用手册（CLI-User-Guide.md），与更新日志弹窗同一套交互。
 *
 * 内容来源（按优先级）：
 *   1. 本地文件 — 打包脚本把 docs/CLI-User-Guide*.md 复制进应用目录，
 *      优先读取可保证内容与当前安装版本一致，且无需联网；
 *   2. GitHub raw — 本地文件缺失（开发/商店版）时联网抓取兜底。
 *
 * 右上角"用系统默认应用打开"入口：本地有手册文件 → 用系统默认程序打开该文件；
 * 本地缺失 → 改为在默认浏览器中打开 GitHub 上的手册。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.IO;
using System.Net.Http;
using System.Threading.Tasks;
using LivePhotoBox.Models;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// CLI 使用手册弹窗服务。点击关于页"CLI 手册"按钮时调用 <see cref="ShowAsync"/>，
    /// 在应用内弹窗渲染 CLI-User-Guide.md。
    /// </summary>
    public static class CliManualDialogService
    {
        // ── 常量：本地手册文件名（与应用 exe 同目录） ──────────────────────
        private const string LocalFileEn = "CLI-User-Guide.md";
        private const string LocalFileZh = "CLI-User-Guide.zh-CN.md";

        // GitHub raw 兜底抓取地址（master 分支，HttpClient 秒开）
        private const string RawUrlEn = "https://raw.githubusercontent.com/LengxiQwQ/live-photo-box/master/docs/CLI-User-Guide.md";
        private const string RawUrlZh = "https://raw.githubusercontent.com/LengxiQwQ/live-photo-box/master/docs/CLI-User-Guide.zh-CN.md";

        // GitHub 网页版手册地址（"用浏览器打开"入口，让普通用户看到渲染格式）
        private const string BlobUrlEn = "https://github.com/LengxiQwQ/live-photo-box/blob/master/docs/CLI-User-Guide.md";
        private const string BlobUrlZh = "https://github.com/LengxiQwQ/live-photo-box/blob/master/docs/CLI-User-Guide.zh-CN.md";

        // 手册内相对链接的基准地址（如 [说明](../README.md) → GitHub 网页版）
        private const string BaseUrl = "https://github.com/LengxiQwQ/live-photo-box/blob/master/docs/";

        // 抓取手册用 HttpClient（raw.githubusercontent 无需 API 头）
        private static readonly HttpClient _httpClient = new()
        {
            Timeout = TimeSpan.FromSeconds(15)
        };

        static CliManualDialogService()
        {
            _httpClient.DefaultRequestHeaders.UserAgent.ParseAdd("LivePhotoBox-CliManual/1.0");
        }

        /// <summary>
        /// 弹出 CLI 手册大窗口。本地手册存在时秒开（不出现转圈）；
        /// 缺失时先转圈，后台联网抓取完成后替换为渲染内容。
        /// </summary>
        public static async Task ShowAsync(XamlRoot xamlRoot)
        {
            var contentHost = new Grid();

            // 本地手册同步读取（磁盘 IO，毫秒级）→ 有就直接渲染，无则转圈联网兜底
            string? localMarkdown = TryReadLocalManual();
            contentHost.Children.Add(localMarkdown != null
                ? BuildRenderedWebView(localMarkdown)
                : BuildLoadingStack());

            var root = new StackPanel
            {
                Spacing = 8,
                HorizontalAlignment = HorizontalAlignment.Stretch
            };
            root.Children.Add(BuildHeaderRow());
            root.Children.Add(contentHost);

            var dialog = new ContentDialog
            {
                Content = root,
                CloseButtonText = ResourceService.GetString("Msg_GotIt"),
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
                RequestedTheme = App.CurrentTheme
            };

            // ContentDialog 宽度由内部资源键控制，不能通过 Width/MinWidth/MaxWidth 属性设置。
            // 手册内容长，窗口比更新日志更大。
            dialog.Resources["ContentDialogMaxWidth"] = 1100.0;
            dialog.Resources["ContentDialogMinWidth"] = 760.0;

            // 继承主窗口强调色，防止按钮按下变白
            if (Application.Current.Resources.TryGetValue("SystemAccentColor", out var accent))
                dialog.Resources["SystemAccentColor"] = accent;
            if (Application.Current.Resources.TryGetValue("SystemControlHighlightAccentBrush", out var highlightBrush))
                dialog.Resources["SystemControlHighlightAccentBrush"] = highlightBrush;

            // 先弹窗、后加载：窗口立即出现，避免用户以为卡住。
            // 后台加载与窗口生命周期解耦：窗口关闭后立即返回，加载结果只对还开着的窗口生效。
            var showTask = dialog.ShowAsync();
            if (localMarkdown == null)
                _ = RunBackgroundLoadAsync(contentHost);
            await showTask;
        }

        /// <summary>加载中的占位内容：转圈 + 提示文字。</summary>
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
                Text = ResourceService.GetString("AboutPage_CliManualDialog_Loading"),
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

        /// <summary>标题行（内容顶部）：左侧标题文字 + 右侧"用系统默认应用打开"入口。</summary>
        private static Grid BuildHeaderRow()
        {
            var titleText = CreateTitleText(ResourceService.GetString("AboutPage_CliManualDialog_Title"));

            var grid = new Grid
            {
                ColumnDefinitions =
                {
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) },
                    new ColumnDefinition { Width = GridLength.Auto }
                }
            };
            grid.Children.Add(titleText);

            var link = BuildOpenExternalLink();
            grid.Children.Add(link);
            Grid.SetColumn(link, 1);
            return grid;
        }

        /// <summary>
        /// 后台加载手册并渲染进对话框内容区。
        /// 本地缺失 → 联网抓取；再失败 → 替换为错误提示（右上角外部打开入口仍可用）。
        /// </summary>
        private static async Task LoadAndRenderAsync(Grid contentHost)
        {
            string? markdown = TryReadLocalManual();
            if (string.IsNullOrWhiteSpace(markdown))
                markdown = await FetchManualAsync();

            if (string.IsNullOrWhiteSpace(markdown))
            {
                contentHost.Children.Clear();
                contentHost.Children.Add(BuildErrorStack());
                return;
            }

            contentHost.Children.Clear();
            contentHost.Children.Add(BuildRenderedWebView(markdown));
        }

        /// <summary>
        /// 构建渲染完成的 WebView2（Markdig → HTML）。
        /// 拦截用户点击的链接：http/https 用默认浏览器打开，相对链接经 baseUrl 跳到 GitHub。
        /// </summary>
        private static Microsoft.UI.Xaml.Controls.WebView2 BuildRenderedWebView(string markdown)
        {
            bool isDark = App.CurrentTheme == ElementTheme.Dark;
            var html = MarkdownRenderService.RenderToHtml(markdown, isDark, BaseUrl);

            var webView = new Microsoft.UI.Xaml.Controls.WebView2
            {
                // 高度与更新日志弹窗一致（460px），避免小屏幕上下被挤出窗口
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
                    LogService.Warn($"WebView2 init failed in CLI manual dialog: {ex.Message}",
                        source: LogSource.System);
                }
            };

            return webView;
        }

        /// <summary>后台加载手册（独立任务，不阻塞弹窗生命周期；所有异常在此兜底）。</summary>
        private static async Task RunBackgroundLoadAsync(Grid contentHost)
        {
            try
            {
                await LoadAndRenderAsync(contentHost);
            }
            catch (Exception ex)
            {
                LogService.Warn($"Failed to load CLI manual: {ex.Message}", source: LogSource.System);
            }
        }

        /// <summary>
        /// 右上角"用系统默认应用打开"入口：本地有手册文件 → 系统默认程序打开该文件；
        /// 本地缺失（开发/商店版）→ 改为默认浏览器打开 GitHub 上的手册。
        /// </summary>
        private static HyperlinkButton BuildOpenExternalLink()
        {
            var button = new HyperlinkButton
            {
                Content = ResourceService.GetString("AboutPage_CliManualDialog_OpenExternal"),
                FontSize = 14,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Right
            };
            button.Click += async (_, _) =>
            {
                string? localPath = TryGetLocalPath();
                if (localPath != null)
                    await FilePickerService.OpenFileAsync(localPath);
                else
                    await FilePickerService.OpenUriAsync(new Uri(GetBrowserUrl()));
            };
            return button;
        }

        /// <summary>加载失败时窗口内的错误内容：提示文字（右上角外部打开入口仍可用）。</summary>
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
                Text = ResourceService.GetString("AboutPage_CliManualDialog_Error"),
                FontSize = 13,
                TextWrapping = TextWrapping.Wrap,
                HorizontalAlignment = HorizontalAlignment.Center
            });
            return stack;
        }

        /// <summary>按当前 UI 语言联网抓取手册 markdown；失败返回 null。</summary>
        private static async Task<string?> FetchManualAsync()
        {
            try
            {
                string url = IsChineseUi() ? RawUrlZh : RawUrlEn;
                LogService.Debug($"Fetching CLI manual: {url}", source: LogSource.System);
                return await _httpClient.GetStringAsync(url);
            }
            catch (Exception ex)
            {
                LogService.Warn($"Failed to fetch CLI manual: {ex.Message}", source: LogSource.System);
                return null;
            }
        }

        /// <summary>按当前 UI 语言返回本地手册文件路径；文件不存在返回 null。</summary>
        private static string? TryGetLocalPath()
        {
            try
            {
                var path = Path.Combine(AppContext.BaseDirectory, IsChineseUi() ? LocalFileZh : LocalFileEn);
                return File.Exists(path) ? path : null;
            }
            catch
            {
                return null;
            }
        }

        /// <summary>读取本地手册内容；文件缺失或读取失败返回 null（走联网兜底）。</summary>
        private static string? TryReadLocalManual()
        {
            string? path = TryGetLocalPath();
            if (path == null)
                return null;

            try
            {
                return File.ReadAllText(path);
            }
            catch (Exception ex)
            {
                LogService.Warn($"Failed to read local CLI manual: {ex.Message}", source: LogSource.System);
                return null;
            }
        }

        /// <summary>按当前 UI 语言返回浏览器打开的 GitHub 网页版手册地址。</summary>
        private static string GetBrowserUrl() => IsChineseUi() ? BlobUrlZh : BlobUrlEn;

        private static bool IsChineseUi() => LanguageService.IsChineseUi();
    }
}
