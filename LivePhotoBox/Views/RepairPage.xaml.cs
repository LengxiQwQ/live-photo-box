using LivePhotoBox.Helpers;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace LivePhotoBox.Views
{
    public sealed partial class RepairPage : Page
    {
        private readonly TaskListAutoScroller _scroller;
        private bool _eventsHooked;
        private bool _scrollViewerHooked;
        private KeyEventHandler? _pageKeyDownHandler;

        // ── 统一预览服务 ──
        private static readonly ImagePreviewService _previewService = new(maxCacheSize: 20, decodePixelWidth: 1920, preloadCount: 2);
        private List<string> _previewPaths = [];
        private int _previewCurrentIndex = -1;

        // 缩略图加载：只记录最后滚动时间，由独立定时器定期检查是否需要加载
        // ContainerContentChanging 本身不做任何重活，避免阻塞 UI 线程导致列表空白
        private long _lastContainerChangeTick;
        private DispatcherQueueTimer? _thumbnailCheckTimer;
        private const int ScrollSettleMs = 100;   // 100ms 无新条目 = 视为"可加载"
        private const int ThumbnailCheckIntervalMs = 200;

        public RepairViewModel ViewModel => AppViewModel.Instance.Repair;

        public RepairPage()
        {
            InitializeComponent();

            _scroller = new TaskListAutoScroller(
                "Repair",
                isActive: () => ViewModel.IsProcessing || ViewModel.IsScanning,
                getTaskCount: () => ViewModel.Tasks.Count,
                getTaskAt: idx => ViewModel.Tasks[idx]);

            Loaded += RepairPage_Loaded;
            Unloaded += RepairPage_Unloaded;
        }

        private void RepairPage_Loaded(object sender, RoutedEventArgs e)
        {
            _scroller.Attach(RepairTaskListView);

            // 灯箱键盘快捷键（← → Esc）
            _pageKeyDownHandler = new KeyEventHandler(OnPageKeyDown);
            AddHandler(UIElement.KeyDownEvent, _pageKeyDownHandler, true);

            if (!_scrollViewerHooked)
            {
                var sv = FindFirstDescendant<ScrollViewer>(RepairTaskListView);
                if (sv != null)
                {
                    sv.ViewChanged += OnScrollViewChanged;
                    _scrollViewerHooked = true;
                }
            }

            // 启动缩略图加载定时器：每 300ms 检查是否需要加载可见条目
            if (_thumbnailCheckTimer == null)
            {
                var disp = App.MainWindow?.DispatcherQueue;
                if (disp != null)
                {
                    _thumbnailCheckTimer = disp.CreateTimer();
                    _thumbnailCheckTimer.Interval = TimeSpan.FromMilliseconds(ThumbnailCheckIntervalMs);
                    _thumbnailCheckTimer.Tick += ThumbnailCheckTimer_Tick;
                    _thumbnailCheckTimer.Start();
                }
            }

            if (_eventsHooked) return;

            ViewModel.TaskStartedForScroll += OnTaskStarted;
            ViewModel.ProcessingCompletedForScroll += OnAllCompleted;
            ViewModel.ScanItemsFlushed += OnItemsFlushed;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _eventsHooked = true;
        }

        private void RepairPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _scroller.NotifyPageUnloading();
            _scroller.Detach();

            if (_pageKeyDownHandler != null)
            {
                RemoveHandler(UIElement.KeyDownEvent, _pageKeyDownHandler);
                _pageKeyDownHandler = null;
            }

            var sv = FindFirstDescendant<ScrollViewer>(RepairTaskListView);
            if (sv != null)
            {
                sv.ViewChanged -= OnScrollViewChanged;
                _scrollViewerHooked = false;
            }

            // 停止缩略图加载定时器
            if (_thumbnailCheckTimer != null)
            {
                _thumbnailCheckTimer.Stop();
                _thumbnailCheckTimer.Tick -= ThumbnailCheckTimer_Tick;
                _thumbnailCheckTimer = null;
            }

            if (!_eventsHooked) return;

            ViewModel.TaskStartedForScroll -= OnTaskStarted;
            ViewModel.ProcessingCompletedForScroll -= OnAllCompleted;
            ViewModel.ScanItemsFlushed -= OnItemsFlushed;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _eventsHooked = false;
        }

        /// <summary>在可视树中查找指定类型的第一个后代</summary>
        private static T? FindFirstDescendant<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = Microsoft.UI.Xaml.Media.VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var descendant = FindFirstDescendant<T>(child);
                if (descendant != null) return descendant;
            }
            return null;
        }

        /// <summary>滚动时更新"当前显示"分组标签</summary>
        private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e)
        {
            UpdateCurrentViewGroup();
        }

        /// <summary>根据列表视口内第一个可见任务确定当前浏览的分组名称</summary>
        private void UpdateCurrentViewGroup()
        {
            if (ViewModel.FilteredTasks.Count == 0)
            {
                ViewModel.CurrentViewGroup = string.Empty;
                return;
            }

            // 遍历所有已实现的容器，找到第一个在视口内的（Y >= 0）
            for (int i = 0; i < ViewModel.FilteredTasks.Count; i++)
            {
                var container = RepairTaskListView.ContainerFromIndex(i);
                if (container is not FrameworkElement element) continue;

                // 获取容器相对于 ListView 视口的 Y 位置
                var transform = element.TransformToVisual(RepairTaskListView);
                double y = transform.TransformPoint(new Windows.Foundation.Point(0, 0)).Y;

                // 容器底部在视口上方 → 继续找
                if (y + element.ActualHeight < 0) continue;

                // 找到了：这是视口内第一个（可能部分可见）的项
                var task = ViewModel.FilteredTasks[i];
                ViewModel.CurrentViewGroup = RepairViewModel.GetTaskGroupName(task);
                return;
            }

            // 所有容器都已滚出视口上方 → 取列表中最后一项的分组
            // （理论上不会走到这里，因为视口内总有内容）
        }

        private void OnTaskStarted(object? sender, RepairTask task) =>
            _scroller.NotifyTaskStarted(task.Index - 1);

        private void OnAllCompleted(object? sender, EventArgs e) =>
            _scroller.NotifyAllCompleted(wasCancelled: ViewModel.WasStoppedByUser);

        private void OnItemsFlushed(object? sender, EventArgs e)
        {
            _scroller.NotifyItemsFlushed();
            UpdateCurrentViewGroup();
        }

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.IsScanning))
            {
                if (ViewModel.IsScanning)
                    _scroller.NotifyScanStarting();
                else
                {
                    _scroller.NotifyScanFinished();
                    // 扫描完成：加载当前可见条目的视频缩略图
                    LoadVisibleVideoThumbnails();
                    UpdateCurrentViewGroup();
                }
            }
            else if (e.PropertyName == nameof(ViewModel.FilterMode))
            {
                UpdateCurrentViewGroup();
            }
            else if (e.PropertyName == nameof(ViewModel.IsProcessing) && ViewModel.IsProcessing)
            {
                _scroller.NotifyProcessingStarting();
            }
            else if (e.PropertyName == nameof(ViewModel.IsPaused) && !ViewModel.IsPaused)
            {
                _scroller.NotifyProcessingResumed();
            }
        }

        // ── 其他事件处理 ──────────────────────────────────

        private void DirectoryBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox) textBox.Text = string.Empty;
        }

        private async void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null)
            {
                ViewModel.InputDirectory = folder.Path;
                await Task.Delay(100);
                if (ViewModel.ScanDirectoryCommand.CanExecute(null))
                    ViewModel.ScanDirectoryCommand.Execute(null);
            }
        }

        private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null) ViewModel.OutputDirectory = folder.Path;
        }

        private void FileButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            try { FilePickerService.RevealInExplorer(path); }
            catch (Exception ex) { LogService.Debug($"RepairPage reveal in explorer failed: {ex.Message}", LogSource.UI); }
        }

        private async void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            _previewPaths = ViewModel.FilteredTasks.Select(t => t.File1Path).ToList();
            int idx = _previewPaths.IndexOf(path);
            if (idx < 0) return;
            OpenPreview(idx);
        }

        private async void OpenPreview(int index)
        {
            _previewCurrentIndex = index;
            LightboxImage.Source = await _previewService.LoadAsync(_previewPaths[index]);
            _previewService.PreloadNeighbors(_previewPaths, index);
            LightboxCounter.Text = $"{index + 1} / {_previewPaths.Count}";
            LightboxOverlay.Visibility = Visibility.Visible;
            LightboxCloseButton.Focus(FocusState.Programmatic);
        }

        private async void Navigate(int direction)
        {
            int newIdx = _previewCurrentIndex + direction;
            if (newIdx < 0 || newIdx >= _previewPaths.Count) return;
            _previewCurrentIndex = newIdx;
            LightboxImage.Source = await _previewService.LoadAsync(_previewPaths[newIdx]);
            _previewService.PreloadNeighbors(_previewPaths, newIdx);
            LightboxCounter.Text = $"{newIdx + 1} / {_previewPaths.Count}";
        }

        private void ClosePreview()
        {
            LightboxOverlay.Visibility = Visibility.Collapsed;
            LightboxImage.Source = null;
            _previewCurrentIndex = -1;
        }

        private void LightboxBackdrop_Tapped(object sender, TappedRoutedEventArgs e) => ClosePreview();
        private void LightboxCloseButton_Click(object sender, RoutedEventArgs e) => ClosePreview();

        private void LightboxOverlay_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
            Navigate(delta < 0 ? 1 : -1);
            e.Handled = true;
        }

        private void OnPageKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (LightboxOverlay.Visibility != Visibility.Visible) return;
            switch (e.Key)
            {
                case Windows.System.VirtualKey.Left:
                case Windows.System.VirtualKey.GamepadDPadLeft:
                    Navigate(-1); e.Handled = true; break;
                case Windows.System.VirtualKey.Right:
                case Windows.System.VirtualKey.GamepadDPadRight:
                    Navigate(1); e.Handled = true; break;
                case Windows.System.VirtualKey.Escape:
                    ClosePreview(); e.Handled = true; break;
            }
        }

        private void StatusTextBlock_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not RepairTask task) return;

            // 根据 Tag 判断是 File1 还是 File2 的状态被点击
            bool isFile2 = element.Tag is string tag && tag == "2";
            ProcessStatus status = isFile2 ? task.File2Status : task.File1Status;
            string details = isFile2 ? task.File2Details : task.File1Details;
            bool hasError = isFile2 ? task.File2HasErrorDetails : task.File1HasErrorDetails;

            if (status != ProcessStatus.Failed || string.IsNullOrWhiteSpace(details)) return;
            if (ErrorDetailTip.IsOpen && ErrorDetailTip.Target == element) { ErrorDetailTip.IsOpen = false; return; }
            ErrorDetailText.Text = details;
            ErrorDetailTip.Target = element;
            ErrorDetailTip.IsOpen = true;
        }

        private void IssueDescription_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not RepairTask task) return;

            // 根据 Tag 判断是 File1 还是 File2 的问题被点击
            bool isFile2 = element.Tag is string tag && tag == "2";
            bool isDiagnosisError = isFile2 ? task.File2IsDiagnosisError : task.File1IsDiagnosisError;
            string issueDesc = isFile2 ? task.File2IssueDescription : task.File1IssueDescription;

            if (!isDiagnosisError) return;
            if (ErrorDetailTip.IsOpen && ErrorDetailTip.Target == element) { ErrorDetailTip.IsOpen = false; return; }
            ErrorDetailText.Text = issueDesc;
            ErrorDetailTip.Target = element;
            ErrorDetailTip.IsOpen = true;
        }

        /// <summary>扫描完成后，加载当前可见条目的缩略图</summary>
        private void LoadVisibleVideoThumbnails()
        {
            LoadVisibleThumbnailsForCurrentViewport(maxPerBatch: 6, staggerMs: 50);
        }

        /// <summary>为当前视口内可见且未加载的条目启动缩略图加载（后台错开）</summary>
        private void LoadVisibleThumbnailsForCurrentViewport(int maxPerBatch = 4, int staggerMs = 0)
        {
            int count = ViewModel.FilteredTasks.Count;
            if (count == 0) return;

            var toLoad = new List<RepairFileEntry?>();
            for (int i = 0; i < count && toLoad.Count < maxPerBatch; i++)
            {
                if (RepairTaskListView.ContainerFromIndex(i) != null &&
                    ViewModel.FilteredTasks[i] is RepairTask task && task.Thumbnail == null)
                {
                    toLoad.Add(task.File1Entry);
                }
            }

            if (toLoad.Count == 0) return;

            _ = Task.Run(async () =>
            {
                foreach (var entry in toLoad)
                {
                    if (entry != null)
                    {
                        var _ = entry.EnsureThumbnailAsync();
                    }
                    if (staggerMs > 0)
                        await Task.Delay(staggerMs);
                }
            });
        }

        /// <summary>定时器回调：滚动停歇 250ms 后加载可见条目的缩略图</summary>
        private void ThumbnailCheckTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            long elapsed = Environment.TickCount64 - Volatile.Read(ref _lastContainerChangeTick);
            if (elapsed >= ScrollSettleMs)
            {
                LoadVisibleThumbnailsForCurrentViewport(maxPerBatch: 4, staggerMs: 50);
            }
        }

        /// <summary>ContainerContentChanging 极致轻量：只取消回收条目的加载 + 记录时间戳。不做任何其他工作，避免阻塞 UI 线程导致列表空白。</summary>
        private void RepairTaskListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue && args.Item is RepairTask oldTask)
            {
                ThumbnailService.CancelPendingVideoLoad(oldTask.File1Entry?.FilePath ?? "");
                return;
            }

            if (args.Item is RepairTask task)
            {
                // 固定每项高度 → 虚拟化引擎无需估算，滚动零抖动
                if (args.ItemContainer is ListViewItem container)
                {
                    container.Height = task.IsPaired ? 136 : 68;
                }
                Interlocked.Exchange(ref _lastContainerChangeTick, Environment.TickCount64);
            }
        }
        private void ErrorDetailTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args) => ErrorDetailTip.Target = null;

        private bool _filterDropDownWidthLocked;

        private void FilterDropDown_Loaded(object sender, RoutedEventArgs e)
        {
            if (_filterDropDownWidthLocked) return;
            if (sender is not DropDownButton btn) return;

            string[] types = [
                ResourceService.GetString("RepairPage_FilterAll"),
                ResourceService.GetString("RepairPage_FilterPairs"),
                ResourceService.GetString("RepairPage_FilterStandaloneImg"),
                ResourceService.GetString("RepairPage_FilterStandaloneVid")
            ];
            string[] statuses = [
                ResourceService.GetString("RepairPage_FilterStatusAll"),
                ResourceService.GetString("RepairPage_FilterStatusRepair"),
                ResourceService.GetString("RepairPage_FilterStatusPerfect")
            ];
            double fontSize = btn.FontSize > 0 ? btn.FontSize : 14.0;

            double maxWidth = 0;
            var tb = new TextBlock
            {
                FontSize = fontSize,
                FontFamily = btn.FontFamily,
                TextWrapping = TextWrapping.NoWrap
            };

            foreach (var type in types)
            {
                foreach (var status in statuses)
                {
                    tb.Text = $"{type}  •  {status}";
                    tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    maxWidth = Math.Max(maxWidth, tb.DesiredSize.Width);
                }
            }

            if (maxWidth > 0)
            {
                // 右侧留足呼吸空间，以最长组合文字为准再加 80px 边距
                const double chromeWidth = 58;
                btn.Width = maxWidth + chromeWidth;
                _filterDropDownWidthLocked = true;
            }
        }

        private static readonly string[] _filterTypeKeys = [
            "RepairPage_FilterAll",
            "RepairPage_FilterPairs",
            "RepairPage_FilterStandaloneImg",
            "RepairPage_FilterStandaloneVid"
        ];
        private static readonly string[] _filterStatusKeys = [
            "RepairPage_FilterStatusAll",
            "RepairPage_FilterStatusRepair",
            "RepairPage_FilterStatusPerfect"
        ];

        private static FontIcon CreateCheckedIcon() => new()
        {
            Glyph = "", // BulletedList 实心圆点
            FontSize = 6,
        };

        private void FilterFlyout_Opening(object sender, object args)
        {
            if (sender is not MenuFlyout flyout) return;

            // 分隔线左右留空
            FilterMenuSeparator.Margin = new Thickness(16, 0, 16, 0);

            // 自动测量所有下拉项文字宽度，取最长者
            string[] headerKeys = ["RepairPage_FilterHeaderType", "RepairPage_FilterHeaderStatus"];
            double maxTextWidth = 0;
            var tb = new TextBlock
            {
                FontSize = 14,
                TextWrapping = TextWrapping.NoWrap
            };

            foreach (var key in headerKeys.Concat(_filterTypeKeys).Concat(_filterStatusKeys))
            {
                tb.Text = ResourceService.GetString(key);
                tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                maxTextWidth = Math.Max(maxTextWidth, tb.DesiredSize.Width);
            }

            // 图标列 ~16px + 间距 + 右侧留白（足够长，不贴边）
            const double itemChrome = 76;
            double itemMinWidth = maxTextWidth + itemChrome;

            // 标题：文件类型（索引 0）
            if (flyout.Items[0] is MenuFlyoutItem typeHeader)
            {
                typeHeader.Text = ResourceService.GetString("RepairPage_FilterHeaderType");
                typeHeader.MinWidth = itemMinWidth;
            }

            // 类型筛选项（索引 1-4）
            for (int i = 0; i < 4; i++)
                SetupMenuItem(flyout.Items[1 + i], _filterTypeKeys[i], i == ViewModel.FilterMode, itemMinWidth);

            // 标题：修复状态（索引 6）
            if (flyout.Items[6] is MenuFlyoutItem statusHeader)
            {
                statusHeader.Text = ResourceService.GetString("RepairPage_FilterHeaderStatus");
                statusHeader.MinWidth = itemMinWidth;
            }

            // 状态筛选项（索引 7-9）
            for (int i = 0; i < 3; i++)
                SetupMenuItem(flyout.Items[7 + i], _filterStatusKeys[i], i == ViewModel.RepairStatusFilter, itemMinWidth);
        }

        private static void SetupMenuItem(MenuFlyoutItemBase item, string resourceKey, bool isSelected, double minWidth)
        {
            if (item is not MenuFlyoutItem menuItem) return;
            menuItem.Text = ResourceService.GetString(resourceKey);
            menuItem.MinWidth = minWidth;
            menuItem.Icon = isSelected ? CreateCheckedIcon() : null;
        }
    }
}
