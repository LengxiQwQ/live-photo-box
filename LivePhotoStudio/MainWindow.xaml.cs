using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using LivePhotoStudio.ViewModels;
using Microsoft.UI.Windowing;
using Microsoft.UI;
using Windows.Graphics;

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
                appWindow.Resize(new SizeInt32(1318, 868));
            }

            NavView.Loaded += (s, e) =>
            {
                if (NavView.SettingsItem is NavigationViewItem settingsItem)
                {
                    settingsItem.Content = "设置";
                }
            };

            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateBackdrop();
            UpdateTheme();

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
                    rootGrid.Background = (Brush)Application.Current.Resources["ApplicationPageBackgroundThemeBrush"];
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