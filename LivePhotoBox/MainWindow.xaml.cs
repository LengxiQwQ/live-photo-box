using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
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
        private const int DefaultWindowWidth = 1200;
        private const int DefaultWindowHeight = 740;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        // 窗口整体透明度
        [DllImport("user32.dll")]
        private static extern int GetWindowLong(IntPtr hWnd, int nIndex);

        [DllImport("user32.dll")]
        private static extern int SetWindowLong(IntPtr hWnd, int nIndex, int dwNewLong);

        [DllImport("user32.dll")]
        private static extern bool SetLayeredWindowAttributes(IntPtr hwnd, uint crKey, byte bAlpha, uint dwFlags);

        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_LAYERED = 0x80000;
        private const uint LWA_ALPHA = 0x2;

        private DesktopAcrylicController? _acrylicController;
        private SystemBackdropConfiguration? _acrylicConfig;
        private ICompositionSupportsSystemBackdrop? _acrylicTarget;
        private CancellationTokenSource? _acrylicDebounceCts;

        public AppViewModel ViewModel => AppViewModel.Instance;

        public MainWindow()
        {
            InitializeComponent();
            LogService.Info("MainWindow constructed.", LogSource.UI);
            Closed += (_, _) =>
            {
                CleanupAcrylicController();
                LogService.Info("MainWindow closed.", LogSource.UI);
                LogService.MarkCleanShutdown();
                ViewModel.Cleanup();
            };
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow != null)
            {
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
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

            NavView.Loaded += (_, _) =>
            {
                if (NavView.SettingsItem is NavigationViewItem settingsItem)
                {
                    settingsItem.Content = ResourceService.GetString("Nav_Settings");
                }
            };

            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            ViewModel.Settings.PropertyChanged += OnSettingsPropertyChanged;
            ViewModel.RequestNavigateToPage += OnRequestNavigateToPage;

            UpdateTheme();
            UpdateBackdrop();
            UpdateStatusBarVisibility();

            // 应用持久化的窗口透明度
            if (ViewModel.Settings.WindowOpacity < 1.0)
            {
                EnableWindowLayering();
                ApplyWindowOpacity();
            }

            NavigateToPage(typeof(Views.HomePage), null);
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppViewModel.IsStatusBarVisible))
            {
                UpdateStatusBarVisibility();
            }
        }

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

        private void UpdateStatusBarVisibility()
        {
            PageStatusBar.Visibility = ViewModel.IsStatusBarVisible ? Visibility.Visible : Visibility.Collapsed;
        }

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

        private void CleanupAcrylicController()
        {
            _acrylicDebounceCts?.Cancel();
            if (_acrylicController == null) return;
            if (_acrylicTarget != null) _acrylicController.RemoveSystemBackdropTarget(_acrylicTarget);
            _acrylicController.Dispose();
            _acrylicController = null;
            _acrylicTarget = null;
            _acrylicConfig = null;
        }

        /// <summary>
        /// 滑块拖拽时频繁触发 → 防抖 250ms，松手后只重建一次 controller。
        /// DesktopAcrylicController.TintOpacity 在 AddSystemBackdropTarget 后设值无效，必须重建。
        /// </summary>
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

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                bool useClassic = AppSettingsService.GetValue("UseClassicSettingsPage", false);
                NavigateToPage(useClassic ? typeof(Views.SettingsPageOld) : typeof(Views.SettingsPage), null);
                return;
            }

            if (args.SelectedItem is not NavigationViewItem { Tag: string tag })
            {
                return;
            }

            switch (tag)
            {
                case "Home": NavigateToPage(typeof(Views.HomePage), null); break;
                case "Combo": NavigateToPage(typeof(Views.ComboPage), "Combo"); break;
                case "Split": NavigateToPage(typeof(Views.SplitPage), "Split"); break;
                case "KeyPhoto": NavigateToPage(typeof(Views.KeyPhotoPage), null); break;
                case "Repair": NavigateToPage(typeof(Views.RepairPage), "Repair"); break;
                case "About": NavigateToPage(typeof(Views.AboutPage), null); break;
            }
        }

        private void NavigateToPage(Type pageType, string? statusPageTag)
        {
            LogService.Info($"NavigateToPage: {pageType.Name}, StatusTag={statusPageTag ?? "(null)"}", LogSource.UI);
            ViewModel.SetCurrentStatusPage(statusPageTag);
            MainFrame.Navigate(pageType);
        }

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

        #region Window Opacity

        private bool _windowLayeringEnabled;

        /// <summary>启用窗口 WS_EX_LAYERED 样式（仅一次），后续通过 SetLayeredWindowAttributes 调整透明度</summary>
        private void EnableWindowLayering()
        {
            if (_windowLayeringEnabled) return;
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            int exStyle = GetWindowLong(hWnd, GWL_EXSTYLE);
            SetWindowLong(hWnd, GWL_EXSTYLE, exStyle | WS_EX_LAYERED);
            _windowLayeringEnabled = true;
        }

        /// <summary>将 ViewModel.WindowOpacity 应用到窗口</summary>
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
