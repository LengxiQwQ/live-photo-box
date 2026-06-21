using LivePhotoBox.Helpers;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
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
            // 第一批数据到达后自适应 ComboBox 宽度（Loaded 时可能还未显示/Vibility 为 Collapsed）
            if (FilterComboBox.Items.Count > 0)
                ComboBoxHelper.AutoFitWidth(FilterComboBox);
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

        private async void FileButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            try { await FilePickerService.OpenFileAsync(path); }
            catch (Exception ex) { LogService.Debug($"RepairPage open file failed: {ex.Message}", LogSource.UI); }
        }

        private void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            try { FilePickerService.RevealInExplorer(path); }
            catch (Exception ex) { LogService.Debug($"RepairPage reveal in explorer failed: {ex.Message}", LogSource.UI); }
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

            // 仅记录"有新内容进入视口"的时刻。加载由 ThumbnailCheckTimer_Tick 负责。
            if (args.Item is RepairTask)
                Interlocked.Exchange(ref _lastContainerChangeTick, Environment.TickCount64);
        }
        private void FilterComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox comboBox)
                ComboBoxHelper.AutoFitWidth(comboBox);
        }

        private void ErrorDetailTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args) => ErrorDetailTip.Target = null;
    }
}
