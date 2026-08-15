/*
 * MainWindow.xaml.cs
 *
 * 主窗口代码后置。继承 Microsoft.UI.Xaml.Window，负责：
 *   - 窗口初始化和 DPI 适配
 *   - NavigationView 页面导航
 *   - 背景材质（Mica / Acrylic）管理和切换
 *   - 窗口透明度控制
 *   - 主题切换与标题栏按钮配色
 *   - 状态栏/历史导航可见性控制
 *
 * 对应 ViewModel：AppViewModel（单例）
 *
 * 生命周期：
 *   - 构造函数 → 窗口初始化 → 材质设置 → 主题应用 → 默认导航到 HomePage
 *   - 全局 ViewModel 属性变更驱动 UI 更新
 */

using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.Threading;
using System.Threading.Tasks;
using System.IO;
using Windows.Graphics;
using Windows.UI;
using LivePhotoBox.Models;
using WinRT;

namespace LivePhotoBox
{
    public sealed partial class MainWindow : Window
    {
        private const int DefaultWindowWidth = 1222;
        private const int DefaultWindowHeight = 731;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        // 窗口整体透明度相关 Win32 API
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        private static extern bool SetWindowTextW(IntPtr hWnd, string lpString);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x80000;
        private const uint LWA_ALPHA = 0x2;

        private DesktopAcrylicController? _acrylicController;
        private SystemBackdropConfiguration? _acrylicConfig;
        private ICompositionSupportsSystemBackdrop? _acrylicTarget;
        private CancellationTokenSource? _acrylicDebounceCts;

        // 首次导航标记：抑制 NavigationView 初始化触发的选中动画
        private bool _isFirstNavigation = true;

        // 全局 AppViewModel 单例
        public AppViewModel ViewModel => AppViewModel.Instance;

        // 全屏预览控件（Lightbox），供各页面调用
        public Controls.LightboxPreview Lightbox => LightboxPreview;

        // 构造函数：初始化窗口、设置标题栏、配置背景材质、绑定 ViewModel 事件、导航到默认页面
        public MainWindow()
        {
            InitializeComponent();
            LogService.Info("MainWindow constructed.", LogSource.UI);

            // 本地化窗口标题。ResourceService 在非打包模式下可能尚未就绪，
            // 这里先设一个初始值；AppTitleBar.Loaded 中会从已解析的 XAML 文字再次设置。
            Title = ResourceService.GetString("MainWindow_Title.Text");

            // AppTitleBar 加载完成后（XAML x:Uid 已解析），从标题栏文字读取正确值，
            // 确保 Alt+Tab、任务栏、Win32 窗口标题全部正确
            AppTitleBar.Loaded += (_, _) =>
            {
                string localizedTitle = TitleBarText.Text;
                if (string.IsNullOrWhiteSpace(localizedTitle)) return;

                Title = localizedTitle;
                IntPtr hwnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                SetWindowTextW(hwnd, localizedTitle);
                var wid = Win32Interop.GetWindowIdFromWindow(hwnd);
                var aw = AppWindow.GetFromWindowId(wid);
                if (aw != null) aw.Title = localizedTitle;
            };

            // 窗口关闭。
            // 行业标准做法：只做"不做就会崩"的事。OS 在进程退出时自动回收内存/句柄/子进程。
            // WinUI 3 特有风险：
            //   a) DesktopAcrylicController 是 DWM COM 接口，窗口句柄销毁后无法释放 → 崩
            //   b) DispatcherTimer.Tick 在窗口销毁后可能被 DispatcherQueue 派发，
            //      回调访问已释放的 XAML 元素 → 0xc0000005
            // 除此之外的"资源释放"都是替 OS 干它本来就会干的事。
            Closed += (_, _) =>
            {
                // 1. 离开当前页面 → 触发 Page.Unloaded → 停止页面级 DispatcherQueue 定时器
                //    （如 RepairPage 的缩略图检查定时器）
                try { MainFrame.Navigate(typeof(Microsoft.UI.Xaml.Controls.Page)); }
                catch { /* 窗口已在销毁中，导航失败是预期的 */ }

                // 2. 释放 Acrylic 控制器（DWM COM，必须在窗口句柄有效时做）
                CleanupAcrylicController();

                // 3. 停止所有 DispatcherTimer 并解除 Tick 回调
                //    Merge / Split / Repair 各有一个 60ms UI 刷新定时器
                ViewModel.Cleanup();

                // Done. OS 接管：回收内存、关闭文件、杀掉 exiftool/ffmpeg 子进程。
                LogService.Info("MainWindow closed.", LogSource.UI);
                LogService.MarkCleanShutdown();
            };

            // 启用自定义标题栏
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            // 获取窗口句柄并设置图标、尺寸和居中位置（支持 DPI 缩放）
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow != null)
            {
                // 设置 AppWindow.Title 控制 Alt+Tab、任务栏中显示的窗口标题
                // （Window.Title 只控制标题栏文字；不设 AppWindow.Title 会回退到
                //  ms-resource:Package_DisplayName，非打包模式下无法解析，Alt+Tab 显示异常）
                appWindow.Title = Title;

                // 直接设置 Win32 窗口标题作为双保险
                // （非打包 WinUI 3 中，Alt+Tab 最终读取的是 HWND 的窗口文字）
                SetWindowTextW(hWnd, Title);

                string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "Icons", "AppIcon.ico");
                if (File.Exists(iconPath))
                {
                    appWindow.SetIcon(iconPath);
                }

                try
                {
                    uint dpi = GetDpiForWindow(hWnd);
                    float scaleFactor = dpi / 96f;

                    int scaledWidth = (int)(DefaultWindowWidth * scaleFactor);
                    int scaledHeight = (int)(DefaultWindowHeight * scaleFactor);

                    appWindow.Resize(new SizeInt32(scaledWidth, scaledHeight));

                    var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                    if (displayArea != null)
                    {
                        var workArea = displayArea.WorkArea;

                        int x = workArea.X + (workArea.Width - scaledWidth) / 2;
                        int y = workArea.Y + (workArea.Height - scaledHeight) / 2;

                        x = Math.Max(workArea.X, x);
                        y = Math.Max(workArea.Y, y);

                        appWindow.Move(new PointInt32(x, y));
                    }
                }
                catch (Exception ex)
                {
                    LogService.Warn($"DPI Scaling initialization failed: {ex.Message}", source: LogSource.UI);
                    appWindow.Resize(new SizeInt32(DefaultWindowWidth, DefaultWindowHeight));
                }
            }

            // NavigationView 加载完成后本地化设置项标签
            NavView.Loaded += (_, _) =>
            {
                if (NavView.SettingsItem is NavigationViewItem settingsItem)
                {
                    settingsItem.Content = ResourceService.GetString("Nav_Settings");
                }
            };

            // 绑定 ViewModel 事件
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ViewModel.Settings.PropertyChanged += OnSettingsPropertyChanged;
            ViewModel.Settings.PropertyChanged += OnSettingsHistoryVisibilityChanged;
            ViewModel.RequestNavigateToPage += OnRequestNavigateToPage;

            // 应用初始化配置
            UpdateTheme();
            UpdateBackdrop();
            UpdateStatusBarVisibility();
            UpdateHistoryNavVisibility();

            // 应用持久化的窗口透明度
            if (ViewModel.Settings.WindowOpacity < 1.0)
            {
                EnableWindowLayering();
                ApplyWindowOpacity();
            }

            // 默认导航到首页（抑制动画，NavigationView 首次选中也会被 _isFirstNavigation 抑制）
            NavigateToPage(typeof(Views.HomePage), null, new SuppressNavigationTransitionInfo());
        }

        // 响应 AppViewModel 属性变更：状态栏可见性
        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppViewModel.IsStatusBarVisible))
            {
                UpdateStatusBarVisibility();
            }
        }

        // 响应 SettingsViewModel 属性变更：背景材质、主题、亚克力透明度、窗口透明度
        private void OnSettingsPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            switch (e.PropertyName)
            {
                case nameof(SettingsViewModel.BackdropIndex):
                    UpdateBackdrop();
                    break;
                case nameof(SettingsViewModel.ElementTheme):
                    UpdateTheme();
                    break;
                case nameof(SettingsViewModel.AcrylicTintOpacity):
                    UpdateAcrylicTintOpacity();
                    break;
                case nameof(SettingsViewModel.WindowOpacity):
                    ApplyWindowOpacity();
                    break;
            }
        }

        // 响应外部页面导航请求，解析 pageTag 并跳转到对应页面
        private void OnRequestNavigateToPage(object? sender, string pageTag)
        {
            if (pageTag.StartsWith("Home"))
            {
                NavView.SelectedItem = NavView.MenuItems[0];

                string? feature = null;
                if (pageTag.Contains("_"))
                {
                    feature = pageTag.Split('_')[1];
                }

                LogService.Info($"NavigateToPage: HomePage, Parameter={feature}", LogSource.UI);
                ViewModel.SetCurrentStatusPage(null);
                MainFrame.Navigate(typeof(Views.HomePage), feature);
            }
        }

        // 更新页面底部状态栏的可见性
        private void UpdateStatusBarVisibility()
        {
            PageStatusBar.Visibility = ViewModel.IsStatusBarVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        // 更新导航栏中历史页面的可见性
        private void UpdateHistoryNavVisibility()
        {
            NavHistory.Visibility = ViewModel.Settings.IsHistoryPageVisible
                ? Visibility.Visible
                : Visibility.Collapsed;
        }

        // 响应历史页面可见性设置变更
        private void OnSettingsHistoryVisibilityChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SettingsViewModel.IsHistoryPageVisible))
                UpdateHistoryNavVisibility();
        }

        // 更新窗口背景材质。根据 BackdropIndex 选择：
        // 0 — Mica Base
        // 1 — Mica BaseAlt
        // 2 — Acrylic Base
        // 3 — Acrylic Thin
        // 4 — None（纯色背景）
        // 在 Acrylic 与 Mica/None 之间切换时清理旧的 Acrylic Controller。
        private void UpdateBackdrop()
        {
            // 先清理旧的 Acrylic Controller
            CleanupAcrylicController();

            int idx = ViewModel.Settings.BackdropIndex;
            if (idx is 2 or 3) // 2=Acrylic  3=Acrylic 薄透
            {
                _acrylicTarget = this.As<ICompositionSupportsSystemBackdrop>();
                _acrylicController = new DesktopAcrylicController
                {
                    Kind = idx == 2
                        ? Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicKind.Base
                        : Microsoft.UI.Composition.SystemBackdrops.DesktopAcrylicKind.Thin,
                    TintOpacity = (float)ViewModel.Settings.AcrylicTintOpacity,
                };
                _acrylicConfig = new SystemBackdropConfiguration
                {
                    IsInputActive = true,
                    Theme = GetCurrentTheme() == ElementTheme.Dark
                        ? Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Dark
                        : Microsoft.UI.Composition.SystemBackdrops.SystemBackdropTheme.Light,
                };
                _acrylicController.SetSystemBackdropConfiguration(_acrylicConfig);
                _acrylicController.AddSystemBackdropTarget(_acrylicTarget);
                SystemBackdrop = null;
            }
            else
            {
                SystemBackdrop = idx switch
                {
                    0 => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base },
                    1 => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
                    _ => null
                };
            }

            if (Content is Grid rootGrid)
            {
                if (idx == 4) // None
                {
                    rootGrid.Background = GetCurrentTheme() == ElementTheme.Dark
                        ? new SolidColorBrush(Microsoft.UI.Colors.Black)
                        : new SolidColorBrush(Microsoft.UI.Colors.White);
                }
                else
                {
                    rootGrid.Background = new SolidColorBrush(Colors.Transparent);
                }
            }
        }

        // 清理 Acrylic Controller 资源，移除背景目标并释放所有相关对象
        private void CleanupAcrylicController()
        {
            _acrylicDebounceCts?.Cancel();
            _acrylicDebounceCts?.Dispose();
            _acrylicDebounceCts = null;

            if (_acrylicController == null) return;
            if (_acrylicTarget != null) _acrylicController.RemoveSystemBackdropTarget(_acrylicTarget);
            _acrylicController.Dispose();
            _acrylicController = null;
            _acrylicTarget = null;
            _acrylicConfig = null;
        }

        // 滑块拖拽时频繁触发 → 防抖 250ms，松手后只重建一次 controller。
        // DesktopAcrylicController.TintOpacity 在 AddSystemBackdropTarget 后设值无效，必须重建。
        private async void UpdateAcrylicTintOpacity()
        {
            _acrylicDebounceCts?.Cancel();
            _acrylicDebounceCts = new CancellationTokenSource();
            var token = _acrylicDebounceCts.Token;
            try
            {
                await Task.Delay(250, token);
                if (!token.IsCancellationRequested)
                    UpdateBackdrop();
            }
            catch (OperationCanceledException) { }
        }

        // 更新窗口主题，同步标题栏按钮颜色，必要时重建背景材质
        private void UpdateTheme()
        {
            if (Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = (ElementTheme)ViewModel.Settings.ElementTheme;
            }

            UpdateTitleBarButtonColors();

            // Acrylic controller 需要随主题重建
            if (ViewModel.Settings.BackdropIndex is 2 or 3 or 4)
            {
                UpdateBackdrop();
            }
        }

        // 根据当前主题设置标题栏按钮的悬停/按下/非活动配色
        private void UpdateTitleBarButtonColors()
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

            if (!AppWindowTitleBar.IsCustomizationSupported() || appWindow.TitleBar == null)
            {
                return;
            }

            if (GetCurrentTheme() == ElementTheme.Dark)
            {
                appWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
                appWindow.TitleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                appWindow.TitleBar.ButtonHoverBackgroundColor = Color.FromArgb(25, 255, 255, 255);
                appWindow.TitleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
                appWindow.TitleBar.ButtonPressedBackgroundColor = Color.FromArgb(51, 255, 255, 255);
                appWindow.TitleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.DarkGray;
            }
            else
            {
                appWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.Black;
                appWindow.TitleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.Black;
                appWindow.TitleBar.ButtonHoverBackgroundColor = Color.FromArgb(25, 0, 0, 0);
                appWindow.TitleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.Black;
                appWindow.TitleBar.ButtonPressedBackgroundColor = Color.FromArgb(51, 0, 0, 0);
                appWindow.TitleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.Gray;
            }
        }

        // 获取当前有效主题（若设为 Default 则根据系统背景色推断）
        private ElementTheme GetCurrentTheme()
        {
            if (Content is FrameworkElement rootElement && rootElement.RequestedTheme != ElementTheme.Default)
            {
                return rootElement.RequestedTheme;
            }

            var settings = new Windows.UI.ViewManagement.UISettings();
            var backgroundColor = settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
            return backgroundColor.R < 128 ? ElementTheme.Dark : ElementTheme.Light;
        }

        // NavigationView 选中项变更时，根据 Tag 或 Settings 项导航到对应页面
        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            // NavigationView 初始化时选中 Home 项会触发一次选中事件，
            // 首次抑制动画避免启动闪动
            NavigationTransitionInfo? transitionInfo = _isFirstNavigation
                ? new SuppressNavigationTransitionInfo() : null;
            _isFirstNavigation = false;

            if (args.IsSettingsSelected)
            {
                NavigateToPage(typeof(Views.SettingsPage), null, transitionInfo);
                return;
            }

            if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
            {
                return;
            }

            switch (tag)
            {
                case "Home": NavigateToPage(typeof(Views.HomePage), null, transitionInfo); break;
                case "Merge": NavigateToPage(typeof(Views.MergePage), null, transitionInfo); break;
                case "Split": NavigateToPage(typeof(Views.SplitPage), null, transitionInfo); break;
                case "History": NavigateToPage(typeof(Views.HistoryPage), null, transitionInfo); break;
                case "Edit": NavigateToPage(typeof(Views.EditPage), null, transitionInfo); break;
                case "PhotoClassify": NavigateToPage(typeof(Views.PhotoClassifyPage), null, transitionInfo); break;
                case "Repair": NavigateToPage(typeof(Views.RepairPage), "Repair", transitionInfo); break;
                case "About": NavigateToPage(typeof(Views.AboutPage), null, transitionInfo); break;
            }
        }

        // 执行 Frame 导航并设置当前状态页标签
        // transitionInfo 为 null 时使用默认动画
        private void NavigateToPage(Type pageType, string? statusPageTag, NavigationTransitionInfo? transitionInfo = null)
        {
            LogService.Info($"NavigateToPage: {pageType.Name}, StatusTag={statusPageTag ?? "(null)"}", LogSource.UI);
            ViewModel.SetCurrentStatusPage(statusPageTag);
            if (transitionInfo != null)
                MainFrame.Navigate(pageType, null, transitionInfo);
            else
                MainFrame.Navigate(pageType);
        }

        // 根据 Tag 字符串切换 NavigationView 的选中项（不触发导航）
        public void SwitchToPageByTag(string tag)
        {
            if (NavView == null) return;

            foreach (var item in NavView.MenuItems)
            {
                if (item is NavigationViewItem navItem && navItem.Tag?.ToString() == tag)
                {
                    NavView.SelectedItem = navItem;
                    break;
                }
            }
        }

        // 导航到设置页面，可附带导航参数（如滚动到指定分类标题）。
        // 先直接导航 Frame（确保参数传递），再更新侧栏选中项。
        // 临时解绑 SelectionChanged 防止重复导航。
        public void NavigateToSettings(string? parameter)
        {
            if (NavView == null) return;

            ViewModel.SetCurrentStatusPage(null);
            MainFrame.Navigate(typeof(Views.SettingsPage), parameter);

            // 更新侧栏，但不触发重复导航
            if (NavView.SelectedItem != NavView.SettingsItem)
            {
                NavView.SelectionChanged -= NavView_SelectionChanged;
                NavView.SelectedItem = NavView.SettingsItem;
                NavView.SelectionChanged += NavView_SelectionChanged;
            }
        }

        #region Window Opacity

        private bool _windowLayeringEnabled;

        // 启用窗口 WS_EX_LAYERED 样式（仅一次），后续通过 SetLayeredWindowAttributes 调整透明度
        private void EnableWindowLayering()
        {
            if (_windowLayeringEnabled) return;
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            SetWindowLong(hWnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
            _windowLayeringEnabled = true;
        }

        // 将 ViewModel.WindowOpacity 应用到窗口
        private void ApplyWindowOpacity()
        {
            double value = ViewModel.Settings.WindowOpacity;
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);

            if (value >= 1.0)
            {
                // 完全不透明 → 移除 LWA_ALPHA，但保留 WS_EX_LAYERED（不影响性能）
                SetLayeredWindowAttributes(hWnd, 0, 255, LWA_ALPHA);
            }
            else
            {
                if (!_windowLayeringEnabled) EnableWindowLayering();
                byte alpha = (byte)(value * 255);
                SetLayeredWindowAttributes(hWnd, 0, alpha, LWA_ALPHA);
            }
        }

        #endregion
    }
}
