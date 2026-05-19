using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.IO;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml.Navigation;

namespace LivePhotoBox.Views
{
    public sealed partial class HomePage : Page
    {
        public HomePage()
        {
            this.InitializeComponent();
            this.Loaded += HomePage_Loaded;
        }

        private void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            if (App.CachedBannerImage == null)
            {
                App.CachedBannerImage = new BitmapImage(new Uri("ms-appx:///Assets/BannerImage.jpg"));
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

        /// <summary>
        /// 核心方法：配置输入输出路径，执行自动扫描并跳转页面
        /// </summary>
        private void SetupAndNavigateDemo(string subFolder, string pageTag, Type pageType)
        {
            try
            {
                // 1. 软件内部隐藏的源素材路径 (在安装包内)
                string internalSamplePath = Path.Combine(AppContext.BaseDirectory, "Assets", "Samples", subFolder);

                // 2. 混合策略路径配置
                // [输入路径] -> 放在系统的 Temp 临时文件夹，OS 会自动回收，不留垃圾
                string tempInputPath = Path.Combine(Path.GetTempPath(), "LivePhotoBox_Demo", subFolder);

                // [输出路径] -> 放在桌面显眼位置，让用户立刻看到效果，且方便手动删除
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string desktopOutputPath = Path.Combine(desktopPath, "LivePhotoBox_Output", subFolder);

                // 3. 将内部素材释放到 Temp 文件夹
                if (!Directory.Exists(tempInputPath))
                {
                    Directory.CreateDirectory(tempInputPath);
                }
                // 每次点击都静默覆盖一次，确保用户即便不小心删了里面某个文件也能恢复
                CopyDirectory(internalSamplePath, tempInputPath);

                // 4. 确保桌面的输出文件夹存在
                if (!Directory.Exists(desktopOutputPath))
                {
                    Directory.CreateDirectory(desktopOutputPath);
                }

                // 5. 将路径填入单例 ViewModel
                AppViewModel.Instance.InputDirectory = tempInputPath;
                AppViewModel.Instance.OutputDirectory = desktopOutputPath;

                // 6. 打开目录配置面板，让用户确认路径填好了
                AppViewModel.Instance.IsDirectoryPanelOpen = true;

                // 7. 自动执行扫描指令
                if (AppViewModel.Instance.ScanDirectoryCommand != null &&
                    AppViewModel.Instance.ScanDirectoryCommand.CanExecute(null))
                {
                    AppViewModel.Instance.ScanDirectoryCommand.Execute(null);
                }

                // 8. 安全切页（调用 MainWindow 的公开方法）
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
                // 生产环境防御性捕捉，避免 IO 异常等不可控因素导致界面直接闪退
            }
        }

        /// <summary>
        /// 辅助方法：复制文件夹及其内容 (强制覆盖)
        /// </summary>
        private void CopyDirectory(string sourceDir, string destinationDir)
        {
            if (!Directory.Exists(sourceDir)) return;

            var dir = new DirectoryInfo(sourceDir);
            DirectoryInfo[] dirs = dir.GetDirectories();

            Directory.CreateDirectory(destinationDir);

            // 复制所有文件
            foreach (FileInfo file in dir.GetFiles())
            {
                string targetFilePath = Path.Combine(destinationDir, file.Name);
                file.CopyTo(targetFilePath, true); // true 表示强行覆盖同名文件
            }

            // 递归复制子文件夹（如果有的话）
            foreach (DirectoryInfo subDir in dirs)
            {
                string newDestinationDir = Path.Combine(destinationDir, subDir.Name);
                CopyDirectory(subDir.FullName, newDestinationDir);
            }
        }

        private void TryMergeDemo_Click(object sender, RoutedEventArgs e)
        {
            // 匹配 Assets/Samples/Merge 文件夹
            SetupAndNavigateDemo("Merge", "Combo", typeof(ComboPage));
        }

        private void TrySplitDemo_Click(object sender, RoutedEventArgs e)
        {
            // 匹配 Assets/Samples/Split 文件夹
            SetupAndNavigateDemo("Split", "Split", typeof(SplitPage));
        }

        private void TryRepairDemo_Click(object sender, RoutedEventArgs e)
        {
            // 匹配 Assets/Samples/Repair 文件夹
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

        // 接收从 MainWindow 传过来的 feature 参数
        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            if (e.Parameter is string feature)
            {
                // 必须在 Loaded 事件中执行，确保页面布局渲染完毕，否则计算高度会出错
                this.Loaded += (s, args) => ScrollToFeature(feature);
            }
        }

        // 核心滚动定位方法
        private void ScrollToFeature(string feature)
        {
            try
            {
                // 根据传入的特征匹配刚才我们在 XAML 里起的卡片名字
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
                    sv.ChangeView(null, point.Y - 24, null, false); // 丝滑滚动到对应教程卡片
                }
            }
            catch { }
        }
    }
}