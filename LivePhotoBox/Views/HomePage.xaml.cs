using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;

namespace LivePhotoBox.Views
{
    public sealed partial class HomePage : Page
    {
        public HomeViewModel ViewModel => AppViewModel.Instance.Home;

        public HomePage()
        {
            InitializeComponent();
            this.Loaded += HomePage_Loaded;
        }

        private void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            if (App.CachedBannerImage == null)
            {
                App.CachedBannerImage = new Microsoft.UI.Xaml.Media.Imaging.BitmapImage(new Uri("ms-appx:///Assets/BannerImage.jpg"));
            }

            if (this.FindName("BannerImage") is Image bannerImage)
            {
                bannerImage.Source = App.CachedBannerImage;
            }

            if (this.FindName("HeroTitleText") is TextBlock heroTitleText &&
                this.FindName("HeroTitleShadow") is TextBlock heroTitleShadow)
            {
                heroTitleShadow.Text = heroTitleText.Text;
            }
        }

        private void SetupAndNavigateDemo(string subFolder, string pageTag, Type pageType)
        {
            try
            {
                string internalSamplePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Samples", subFolder);
                string tempInputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LivePhotoBox_Demo", subFolder);
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string desktopOutputPath = System.IO.Path.Combine(desktopPath, "LivePhotoBox_Output", subFolder);

                if (!System.IO.Directory.Exists(tempInputPath))
                {
                    System.IO.Directory.CreateDirectory(tempInputPath);
                }
                CopyDirectory(internalSamplePath, tempInputPath);

                if (!System.IO.Directory.Exists(desktopOutputPath))
                {
                    System.IO.Directory.CreateDirectory(desktopOutputPath);
                }

                // 根据目标页面设置对应的 ViewModel
                switch (pageTag)
                {
                    case "Combo":
                        AppViewModel.Instance.Combo.InputDirectory = tempInputPath;
                        AppViewModel.Instance.Combo.OutputDirectory = desktopOutputPath;
                        AppViewModel.Instance.Combo.IsDirectoryPanelOpen = true;
                        if (AppViewModel.Instance.Combo.ScanDirectoryCommand.CanExecute(null))
                        {
                            AppViewModel.Instance.Combo.ScanDirectoryCommand.Execute(null);
                        }
                        break;
                    case "Split":
                        AppViewModel.Instance.Split.InputDirectory = tempInputPath;
                        AppViewModel.Instance.Split.OutputDirectory = desktopOutputPath;
                        AppViewModel.Instance.Split.IsDirectoryPanelOpen = true;
                        if (AppViewModel.Instance.Split.ScanDirectoryCommand.CanExecute(null))
                        {
                            AppViewModel.Instance.Split.ScanDirectoryCommand.Execute(null);
                        }
                        break;
                    case "Repair":
                        AppViewModel.Instance.Repair.InputDirectory = tempInputPath;
                        AppViewModel.Instance.Repair.IsDirectoryPanelOpen = true;
                        if (AppViewModel.Instance.Repair.ScanDirectoryCommand.CanExecute(null))
                        {
                            AppViewModel.Instance.Repair.ScanDirectoryCommand.Execute(null);
                        }
                        break;
                }

                if (App.MainWindow is MainWindow mainWindow)
                {
                    mainWindow.SwitchToPageByTag(pageTag);
                }
                else
                {
                    this.Frame?.Navigate(pageType);
                }
            }
            catch (Exception)
            {
            }
        }

        private void CopyDirectory(string sourceDir, string destinationDir)
        {
            if (!System.IO.Directory.Exists(sourceDir)) return;

            var dir = new System.IO.DirectoryInfo(sourceDir);
            System.IO.DirectoryInfo[] dirs = dir.GetDirectories();

            System.IO.Directory.CreateDirectory(destinationDir);

            foreach (var file in dir.GetFiles())
            {
                string targetFilePath = System.IO.Path.Combine(destinationDir, file.Name);
                file.CopyTo(targetFilePath, true);
            }

            foreach (var subDir in dirs)
            {
                string newDestinationDir = System.IO.Path.Combine(destinationDir, subDir.Name);
                CopyDirectory(subDir.FullName, newDestinationDir);
            }
        }

        private void TryMergeDemo_Click(object sender, RoutedEventArgs e)
        {
            SetupAndNavigateDemo("Merge", "Combo", typeof(ComboPage));
        }

        private void TrySplitDemo_Click(object sender, RoutedEventArgs e)
        {
            SetupAndNavigateDemo("Split", "Split", typeof(SplitPage));
        }

        private void TryRepairDemo_Click(object sender, RoutedEventArgs e)
        {
            SetupAndNavigateDemo("Repair", "Repair", typeof(RepairPage));
        }

        private void FloatingGuideButton_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                if (this.FindName("CoreFeaturesTitle") is TextBlock target && this.FindName("RootScrollViewer") is ScrollViewer sv)
                {
                    var transform = target.TransformToVisual(sv.Content as UIElement ?? sv);
                    var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                    sv.ChangeView(null, point.Y - 24, null, false);
                }
            }
            catch { }
        }

        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is string feature)
            {
                this.Loaded += (s, args) => ScrollToFeature(feature);
            }
        }

        private void ScrollToFeature(string feature)
        {
            try
            {
                string targetName = feature switch
                {
                    "Combo" => "MergeTutorialBorder",
                    "Split" => "SplitTutorialBorder",
                    "Repair" => "RepairTutorialBorder",
                    _ => "CoreFeaturesTitle"
                };

                if (this.FindName(targetName) is UIElement target && this.FindName("RootScrollViewer") is ScrollViewer sv)
                {
                    var transform = target.TransformToVisual(sv.Content as UIElement ?? sv);
                    var point = transform.TransformPoint(new Windows.Foundation.Point(0, 0));
                    sv.ChangeView(null, point.Y - 24, null, false);
                }
            }
            catch { }
        }
    }
}
