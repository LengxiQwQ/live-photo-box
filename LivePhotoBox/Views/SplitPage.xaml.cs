using LivePhotoBox.Helpers;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;

namespace LivePhotoBox.Views
{
    public sealed partial class SplitPage : Page
    {
        private readonly TaskListAutoScroller _scroller;
        private bool _eventsHooked;
        private KeyEventHandler? _pageKeyDownHandler;

        // ── 统一预览服务 ──
        private static readonly ImagePreviewService _previewService = new(maxCacheSize: 20, decodePixelWidth: 1920, preloadCount: 2);
        private List<string> _previewPaths = [];
        private int _previewCurrentIndex = -1;

        public SplitViewModel ViewModel => AppViewModel.Instance.Split;

        public SplitPage()
        {
            InitializeComponent();

            _scroller = new TaskListAutoScroller(
                "Split",
                isActive: () => ViewModel.IsProcessing || ViewModel.IsScanning,
                getTaskCount: () => ViewModel.Tasks.Count,
                getTaskAt: idx => ViewModel.Tasks[idx]);

            Loaded += SplitPage_Loaded;
            Unloaded += SplitPage_Unloaded;
        }

        private void FormatComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox comboBox)
                ComboBoxHelper.AutoFitWidth(comboBox);
        }

        private void SplitPage_Loaded(object sender, RoutedEventArgs e)
        {
            _scroller.Attach(SplitTaskListView);

            // 灯箱键盘快捷键（← → Esc）
            _pageKeyDownHandler = new KeyEventHandler(OnPageKeyDown);
            AddHandler(UIElement.KeyDownEvent, _pageKeyDownHandler, true);

            if (_eventsHooked) return;

            ViewModel.TaskStartedForScroll += OnTaskStarted;
            ViewModel.ProcessingCompletedForScroll += OnAllCompleted;
            ViewModel.ScanItemsFlushed += OnItemsFlushed;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _eventsHooked = true;
        }

        private void SplitPage_Unloaded(object sender, RoutedEventArgs e)
        {
            _scroller.NotifyPageUnloading();
            _scroller.Detach();

            if (_pageKeyDownHandler != null)
            {
                RemoveHandler(UIElement.KeyDownEvent, _pageKeyDownHandler);
                _pageKeyDownHandler = null;
            }

            if (!_eventsHooked) return;

            ViewModel.TaskStartedForScroll -= OnTaskStarted;
            ViewModel.ProcessingCompletedForScroll -= OnAllCompleted;
            ViewModel.ScanItemsFlushed -= OnItemsFlushed;
            ViewModel.PropertyChanged -= OnViewModelPropertyChanged;
            _eventsHooked = false;
        }

        private void OnTaskStarted(object? sender, SplitTask task) =>
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
            catch (Exception ex) { LogService.Debug($"SplitPage reveal in explorer failed: {ex.Message}", LogSource.UI); }
        }

        private async void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;
            _previewPaths = ViewModel.Tasks.Select(t => t.SourcePath).ToList();
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

        private void SplitTaskListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) { }

        private void StatusTextBlock_Tapped(object sender, Microsoft.UI.Xaml.Input.TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not SplitTask task) return;
            if (task.Status != ProcessStatus.Failed || string.IsNullOrWhiteSpace(task.Details)) return;

            if (ErrorDetailTip.IsOpen && ErrorDetailTip.Target == element) { ErrorDetailTip.IsOpen = false; return; }
            ErrorDetailText.Text = task.Details;
            ErrorDetailTip.Target = element;
            ErrorDetailTip.IsOpen = true;
        }

        private void ErrorDetailTip_Closed(Microsoft.UI.Xaml.Controls.TeachingTip sender, Microsoft.UI.Xaml.Controls.TeachingTipClosedEventArgs args) =>
            ErrorDetailTip.Target = null;
    }
}
