/*
 * RepairPage.xaml.cs
 *
 * 实况照片修复页面的代码后置。
 * 提供对损坏或不完整的实况照片进行修复的功能。
 * 包含任务列表自动滚动、缩略图延迟加载、文件夹浏览、全屏预览、错误详情提示和筛选菜单。
 *
 * 对应 ViewModel：RepairViewModel
 *
 * 生命周期：
 *   - 构造函数 → 创建 TaskListAutoScroller，注册 Loaded/Unloaded
 *   - Loaded → 附加自动滚动器，查找 ScrollViewer 并注册 ViewChanged，启动缩略图检查定时器，绑定 ViewModel 事件
 *   - Unloaded → 分离自动滚动器，停止定时器，解绑事件
 *   - 缩略图通过 DispatcherQueueTimer 定期检查视口中可见列表项的状态并加载
 */

using LivePhotoBox.Helpers;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Storage;

namespace LivePhotoBox.Views
{
    public sealed partial class RepairPage : Page
    {
        // 任务列表自动滚动器，在处理/扫描过程中保持当前任务可见
        private readonly TaskListAutoScroller _scroller;

        // 回到顶部悬浮按钮辅助类
        private ScrollToTopButtonHelper? _scrollToTopHelper;

        // 是否已绑定 ViewModel 事件
        private bool _eventsHooked;

        // ── 拖拽文件夹状态 ──
        /// <summary>拖入的 StorageItems 是否全是文件夹</summary>
        private bool _isDropAllFolders;

        // 是否已挂载 ScrollViewer.ViewChanged 事件
        private bool _scrollViewerHooked;

        // 缩略图加载：只记录最后滚动时间，由独立定时器定期检查是否需要加载
        private long _lastContainerChangeTick;
        private DispatcherQueueTimer? _thumbnailCheckTimer;
        private const int ScrollSettleMs = 100;
        private const int ThumbnailCheckIntervalMs = 200;

        // 关联的 RepairViewModel
        public RepairViewModel ViewModel => AppViewModel.Instance.Repair;

        // 构造函数：初始化组件、创建自动滚动器、注册加载/卸载事件
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

        // 页面加载完成后附加自动滚动器、挂载滚动事件、启动缩略图定时器、绑定 ViewModel 事件
        private void RepairPage_Loaded(object sender, RoutedEventArgs e)
        {
            _scroller.Attach(RepairTaskListView);

            // 回到顶部悬浮按钮
            _scrollToTopHelper ??= new ScrollToTopButtonHelper(RepairTaskListView, ScrollToTopButton);
            _scrollToTopHelper.Attach();

            // 查找 ListView 内部的 ScrollViewer 并注册 ViewChanged 事件
            if (!_scrollViewerHooked)
            {
                var sv = FindFirstDescendant<ScrollViewer>(RepairTaskListView);
                if (sv != null)
                {
                    sv.ViewChanged += OnScrollViewChanged;
                    _scrollViewerHooked = true;
                }
            }

            // 创建缩略图检查定时器（独立于滚动事件，避免频繁触发）
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

            AttachDragEvents();

            if (_eventsHooked) return;

            ViewModel.TaskStartedForScroll += OnTaskStarted;
            ViewModel.ProcessingCompletedForScroll += OnAllCompleted;
            ViewModel.ScanItemsFlushed += OnItemsFlushed;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _eventsHooked = true;
        }

        // 页面卸载时分离自动滚动器、移除滚动事件、停止定时器、解绑 ViewModel 事件
        private void RepairPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _scrollToTopHelper?.Detach();

            _scroller.NotifyPageUnloading();
            _scroller.Detach();

            var sv = FindFirstDescendant<ScrollViewer>(RepairTaskListView);
            if (sv != null)
            {
                sv.ViewChanged -= OnScrollViewChanged;
                _scrollViewerHooked = false;
            }

            if (_thumbnailCheckTimer != null)
            {
                _thumbnailCheckTimer.Stop();
                _thumbnailCheckTimer.Tick -= ThumbnailCheckTimer_Tick;
                _thumbnailCheckTimer = null;
            }

            DetachDragEvents();

            if (!_eventsHooked) return;

            ViewModel.TaskStartedForScroll -= OnTaskStarted;
            ViewModel.ProcessingCompletedForScroll -= OnAllCompleted;
            ViewModel.ScanItemsFlushed -= OnItemsFlushed;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _eventsHooked = false;
        }

        // 在可视树中查找第一个指定类型的子元素（深度优先）
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

        // ScrollViewer 滚动时更新当前视图组
        private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
            UpdateCurrentViewGroup();

        // 根据当前滚动位置更新 ViewModel 的 CurrentViewGroup（用于 UI 显示当前分组）
        private void UpdateCurrentViewGroup()
        {
            if (ViewModel.FilteredTasks.Count == 0) { ViewModel.CurrentViewGroup = string.Empty; return; }

            for (int i = 0; i < ViewModel.FilteredTasks.Count; i++)
            {
                var container = RepairTaskListView.ContainerFromIndex(i);
                if (container is not FrameworkElement element) continue;
                var transform = element.TransformToVisual(RepairTaskListView);
                double y = transform.TransformPoint(new Windows.Foundation.Point(0, 0)).Y;
                if (y + element.ActualHeight < 0) continue;
                ViewModel.CurrentViewGroup = RepairViewModel.GetTaskGroupName(ViewModel.FilteredTasks[i]);
                return;
            }
        }

        // 任务开始处理时通知自动滚动器定位
        private void OnTaskStarted(object? sender, RepairTask task) =>
            _scroller.NotifyTaskStarted(task.Index - 1);

        // 所有任务处理完成时通知自动滚动器
        private void OnAllCompleted(object? sender, EventArgs e) =>
            _scroller.NotifyAllCompleted(wasCancelled: ViewModel.WasStoppedByUser);

        // 扫描项刷新时通知自动滚动器并更新视图组
        private void OnItemsFlushed(object? sender, EventArgs e)
        {
            _scroller.NotifyItemsFlushed();
            UpdateCurrentViewGroup();
        }

        // 响应 ViewModel 属性变更，通知自动滚动器状态变化
        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.IsScanning))
            {
                if (ViewModel.IsScanning)
                    _scroller.NotifyScanStarting();
                else
                {
                    _scroller.NotifyScanFinished();
                    LoadVisibleVideoThumbnails();
                    UpdateCurrentViewGroup();
                }
            }
            else if (e.PropertyName == nameof(ViewModel.FilterMode))
                UpdateCurrentViewGroup();
            else if (e.PropertyName == nameof(ViewModel.IsProcessing) && ViewModel.IsProcessing)
                _scroller.NotifyProcessingStarting();
            else if (e.PropertyName == nameof(ViewModel.IsPaused) && !ViewModel.IsPaused)
                _scroller.NotifyProcessingResumed();
        }

        // ── 文件操作 ──────────────────────────────────

        // 输入/输出路径文本框获得焦点时清空内容
        private void DirectoryBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox) textBox.Text = string.Empty;
        }

        // 浏览输入目录按钮点击：选择文件夹、更新 ViewModel 并自动触发扫描
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

        // 浏览输出目录按钮点击：选择文件夹并更新 ViewModel
        private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null) ViewModel.OutputDirectory = folder.Path;
        }

        // 文件按钮点击：在资源管理器中打开文件所在位置
        private void FileButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            try { FilePickerService.RevealInExplorer(path); }
            catch (Exception ex) { LogService.Debug($"RepairPage reveal in explorer failed: {ex.Message}", LogSource.UI); }
        }

        // ── 全屏预览 ──────────────────────────────────

        // 缩略图按钮点击：在 Lightbox 中全屏预览文件（复用扫描配对信息，零 I/O）
        private void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            var items = LightboxItemSource.FromRepairTasks(ViewModel.FilteredTasks);
            int idx = items.FindIndex(i => i.ImagePath == path);
            if (idx < 0) return;
            _ = ((MainWindow)App.MainWindow!).Lightbox.ShowAsync(items, idx);
        }

        // ── 错误详情提示 ──────────────────────────────────

        // 点击状态文本显示错误详情 TeachingTip（同时支持 File1 和 File2）
        private void StatusTextBlock_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not RepairTask task) return;

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

        // 点击问题描述文本显示诊断详情
        private void IssueDescription_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not RepairTask task) return;

            bool isFile2 = element.Tag is string tag && tag == "2";
            if (isFile2 ? !task.File2IsDiagnosisError : !task.File1IsDiagnosisError) return;

            string issueDesc = isFile2 ? task.File2IssueDescription : task.File1IssueDescription;
            if (ErrorDetailTip.IsOpen && ErrorDetailTip.Target == element) { ErrorDetailTip.IsOpen = false; return; }
            ErrorDetailText.Text = issueDesc;
            ErrorDetailTip.Target = element;
            ErrorDetailTip.IsOpen = true;
        }

        // 加载视口中可见的视频缩略图
        private void LoadVisibleVideoThumbnails() =>
            LoadVisibleThumbnailsForCurrentViewport(maxPerBatch: 6, staggerMs: 50);

        // 从当前视口中找到尚未加载缩略图的 RepairTask，分批异步加载
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
                    if (entry != null) { var _ = entry.EnsureThumbnailAsync(); }
                    if (staggerMs > 0) await Task.Delay(staggerMs);
                }
            });
        }

        // 缩略图检查定时器触发：当滚动停止超过阈值后，加载可见区域的缩略图
        private void ThumbnailCheckTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            if (Environment.TickCount64 - Volatile.Read(ref _lastContainerChangeTick) >= ScrollSettleMs)
                LoadVisibleThumbnailsForCurrentViewport(maxPerBatch: 4, staggerMs: 50);
        }

        // ListView 容器内容变更时记录时间戳，并根据任务类型设置容器高度
        private void RepairTaskListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue && args.Item is RepairTask oldTask)
            {
                ThumbnailService.CancelPendingVideoLoad(oldTask.File1Entry?.FilePath ?? "");
                return;
            }

            if (args.Item is RepairTask task)
            {
                if (args.ItemContainer is ListViewItem container)
                    container.Height = task.IsPaired ? 136 : 68;
                Interlocked.Exchange(ref _lastContainerChangeTick, Environment.TickCount64);
            }
        }

        // 错误详情提示关闭时清除目标引用
        private void ErrorDetailTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args) =>
            ErrorDetailTip.Target = null;

        private bool _filterDropDownWidthLocked;

        // 筛选下拉框加载完成后，根据最长选项文字自动适配宽度
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
            var tb = new TextBlock { FontSize = fontSize, FontFamily = btn.FontFamily, TextWrapping = TextWrapping.NoWrap };

            foreach (var type in types)
            {
                foreach (var status in statuses)
                {
                    tb.Text = $"{type}  •  {status}";
                    tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                    maxWidth = Math.Max(maxWidth, tb.DesiredSize.Width);
                }
            }

            if (maxWidth > 0) { btn.Width = maxWidth + 58; _filterDropDownWidthLocked = true; }
        }

        private static readonly string[] _filterTypeKeys = [
            "RepairPage_FilterAll", "RepairPage_FilterPairs",
            "RepairPage_FilterStandaloneImg", "RepairPage_FilterStandaloneVid"
        ];
        private static readonly string[] _filterStatusKeys = [
            "RepairPage_FilterStatusAll", "RepairPage_FilterStatusRepair", "RepairPage_FilterStatusPerfect"
        ];

        // 创建选中状态的图标（对勾）
        private static FontIcon CreateCheckedIcon() => new() { Glyph = "", FontSize = 6 };

        // 筛选菜单打开时动态设置菜单项的本地化文字、宽度和选中状态图标
        private void FilterFlyout_Opening(object sender, object args)
        {
            if (sender is not MenuFlyout flyout) return;

            FilterMenuSeparator.Margin = new Thickness(16, 0, 16, 0);

            string[] headerKeys = ["RepairPage_FilterHeaderType", "RepairPage_FilterHeaderStatus"];
            double maxTextWidth = 0;
            var tb = new TextBlock { FontSize = 14, TextWrapping = TextWrapping.NoWrap };

            foreach (var key in headerKeys.Concat(_filterTypeKeys).Concat(_filterStatusKeys))
            {
                tb.Text = ResourceService.GetString(key);
                tb.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
                maxTextWidth = Math.Max(maxTextWidth, tb.DesiredSize.Width);
            }

            double itemMinWidth = maxTextWidth + 76;

            if (flyout.Items[0] is MenuFlyoutItem typeHeader)
            { typeHeader.Text = ResourceService.GetString("RepairPage_FilterHeaderType"); typeHeader.MinWidth = itemMinWidth; }
            for (int i = 0; i < 4; i++)
                SetupMenuItem(flyout.Items[1 + i], _filterTypeKeys[i], i == ViewModel.FilterMode, itemMinWidth);
            if (flyout.Items[6] is MenuFlyoutItem statusHeader)
            { statusHeader.Text = ResourceService.GetString("RepairPage_FilterHeaderStatus"); statusHeader.MinWidth = itemMinWidth; }
            for (int i = 0; i < 3; i++)
                SetupMenuItem(flyout.Items[7 + i], _filterStatusKeys[i], i == ViewModel.RepairStatusFilter, itemMinWidth);
        }

        // 设置菜单项的本地化文字、最小宽度和选中图标
        private static void SetupMenuItem(MenuFlyoutItemBase item, string resourceKey, bool isSelected, double minWidth)
        {
            if (item is not MenuFlyoutItem menuItem) return;
            menuItem.Text = ResourceService.GetString(resourceKey);
            menuItem.MinWidth = minWidth;
            menuItem.Icon = isSelected ? CreateCheckedIcon() : null;
        }

        // 跳转到修复相关设置
        private void GoToRepairSettings_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is MainWindow mainWindow)
                mainWindow.NavigateToSettings("Repair");
        }

        // ════════════════════════════════════════════════════════════
        //  拖拽文件夹导入（Drag & Drop）
        // ════════════════════════════════════════════════════════════

        private void AttachDragEvents()
        {
            RepairTaskListSurface.DragEnter += TaskList_DragEnter;
            RepairTaskListSurface.DragOver += TaskList_DragOver;
            RepairTaskListSurface.DragLeave += TaskList_DragLeave;
            RepairTaskListSurface.Drop += TaskList_Drop;
        }

        private void DetachDragEvents()
        {
            RepairTaskListSurface.DragEnter -= TaskList_DragEnter;
            RepairTaskListSurface.DragOver -= TaskList_DragOver;
            RepairTaskListSurface.DragLeave -= TaskList_DragLeave;
            RepairTaskListSurface.Drop -= TaskList_Drop;
        }

        /// <summary>拖入时异步检测内容是否全是文件夹</summary>
        private async void TaskList_DragEnter(object sender, DragEventArgs e)
        {
            _isDropAllFolders = false;
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var deferral = e.GetDeferral();
                try
                {
                    var items = await e.DataView.GetStorageItemsAsync();
                    _isDropAllFolders = items.Count > 0
                        && items.All(i => i is StorageFolder);
                }
                catch { _isDropAllFolders = false; }
                finally { deferral.Complete(); }
            }
        }

        private void TaskList_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;

            if (_isDropAllFolders)
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.DragUIOverride.IsGlyphVisible = true;
                e.DragUIOverride.IsCaptionVisible = false;
                DragOverlay.Visibility = Visibility.Visible;
            }

            e.Handled = true;
        }

        private void TaskList_DragLeave(object sender, DragEventArgs e)
        {
            DragOverlay.Visibility = Visibility.Collapsed;
            _isDropAllFolders = false;
            e.Handled = true;
        }

        /// <summary>拖拽释放：提取文件夹路径 → 设置 ViewModel.InputDirectory 触发自动扫描</summary>
        private async void TaskList_Drop(object sender, DragEventArgs e)
        {
            try
            {
                DragOverlay.Visibility = Visibility.Collapsed;

                if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                    return;

                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count == 0) return;

                // 取第一个文件夹路径
                string? targetPath = null;
                foreach (var item in items)
                {
                    if (item is StorageFolder folder)
                    {
                        targetPath = folder.Path;
                        break;
                    }
                }

                if (string.IsNullOrEmpty(targetPath) || !Directory.Exists(targetPath))
                    return;

                LogService.Repair($"Drop folder: {targetPath}");
                ViewModel.InputDirectory = targetPath;

                e.Handled = true;
            }
            catch (Exception ex)
            {
                LogService.Repair($"Drop CRASH: {ex.GetType().Name}: {ex.Message}", LogLevel.Error, ex);
            }
        }
    }
}
