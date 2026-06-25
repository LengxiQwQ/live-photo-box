using LivePhotoBox.Helpers;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Linq;

namespace LivePhotoBox.Views
{
    public sealed partial class MergePage : Page
    {
        private readonly TaskListAutoScroller _scroller;
        private bool _eventsHooked;

        public MergeViewModel ViewModel => AppViewModel.Instance.Merge;

        public MergePage()
        {
            InitializeComponent();

            _scroller = new TaskListAutoScroller(
                "Merge",
                isActive: () => ViewModel.IsProcessing || ViewModel.IsScanning,
                getTaskCount: () => ViewModel.Tasks.Count,
                getTaskAt: idx => ViewModel.Tasks[idx]);

            Loaded += MergePage_Loaded;
            Unloaded += MergePage_Unloaded;
        }

        private void ProtocolComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox comboBox)
                ComboBoxHelper.AutoFitWidth(comboBox);
        }

        private void MergePage_Loaded(object sender, RoutedEventArgs e)
        {
            _scroller.Attach(MergeTaskListView);

            if (_eventsHooked) return;

            ViewModel.TaskStartedForScroll += OnTaskStarted;
            ViewModel.ProcessingCompletedForScroll += OnAllCompleted;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _eventsHooked = true;
        }

        private void MergePage_Unloaded(object sender, RoutedEventArgs e)
        {
            _scroller.NotifyPageUnloading();
            _scroller.Detach();

            if (!_eventsHooked) return;

            ViewModel.TaskStartedForScroll -= OnTaskStarted;
            ViewModel.ProcessingCompletedForScroll -= OnAllCompleted;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _eventsHooked = false;
        }

        private void OnTaskStarted(object? sender, MergeTask task) =>
            _scroller.NotifyTaskStarted(task.Index - 1);

        private void OnAllCompleted(object? sender, EventArgs e) =>
            _scroller.NotifyAllCompleted(wasCancelled: ViewModel.WasStoppedByUser);

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
                _scroller.NotifyProcessingResumed();
            }
        }

        // ── 文件操作 ──────────────────────────────────

        private void DirectoryBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox) textBox.Text = string.Empty;
        }

        private async void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null) ViewModel.InputDirectory = folder.Path;
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
            catch (Exception ex) { LogService.Debug($"MergePage reveal in explorer failed: {ex.Message}", LogSource.UI); }
        }

        private void FileGroupButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            try { FilePickerService.RevealInExplorer(path); }
            catch (Exception ex) { LogService.Debug($"MergePage reveal in explorer failed: {ex.Message}", LogSource.UI); }
        }

        private void MergeTaskListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) { }

        // ── 全屏预览 ──────────────────────────────────

        private void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            var paths = ViewModel.Tasks.Select(t => t.ImagePath).ToList();
            int idx = paths.IndexOf(path);
            if (idx < 0) return;
            _ = ((MainWindow)App.MainWindow!).Lightbox.ShowAsync(paths, idx);
        }

        // ── 错误详情提示 ──────────────────────────────────

        private void StatusTextBlock_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not MergeTask task) return;
            if (task.Status != ProcessStatus.Failed || string.IsNullOrWhiteSpace(task.Details)) return;

            if (ErrorDetailTip.IsOpen && ErrorDetailTip.Target == element) { ErrorDetailTip.IsOpen = false; return; }
            ErrorDetailText.Text = task.Details;
            ErrorDetailTip.Target = element;
            ErrorDetailTip.IsOpen = true;
        }

        private void ErrorDetailTip_Closed(TeachingTip sender, TeachingTipClosedEventArgs args) =>
            ErrorDetailTip.Target = null;

        private void GoToMergeSettings_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is MainWindow mainWindow)
                mainWindow.NavigateToSettings("Merge");
        }
    }
}
