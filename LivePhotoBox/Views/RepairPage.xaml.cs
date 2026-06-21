using LivePhotoBox.Helpers;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using System;
using System.Threading.Tasks;

namespace LivePhotoBox.Views
{
    public sealed partial class RepairPage : Page
    {
        private readonly TaskListAutoScroller _scroller;
        private bool _eventsHooked;

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

            if (!_eventsHooked) return;

            ViewModel.TaskStartedForScroll -= OnTaskStarted;
            ViewModel.ProcessingCompletedForScroll -= OnAllCompleted;
            ViewModel.ScanItemsFlushed -= OnItemsFlushed;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _eventsHooked = false;
        }

        private void OnTaskStarted(object? sender, RepairTask task) =>
            _scroller.NotifyTaskStarted(task.Index - 1);

        private void OnAllCompleted(object? sender, EventArgs e) =>
            _scroller.NotifyAllCompleted(wasCancelled: ViewModel.WasStoppedByUser);

        private void OnItemsFlushed(object? sender, EventArgs e) =>
            _scroller.NotifyItemsFlushed();

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
                }
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

        /// <summary>扫描完成后，为当前可见的条目加载视频缩略图</summary>
        private void LoadVisibleVideoThumbnails()
        {
            int count = ViewModel.Tasks.Count;
            if (count == 0) return;

            for (int i = 0; i < count; i++)
            {
                // ContainerFromIndex 非 null = 该条目当前在可视区域内
                if (RepairTaskListView.ContainerFromIndex(i) != null &&
                    ViewModel.Tasks[i] is RepairTask task && task.Thumbnail == null)
                {
                    task.File1Entry?.EnsureThumbnailAsync();
                }
            }
        }

        private void RepairTaskListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            // 容器回收（滚动出屏幕）→ 取消还在队列中等待的视频加载
            if (args.InRecycleQueue && args.Item is RepairTask oldTask)
            {
                ThumbnailService.CancelPendingVideoLoad(oldTask.File1Entry?.FilePath ?? "");
                return;
            }

            // 可见条目：确保缩略图加载
            if (args.Item is RepairTask task && task.Thumbnail == null)
            {
                // 如果关闭了"扫描时加载"，扫描期间不触发视频加载
                if (ViewModel.IsScanning && !AppSettingsService.GetValue("IsRepairScanLoadThumbnail", false))
                    return;

                task.File1Entry?.EnsureThumbnailAsync();
            }
        }
        private void ErrorDetailTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args) => ErrorDetailTip.Target = null;
    }
}
