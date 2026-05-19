using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Navigation;
using System.IO;

namespace LivePhotoBox.Views
{
    public sealed partial class HomePage : Page
    {
        public HomePage()
        {
            this.InitializeComponent();
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
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

        private void FloatingGuideButton_Click(object sender, RoutedEventArgs e)
        {
        }

        // ==========================================
        // 🎯 一键试用体验功能
        // ==========================================
        private void TryMerge_Click(object sender, RoutedEventArgs e)
        {
            string samplePath = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Assets", "Samples", "Merge");
            if (Directory.Exists(samplePath))
            {
                AppViewModel.Instance.InputDirectory = samplePath;
                if (AppViewModel.Instance.ScanDirectoryCommand.CanExecute(null))
                {
                    AppViewModel.Instance.ScanDirectoryCommand.Execute(null);
                }
            }
            NavigateAndSyncSidebar(typeof(ComboPage), "Combo");
        }

        private void TrySplit_Click(object sender, RoutedEventArgs e)
        {
            string samplePath = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Assets", "Samples", "Split");
            if (Directory.Exists(samplePath))
            {
                AppViewModel.Instance.SplitInputDirectory = samplePath;
                if (AppViewModel.Instance.ScanSplitDirectoryCommand.CanExecute(null))
                {
                    AppViewModel.Instance.ScanSplitDirectoryCommand.Execute(null);
                }
            }
            NavigateAndSyncSidebar(typeof(SplitPage), "Split");
        }

        private void TryRepair_Click(object sender, RoutedEventArgs e)
        {
            string samplePath = Path.Combine(System.AppDomain.CurrentDomain.BaseDirectory, "Assets", "Samples", "Repair");
            if (Directory.Exists(samplePath))
            {
                AppViewModel.Instance.RepairInputDirectory = samplePath;
                if (AppViewModel.Instance.ScanRepairDirectoryCommand.CanExecute(null))
                {
                    AppViewModel.Instance.ScanRepairDirectoryCommand.Execute(null);
                }
            }
            NavigateAndSyncSidebar(typeof(RepairPage), "Repair");
        }

        // 高级跳转方法：不但切页面，还自动把侧边栏(NavView)的选中项拨过去，告别UI脱节！
        private void NavigateAndSyncSidebar(System.Type pageType, string featureTag)
        {
            this.Frame?.Navigate(pageType);

            if (App.MainWindow?.Content is FrameworkElement root)
            {
                var navView = FindChild<NavigationView>(root);
                if (navView != null)
                {
                    foreach (var item in navView.MenuItems)
                    {
                        if (item is NavigationViewItem navItem)
                        {
                            string itemTag = navItem.Tag?.ToString() ?? "";
                            if (itemTag.Contains(featureTag, System.StringComparison.OrdinalIgnoreCase))
                            {
                                navView.SelectedItem = navItem;
                                break;
                            }
                        }
                    }
                }
            }
        }

        // 递归查找控件工具方法
        private T? FindChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T typedChild) return typedChild;
                var result = FindChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }
    }
}
