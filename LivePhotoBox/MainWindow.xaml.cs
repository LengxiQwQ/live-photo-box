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
 *   - 任务栏进度条（ITaskbarList3，仅处理队列进行时显示）
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
using System.Collections.Generic;
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
    // 任务栏进度条状态（TBPF_* 原生标志位）。
    // 底层类型用 int（I4，与原生 TBPF UINT 位对齐一致），保证 [ComImport] vtable 调用正确。
    internal enum TaskbarProgressFlags : int
    {
        NoProgress = 0x00000000,     // 隐藏任务栏进度
        Indeterminate = 0x00000001,  // 无限循环动画（扫描中 / 首个任务完成前显示）
        Normal = 0x00000002,         // 正常确定进度（绿色）
        Error = 0x00000004,          // 错误（红色）
        Paused = 0x00000008          // 暂停（黄色）
    }

    // ITaskbarList3 COM 接口。只声明本项目实际调用的方法，且声明顺序必须与
    // 原生 vtable 槽位一致（.NET COM 互操作按声明顺序映射槽位）：
    //   IUnknown(0-2) → HrInit(3) → AddTab(4) → DeleteTab(5) → ActivateTab(6)
    //   → SetActiveAlt(7) → MarkFullscreenWindow(8) → SetProgressValue(9)
    //   → SetProgressState(10)
    // 槽位 11 及以后的方法（RegisterTab、SetTabOrder 等）本项目从不调用，
    // 因此无需声明占位；若将来要调用它们，必须按原生顺序补全，否则槽位错位
    // 会导致静默失败或崩溃。
    // 注意：不要改用 ITaskbarList4（IID C43DC798-...）。部分 Windows 环境
    // （如第三方任务栏替换/精简系统）对 TaskbarList coclass 的
    // ITaskbarList4 QI 会返回 E_NOINTERFACE，导致初始化失败；而 ITaskbarList3
    // 是社区成熟实现（DevWinUI、Windows Terminal）采用的最小可靠接口，
    // SetProgressValue/SetProgressState 位于槽位 9/10，足够本功能使用。
    [ComImport]
    [Guid("ea1afb91-9e28-4b86-90e9-9e9f8a5eefaf")]
    [InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    internal interface ITaskbarList3
    {
        // ITaskbarList
        void HrInit();
        void AddTab(IntPtr hwnd);
        void DeleteTab(IntPtr hwnd);
        void ActivateTab(IntPtr hwnd);
        void SetActiveAlt(IntPtr hwnd);
        // ITaskbarList2
        void MarkFullscreenWindow(IntPtr hwnd, [MarshalAs(UnmanagedType.Bool)] bool fFullscreen);
        // ITaskbarList3
        void SetProgressValue(IntPtr hwnd, ulong ullCompleted, ulong ullTotal);
        void SetProgressState(IntPtr hwnd, TaskbarProgressFlags tbpFlags);
    }

    public sealed partial class MainWindow : Window
    {
        private const int DefaultWindowWidth = 1222;
        private const int DefaultWindowHeight = 731;

        // 窗口布局记忆键名（位置不再记忆，启动永远居中）
        private const string WndKeyW = "MainWindow_Width";
        private const string WndKeyH = "MainWindow_Height";
        private const string WndKeyMax = "MainWindow_Maximized";

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

        // ── 任务栏进度条 ──────────────────────────────────
        // ITaskbarList3 COM 单例，用于在任务栏按钮上显示进度条。
        private static ITaskbarList3? _taskbarList;
        // 缓存窗口句柄，避免重复调用 WindowNative.GetWindowHandle。
        private IntPtr _windowHandle;
        // 成功闪烁的 CancellationTokenSource：处理完成时短暂显示 100% 后清除。
        private CancellationTokenSource? _successFlashCts;
        // 最近一次处于"处理中"（IsProcessing=true）的页面。
        // 用于区分"处理任务被取消"（任务栏显示红色）与"扫描被取消"（任务栏不显示）。
        private WorkViewModelBase? _lastProcessingVm;
        // 各扫描源（合并/拆分/修复/编辑页左侧目录）最近一次扫描的开始时间，
        // 多扫描同时进行时显示"最新开始"的那个。
        private readonly Dictionary<object, DateTime> _scanStartTimes = new();

        // 重置布局后的重启标记：避免重启退出时 Closed 事件把当前（旧）尺寸写回设置
        public bool IsRestartingAfterLayoutReset { get; set; }

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
                // 0. 记住窗口位置/大小/最大化状态（用户可在设置里关闭）
                SaveWindowLayout();

                // 1. 离开当前页面 → 触发 Page.Unloaded → 停止页面级 DispatcherQueue 定时器
                //    （如 RepairPage 的缩略图检查定时器）
                try { MainFrame.Navigate(typeof(Microsoft.UI.Xaml.Controls.Page)); }
                catch { /* 窗口已在销毁中，导航失败是预期的 */ }

                // 2. 释放 Acrylic 控制器（DWM COM，必须在窗口句柄有效时做）
                CleanupAcrylicController();

                // 2.5 取消任务栏成功闪烁延迟任务，并清除任务栏进度
                _successFlashCts?.Cancel();
                _successFlashCts?.Dispose();
                _successFlashCts = null;
                ClearTaskbarProgress();

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

            // 初始化任务栏进度条（ITaskbarList3 COM）
            InitializeTaskbarList();

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

                    // 窗口位置不记忆：无论是否开启"记住窗口布局"，都按默认/记忆的尺寸
                    // 恢复后，把窗口居中到当前显示器工作区。
                    int restoreW = DefaultWindowWidth;
                    int restoreH = DefaultWindowHeight;
                    if (ViewModel.Settings.IsRememberWindowLayout)
                    {
                        restoreW = AppSettingsService.GetValue(WndKeyW, DefaultWindowWidth);
                        restoreH = AppSettingsService.GetValue(WndKeyH, DefaultWindowHeight);
                        if (restoreW <= 0) restoreW = DefaultWindowWidth;
                        if (restoreH <= 0) restoreH = DefaultWindowHeight;
                    }

                    // 限制在显示器工作区内，避免记忆的尺寸比当前屏幕还大
                    var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                    if (displayArea != null)
                    {
                        var workArea = displayArea.WorkArea;
                        restoreW = Math.Min(restoreW, workArea.Width);
                        restoreH = Math.Min(restoreH, workArea.Height);
                    }

                    appWindow.Resize(new SizeInt32((int)(restoreW * scaleFactor), (int)(restoreH * scaleFactor)));

                    // 居中显示
                    if (displayArea != null)
                    {
                        var workArea = displayArea.WorkArea;
                        int x = workArea.X + (workArea.Width - appWindow.Size.Width) / 2;
                        int y = workArea.Y + (workArea.Height - appWindow.Size.Height) / 2;
                        x = Math.Max(workArea.X, x);
                        y = Math.Max(workArea.Y, y);
                        appWindow.Move(new PointInt32(x, y));
                    }

                    // 若记忆了最大化状态，则在居中后恢复最大化
                    if (ViewModel.Settings.IsRememberWindowLayout
                        && AppSettingsService.GetValue(WndKeyMax, false))
                    {
                        ShowWindow(hWnd, SW_MAXIMIZE);
                    }
                }
                catch (Exception ex)
                {
                    LogService.Warn($"DPI Scaling initialization failed: {ex.Message}", source: LogSource.UI);
                    appWindow.Resize(new SizeInt32(DefaultWindowWidth, DefaultWindowHeight));
                }
            }

            // 预热任务栏进度条：此时窗口句柄已可用（上面已成功用于设置图标/尺寸），
            // 提前在 UI 线程建立 ITaskbarList3 COM 连接，避免首次进度更新从
            // 工作线程触发时才初始化（COM 对象公寓模型与句柄获取时机的不确定性）。
            // 初始化失败不阻塞主功能，仅记录 Warn。
            _windowHandle = hWnd;
            EnsureTaskbarList();

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
            // 编辑页左侧目录扫描：IsScanning 变化时刷新任务栏（无处理时显示动画条）
            ViewModel.Edit.PropertyChanged += OnEditViewModelPropertyChanged;

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

        // 响应 AppViewModel 属性变更：状态栏可见性、任务栏进度条
        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName is nameof(AppViewModel.IsStatusBarVisible))
            {
                UpdateStatusBarVisibility();
                return;
            }

            // 任务栏进度条：直接读取三个页面 ViewModel 各自的进度/状态，
            // 不依赖 AppViewModel 的全局 FooterProgress* 值（那个已禁用）。
            // AppViewModel.OnChildPropertyChangedHandler 在子 VM 的 IsProcessing /
            // Progress / ProgressBarState 变化时触发 NotifyFooterProperties()，
            // 这里监听其转发的 PropertyChanged 事件来驱动刷新。
            switch (e.PropertyName)
            {
                case nameof(AppViewModel.FooterIsIndeterminate):
                case nameof(AppViewModel.FooterProgressBarState):
                case nameof(AppViewModel.FooterProgressBarValue):
                case nameof(AppViewModel.IsAnyWorkPageProcessing):
                    UpdateTaskbarProgress();
                    break;
            }
        }

        // 响应 EditViewModel 属性变更：左侧目录扫描开始/结束时刷新任务栏。
        private void OnEditViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(EditViewModel.IsScanning))
            {
                UpdateTaskbarProgress();
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

        #region Window Layout Persistence

        // 保存当前窗口大小/最大化状态（不记忆位置，启动时永远居中）
        private void SaveWindowLayout()
        {
            try
            {
                // 重置布局后即将重启，跳过保存，否则刚删除的键又被写回
                if (IsRestartingAfterLayoutReset) return;
                if (!ViewModel.Settings.IsRememberWindowLayout) return;

                IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
                var windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
                var appWindow = AppWindow.GetFromWindowId(windowId);
                if (appWindow == null) return;

                uint dpi = GetDpiForWindow(hWnd);
                float scaleFactor = dpi / 96f;

                // 用 rcNormalPosition（还原态矩形）作为记忆的窗口大小：
                // 全屏/最大化时 appWindow.Size 是工作区全尺寸，若存它，
                // 退出最大化后窗口模式会错误地保持全屏大小——没有独立的窗口模式记忆。
                // rcNormalPosition 始终记录"窗口模式"下的矩形，最大化/最小化不影响它。
                int logicalW, logicalH;
                bool isMaximized = false;
                var placement = new WINDOWPLACEMENT();
                placement.Length = Marshal.SizeOf<WINDOWPLACEMENT>();
                if (GetWindowPlacement(hWnd, ref placement))
                {
                    logicalW = (int)((placement.NormalPositionRight - placement.NormalPositionLeft) / scaleFactor);
                    logicalH = (int)((placement.NormalPositionBottom - placement.NormalPositionTop) / scaleFactor);
                    isMaximized = placement.ShowCmd == SW_MAXIMIZE;
                }
                else
                {
                    // 回退：用 AppWindow 当前物理尺寸
                    logicalW = (int)(appWindow.Size.Width / scaleFactor);
                    logicalH = (int)(appWindow.Size.Height / scaleFactor);
                }

                AppSettingsService.SetValue(WndKeyW, logicalW);
                AppSettingsService.SetValue(WndKeyH, logicalH);
                AppSettingsService.SetValue(WndKeyMax, isMaximized);

                LogService.Info($"Window layout saved: {logicalW}x{logicalH} max={isMaximized}", LogSource.UI);
            }
            catch (Exception ex)
            {
                LogService.Warn($"Window layout save failed: {ex.Message}", source: LogSource.UI);
            }
        }

        // Win32 常量
        private const int SW_MAXIMIZE = 3;
        private const int SW_RESTORE = 9;

        [DllImport("user32.dll")]
        private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

        [DllImport("user32.dll")]
        private static extern bool SetForegroundWindow(IntPtr hWnd);

        // 通知点击后窗口带到前台（App 回调调用，需 public）。
        public void ActivateFromNotification()
        {
            if (_windowHandle == IntPtr.Zero)
            {
                _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
            }
            if (_windowHandle == IntPtr.Zero) return;
            ShowWindow(_windowHandle, SW_RESTORE);
            SetForegroundWindow(_windowHandle);
        }

        // 通知点击导航：根据 toast 激活参数跳到对应页面（App 回调调用，需 public）。
        public void NavigateFromNotification(string? tag)
        {
            switch (tag)
            {
                case "Merge": SwitchToPageByTag("Merge"); break;
                case "Split": SwitchToPageByTag("Split"); break;
                case "Repair": SwitchToPageByTag("Repair"); break;
            }
        }

        [DllImport("user32.dll")]
        private static extern bool GetWindowPlacement(IntPtr hWnd, ref WINDOWPLACEMENT lpwndpl);

        [StructLayout(LayoutKind.Sequential)]
        private struct WINDOWPLACEMENT
        {
            public int Length;
            public int Flags;
            public int ShowCmd;
            public int PtMinPositionX;
            public int PtMinPositionY;
            public int PtMaxPositionX;
            public int PtMaxPositionY;
            public int NormalPositionLeft;
            public int NormalPositionTop;
            public int NormalPositionRight;
            public int NormalPositionBottom;
        }

        #endregion

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

        #region Taskbar Progress

        // 成功完成后任务栏 100% 绿色保持时长（毫秒）。
        private const int SuccessFlashDelayMs = 1500;

        // 初始化任务栏进度条（延迟初始化：窗口句柄和 COM 接口首次需要时才建立）。
        private void InitializeTaskbarList()
        {
            _windowHandle = IntPtr.Zero;
        }

        // 核心更新入口：任务栏跟随软件内进度条——
        //   处理中（IsProcessing）→ 跟随该页面进度条（首个任务完成前动画，之后百分比）；
        //   无处理但有扫描（三个工作页或编辑页左侧目录）→ 跟随"最新开始"扫描源的扫描进度条：
        //     枚举阶段（总数未知）→ 不确定动画条；拿到总数后 → 确定型扫描百分比；
        //   取消 → 红色；完成 → 100% 闪烁后消失。
        // 直接读取各页面 ViewModel（Merge/Split/Repair），
        // 不依赖 AppViewModel 的全局 FooterProgress* 属性（那个已禁用，且需要 CurrentStatusPageTag）。
        private void UpdateTaskbarProgress()
        {
            // 每次调用都尝试延迟初始化（窗口句柄和 COM 接口首次需要时才建立）
            if (!EnsureTaskbarList()) return;

            try
            {
                // 维护各页面扫描开始时间（多页面同时扫描时取"最新开始"者）
                UpdateScanTracking();

                // 1) 有页面在处理（三个页面互斥，只能有一个）→ 跟随该页面的进度条
                WorkViewModelBase? activeVm = null;
                if (ViewModel.Merge.IsProcessing) activeVm = ViewModel.Merge;
                else if (ViewModel.Split.IsProcessing) activeVm = ViewModel.Split;
                else if (ViewModel.Repair.IsProcessing) activeVm = ViewModel.Repair;

                if (activeVm != null)
                {
                    // 有页面在处理 → 取消上一次的成功闪烁，立即按最新状态刷新
                    _lastProcessingVm = activeVm;
                    CancelSuccessFlash();

                    switch (activeVm.ProgressBarState)
                    {
                        case ProgressBarState.Processing:
                        case ProgressBarState.Pausing:
                            UpdateProcessingTaskbar(activeVm.Progress);
                            break;

                        case ProgressBarState.Paused:
                            _taskbarList!.SetProgressState(_windowHandle, TaskbarProgressFlags.Paused);
                            _taskbarList!.SetProgressValue(
                                _windowHandle,
                                (ulong)Math.Clamp(activeVm.Progress, 0, 100),
                                100);
                            break;

                        case ProgressBarState.Cancelled:
                            _taskbarList!.SetProgressState(_windowHandle, TaskbarProgressFlags.Error);
                            _taskbarList!.SetProgressValue(
                                _windowHandle,
                                (ulong)Math.Clamp(activeVm.Progress, 0, 100),
                                100);
                            break;

                        default:
                            // Idle / 未知状态 → 不显示
                            ClearTaskbarProgress();
                            break;
                    }
                    return;
                }

                // 2) 没有处理但有页面在扫描 → 跟随"最新开始"扫描页面的扫描进度条：
                //    枚举阶段（总数未知，IsScanIndeterminate）→ 不确定动画条；
                //    拿到总数后 → 确定型百分比（FooterProgressValue）。
                var scanVm = GetNewestScanningVm();
                if (scanVm != null)
                {
                    CancelSuccessFlash();
                    if (scanVm is WorkViewModelBase wvm && !wvm.IsScanIndeterminate)
                    {
                        double scanValue = wvm switch
                        {
                            MergeViewModel m => m.FooterProgressValue,
                            SplitViewModel s => s.FooterProgressValue,
                            RepairViewModel r => r.FooterProgressValue,
                            _ => 0
                        };
                        _taskbarList!.SetProgressState(_windowHandle, TaskbarProgressFlags.Normal);
                        _taskbarList.SetProgressValue(
                            _windowHandle,
                            (ulong)Math.Clamp(scanValue, 0, 100),
                            100);
                    }
                    else
                    {
                        // 枚举阶段或编辑页扫描（无确定进度）：不确定动画条
                        _taskbarList!.SetProgressState(_windowHandle, TaskbarProgressFlags.Indeterminate);
                    }
                    return;
                }

                // 3) 无处理无扫描：检查是否停留在"成功"或"取消"终态
                //    （这两个状态在 IsProcessing=false 后仍保留在子 VM 上）。
                var successVm = FindTerminalStateVm(ProgressBarState.Success);
                if (successVm != null)
                {
                    // 刚完成 → 短暂显示 100% 绿色，1.5 秒后清除
                    CancelSuccessFlash();
                    _taskbarList!.SetProgressState(_windowHandle, TaskbarProgressFlags.Normal);
                    _taskbarList.SetProgressValue(_windowHandle, 100, 100);
                    ScheduleSuccessClear();
                    return;
                }

                // 已取消 → 红色（保持到下一次操作开始或状态清除，与页面内进度条语义一致）。
                // 只针对"处理任务被取消"：扫描被取消时 ProgressBarState 同样是 Cancelled，
                // 用 _lastProcessingVm 区分。
                var cancelledVm = FindTerminalStateVm(ProgressBarState.Cancelled);
                if (cancelledVm != null && cancelledVm == _lastProcessingVm)
                {
                    CancelSuccessFlash();
                    _taskbarList!.SetProgressState(_windowHandle, TaskbarProgressFlags.Error);
                    _taskbarList.SetProgressValue(
                        _windowHandle,
                        (ulong)Math.Clamp(cancelledVm.Progress, 0, 100),
                        100);
                    return;
                }

                CancelSuccessFlash();
                ClearTaskbarProgress();
                // 处理刚结束时会先经过这里（IsProcessing=false 但 ProgressBarState 仍短暂是
                // Processing），紧接着才变为 Cancelled/Success，因此只有确认所有页面都已离开
                // "处理类"状态（空闲/扫描等）才清除标记，否则取消红色或成功闪烁会因标记丢失而不显示。
                if (!IsAnyProcessingLikeState())
                {
                    _lastProcessingVm = null;
                }
            }
            catch (Exception ex)
            {
                // COM 调用失败（如桌面切换、explorer 重启）时静默降级，下次成功自动恢复
                LogService.Warn($"Taskbar progress update failed: {ex.Message}", source: LogSource.UI);
            }
        }

        // 处理中的任务栏显示：进度仍为 0（首个任务未完成）时保持不确定动画条，
        // 保证全程可见、不出现空白期；一旦进度 > 0 立即切确定型百分比。
        private void UpdateProcessingTaskbar(double progress)
        {
            if (progress <= 0)
            {
                _taskbarList!.SetProgressState(_windowHandle, TaskbarProgressFlags.Indeterminate);
            }
            else
            {
                _taskbarList!.SetProgressState(_windowHandle, TaskbarProgressFlags.Normal);
                _taskbarList.SetProgressValue(
                    _windowHandle,
                    (ulong)Math.Clamp(progress, 0, 100),
                    100);
            }
        }

        // 维护各扫描源开始时间（合并/拆分/修复 + 编辑页左侧目录）：
        // 扫描中记录起始时间，扫描结束移除。
        private void UpdateScanTracking()
        {
            foreach (var vm in new WorkViewModelBase[] { ViewModel.Merge, ViewModel.Split, ViewModel.Repair })
            {
                if (vm.IsScanning)
                {
                    if (!_scanStartTimes.ContainsKey(vm))
                    {
                        _scanStartTimes[vm] = DateTime.UtcNow;
                    }
                }
                else
                {
                    _scanStartTimes.Remove(vm);
                }
            }

            var edit = ViewModel.Edit;
            if (edit.IsScanning)
            {
                if (!_scanStartTimes.ContainsKey(edit))
                {
                    _scanStartTimes[edit] = DateTime.UtcNow;
                }
            }
            else
            {
                _scanStartTimes.Remove(edit);
            }
        }

        // 返回正在扫描且开始时间最新的页面；无扫描时返回 null。
        private object? GetNewestScanningVm()
        {
            object? newest = null;
            DateTime newestStart = DateTime.MinValue;
            foreach (var kv in _scanStartTimes)
            {
                if (kv.Value > newestStart)
                {
                    newest = kv.Key;
                    newestStart = kv.Value;
                }
            }
            return newest;
        }

        // 是否有页面仍停留在"处理类"状态（Processing/Pausing/Paused）。
        // 用于区分"处理刚结束的过渡瞬间"与"确实已回到空闲/扫描"。
        private bool IsAnyProcessingLikeState()
        {
            return ViewModel.Merge.ProgressBarState is ProgressBarState.Processing
                    or ProgressBarState.Pausing
                    or ProgressBarState.Paused
                || ViewModel.Split.ProgressBarState is ProgressBarState.Processing
                    or ProgressBarState.Pausing
                    or ProgressBarState.Paused
                || ViewModel.Repair.ProgressBarState is ProgressBarState.Processing
                    or ProgressBarState.Pausing
                    or ProgressBarState.Paused;
        }

        // 在没有页面处理时，查找仍停留在指定终态（Success/Cancelled）的页面。
        private WorkViewModelBase? FindTerminalStateVm(ProgressBarState state)
        {
            if (ViewModel.Merge.ProgressBarState == state) return ViewModel.Merge;
            if (ViewModel.Split.ProgressBarState == state) return ViewModel.Split;
            if (ViewModel.Repair.ProgressBarState == state) return ViewModel.Repair;
            return null;
        }

        // 延迟到需要时创建 ITaskbarList3 COM 单例（Explorer 必须已就绪）。
        // 同时也延迟获取窗口句柄，确保 WindowNative.GetWindowHandle 有效。
        // 返回 true 表示成功就绪，false 表示暂不可用。
        private bool EnsureTaskbarList()
        {
            if (_taskbarList != null) return true;
            try
            {
                // 窗口句柄可能尚未获取（构造函数中设为 IntPtr.Zero），现在延迟获取
                if (_windowHandle == IntPtr.Zero)
                {
                    _windowHandle = WinRT.Interop.WindowNative.GetWindowHandle(this);
                    if (_windowHandle == IntPtr.Zero) return false; // 仍无效，稍后再试
                }

                var type = Type.GetTypeFromCLSID(new Guid("56fdf344-fd6d-11d0-958a-006097c9a090"));
                if (type == null) return false;
                _taskbarList = Activator.CreateInstance(type) as ITaskbarList3;
                if (_taskbarList == null) return false;
                _taskbarList.HrInit();
                return true;
            }
            catch (Exception ex)
            {
                // 任务栏进度是增强功能，初始化失败不阻塞功能；下次再尝试。
                LogService.Warn($"Taskbar progress init failed: {ex.Message}", source: LogSource.UI);
                _taskbarList = null;
                return false;
            }
        }

        // 隐藏任务栏进度条（TBPF_NOPROGRESS）。
        private void ClearTaskbarProgress()
        {
            try
            {
                _taskbarList?.SetProgressState(_windowHandle, TaskbarProgressFlags.NoProgress);
            }
            catch (Exception ex)
            {
                LogService.Warn($"Taskbar progress clear failed: {ex.Message}", source: LogSource.UI);
            }
        }

        // 取消待执行的成功闪烁清除（防止旧任务在切换页面后误清新任务的进度条）。
        private void CancelSuccessFlash()
        {
            _successFlashCts?.Cancel();
            _successFlashCts?.Dispose();
            _successFlashCts = null;
        }

        // 处理成功完成后延迟清除任务栏进度（短暂显示 100% 绿色）。
        private void ScheduleSuccessClear()
        {
            _successFlashCts = new CancellationTokenSource();
            var token = _successFlashCts.Token;
            DispatcherQueue.TryEnqueue(async () =>
            {
                try
                {
                    await Task.Delay(SuccessFlashDelayMs, token).ConfigureAwait(true);
                    if (!token.IsCancellationRequested)
                    {
                        ClearTaskbarProgress();
                    }
                }
                catch (TaskCanceledException)
                {
                    // 新任务开始或窗口关闭时取消，忽略
                }
            });
        }

        #endregion
    }
}
