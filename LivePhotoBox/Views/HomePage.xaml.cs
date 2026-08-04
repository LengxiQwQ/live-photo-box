/*
 * HomePage.xaml.cs
 *
 * 首页的代码后置。作为应用启动后的默认着陆页，提供：
 *   - 横幅图片展示
 *   - 功能教程卡片（Merge / Split / Repair）的悬浮预览（Hover）
 *   - 试玩演示功能（SetupAndNavigateDemo）
 *   - 浮动引导按钮滚动到核心功能区域
 *   - 导航参数支持（滚动到指定功能区域）
 *
 * 对应 ViewModel：HomeViewModel
 *
 * 生命周期：
 *   - 构造函数 → 注册加载事件
 *   - HomePage_Loaded → 设置横幅、隐藏教程"试一下"区域
 *   - OnNavigatedTo → 处理导航参数（滚动到指定特征）
 *   - 悬浮预览通过 PointerEntered/Exited/Moved 实现
 */

using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;

namespace LivePhotoBox.Views
{
    public sealed partial class HomePage : Page
    {
        // 关联的 HomeViewModel
        public HomeViewModel ViewModel => AppViewModel.Instance.Home;

        private bool _isHoverActive;
        private double _previewWidth;
        private double _previewHeight;
        private double _origImgW;
        private double _origImgH;
        private readonly PointerEventHandler _scrollViewerMovedHandler;
        private readonly Dictionary<string, (double Width, double Height)> _imageSizes = new();
        private RoutedEventHandler? _scrollToFeatureHandler;

        // 构造函数：初始化鼠标移动处理器和组件，注册加载事件
        public HomePage()
        {
            _scrollViewerMovedHandler = ScrollViewer_PointerMoved;
            InitializeComponent();
            this.Loaded += HomePage_Loaded;
        }

        // 页面加载完成后的初始化：设置横幅图片、文字阴影、隐藏教程"试一下"区域
        private void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            // 从 App 缓存中获取横幅图片，首次加载则从设置中读取
            if (App.CachedBannerImage == null)
            {
                App.CachedBannerImage = App.LoadBannerImageFromSettings();
            }

            if (this.FindName("BannerImage") is Image bannerImage)
            {
                bannerImage.Source = App.CachedBannerImage;
            }

            // 主标题文字阴影同步
            if (this.FindName("HeroTitleText") is TextBlock heroTitleText &&
                this.FindName("HeroTitleShadow") is TextBlock heroTitleShadow)
            {
                heroTitleShadow.Text = heroTitleText.Text;
            }

            // 隐藏教程底部的"试一下"提示和按钮
            if (this.FindName("MergeTutorialReadyDivider") is UIElement mergeDivider)
                mergeDivider.Visibility = Visibility.Collapsed;
            if (this.FindName("MergeTutorialReadySection") is UIElement mergeSection)
                mergeSection.Visibility = Visibility.Collapsed;
            if (this.FindName("SplitTutorialReadyDivider") is UIElement splitDivider)
                splitDivider.Visibility = Visibility.Collapsed;
            if (this.FindName("SplitTutorialReadySection") is UIElement splitSection)
                splitSection.Visibility = Visibility.Collapsed;
            if (this.FindName("RepairTutorialReadyDivider") is UIElement repairDivider)
                repairDivider.Visibility = Visibility.Collapsed;
            if (this.FindName("RepairTutorialReadySection") is UIElement repairSection)
                repairSection.Visibility = Visibility.Collapsed;
        }

        // 教程图片加载完成后，隐藏占位边框并记录原始尺寸
        private void TutorialImage_Opened(object sender, RoutedEventArgs e)
        {
            if (sender is Image image)
            {
                string placeholderName = image.Name + "Placeholder";
                if (this.FindName(placeholderName) is Border placeholder)
                {
                    placeholder.Visibility = Visibility.Collapsed;
                }

                if (image.Source is Microsoft.UI.Xaml.Media.Imaging.BitmapImage bitmap)
                {
                    _imageSizes[image.Name] = (bitmap.PixelWidth, bitmap.PixelHeight);
                }
            }
        }

        // 教程图片加载失败时，显示占位边框
        private void TutorialImage_Failed(object sender, Microsoft.UI.Xaml.ExceptionRoutedEventArgs e)
        {
            if (sender is Image image)
            {
                string imageName = image.Name + "Placeholder";
                if (this.FindName(imageName) is Border placeholder)
                {
                    placeholder.Visibility = Visibility.Visible;
                }
            }
        }

        // 鼠标进入教程图片区域时，显示悬浮大图预览
        // 全屏模式下不触发（窗口本来就很大，不需要额外预览）
        private void TutorialImageBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (_isHoverActive) return;
                // 窗口全屏或最大化时，不需要额外预览
                if (App.MainWindow?.AppWindow?.Presenter is FullScreenPresenter) return;
                if (App.MainWindow?.AppWindow?.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized }) return;
                if (sender is not Border border) return;

                Image? sourceImage = border.Child as Image;
                if (sourceImage == null || sourceImage.Source == null) return;

                double imgW, imgH;
                if (_imageSizes.TryGetValue(sourceImage.Name, out var size))
                {
                    imgW = size.Width;
                    imgH = size.Height;
                }
                else
                {
                    imgW = sourceImage.ActualWidth;
                    imgH = sourceImage.ActualHeight;
                }
                if (imgW <= 0 || imgH <= 0) return;

                var posInPage = e.GetCurrentPoint(this).Position;
                double winW = this.XamlRoot.Size.Width;
                double winH = this.XamlRoot.Size.Height;
                double maxW = winW * 0.55;
                double maxH = winH * 0.55;
                double scale = Math.Min(Math.Min(maxW / imgW, maxH / imgH), 1.0);

                HoverImage.Source = sourceImage.Source;
                HoverImage.Width = imgW * scale;
                HoverImage.Height = imgH * scale;
                _previewWidth = imgW * scale;
                _previewHeight = imgH * scale;
                _origImgW = imgW;
                _origImgH = imgH;

                HoverOverlay.Width = winW;
                HoverOverlay.Height = winH;
                Canvas.SetLeft(HoverImageBorder, 20);
                Canvas.SetTop(HoverImageBorder, 20);

                _isHoverActive = true;
                HoverOverlay.Visibility = Visibility.Visible;
                RootScrollViewer.AddHandler(PointerMovedEvent, _scrollViewerMovedHandler, true);
            }
            catch (Exception ex)
            {
                LogService.Warn($"Hover Enter error: {ex.Message}", source: LogSource.UI);
            }
        }

        // 鼠标离开教程图片区域时，隐藏悬浮预览
        private void TutorialImageBorder_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (!_isHoverActive) return;
                _isHoverActive = false;
                HoverOverlay.Visibility = Visibility.Collapsed;
                HoverImage.Source = null;
                RootScrollViewer.RemoveHandler(PointerMovedEvent, _scrollViewerMovedHandler);
            }
            catch (Exception ex)
            {
                LogService.Warn($"Hover Exit error: {ex.Message}", source: LogSource.UI);
            }
        }

        // 鼠标在页面内移动时，动态更新悬浮预览的位置（跟随光标，自动避让窗口边缘）
        private void ScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (!_isHoverActive || HoverImage.Source == null) return;
                // 若窗口在全屏或最大化状态下，关闭预览
                if (App.MainWindow?.AppWindow?.Presenter is FullScreenPresenter ||
                    App.MainWindow?.AppWindow?.Presenter is OverlappedPresenter { State: OverlappedPresenterState.Maximized })
                {
                    _isHoverActive = false;
                    HoverOverlay.Visibility = Visibility.Collapsed;
                    HoverImage.Source = null;
                    RootScrollViewer.RemoveHandler(PointerMovedEvent, _scrollViewerMovedHandler);
                    return;
                }

                var posInPage = e.GetCurrentPoint(this).Position;
                double winW = this.XamlRoot.Size.Width;
                double winH = this.XamlRoot.Size.Height;

                double maxW = winW * 0.55;
                double maxH = winH * 0.55;
                double scale = Math.Min(Math.Min(maxW / _origImgW, maxH / _origImgH), 1.0);
                _previewWidth = _origImgW * scale;
                _previewHeight = _origImgH * scale;
                HoverImage.Width = _previewWidth;
                HoverImage.Height = _previewHeight;

                double halfH = winH / 2;
                double margin = 20;
                double left, top;

                if (posInPage.X - _previewWidth - margin < 0)
                {
                    left = margin;
                    if (posInPage.Y <= halfH)
                    {
                        top = posInPage.Y + margin;
                    }
                    else
                    {
                        top = posInPage.Y - _previewHeight - margin;
                    }
                }
                else
                {
                    left = posInPage.X - _previewWidth - margin;
                    if (posInPage.Y <= halfH)
                    {
                        top = posInPage.Y + margin;
                    }
                    else
                    {
                        top = posInPage.Y - _previewHeight - margin;
                    }
                }

                if (left < margin) left = margin;
                if (left + _previewWidth > winW - margin) left = winW - _previewWidth - margin;
                if (top < margin) top = margin;
                if (top + _previewHeight > winH - margin) top = winH - _previewHeight - margin;

                Canvas.SetLeft(HoverImageBorder, left);
                Canvas.SetTop(HoverImageBorder, top);
            }
            catch (Exception ex)
            {
                LogService.Warn($"Hover Move error: {ex.Message}", source: LogSource.UI);
            }
        }

        // 准备演示数据并导航到目标功能页面。
        // 优先使用用户已下载的本地示例（Temp 目录），若无则提示前往设置页下载。
        // subFolder: 示例子目录名（Merge / Split / Repair）
        // pageTag: 页面对应的侧栏 Tag
        // pageType: 目标页面类型
        private async void SetupAndNavigateDemo(string subFolder, string pageTag, Type pageType)
        {
            try
            {
                string localizedSubFolder = pageTag switch
                {
                    "Merge" => ResourceService.GetString("HomePage_DemoSubFolder_Merge"),
                    "Split" => ResourceService.GetString("HomePage_DemoSubFolder_Split"),
                    "Repair" => ResourceService.GetString("HomePage_DemoSubFolder_Repair"),
                    _ => subFolder
                };

                string tempInputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LivePhotoBox_Demo", subFolder);

                // 检查本地是否已有示例文件
                if (!System.IO.Directory.Exists(tempInputPath) ||
                    System.IO.Directory.GetFiles(tempInputPath, "*.*", System.IO.SearchOption.AllDirectories).Length == 0)
                {
                    // 无本地示例 → 引导用户前往设置页下载
                    if (App.MainWindow?.Content?.XamlRoot != null)
                    {
                        await DialogService.ShowDualAsync(
                            App.MainWindow.Content.XamlRoot,
                            ResourceService.GetString("HomePage_NoSample_Title"),
                            ResourceService.GetString("HomePage_NoSample_Message"),
                            primaryText: ResourceService.GetString("HomePage_NoSample_GoToSettings"),
                            closeText: ResourceService.GetString("Msg_Cancel"));
                        // 导航到设置页的示例下载区域
                        if (App.MainWindow is MainWindow mw)
                            mw.NavigateToSettings("SampleContent");
                    }
                    return;
                }

                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string desktopOutputPath = System.IO.Path.Combine(desktopPath, ResourceService.GetString("HomePage_DemoOutputFolder"), localizedSubFolder);

                if (!System.IO.Directory.Exists(desktopOutputPath))
                {
                    System.IO.Directory.CreateDirectory(desktopOutputPath);
                }

                switch (pageTag)
                {
                    case "Merge":
                        AppViewModel.Instance.Merge.InputDirectory = tempInputPath;
                        AppViewModel.Instance.Merge.OutputDirectory = desktopOutputPath;
                        AppViewModel.Instance.Merge.IsDirectoryPanelOpen = true;
                        // OnInputDirectoryChanged 自动触发扫描（若值未变，显式调用兜底）。
                        // TryGuardScanClick 200ms 防抖阻止与自动触发重复执行。
                        if (AppViewModel.Instance.Merge.ScanDirectoryCommand.CanExecute(null))
                            AppViewModel.Instance.Merge.ScanDirectoryCommand.Execute(null);
                        break;
                    case "Split":
                        AppViewModel.Instance.Split.InputDirectory = tempInputPath;
                        AppViewModel.Instance.Split.OutputDirectory = desktopOutputPath;
                        AppViewModel.Instance.Split.IsDirectoryPanelOpen = true;
                        if (AppViewModel.Instance.Split.ScanDirectoryCommand.CanExecute(null))
                            AppViewModel.Instance.Split.ScanDirectoryCommand.Execute(null);
                        break;
                    case "Repair":
                        AppViewModel.Instance.Repair.InputDirectory = tempInputPath;
                        AppViewModel.Instance.Repair.OutputDirectory = desktopOutputPath;
                        AppViewModel.Instance.Repair.IsOutputToDirectory = true;
                        AppViewModel.Instance.Repair.IsDirectoryPanelOpen = true;
                        if (AppViewModel.Instance.Repair.ScanDirectoryCommand.CanExecute(null))
                            AppViewModel.Instance.Repair.ScanDirectoryCommand.Execute(null);
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
            catch (Exception ex)
            {
                LogService.Warn($"Navigation failed: {ex.Message}", source: LogSource.UI);
            }
        }

        // 递归复制目录及其子目录的所有文件
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

        // 跳转到设置页面的严格实况照片扫描设置项
        private void GoToSettingsStrictScan_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is MainWindow mainWindow)
            {
                mainWindow.NavigateToSettings("StrictLivePhotoScan");
            }
        }

        // 点击"试一下"合并演示，加载示例并导航到合并页
        private void TryMergeDemo_Click(object sender, RoutedEventArgs e)
        {
            LogService.Info("Demo: loading Merge sample & navigating to Merge page.", LogSource.UI);
            SetupAndNavigateDemo("Merge", "Merge", typeof(MergePage));
        }

        // 点击"试一下"拆分演示，加载示例并导航到拆分页
        private void TrySplitDemo_Click(object sender, RoutedEventArgs e)
        {
            LogService.Info("Demo: loading Split sample & navigating to Split page.", LogSource.UI);
            SetupAndNavigateDemo("Split", "Split", typeof(SplitPage));
        }

        // 点击"试一下"修复演示，加载示例并导航到修复页
        private void TryRepairDemo_Click(object sender, RoutedEventArgs e)
        {
            LogService.Info("Demo: loading Repair sample & navigating to Repair page.", LogSource.UI);
            SetupAndNavigateDemo("Repair", "Repair", typeof(RepairPage));
        }

        // 浮动引导按钮：平滑滚动到核心功能标题区域
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
            catch (Exception ex) { LogService.Debug($"FloatingGuideButton scroll failed: {ex.Message}", LogSource.UI); }
        }

        // 导航到本页时处理参数：若有 feature 参数，在页面加载完成后自动滚动到对应教程区域
        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            // 清理上一次的滚动处理器，防止缓存页切回时重复滚动
            if (_scrollToFeatureHandler != null)
            {
                Loaded -= _scrollToFeatureHandler;
                _scrollToFeatureHandler = null;
            }

            if (e.Parameter is string feature)
            {
                _scrollToFeatureHandler = (_, _) =>
                {
                    // 一次性执行，用完即弃
                    Loaded -= _scrollToFeatureHandler;
                    _scrollToFeatureHandler = null;
                    ScrollToFeature(feature);
                };
                Loaded += _scrollToFeatureHandler;
            }
        }

        // 根据功能名称滚动到页面上对应的教程区域
        private void ScrollToFeature(string feature)
        {
            try
            {
                string targetName = feature switch
                {
                    "Merge" => "CoreFeaturesTitle",
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
            catch (Exception ex) { LogService.Debug($"ScrollToFeature failed: {ex.Message}", LogSource.UI); }
        }
    }
}
