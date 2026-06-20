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
                    _scroller.NotifyScanFinished();
            }
            else if (e.PropertyName == nameof(ViewModel.IsProcessing) && ViewModel.IsProcessing)
            {
                _scroller.NotifyProcessingStarting();
            }
            else if (e.PropertyName == nameof(ViewModel.IsPaused) && !ViewModel.IsPaused)
            {
                // 暂停恢复：重置滚动追踪，确保后续任务正常触发滚动
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
            if (task.Status != ProcessStatus.Failed || string.IsNullOrWhiteSpace(task.Details)) return;
            if (ErrorDetailTip.IsOpen && ErrorDetailTip.Target == element) { ErrorDetailTip.IsOpen = false; return; }
            ErrorDetailText.Text = task.Details;
            ErrorDetailTip.Target = element;
            ErrorDetailTip.IsOpen = true;
        }

        private void IssueDescription_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not RepairTask task) return;
            if (!task.IsDiagnosisError) return;
            if (ErrorDetailTip.IsOpen && ErrorDetailTip.Target == element) { ErrorDetailTip.IsOpen = false; return; }
            ErrorDetailText.Text = task.IssueDescription;
            ErrorDetailTip.Target = element;
            ErrorDetailTip.IsOpen = true;
        }

        private void RepairTaskListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) { }
        private void ErrorDetailTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args) => ErrorDetailTip.Target = null;
    }
}
