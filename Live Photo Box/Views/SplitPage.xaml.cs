/*
 * SplitPage.xaml.cs
 *
 * 实况照片拆分页面的代码后置。
 * 提供将实况照片拆分为独立图片和视频文件的功能。
 * 包含任务列表自动滚动、文件夹选择、全屏预览和错误详情提示。
 *
 * 对应 ViewModel：SplitViewModel
 *
 * 生命周期：
 *   - 构造函数 → 创建 TaskListAutoScroller，注册 Loaded/Unloaded
 *   - Loaded → 附加自动滚动器，绑定 ViewModel 事件
 *   - Unloaded → 分离自动滚动器，解绑事件
 *   - 用户操作（浏览文件夹、打开文件、预览等）通过事件处理
 */

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
    public sealed partial class SplitPage : Page
    {
        // 任务列表自动滚动器，在处理/扫描过程中保持当前任务可见
        private readonly TaskListAutoScroller _scroller;

        // 是否已绑定 ViewModel 事件，防止重复绑定
        private bool _eventsHooked;

        // 关联的 SplitViewModel
        public SplitViewModel ViewModel => AppViewModel.Instance.Split;

        // 构造函数：初始化组件、创建自动滚动器、注册加载/卸载事件
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

        // 输出格式下拉框加载完成后自动适配宽度
        private void FormatComboBox_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is ComboBox comboBox)
                ComboBoxHelper.AutoFitWidth(comboBox);
        }

        // 页面加载完成后附加自动滚动器，绑定 ViewModel 事件
        private void SplitPage_Loaded(object sender, RoutedEventArgs e)
        {
            _scroller.Attach(SplitTaskListView);

            if (_eventsHooked) return;

            ViewModel.TaskStartedForScroll += OnTaskStarted;
            ViewModel.ProcessingCompletedForScroll += OnAllCompleted;
            ViewModel.ScanItemsFlushed += OnItemsFlushed;
            ViewModel.PropertyChanged += OnViewModelPropertyChanged;
            _eventsHooked = true;
        }

        // 页面卸载时分离自动滚动器，解绑 ViewModel 事件
        private void SplitPage_Unloaded(object sender, RoutedEventArgs e)
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

        // 任务开始处理时通知自动滚动器定位
        private void OnTaskStarted(object? sender, SplitTask task) =>
            _scroller.NotifyTaskStarted(task.Index - 1);

        // 所有任务处理完成时通知自动滚动器
        private void OnAllCompleted(object? sender, EventArgs e) =>
            _scroller.NotifyAllCompleted(wasCancelled: ViewModel.WasStoppedByUser);

        // 扫描项刷新时通知自动滚动器
        private void OnItemsFlushed(object? sender, EventArgs e) =>
            _scroller.NotifyItemsFlushed();

        // 响应 ViewModel 属性变更，通知自动滚动器状态变化
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

        // 输入/输出路径文本框获得焦点时清空内容
        private void DirectoryBox_GotFocus(object sender, RoutedEventArgs e)
        {
            if (sender is TextBox textBox) textBox.Text = string.Empty;
        }

        // 浏览输入目录按钮点击：选择文件夹并更新 ViewModel
        private async void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null) ViewModel.InputDirectory = folder.Path;
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
            catch (Exception ex) { LogService.Debug($"SplitPage reveal in explorer failed: {ex.Message}", LogSource.UI); }
        }

        // 加一把简单的状态锁，防止用户在短暂加载时疯狂狂点导致灾难
        private bool _isOpeningLightbox = false;

        // 缩略图按钮点击：在 Lightbox 中全屏预览文件（扫描时已解析视频信息，零 I/O）
        private void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            if (_isOpeningLightbox) return;
            if (sender is not Button { Tag: string path } || string.IsNullOrWhiteSpace(path)) return;

            _isOpeningLightbox = true;
            try
            {
                var items = LightboxItemSource.FromSplitTasks(ViewModel.Tasks);
                int idx = items.FindIndex(i => i.ImagePath == path);
                if (idx >= 0)
                    _ = ((MainWindow)App.MainWindow!).Lightbox.ShowAsync(items, idx);
            }
            finally
            {
                _isOpeningLightbox = false;
            }
        }

        private void SplitTaskListView_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args) { }

        // 点击状态文本显示错误详情 TeachingTip
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

        // 错误详情提示关闭时清除目标引用
        private void ErrorDetailTip_Closed(Microsoft.UI.Xaml.Controls.TeachingTip sender, Microsoft.UI.Xaml.Controls.TeachingTipClosedEventArgs args) =>
            ErrorDetailTip.Target = null;

        // 跳转到拆分相关设置
        private void GoToSplitSettings_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow is MainWindow mainWindow)
                mainWindow.NavigateToSettings("Split");
        }
    }
}
