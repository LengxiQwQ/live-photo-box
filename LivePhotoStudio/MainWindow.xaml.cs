using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using LivePhotoStudio.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using Windows.Graphics;
using Windows.UI;

namespace LivePhotoStudio
{
    public sealed partial class MainWindow : Window
    {
        public SharedViewModel ViewModel => SharedViewModel.Instance;

        public MainWindow()
        {
            this.InitializeComponent();
            this.ExtendsContentIntoTitleBar = true;
            this.SetTitleBar(AppTitleBar);

            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

            if (appWindow != null)
            {
                int windowWidth = 1414;
                int windowHeight = 928;
                appWindow.Resize(new SizeInt32(windowWidth, windowHeight));

                // Center the window on the current display
                try
                {
                    var displayArea = DisplayArea.GetFromWindowId(windowId, DisplayAreaFallback.Primary);
                    var workArea = displayArea.WorkArea;

                    int x = workArea.X + (workArea.Width - windowWidth) / 2;
                    int y = workArea.Y + (workArea.Height - windowHeight) / 2;

                    appWindow.Move(new PointInt32(x, y));
                }
                catch
                {
                    // If centering fails, just use default position
                }
            }

            NavView.Loaded += (s, e) =>
            {
                if (NavView.SettingsItem is NavigationViewItem settingsItem)
                {
                    settingsItem.Content = "设置";
                }
            };

            ViewModel.PropertyChanged += OnViewModelPropertyChanged;

            // Apply theme first, then backdrop so pure color mode uses correct background
            UpdateTheme();
            UpdateBackdrop();

            // 这里的导航改为了 HomePage
            MainFrame.Navigate(typeof(Views.HomePage));
        }

        private void OnViewModelPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SharedViewModel.BackdropIndex)) UpdateBackdrop();
            if (e.PropertyName == nameof(SharedViewModel.ElementTheme)) UpdateTheme();
        }

        private void UpdateBackdrop()
        {
            this.SystemBackdrop = ViewModel.BackdropIndex switch
            {
                0 => new MicaBackdrop() { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base },
                1 => new MicaBackdrop() { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.BaseAlt },
                2 => new DesktopAcrylicBackdrop(),
                _ => null
            };

            if (this.Content is Grid rootGrid)
            {
                if (ViewModel.BackdropIndex == 3)
                {
                    // Pure color mode: determine color based on current element theme
                    ElementTheme currentTheme = ElementTheme.Default;
                    if (rootGrid is FrameworkElement fe)
                    {
                        currentTheme = fe.RequestedTheme;
                    }

                    // If theme is Default, check the actual system theme
                    if (currentTheme == ElementTheme.Default)
                    {
                        var settings = new Windows.UI.ViewManagement.UISettings();
                        var frameworkTheme = settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                        // If background is black (0,0,0), system is dark; otherwise light
                        currentTheme = (frameworkTheme.R < 128) ? ElementTheme.Dark : ElementTheme.Light;
                    }

                    // Set appropriate background color based on theme
                    if (currentTheme == ElementTheme.Dark)
                    {
                        rootGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.Black);
                    }
                    else
                    {
                        rootGrid.Background = new SolidColorBrush(Microsoft.UI.Colors.White);
                    }
                }
                else
                {
                    rootGrid.Background = new SolidColorBrush(Colors.Transparent);
                }
            }
        }

        private void UpdateTheme()
        {
            if (this.Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = (ElementTheme)ViewModel.ElementTheme;
            }

            // [新增] 每次切换主题时，手动同步标题栏右侧三大金刚键的颜色
            UpdateTitleBarButtonColors();

            // Also update backdrop when theme changes to ensure pure color mode uses correct background
            if (ViewModel.BackdropIndex == 3)
            {
                UpdateBackdrop();
            }
        }

        // [新增] 核心修复方法：根据当前深浅色模式重绘系统标题栏控制按钮的颜色
        private void UpdateTitleBarButtonColors()
        {
            IntPtr hWnd = WinRT.Interop.WindowNative.GetWindowHandle(this);
            WindowId windowId = Win32Interop.GetWindowIdFromWindow(hWnd);
            AppWindow appWindow = AppWindow.GetFromWindowId(windowId);

            // 检查系统 API 是否支持自定义标题栏颜色
            if (AppWindowTitleBar.IsCustomizationSupported() && appWindow.TitleBar != null)
            {
                ElementTheme currentTheme = (ElementTheme)ViewModel.ElementTheme;

                // 若为跟随系统，需主动读取系统当前的色彩设置
                if (currentTheme == ElementTheme.Default)
                {
                    var settings = new Windows.UI.ViewManagement.UISettings();
                    var frameworkTheme = settings.GetColorValue(Windows.UI.ViewManagement.UIColorType.Background);
                    currentTheme = (frameworkTheme.R < 128) ? ElementTheme.Dark : ElementTheme.Light;
                }

                if (currentTheme == ElementTheme.Dark)
                {
                    // 深色模式下：图标设为纯白，悬停/按下时的底色调整为半透明白色
                    appWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.White;
                    appWindow.TitleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.White;
                    appWindow.TitleBar.ButtonHoverBackgroundColor = Color.FromArgb(25, 255, 255, 255);
                    appWindow.TitleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.White;
                    appWindow.TitleBar.ButtonPressedBackgroundColor = Color.FromArgb(51, 255, 255, 255);
                    appWindow.TitleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.DarkGray;
                }
                else
                {
                    // 浅色模式下：图标设为纯黑，悬停/按下时的底色调整为半透明黑色
                    appWindow.TitleBar.ButtonForegroundColor = Microsoft.UI.Colors.Black;
                    appWindow.TitleBar.ButtonHoverForegroundColor = Microsoft.UI.Colors.Black;
                    appWindow.TitleBar.ButtonHoverBackgroundColor = Color.FromArgb(25, 0, 0, 0);
                    appWindow.TitleBar.ButtonPressedForegroundColor = Microsoft.UI.Colors.Black;
                    appWindow.TitleBar.ButtonPressedBackgroundColor = Color.FromArgb(51, 0, 0, 0);
                    appWindow.TitleBar.ButtonInactiveForegroundColor = Microsoft.UI.Colors.Gray;
                }
            }
        }

        private void NavView_SelectionChanged(NavigationView sender, NavigationViewSelectionChangedEventArgs args)
        {
            if (args.IsSettingsSelected)
            {
                MainFrame.Navigate(typeof(Views.SettingsPage));
            }
            else if (args.SelectedItem is NavigationViewItem item)
            {
                if (item.Tag is string tag)
                {
                    switch (tag)
                    {
                        case "Home": MainFrame.Navigate(typeof(Views.HomePage)); break;
                        case "Combo": MainFrame.Navigate(typeof(Views.ComboPage)); break;
                        case "Split": MainFrame.Navigate(typeof(Views.SplitPage)); break;
                        case "Repair": MainFrame.Navigate(typeof(Views.RepairPage)); break;
                        case "Console": MainFrame.Navigate(typeof(Views.ConsolePage)); break;
                        case "About": MainFrame.Navigate(typeof(Views.AboutPage)); break;
                    }
                }
            }
        }
    }
}