using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI;
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
        public HomeViewModel ViewModel => AppViewModel.Instance.Home;

        private bool _isHoverActive;
        private double _previewWidth;
        private double _previewHeight;
        private double _origImgW;
        private double _origImgH;
        private readonly PointerEventHandler _scrollViewerMovedHandler;
        private readonly Dictionary<string, (double Width, double Height)> _imageSizes = new();

        public HomePage()
        {
            _scrollViewerMovedHandler = ScrollViewer_PointerMoved;
            InitializeComponent();
            this.Loaded += HomePage_Loaded;
        }

        private void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            if (App.CachedBannerImage == null)
            {
                App.CachedBannerImage = App.LoadBannerImageFromSettings();
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

            // 隐藏教程底部的"试一下"提示和按钮
            if (this.FindName("MergeTutorialReadyDivider") is UIElement mergeDivider)
                mergeDivider.Visibility = Visibility.Collapsed;
            if (this.FindName("MergeTutorialReadySection") is UIElement mergeSection)
                mergeSection.Visibility = Visibility.Collapsed;
            if (this.FindName("SplitTutorialReadyDivider") is UIElement splitDivider)
                splitDivider.Visibility = Visibility.Collapsed;
            if (this.FindName("SplitTutorialReadySection") is UIElement splitSection)
                splitSection.Visibility = Visibility.Collapsed;
        }

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

        private void TutorialImageBorder_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (_isHoverActive) return;
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

        private void ScrollViewer_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            try
            {
                if (!_isHoverActive || HoverImage.Source == null) return;

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

        private void SetupAndNavigateDemo(string subFolder, string pageTag, Type pageType)
        {
            try
            {
                // subFolder 是内部资源目录名（英文，必须与 Assets/Samples/ 下的物理文件夹名一致）
                // localizedSubFolder 是用户可见的输出子文件夹名（跟随界面语言）
                string localizedSubFolder = pageTag switch
                {
                    "Combo" => ResourceService.GetString("HomePage_DemoSubFolder_Merge"),
                    "Split" => ResourceService.GetString("HomePage_DemoSubFolder_Split"),
                    "Repair" => ResourceService.GetString("HomePage_DemoSubFolder_Repair"),
                    _ => subFolder
                };

                string internalSamplePath = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "Samples", subFolder);
                string tempInputPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LivePhotoBox_Demo", subFolder);
                string desktopPath = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
                string desktopOutputPath = System.IO.Path.Combine(desktopPath, ResourceService.GetString("HomePage_DemoOutputFolder"), localizedSubFolder);

                if (!System.IO.Directory.Exists(tempInputPath))
                {
                    System.IO.Directory.CreateDirectory(tempInputPath);
                }
                CopyDirectory(internalSamplePath, tempInputPath);

                if (!System.IO.Directory.Exists(desktopOutputPath))
                {
                    System.IO.Directory.CreateDirectory(desktopOutputPath);
                }

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
            catch (Exception ex)
            {
                LogService.Warn($"Navigation failed: {ex.Message}", source: LogSource.UI);
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
            LogService.Info("Demo: loading Merge sample & navigating to Combo page.", LogSource.UI);
            SetupAndNavigateDemo("Merge", "Combo", typeof(ComboPage));
        }

        private void TrySplitDemo_Click(object sender, RoutedEventArgs e)
        {
            LogService.Info("Demo: loading Split sample & navigating to Split page.", LogSource.UI);
            SetupAndNavigateDemo("Split", "Split", typeof(SplitPage));
        }

        private void TryRepairDemo_Click(object sender, RoutedEventArgs e)
        {
            LogService.Info("Demo: loading Repair sample & navigating to Repair page.", LogSource.UI);
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
            catch (Exception ex) { LogService.Debug($"FloatingGuideButton scroll failed: {ex.Message}", LogSource.UI); }
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
                    "Combo" => "CoreFeaturesTitle",
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
