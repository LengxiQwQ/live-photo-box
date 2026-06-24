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
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Views
{
    public sealed partial class RepairPage : Page
    {
        private readonly TaskListAutoScroller _scroller;
        private bool _eventsHooked;
        private bool _scrollViewerHooked;

        // 缩略图加载：只记录最后滚动时间，由独立定时器定期检查是否需要加载
        private long _lastContainerChangeTick;
        private DispatcherQueueTimer? _thumbnailCheckTimer;
        private const int ScrollSettleMs = 100;
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

            if (!_scrollViewerHooked)
            {
                var sv = FindFirstDescendant<ScrollViewer>(RepairTaskListView);
                if (sv != null)
                {
                    sv.ViewChanged += OnScrollViewChanged;
                    _scrollViewerHooked = true;
                }
            }

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

            if (!_eventsHooked) return;

            ViewModel.TaskStartedForScroll -= OnTaskStarted;
            ViewModel.ProcessingCompletedForScroll -= OnAllCompleted;
            ViewModel.ScanItemsFlushed -= OnItemsFlushed;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _eventsHooked = false;
        }

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

        private void OnScrollViewChanged(object? sender, ScrollViewerViewChangedEventArgs e) =>
            UpdateCurrentViewGroup();

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

        // ── 全屏预览 ──────────────────────────────────

        private void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            var paths = ViewModel.FilteredTasks.Select(t => t.File1Path).ToList();
            int idx = paths.IndexOf(path);
            if (idx < 0) return;
            _ = ((MainWindow)App.MainWindow!).Lightbox.ShowAsync(paths, idx);
        }

        // ── 错误详情提示 ──────────────────────────────────

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

        private void LoadVisibleVideoThumbnails() =>
            LoadVisibleThumbnailsForCurrentViewport(maxPerBatch: 6, staggerMs: 50);

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

        private void ThumbnailCheckTimer_Tick(DispatcherQueueTimer sender, object args)
        {
            if (Environment.TickCount64 - Volatile.Read(ref _lastContainerChangeTick) >= ScrollSettleMs)
                LoadVisibleThumbnailsForCurrentViewport(maxPerBatch: 4, staggerMs: 50);
        }

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

        private void ErrorDetailTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args) =>
            ErrorDetailTip.Target = null;

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

        private static FontIcon CreateCheckedIcon() => new() { Glyph = "", FontSize = 6 };

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

        private static void SetupMenuItem(MenuFlyoutItemBase item, string resourceKey, bool isSelected, double minWidth)
        {
            if (item is not MenuFlyoutItem menuItem) return;
            menuItem.Text = ResourceService.GetString(resourceKey);
            menuItem.MinWidth = minWidth;
            menuItem.Icon = isSelected ? CreateCheckedIcon() : null;
        }
    }
}
