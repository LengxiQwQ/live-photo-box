using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using System;
using System.ComponentModel;
using LivePhotoStudio.ViewModels;

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

            // 监听设置变化
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateBackdrop();
            UpdateTheme();

            // 导航到默认页面
            MainFrame.Navigate(typeof(Views.ComboPage));
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SharedViewModel.BackdropIndex)) UpdateBackdrop();
            if (e.PropertyName == nameof(SharedViewModel.ElementTheme)) UpdateTheme();
        }

        private void UpdateBackdrop()
        {
            this.SystemBackdrop = ViewModel.BackdropIndex switch
            {
                0 => new MicaBackdrop() { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base },
                1 => new MicaBackdrop() { Kind = Microsoft.UI.Composition.SystemBackdrops.MicaKind.Base },
                2 => new DesktopAcrylicBackdrop(),
                _ => null
            };
        }

        private void UpdateTheme()
        {
            if (Content is FrameworkElement rootElement)
            {
                rootElement.RequestedTheme = ViewModel.ElementTheme switch
                {
                    1 => ElementTheme.Light,
                    2 => ElementTheme.Dark,
                    _ => ElementTheme.Default
                };
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
                string tag = item.Tag as string;
                switch (tag)
                {
                    case "Combo":
                        MainFrame.Navigate(typeof(Views.ComboPage));
                        break;
                    case "Split":
                        MainFrame.Navigate(typeof(Views.SplitPage));
                        break;
                    case "Repair":
                        MainFrame.Navigate(typeof(Views.RepairPage));
                        break;
                }
            }
        }
    }
}