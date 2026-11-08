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
using System.Collections.Specialized;
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

        // 页面激活期间接管窗口级快捷键，避免外层 NavigationView 抢占队列方向键。
        private UIElement? _repairShortcutHost;
        private readonly List<KeyboardAccelerator> _repairKeyboardAccelerators = [];

        // 停止不可恢复，必须在一秒内连续按两次 Esc。
        private long _lastStopEscapeTick;

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
                getTaskCount: () => ViewModel.FilteredTasks.Count,
                getTaskAt: idx => ViewModel.FilteredTasks[idx]);

            Loaded += RepairPage_Loaded;
            Unloaded += RepairPage_Unloaded;
        }

        // 页面加载完成后附加自动滚动器、挂载滚动事件、启动缩略图定时器、绑定 ViewModel 事件
        private void RepairPage_Loaded(object sender, RoutedEventArgs e)
        {
            AttachRepairShortcuts();
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
            if (ThumbnailService.IsLoadingEnabled && _thumbnailCheckTimer == null)
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
            ViewModel.ScanPresentationRequested += PrepareScanPresentationAsync;
            ViewModel.ScanSessionStarting += OnScanSessionStarting;
            ViewModel.ScanResultsFinalizing += OnScanResultsFinalizing;
            _eventsHooked = true;
        }

        // 页面卸载时分离自动滚动器、移除滚动事件、停止定时器、解绑 ViewModel 事件
        private void RepairPage_Unloaded(object sender, RoutedEventArgs e)
        {
            ErrorDetailTip.Dismiss();
            DetachRepairShortcuts();
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
            ViewModel.ScanPresentationRequested -= PrepareScanPresentationAsync;
            ViewModel.ScanSessionStarting -= OnScanSessionStarting;
            ViewModel.ScanResultsFinalizing -= OnScanResultsFinalizing;
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

            var panel = RepairTaskListView.ItemsPanelRoot as ItemsStackPanel;
            int first = Math.Max(0, panel?.FirstVisibleIndex ?? 0);
            int end = Math.Min(ViewModel.FilteredTasks.Count, panel is { LastVisibleIndex: >= 0 } ? panel.LastVisibleIndex + 1 : first + 32);
            for (int i = first; i < end; i++)
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
            _scroller.NotifyTaskStarted(ViewModel.FilteredTasks.IndexOf(task));

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
        private void OnScanResultsFinalizing(object? sender, EventArgs args) => _scroller.NotifyScanFinished();

        private void OnScanSessionStarting(object? sender, EventArgs e)
        {
            _scroller.NotifyScanStarting();
        }

        private Task PrepareScanPresentationAsync(System.Threading.CancellationToken token) =>
            ScanResultsPresenter.WaitForLayoutAsync(this, token, RepairTaskListView);

        private void OnViewModelPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(ViewModel.IsScanning))
            {
                if (!ViewModel.IsScanning)
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
            try
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
            catch (Exception ex)
            {
                LogService.Repair($"BrowseInput failed: {ex.Message}", LogLevel.Error, ex);
            }
        }

        // 浏览输出目录按钮点击：选择文件夹并更新 ViewModel
        private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = await FilePickerService.PickFolderAsync();
                if (folder != null) ViewModel.OutputDirectory = folder.Path;
            }
            catch (Exception ex)
            {
                LogService.Repair($"BrowseOutput failed: {ex.Message}", LogLevel.Error, ex);
            }
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
        private void PreviewTask(RepairTask task)
        {
            var previewTasks = ViewModel.FilteredTasks.Where(t => !t.IsGroupHeader).ToList();
            int idx = previewTasks.IndexOf(task);
            if (idx < 0) return;
            _ = ((MainWindow)App.MainWindow!).Lightbox.ShowInspectedAsync(
                () => LightboxItemSource.FromRepairTasks(previewTasks), idx);
        }

        private void AttachRepairShortcuts()
        {
            if (_repairShortcutHost != null || App.MainWindow?.Content is not UIElement host)
                return;

            host.PreviewKeyDown += RepairShortcutHost_PreviewKeyDown;
            _repairShortcutHost = host;
            RegisterRepairKeyboardAccelerators();
        }

        private void RegisterRepairKeyboardAccelerators()
        {
            RegisterRepairKeyboardAccelerator(Windows.System.VirtualKey.O,
                Windows.System.VirtualKeyModifiers.Control,
                () => TryExecuteRepairCommand(ViewModel.PickInputDirectoryCommand));

            RegisterRepairKeyboardAccelerator(Windows.System.VirtualKey.O,
                Windows.System.VirtualKeyModifiers.Menu,
                () => TryExecuteRepairCommand(ViewModel.PickOutputDirectoryCommand));

            RegisterRepairKeyboardAccelerator(Windows.System.VirtualKey.I,
                Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
                () => TryExecuteRepairCommand(ViewModel.OpenRepairInputFolderCommand));

            RegisterRepairKeyboardAccelerator(Windows.System.VirtualKey.E,
                Windows.System.VirtualKeyModifiers.Control | Windows.System.VirtualKeyModifiers.Shift,
                () => TryExecuteRepairCommand(ViewModel.OpenRepairOutputFolderCommand));

            RegisterRepairKeyboardAccelerator(Windows.System.VirtualKey.Enter,
                Windows.System.VirtualKeyModifiers.Control, () =>
                {
                    if (ViewModel.IsScanning || ViewModel.IsProcessing) return false;
                    return TryExecuteRepairCommand(ViewModel.ToggleProcessCommand);
                });

            RegisterRepairKeyboardAccelerator(Windows.System.VirtualKey.F,
                Windows.System.VirtualKeyModifiers.Control, OpenRepairFilter);

            RegisterRepairKeyboardAccelerator((Windows.System.VirtualKey)188,
                Windows.System.VirtualKeyModifiers.Control, () =>
                {
                    GoToRepairSettings_Click(this, new RoutedEventArgs());
                    return true;
                });
        }

        private void RegisterRepairKeyboardAccelerator(Windows.System.VirtualKey key,
            Windows.System.VirtualKeyModifiers modifiers, Func<bool> execute)
        {
            var accelerator = new KeyboardAccelerator { Key = key, Modifiers = modifiers };
            accelerator.Invoked += (_, args) => args.Handled = execute();
            _repairShortcutHost!.KeyboardAccelerators.Add(accelerator);
            _repairKeyboardAccelerators.Add(accelerator);
        }

        private static bool TryExecuteRepairCommand(System.Windows.Input.ICommand command, object? parameter = null)
        {
            if (!command.CanExecute(parameter)) return false;
            command.Execute(parameter);
            return true;
        }

        private bool OpenRepairFilter()
        {
            if (FilterDropDown.Visibility != Visibility.Visible || !FilterDropDown.IsEnabled)
                return false;

            FilterDropDown.Focus(FocusState.Programmatic);
            FilterDropDown.Flyout?.ShowAt(FilterDropDown);
            return true;
        }

        private void DetachRepairShortcuts()
        {
            if (_repairShortcutHost == null)
                return;

            _repairShortcutHost.PreviewKeyDown -= RepairShortcutHost_PreviewKeyDown;
            foreach (var accelerator in _repairKeyboardAccelerators)
                _repairShortcutHost.KeyboardAccelerators.Remove(accelerator);
            _repairKeyboardAccelerators.Clear();
            _repairShortcutHost = null;
        }

        private void RepairShortcutHost_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (App.MainWindow is MainWindow { Lightbox.IsOpen: true })
                return;

            const Windows.System.VirtualKey oemComma = (Windows.System.VirtualKey)188;
            const ushort commaScanCode = 0x33;
            bool controlDown = IsRepairModifierDown(Windows.System.VirtualKey.Control);
            bool shiftDown = IsRepairModifierDown(Windows.System.VirtualKey.Shift);
            bool altDown = IsRepairModifierDown(Windows.System.VirtualKey.Menu);
            bool isCommaKey = e.Key == oemComma
                || e.OriginalKey == oemComma
                || e.KeyStatus.ScanCode == commaScanCode;

            bool isGlobalShortcut = e.Key is Windows.System.VirtualKey.F1
                    or Windows.System.VirtualKey.F5
                    or Windows.System.VirtualKey.Escape
                || (controlDown && e.Key is Windows.System.VirtualKey.O
                    or Windows.System.VirtualKey.I
                    or Windows.System.VirtualKey.E
                    or Windows.System.VirtualKey.F
                    or Windows.System.VirtualKey.Enter)
                || (altDown && e.Key == Windows.System.VirtualKey.O)
                || (controlDown && isCommaKey);

            DependencyObject? focused = _repairShortcutHost?.XamlRoot != null
                ? FocusManager.GetFocusedElement(_repairShortcutHost.XamlRoot) as DependencyObject
                : null;
            if (ShouldPreserveRepairShortcut(focused) && !isGlobalShortcut)
                return;

            bool noModifiers = !controlDown && !shiftDown && !altDown;
            bool onlyControl = controlDown && !shiftDown && !altDown;
            bool controlShift = controlDown && shiftDown && !altDown;
            bool focusQueue = false;

            if (onlyControl && e.Key == Windows.System.VirtualKey.O
                && ViewModel.PickInputDirectoryCommand.CanExecute(null))
            {
                ViewModel.PickInputDirectoryCommand.Execute(null);
                e.Handled = true;
            }
            else if (altDown && !controlDown && !shiftDown
                && e.Key == Windows.System.VirtualKey.O
                && ViewModel.PickOutputDirectoryCommand.CanExecute(null))
            {
                ViewModel.PickOutputDirectoryCommand.Execute(null);
                e.Handled = true;
            }
            else if (controlShift && e.Key == Windows.System.VirtualKey.I
                && ViewModel.OpenRepairInputFolderCommand.CanExecute(null))
            {
                ViewModel.OpenRepairInputFolderCommand.Execute(null);
                e.Handled = true;
            }
            else if (controlShift && e.Key == Windows.System.VirtualKey.E
                && ViewModel.OpenRepairOutputFolderCommand.CanExecute(null))
            {
                ViewModel.OpenRepairOutputFolderCommand.Execute(null);
                e.Handled = true;
            }
            else if (noModifiers && e.Key == Windows.System.VirtualKey.F5
                && !ViewModel.IsScanning && !ViewModel.IsProcessing
                && ViewModel.ScanDirectoryCommand.CanExecute(null))
            {
                ViewModel.ScanDirectoryCommand.Execute(null);
                e.Handled = true;
            }
            else if (onlyControl && e.Key == Windows.System.VirtualKey.Enter
                && !ViewModel.IsScanning && !ViewModel.IsProcessing
                && ViewModel.ToggleProcessCommand.CanExecute(null))
            {
                ViewModel.ToggleProcessCommand.Execute(null);
                e.Handled = true;
            }
            else if (noModifiers && e.Key == Windows.System.VirtualKey.Escape
                && (ViewModel.IsScanning || ViewModel.IsProcessing))
            {
                long now = Environment.TickCount64;
                bool confirmed = now - _lastStopEscapeTick <= 1000;
                _lastStopEscapeTick = confirmed ? 0 : now;

                if (confirmed && ViewModel.IsScanning && ViewModel.ScanDirectoryCommand.CanExecute(null))
                {
                    ViewModel.ScanDirectoryCommand.Execute(null);
                }
                else if (confirmed && ViewModel.IsProcessing && ViewModel.ToggleProcessCommand.CanExecute(null))
                {
                    ViewModel.ToggleProcessCommand.Execute(null);
                }
                e.Handled = true;
            }
            else if (noModifiers && e.Key == Windows.System.VirtualKey.C
                && !ViewModel.IsScanning && !ViewModel.IsProcessing && ViewModel.Tasks.Count > 0)
            {
                ViewModel.ToggleSecondaryActionCommand.Execute(null);
                e.Handled = true;
                focusQueue = true;
            }
            else if (noModifiers && e.Key == Windows.System.VirtualKey.P && ViewModel.IsProcessing)
            {
                ViewModel.ToggleSecondaryActionCommand.Execute(null);
                e.Handled = true;
            }
            else if (onlyControl && e.Key == Windows.System.VirtualKey.F)
            {
                e.Handled = OpenRepairFilter();
                return;
            }
            else if (noModifiers && e.Key == Windows.System.VirtualKey.F1)
            {
                e.Handled = TryExecuteRepairCommand(ViewModel.GoToTutorialCommand, "Repair");
            }
            else if (onlyControl && isCommaKey)
            {
                GoToRepairSettings_Click(this, e);
                e.Handled = true;
            }
            else if (noModifiers && e.Key is Windows.System.VirtualKey.Up or Windows.System.VirtualKey.Down)
            {
                e.Handled = TryNavigateRepairTask(e.Key);
                focusQueue = e.Handled;
            }
            else if (noModifiers && e.Key is Windows.System.VirtualKey.Enter or Windows.System.VirtualKey.Space
                && RepairTaskListView.SelectedItem is RepairTask previewTask)
            {
                PreviewTask(previewTask);
                e.Handled = true;
                focusQueue = true;
            }

            if (focusQueue)
                RepairTaskListView.Focus(FocusState.Programmatic);
        }

        private bool TryNavigateRepairTask(Windows.System.VirtualKey key)
        {
            if (ViewModel.FilteredTasks.Count == 0)
                return false;

            int currentIndex = RepairTaskListView.SelectedIndex;
            int nextIndex = currentIndex < 0
                ? 0
                : key == Windows.System.VirtualKey.Up
                    ? Math.Max(0, currentIndex - 1)
                    : Math.Min(ViewModel.FilteredTasks.Count - 1, currentIndex + 1);

            RepairTaskListView.SelectedIndex = nextIndex;
            RepairTaskListView.ScrollIntoView(ViewModel.FilteredTasks[nextIndex]);
            return true;
        }

        private static bool IsRepairModifierDown(Windows.System.VirtualKey key)
        {
            if (IsRepairKeyDown(key)) return true;
            return key switch
            {
                Windows.System.VirtualKey.Control =>
                    IsRepairKeyDown(Windows.System.VirtualKey.LeftControl)
                    || IsRepairKeyDown(Windows.System.VirtualKey.RightControl),
                Windows.System.VirtualKey.Shift =>
                    IsRepairKeyDown(Windows.System.VirtualKey.LeftShift)
                    || IsRepairKeyDown(Windows.System.VirtualKey.RightShift),
                Windows.System.VirtualKey.Menu =>
                    IsRepairKeyDown(Windows.System.VirtualKey.LeftMenu)
                    || IsRepairKeyDown(Windows.System.VirtualKey.RightMenu),
                _ => false
            };
        }

        private static bool IsRepairKeyDown(Windows.System.VirtualKey key) =>
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        private static bool ShouldPreserveRepairShortcut(DependencyObject? source)
        {
            for (DependencyObject? current = source; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is TextBox
                    or RichEditBox
                    or PasswordBox
                    or NumberBox
                    or ComboBox
                    or Slider)
                    return true;
            }
            return false;
        }

        private void ThumbnailButton_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not Button { DataContext: RepairTask task }) return;
            PreviewTask(task);
        }

        // ── 操作列按钮处理 ──────────────────────────────────

        // "在文件夹中查看"直接定位按钮（兼容旧调用）
        private void ShowInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: RepairTask task }) return;

            string? targetPath = task.File1Entry?.PublishedOutputs.FirstOrDefault()?.Path
                ?? task.File2Entry?.PublishedOutputs.FirstOrDefault()?.Path
                ?? task.File1Path;

            if (string.IsNullOrWhiteSpace(targetPath)) return;

            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                FilePickerService.RevealInExplorer(targetPath);
            }
            else if (!string.IsNullOrWhiteSpace(task.File1Path) && File.Exists(task.File1Path))
            {
                FilePickerService.RevealInExplorer(task.File1Path);
            }
        }

        // Row 0（照片）专属在文件夹中查看
        private void ShowInFolder_File1_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: RepairTask task }) return;

            string? targetPath = task.File1Entry?.PublishedOutputs.FirstOrDefault()?.Path
                ?? task.File1Path;

            if (string.IsNullOrWhiteSpace(targetPath)) return;

            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                FilePickerService.RevealInExplorer(targetPath);
            }
            else if (!string.IsNullOrWhiteSpace(task.File1Path) && File.Exists(task.File1Path))
            {
                FilePickerService.RevealInExplorer(task.File1Path);
            }
        }

        // Row 2（视频）专属在文件夹中查看
        private void ShowInFolder_File2_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: RepairTask task }) return;

            string? targetPath = task.File2Entry?.PublishedOutputs.FirstOrDefault()?.Path
                ?? task.File2Path;

            if (string.IsNullOrWhiteSpace(targetPath)) return;

            if (File.Exists(targetPath) || Directory.Exists(targetPath))
            {
                FilePickerService.RevealInExplorer(targetPath);
            }
            else if (!string.IsNullOrWhiteSpace(task.File2Path) && File.Exists(task.File2Path))
            {
                FilePickerService.RevealInExplorer(task.File2Path);
            }
        }

        private void Flyout_OpenOutputFolder_Click(object sender, RoutedEventArgs e)
        {
            OpenTargetOutputFolder();
        }

        private void Flyout_OpenOutputFolder_File1_Click(object sender, RoutedEventArgs e)
        {
            OpenTargetOutputFolder();
        }

        private void Flyout_OpenOutputFolder_File2_Click(object sender, RoutedEventArgs e)
        {
            OpenTargetOutputFolder();
        }

        private void OpenTargetOutputFolder()
        {
            try
            {
                string targetDir = ViewModel.GetEffectiveOutputDirectory();
                if (string.IsNullOrWhiteSpace(targetDir)) return;

                if (!Directory.Exists(targetDir))
                {
                    Directory.CreateDirectory(targetDir);
                }

                FilePickerService.OpenFolderInExplorer(targetDir);
            }
            catch (Exception ex)
            {
                LogService.Repair($"OpenTargetOutputFolder error: {ex.Message}", LogLevel.Error, ex);
            }
        }

        private void Flyout_ShowSourceInFolder_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: RepairTask task }) return;

            string? sourcePath = task.File1Path ?? task.File2Path;
            if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
            {
                FilePickerService.RevealInExplorer(sourcePath);
            }
        }

        private void Flyout_ShowSourceInFolder_File1_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: RepairTask task }) return;

            string? sourcePath = task.File1Path;
            if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
            {
                FilePickerService.RevealInExplorer(sourcePath);
            }
        }

        private void Flyout_ShowSourceInFolder_File2_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: RepairTask task }) return;

            string? sourcePath = task.File2Path;
            if (!string.IsNullOrWhiteSpace(sourcePath) && File.Exists(sourcePath))
            {
                FilePickerService.RevealInExplorer(sourcePath);
            }
        }

        private void Flyout_Preview_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: RepairTask task }) return;
            PreviewTask(task);
        }

        private void Flyout_DeleteTask_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: RepairTask task }) return;
            ViewModel.RemoveTask(task);
        }

        private void Flyout_DeleteTask_File1_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: RepairTask task }) return;
            ViewModel.RemoveTaskSlot(task, 1);
        }

        private void Flyout_DeleteTask_File2_Click(object sender, RoutedEventArgs e)
        {
            if (sender is not FrameworkElement { Tag: RepairTask task }) return;
            ViewModel.RemoveTaskSlot(task, 2);
        }

        // ── 诊断与状态详情提示（严格分离，各司其职） ─────────────────

        // 提取已隔离的具体白话原因
        private static string ExtractIsolationReason(RepairFileEntry? entry, string? details)
        {
            if (!string.IsNullOrWhiteSpace(details))
            {
                var lines = details.Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries);

                // 1. 优先提取明确的“未完成／待确认原因：”行
                foreach (var rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (line.Contains("未完成／待确认原因：") || line.Contains("未完成/待确认原因："))
                    {
                        int colonIdx = line.IndexOf('：');
                        if (colonIdx >= 0 && colonIdx + 1 < line.Length)
                        {
                            string reason = line[(colonIdx + 1)..].Trim();
                            if (!string.IsNullOrEmpty(reason)) return reason;
                        }
                    }
                }

                // 2. 匹配包含隔离原因关键词的行
                foreach (var rawLine in lines)
                {
                    string line = rawLine.Trim();
                    if (line.Contains("隔离") || line.Contains("保留证明") || line.Contains("未获完整保留") ||
                        line.Contains("配对 MOV") || line.Contains("未完成保留验证") || line.Contains("保留原方向") ||
                        line.Contains("保留原视频") || line.Contains("保留原照片"))
                    {
                        int colonIdx = line.IndexOf('：');
                        if (colonIdx >= 0 && colonIdx + 1 < line.Length && (line.StartsWith("结果") || line.StartsWith("配对结果") || line.StartsWith("未完成")))
                        {
                            string sub = line[(colonIdx + 1)..].Trim();
                            if (!string.IsNullOrEmpty(sub)) return sub;
                        }
                        return line;
                    }
                }
            }

            if (entry?.ExecutionOutcomeKey is { } key && key.StartsWith("warning", StringComparison.Ordinal))
            {
                string? outcomeStr = ResourceService.GetString("Repair_Outcome_" + key);
                if (!string.IsNullOrWhiteSpace(outcomeStr)) return outcomeStr;
            }

            return ResourceService.GetString("RepairTip_DefaultIsolationReason")
                ?? "来源元数据保留验证未完全通过，为保护原文件安全已导出至隔离目录。";
        }

        // 专属于“诊断结果”列的提示框：仅展示文件诊断发现了什么问题
        private static string FormatDiagnosisTip(RepairFileEntry? entry, ProcessStatus status, string rawDetails)
        {
            if (entry == null) return string.Empty;

            string prefixDiag = ResourceService.GetString("RepairTip_DiagnosisPrefix") ?? "诊断：";
            string reasonPrefix = ResourceService.GetString("RepairTip_ReasonPrefix") ?? "原因：";

            var ar = entry.AnalysisResult;
            if (ar != null)
            {
                if (ar.IssueType == RepairIssueType.Perfect)
                {
                    return prefixDiag + (ResourceService.GetString("RepairTip_Perfect") ?? "检查正常，文件无需修复。");
                }
                if (ar.IssueType is RepairIssueType.NonApple or RepairIssueType.OtherDevice)
                {
                    return prefixDiag + (ResourceService.GetString("RepairTip_NonApple") ?? "非苹果或三星动态照片，跳过修复。");
                }
                if (ar.IssueType == RepairIssueType.Error || entry.IsDiagnosisError)
                {
                    return reasonPrefix + LocalizeErrorMessage(!string.IsNullOrWhiteSpace(rawDetails) ? rawDetails : ar.IssueDescription);
                }

                var reasons = new List<string>();
                if (ar.NeedsImageOrientationRepair && ar.RotationAngle != 0)
                    reasons.Add(string.Format(ResourceService.GetString("RepairTip_Rotation") ?? "照片方向包含 {0}° 旋转", ar.RotationAngle));
                if (ar.HasThumbnail)
                    reasons.Add(ResourceService.GetString("RepairTip_Thumbnail") ?? "包含多余内嵌缩略图，可精简体积");
                if (ar.HasHeicOrientationConflict)
                    reasons.Add(ResourceService.GetString("RepairTip_Conflict") ?? "HEIF 方向信息存在冲突");
                if (ar.VideoRotationAngle != 0)
                    reasons.Add(string.Format(ResourceService.GetString("RepairTip_VideoRotation") ?? "实况视频包含 {0}° 旋转", ar.VideoRotationAngle));
                if (ar.VideoTrackTransform == "flip_vertical")
                    reasons.Add(ResourceService.GetString("Tag_FlipVertical") ?? "视频上下翻转");
                else if (ar.VideoTrackTransform == "flip_horizontal")
                    reasons.Add(ResourceService.GetString("Tag_FlipHorizontal") ?? "视频左右翻转");
                else if (ar.NeedsEmbeddedVideoTransform && ar.VideoRotationAngle == 0)
                    reasons.Add(ResourceService.GetString("Tag_VideoOrientationMirror") ?? "视频镜像标签");

                if (reasons.Count > 0)
                {
                    string separator = prefixDiag.EndsWith(' ') ? "; " : "；";
                    string endPeriod = prefixDiag.EndsWith(' ') ? "." : "。";
                    return prefixDiag + string.Join(separator, reasons) + endPeriod;
                }

                return prefixDiag + (ResourceService.GetString("RepairTip_Perfect") ?? "检查正常，文件无需修复。");
            }

            if (entry.IsDiagnosisError || status == ProcessStatus.Failed)
            {
                return reasonPrefix + LocalizeErrorMessage(rawDetails);
            }

            return prefixDiag + (ResourceService.GetString("RepairTip_Perfect") ?? "检查正常，文件无需修复。");
        }

        // 专属于“状态”列的提示框：仅展示当前状态与执行结果（为什么隔离/为什么失败/处理结果等）
        private static string FormatStatusTip(RepairFileEntry? entry, ProcessStatus status, string rawDetails)
        {
            if (entry == null) return string.Empty;

            var lines = new List<string>();
            string statusPrefix = ResourceService.GetString("RepairTip_StatusPrefix") ?? "状态：";
            string reasonPrefix = ResourceService.GetString("RepairTip_ReasonPrefix") ?? "原因：";
            string outcomePrefix = ResourceService.GetString("RepairTip_OutcomePrefix") ?? "结果：";

            // 1. 已取消
            if (entry.IsCancelled)
            {
                lines.Add(statusPrefix + (ResourceService.GetString("Repair_Status_Cancelled") ?? "已取消。"));
                lines.Add(reasonPrefix + (ResourceService.GetString("RepairTip_CancelledDesc") ?? "用户已手动停止或取消修复任务。"));
                return string.Join(Environment.NewLine, lines);
            }

            // 2. 正在修复中
            if (status == ProcessStatus.Processing)
            {
                lines.Add(statusPrefix + (ResourceService.GetString("Repair_Status_Processing") ?? "正在修复中..."));
                return string.Join(Environment.NewLine, lines);
            }

            // 3. 已隔离（明确指出为什么隔离）
            bool isIsolated = status == ProcessStatus.Warning ||
                              entry.ExecutionStatus is "warning" or "isolated" ||
                              (entry.ExecutionStatus?.StartsWith("isolated", StringComparison.Ordinal) == true) ||
                              entry.PublishedOutputs.Any(x => x.Kind.StartsWith("isolated", StringComparison.Ordinal));

            if (isIsolated)
            {
                lines.Add(statusPrefix + (ResourceService.GetString("Repair_Status_Isolated") ?? "已隔离。"));
                string reason = ExtractIsolationReason(entry, rawDetails);
                lines.Add(reasonPrefix + reason);
                return string.Join(Environment.NewLine, lines);
            }

            // 4. 修复失败（明确指出为什么失败）
            if (status == ProcessStatus.Failed || entry.ExecutionStatus == "failed" || entry.IsDiagnosisError)
            {
                lines.Add(statusPrefix + (ResourceService.GetString("Repair_Status_Failed") ?? "修复失败。"));
                string errorMsg = LocalizeErrorMessage(rawDetails);
                lines.Add(reasonPrefix + errorMsg);
                return string.Join(Environment.NewLine, lines);
            }

            // 5. 修复完成
            if (entry.ExecutionStatus == "success" || (status == ProcessStatus.Success && entry.NeedsRepair))
            {
                lines.Add(statusPrefix + (ResourceService.GetString("Repair_Status_Completed") ?? "修复完成。"));
                lines.Add(outcomePrefix + (ResourceService.GetString("RepairTip_CompletedDesc") ?? "已按修复规则成功修复并导出，原文件保持不变。"));
                return string.Join(Environment.NewLine, lines);
            }

            // 6. 无需修复
            if (entry.ExecutionStatus is "healthy" or "not_needed" || !entry.NeedsRepair || entry.AnalysisResult?.IssueType == RepairIssueType.Perfect)
            {
                lines.Add(statusPrefix + (ResourceService.GetString("Repair_Status_NoRepairNeeded") ?? "无需修复。"));
                lines.Add(outcomePrefix + (ResourceService.GetString("RepairTip_NoRepairNeededDesc") ?? "文件检查正常，不需要执行修复。"));
                return string.Join(Environment.NewLine, lines);
            }

            // 7. 已跳过
            if (entry.ExecutionStatus == "skipped")
            {
                lines.Add(statusPrefix + (ResourceService.GetString("RepairTip_Status_Skipped") ?? "已跳过，原文件保持不变。"));
                return string.Join(Environment.NewLine, lines);
            }

            // 8. 等待修复
            if (entry.NeedsRepair && status == ProcessStatus.Pending)
            {
                lines.Add(statusPrefix + (ResourceService.GetString("Repair_Status_Waiting") ?? "等待修复。"));
                lines.Add(outcomePrefix + (ResourceService.GetString("RepairTip_WaitingDesc") ?? "点击“开始修复”后将按设定的规则进行修复。"));
                return string.Join(Environment.NewLine, lines);
            }

            // 默认回退
            lines.Add(statusPrefix + (ResourceService.GetString("Repair_Status_Waiting") ?? "等待修复。"));
            return string.Join(Environment.NewLine, lines);
        }

        // 兼容旧调用的全量包装
        private static string FormatRepairTipDetails(RepairFileEntry? entry, ProcessStatus status, string rawDetails)
        {
            return FormatDiagnosisTip(entry, status, rawDetails);
        }

        private static string LocalizeErrorMessage(string? error)
        {
            if (string.IsNullOrWhiteSpace(error))
                return ResourceService.GetString("RepairTip_Error") ?? "文件读取或结构校验失败，无法安全修复。";

            if (error.Contains("being used by another process", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("Sharing violation", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("占用", StringComparison.OrdinalIgnoreCase))
            {
                return ResourceService.GetString("RepairTip_FileInUse") ?? "文件正被其他程序占用，请关闭相关程序后重试。";
            }
            if (error.Contains("Access to the path", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("UnauthorizedAccess", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("Access is denied", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("拒绝访问", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("权限", StringComparison.OrdinalIgnoreCase))
            {
                return ResourceService.GetString("RepairTip_AccessDenied") ?? "访问被拒绝，请检查文件读写权限。";
            }
            if (error.Contains("space on the disk", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("空间不足", StringComparison.OrdinalIgnoreCase))
            {
                return ResourceService.GetString("RepairTip_DiskFull") ?? "磁盘空间不足，无法保存修复后的文件。";
            }
            if (error.Contains("FileNotFound", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("Could not find file", StringComparison.OrdinalIgnoreCase) ||
                error.Contains("未找到", StringComparison.OrdinalIgnoreCase))
            {
                return ResourceService.GetString("RepairTip_FileNotFound") ?? "未找到源文件，请确认文件是否存在。";
            }

            return ResourceService.GetString("RepairTip_Error") ?? "文件读取或结构校验失败，无法安全修复。";
        }

        private static (string? prefix, string content) SplitLabelAndContent(string line)
        {
            if (string.IsNullOrWhiteSpace(line)) return (null, line);

            // 优先匹配中文全角冒号 "："
            int zhIndex = line.IndexOf('：');
            if (zhIndex > 0 && zhIndex <= 10)
            {
                return (line.Substring(0, zhIndex + 1), line.Substring(zhIndex + 1).TrimStart());
            }

            // 匹配英文冒号加空格 ": "
            int enIndex = line.IndexOf(": ", System.StringComparison.Ordinal);
            if (enIndex > 0 && enIndex <= 15)
            {
                return (line.Substring(0, enIndex + 2), line.Substring(enIndex + 2).TrimStart());
            }

            return (null, line);
        }

        private void SetErrorDetailContent(string tipText)
        {
            ErrorDetailPanel.Children.Clear();
            if (string.IsNullOrWhiteSpace(tipText)) return;

            var lines = tipText.Split(new[] { "\r\n", "\r", "\n" }, System.StringSplitOptions.RemoveEmptyEntries);
            var grid = new Grid();
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            int rowIndex = 0;
            foreach (var rawLine in lines)
            {
                string line = rawLine.Trim();
                if (string.IsNullOrEmpty(line)) continue;

                grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
                var (prefix, content) = SplitLabelAndContent(line);

                if (!string.IsNullOrEmpty(prefix))
                {
                    var lbl = new TextBlock
                    {
                        Text = prefix,
                        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                        Foreground = (Microsoft.UI.Xaml.Media.Brush)Application.Current.Resources["TextFillColorSecondaryBrush"],
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 2, 4, 2)
                    };
                    var val = new TextBlock
                    {
                        Text = content,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    Grid.SetRow(lbl, rowIndex);
                    Grid.SetColumn(lbl, 0);
                    Grid.SetRow(val, rowIndex);
                    Grid.SetColumn(val, 1);
                    grid.Children.Add(lbl);
                    grid.Children.Add(val);
                }
                else
                {
                    var tb = new TextBlock
                    {
                        Text = line,
                        TextWrapping = TextWrapping.Wrap,
                        IsTextSelectionEnabled = true,
                        VerticalAlignment = VerticalAlignment.Top,
                        Margin = new Thickness(0, 2, 0, 2)
                    };
                    Grid.SetRow(tb, rowIndex);
                    Grid.SetColumn(tb, 0);
                    Grid.SetColumnSpan(tb, 2);
                    grid.Children.Add(tb);
                }

                rowIndex++;
            }

            ErrorDetailPanel.Children.Add(grid);
        }

        // 点击状态文本显示状态详情 TeachingTip（专属于状态与执行结果，明确为什么隔离/失败等）
        private void StatusTextBlock_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not RepairTask task) return;

            bool isFile2 = element.Tag is string tag && tag == "2";
            ProcessStatus status = isFile2 ? task.File2Status : task.File1Status;
            string details = isFile2 ? task.File2Details : task.File1Details;
            var entry = isFile2 ? task.File2Entry : task.File1Entry;
            string tipText = FormatStatusTip(entry, status, details);
            if (string.IsNullOrWhiteSpace(tipText)) return;
            SetErrorDetailContent(tipText);
            ErrorDetailTip.ToggleAt(element);
        }

        // 点击问题描述文本显示诊断详情 TeachingTip（专属于诊断，仅展示检查发现的问题）
        private void IssueDescription_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element) return;
            if (element.DataContext is not RepairTask task) return;

            bool isFile2 = element.Tag is string tag && tag == "2";
            var entry = isFile2 ? task.File2Entry : task.File1Entry;
            ProcessStatus status = isFile2 ? task.File2Status : task.File1Status;
            string details = isFile2 ? task.File2Details : task.File1Details;
            string tipText = FormatDiagnosisTip(entry, status, details);
            if (string.IsNullOrWhiteSpace(tipText)) return;
            SetErrorDetailContent(tipText);
            ErrorDetailTip.ToggleAt(element);
        }

        // 加载视口中可见的视频缩略图
        private void LoadVisibleVideoThumbnails() =>
            LoadVisibleThumbnailsForCurrentViewport(maxPerBatch: 6, staggerMs: 50);

        // 从当前视口中找到尚未加载缩略图的 RepairTask，分批异步加载
        private void LoadVisibleThumbnailsForCurrentViewport(int maxPerBatch = 4, int staggerMs = 0)
        {
            if (!ThumbnailService.IsLoadingEnabled) return;
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
            if (!ThumbnailService.IsLoadingEnabled) return;
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
                EmptyQueueHint.Visibility = Visibility.Collapsed;
            }

            e.Handled = true;
        }

        private void TaskList_DragLeave(object sender, DragEventArgs e)
        {
            DragOverlay.Visibility = Visibility.Collapsed;
            EmptyQueueHint.ClearValue(UIElement.VisibilityProperty);
            _isDropAllFolders = false;
            e.Handled = true;
        }

        /// <summary>拖拽释放：提取文件夹路径 → 设置 ViewModel.InputDirectory 触发自动扫描</summary>
        private async void TaskList_Drop(object sender, DragEventArgs e)
        {
            try
            {
                DragOverlay.Visibility = Visibility.Collapsed;
                EmptyQueueHint.ClearValue(UIElement.VisibilityProperty);

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
