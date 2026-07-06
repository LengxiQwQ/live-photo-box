/*
 * KeyPhotoPage.xaml.cs
 *
 * 实况照片主图更换页面的代码后置。
 * 处理 UI 事件 + 时间轴滚轮左右滚动 + 滚动条强制粗壮兜底。
 */

using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System.Collections.Generic;

namespace LivePhotoBox.Views
{
    public sealed partial class KeyPhotoPage : Page
    {
        public KeyPhotoViewModel ViewModel => AppViewModel.Instance.KeyPhoto;

        public KeyPhotoPage()
        {
            InitializeComponent();
            Loaded += KeyPhotoPage_Loaded;
        }

        private void KeyPhotoPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= KeyPhotoPage_Loaded;

            var items = new List<int>();
            for (int i = 0; i < ViewModel.TimelineThumbnailCount; i++)
                items.Add(i);
            TimelineThumbnailsControl.ItemsSource = items;

            // 兜底：延迟一帧再强制执行，确保模板已加载
            DispatcherQueue.TryEnqueue(() => ForceScrollBarsAlwaysThick());
        }

        /// <summary>
        /// 时间轴鼠标滚轮 → 水平滚动。
        /// </summary>
        private void TimelineScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
            TimelineScrollViewer.ScrollToHorizontalOffset(
                TimelineScrollViewer.HorizontalOffset - delta);
            e.Handled = true;
        }

        /// <summary>
        /// 遍历所有 ScrollBar 强制设为 TouchIndicator 模式（配合 XAML 隐式 Style 双重保障）。
        /// </summary>
        private void ForceScrollBarsAlwaysThick()
        {
            // ListView 内部 ScrollViewer 垂直滚动条设为始终可见
            var listViewSv = FindVisualChild<ScrollViewer>(FileItemListView);
            if (listViewSv != null)
            {
                listViewSv.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
                var vBar = listViewSv.FindName("VerticalScrollBar") as ScrollBar;
                if (vBar != null)
                    vBar.IndicatorMode = ScrollingIndicatorMode.TouchIndicator;
            }

            // 时间轴水平滚动条
            var hBar = TimelineScrollViewer.FindName("HorizontalScrollBar") as ScrollBar;
            if (hBar != null)
                hBar.IndicatorMode = ScrollingIndicatorMode.TouchIndicator;

            // 递归兜底：遍历页面内所有 ScrollBar
            SetAllScrollBarsIndicatorMode(this);
        }

        private static void SetAllScrollBarsIndicatorMode(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollBar bar)
                {
                    bar.IndicatorMode = ScrollingIndicatorMode.TouchIndicator;
                    bar.Loaded += (s, _) => ((ScrollBar)s).IndicatorMode = ScrollingIndicatorMode.TouchIndicator;
                }
                SetAllScrollBarsIndicatorMode(child);
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null)
                ViewModel.CurrentDirectory = folder.Path;
        }
    }
}
