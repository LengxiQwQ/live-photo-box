using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.Runtime.InteropServices;
using System.ComponentModel;
using System.IO;
using Windows.Graphics;
using Windows.UI;

namespace LivePhotoBox
{
    public sealed partial class MainWindow : Window
    {
        // 这里是你设置的基准宽度和高度（对应 100% 缩放下的逻辑大小）
        private const int DefaultWindowWidth = 1120;
        private const int DefaultWindowHeight = 694;

        [DllImport("user32.dll")]
        private static extern uint GetDpiForWindow(IntPtr hwnd);

        public AppViewModel ViewModel => AppViewModel.Instance;

        public MainWindow()
        {
            InitializeComponent();
            CrashLogService.RecordBreadcrumb("MainWindow constructed.");
            Closed += (_, _) =>
            {
                CrashLogService.RecordBreadcrumb("MainWindow closed.");
                CrashLogService.MarkCleanShutdown();
            };
            ExtendsContentIntoTitleBar = true;
            SetTitleBar(AppTitleBar);

            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            Microsoft.UI.WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow != null)
            {
                // 加载任务栏和窗口图标
                string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "AppIcon.ico");
                if (File.Exists(iconPath))
                {
                    appWindow.SetIcon(iconPath);
                }

                // =================================================================
                // 窗口大小 DPI 自适应与屏幕自适应居中逻辑
                // =================================================================
                try
                {
                    // 获取当前窗口所在显示器的实际 DPI
                    uint dpi = GetDpiForWindow(hWnd);
                    float scaleFactor = dpi / 96f;

                    // 计算当前缩放率下，实际需要的物理像素大小
                    int scaledWidth = (int)(DefaultWindowWidth * scaleFactor);
                    int scaledHeight = (int)(DefaultWindowHeight * scaleFactor);

                    // 动态调整窗口到自适应的物理大小
                    appWindow.Resize(new SizeInt32(scaledWidth, scaledHeight));

                    // 获取当前显示器的可用工作区（避开任务栏），让窗口完美居中
                    var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                    if (displayArea != null)
                    {
                        var workArea = displayArea.WorkArea;

                        int x = workArea.X + (workArea.Width - scaledWidth) / 2;
                        int y = workArea.Y + (workArea.Height - scaledHeight) / 2;

                        // 确保窗口不会移出屏幕边界
                        x = Math.Max(workArea.X, x);
                        y = Math.Max(workArea.Y, y);

                        appWindow.Move(new PointInt32(x, y));
                    }
                }
                catch (Exception ex)
                {
                    // 防御性设计：如果缩放逻辑失败，回退到默认大小，防止程序崩溃
                    System.Diagnostics.Debug.WriteLine($"DPI Scaling initialization failed: {ex.Message}");
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

            UpdateTheme();
            UpdateBackdrop();
            UpdateStatusBarVisibility();
            NavigateToPage(typeof(Views.HomePage), null);
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(AppViewModel.BackdropIndex)) UpdateBackdrop();
            if (e.PropertyName == nameof(AppViewModel.ElementTheme)) UpdateTheme();
            if (e.PropertyName == nameof(AppViewModel.IsStatusBarVisible)) UpdateStatusBarVisibility();
        }

        private void UpdateStatusBarVisibility()
        {
            PageStatusBar.Visibility = ViewModel.IsStatusBarVisible ? Visibility.Visible : Visibility.Collapsed;
        }

        private void UpdateBackdrop()
        {
            SystemBackdrop = ViewModel.BackdropIndex switch
            {
                0 => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base },
                1 => new MicaBackdrop { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
                2 => new DesktopAcrylicBackdrop(),
                _ => null
            };

            if (Content is Grid rootGrid)
            {
                if (ViewModel.BackdropIndex == 3)
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

        private void UpdateTheme()
        {
            if (Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = (ElementTheme)ViewModel.ElementTheme;
            }

            UpdateTitleBarButtonColors();

            if (ViewModel.BackdropIndex == 3)
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
                NavigateToPage(typeof(Views.SettingsPage), null);
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
            CrashLogService.RecordBreadcrumb($"NavigateToPage: {pageType.Name}, StatusTag={statusPageTag ?? "(null)"}");
            ViewModel.SetCurrentStatusPage(statusPageTag);
            MainFrame.Navigate(pageType);
        }
    }
}