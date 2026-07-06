/*
 * KeyPhotoPage.xaml.cs
 *
 * 实况照片主图更换页面的代码后置。
 * 处理 UI 事件 + 时间轴滚轮左右滚动 + 选中边框管理。
 */

using LivePhotoBox.Helpers;
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

        private object? _lastSelectedItem;
        private bool _isRestoringSelection;

        public KeyPhotoPage()
        {
            InitializeComponent();
            Loaded += KeyPhotoPage_Loaded;
            FileItemListView.ContainerContentChanging += OnContainerContentChanging;
        }

        private void KeyPhotoPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= KeyPhotoPage_Loaded;

            var items = new List<int>();
            for (int i = 0; i < ViewModel.TimelineThumbnailCount; i++)
                items.Add(i);
            TimelineThumbnailsControl.ItemsSource = items;

            LivePhotoBox.Helpers.ComboBoxHelper.AutoFitWidth(SortComboBox);

            DispatcherQueue.TryEnqueue(() => ForceScrollBarsAlwaysThick());
            DispatcherQueue.TryEnqueue(() => RefreshSelectedItemBorder());
        }

        private void TimelineScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
            TimelineScrollViewer.ScrollToHorizontalOffset(
                TimelineScrollViewer.HorizontalOffset - delta);
            e.Handled = true;
        }

        private void ForceScrollBarsAlwaysThick()
        {
            var listViewSv = FindVisualChild<ScrollViewer>(FileItemListView);
            if (listViewSv != null)
            {
                listViewSv.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;
                var vBar = listViewSv.FindName("VerticalScrollBar") as ScrollBar;
                if (vBar != null)
                    vBar.IndicatorMode = ScrollingIndicatorMode.TouchIndicator;
            }

            var hBar = TimelineScrollViewer.FindName("HorizontalScrollBar") as ScrollBar;
            if (hBar != null)
                hBar.IndicatorMode = ScrollingIndicatorMode.TouchIndicator;

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

        // ──────────────── 选中项边框管理 ────────────────

        private void FileItemListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isRestoringSelection) return;

            if (FileItemListView.SelectedItem == null && _lastSelectedItem != null)
            {
                _isRestoringSelection = true;
                FileItemListView.SelectedItem = _lastSelectedItem;
                _isRestoringSelection = false;
                return;
            }

            _lastSelectedItem = FileItemListView.SelectedItem;

            // 清除旧选中项边框
            foreach (var item in e.RemovedItems)
            {
                if (FileItemListView.ContainerFromItem(item) is ListViewItem c)
                    ClearSelectedBorder(c);
            }

            // 新选中项设置边框
            foreach (var item in e.AddedItems)
            {
                if (FileItemListView.ContainerFromItem(item) is ListViewItem c)
                    ApplySelectedBorder(c);
            }
        }

        /// <summary>
        /// 虚拟化回收/重建容器时同步边框状态。
        /// </summary>
        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                ClearSelectedBorder(args.ItemContainer as ListViewItem);
                return;
            }

            // 容器展示新内容时，如果是当前选中项则恢复边框
            if (args.Item == FileItemListView.SelectedItem && args.ItemContainer is ListViewItem container)
            {
                container.Loaded += OnSelectedContainerReLoaded;
            }
        }

        private void OnSelectedContainerReLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is ListViewItem container)
            {
                container.Loaded -= OnSelectedContainerReLoaded;
                ApplySelectedBorder(container);
            }
        }

        private void RefreshSelectedItemBorder()
        {
            if (FileItemListView.SelectedItem != null &&
                FileItemListView.ContainerFromItem(FileItemListView.SelectedItem) is ListViewItem container)
            {
                ApplySelectedBorder(container);
            }
        }

        private static void ApplySelectedBorder(ListViewItem container)
        {
            if (container == null) return;
            var accentColor = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
            container.BorderBrush = new SolidColorBrush(accentColor) { Opacity = 0.88 };
            container.BorderThickness = new Thickness(2);
        }

        private static void ClearSelectedBorder(ListViewItem? container)
        {
            if (container == null) return;
            container.BorderBrush = null;
            container.BorderThickness = new Thickness(0);
        }
    }
}
