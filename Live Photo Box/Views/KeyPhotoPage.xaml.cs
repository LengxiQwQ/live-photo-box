/*
 * KeyPhotoPage.xaml.cs
 *
 * 实况照片主图更换页面的代码后置。
 * 处理 UI 事件 + 时间轴滚轮左右滚动 + 自定义卡片交互（悬停/选中/按下）。
 *
 * 卡片视觉由 ItemTemplate 内的 Border 直接承载，ListViewItem 模板已剥离为裸 ContentPresenter，
 * 彻底消除 ListViewItem 内置的 PointerDownThemeAnimation（下压偏移动画）。
 */

using LivePhotoBox.Helpers;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace LivePhotoBox.Views
{
    public sealed partial class KeyPhotoPage : Page
    {
        public KeyPhotoViewModel ViewModel => AppViewModel.Instance.KeyPhoto;

        // ── 文件列表卡片交互状态 ──
        private Border? _hoveredCard;
        private Border? _pressedCard;

        // ── 时间轴卡片交互状态 ──
        private Border? _hoveredTimelineCard;
        private Border? _pressedTimelineCard;

        // ── 缓存画刷（系统换强调色时重建）──
        private SolidColorBrush _selectedBg = null!;
        private SolidColorBrush _selectedHoverBg = null!;
        private SolidColorBrush _selectedPressedBg = null!;
        private SolidColorBrush _selectedBorder = null!;
        private SolidColorBrush _hoverBg = null!;
        private SolidColorBrush _pressedBg = null!;
        private readonly SolidColorBrush _transparent = new(Microsoft.UI.Colors.Transparent);

        // 监听系统强调色变化
        private readonly Windows.UI.ViewManagement.UISettings _uiSettings = new();

        // ── 左侧面板折叠状态 ──
        private bool _isLeftPanelCollapsed;
        private const double LeftPanelExpandedWidth = 320;
        // 92px = ListView margin(6) + padding(4) + Border padding(20) + Thumb(52) + Spacing(10)
        // 恰好完整露出缩略图，文字列空间归零自然不可见。
        private const double LeftPanelCollapsedWidth = 100;

        // ── 预览最大化状态 ──
        private bool _isPreviewMaximized;

        // ── 时间轴居中滚动取消令牌 ──
        private CancellationTokenSource? _scrollCts;

        public KeyPhotoPage()
        {
            InitializeComponent();

            RebuildAllBrushes();

            // 系统换强调色时实时更新
            _uiSettings.ColorValuesChanged += OnSystemColorValuesChanged;
            Unloaded += (s, e) =>
            {
                _uiSettings.ColorValuesChanged -= OnSystemColorValuesChanged;
                ViewModel.RequestScrollToFrame -= OnRequestScrollToFrame;
                ViewModel.Cleanup();
            };

            // 时间轴照片帧自动滚动
            ViewModel.RequestScrollToFrame += OnRequestScrollToFrame;

            Loaded += KeyPhotoPage_Loaded;
            FileItemListView.ContainerContentChanging += OnContainerContentChanging;
            TimelineListView.ContainerContentChanging += OnTimelineContainerContentChanging;
        }

        /// <summary>重建所有强调色+悬停/按下画刷（系统换主题时调用）</summary>
        private void RebuildAllBrushes()
        {
            var accent = (Windows.UI.Color)Application.Current.Resources["SystemAccentColor"];
            _selectedBg = new SolidColorBrush(accent) { Opacity = 0.15 };
            _selectedHoverBg = new SolidColorBrush(accent) { Opacity = 0.25 };
            _selectedPressedBg = new SolidColorBrush(accent) { Opacity = 0.35 };
            _selectedBorder = new SolidColorBrush(accent) { Opacity = 0.88 };

            var hoverBrush = (SolidColorBrush)Application.Current.Resources["SystemControlHighlightListLowBrush"];
            _hoverBg = new SolidColorBrush(hoverBrush.Color) { Opacity = hoverBrush.Opacity };
            var pressedBrush = (SolidColorBrush)Application.Current.Resources["SystemControlHighlightListMediumBrush"];
            _pressedBg = new SolidColorBrush(pressedBrush.Color) { Opacity = pressedBrush.Opacity };
        }

        /// <summary>系统强调色变化 → 重建画刷 + 刷新所有卡片视觉</summary>
        private void OnSystemColorValuesChanged(Windows.UI.ViewManagement.UISettings sender, object args)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                RebuildAllBrushes();
                RefreshAllCardVisuals();
            });
        }

        /// <summary>遍历所有可见卡片容器，刷新其视觉状态</summary>
        private void RefreshAllCardVisuals()
        {
            foreach (var item in FileItemListView.Items)
            {
                if (FileItemListView.ContainerFromItem(item) is ListViewItem container)
                {
                    var card = FindVisualChild<Border>(container);
                    if (card != null)
                        UpdateCardVisual(card, IsCardSelected(card),
                            hovered: _hoveredCard == card, pressed: _pressedCard == card);
                }
            }
        }

        // ════════════════════════════════════════════════════════════
        //  左侧面板折叠 / 展开
        // ════════════════════════════════════════════════════════════

        private void CollapsePanelButton_Click(object sender, RoutedEventArgs e)
        {
            _isLeftPanelCollapsed = !_isLeftPanelCollapsed;

            // 切换列宽
            LeftPanelColumn.Width = new GridLength(
                _isLeftPanelCollapsed ? LeftPanelCollapsedWidth : LeftPanelExpandedWidth);

            // 隐藏/显示控件区
            var collapsed = _isLeftPanelCollapsed ? Visibility.Collapsed : Visibility.Visible;
            PanelControlsArea.Visibility = collapsed;
            FileCountText.Visibility = collapsed;
            FilterComboBox.Visibility = collapsed;
            PanelTitleText.Visibility = _isLeftPanelCollapsed
                ? Visibility.Collapsed : Visibility.Visible;

            // 切换箭头图标
            CollapseButtonIcon.Glyph = _isLeftPanelCollapsed ? "" : "";
            ToolTipService.SetToolTip(CollapsePanelButton,
                _isLeftPanelCollapsed ? "展开面板" : "折叠面板");

            // 折叠时缩小 ListView 右侧留白 + 隐藏文字面板
            FileItemListView.Padding = new Thickness(
                0, 0, _isLeftPanelCollapsed ? 4 : 14, 0);
            var textVis = _isLeftPanelCollapsed ? Visibility.Collapsed : Visibility.Visible;
            foreach (var item in FileItemListView.Items)
            {
                if (FileItemListView.ContainerFromItem(item) is ListViewItem c)
                {
                    var card = FindVisualChild<Border>(c);
                    if (card != null)
                        SetTextPanelVisible(card, textVis);
                }
            }
        }

        // ════════════════════════════════════════════════════════════
        //  预览最大化：隐藏所有面板，只保留预览图
        // ════════════════════════════════════════════════════════════

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            _isPreviewMaximized = !_isPreviewMaximized;

            if (_isPreviewMaximized)
            {
                TopBarGrid.Visibility = Visibility.Collapsed;
                LeftPanelColumn.Width = new GridLength(0);
                PanelSpacerColumn.Width = new GridLength(0);
                TimelinePanel.Visibility = Visibility.Collapsed;
                InfoPanel.Visibility = Visibility.Collapsed;
                MainContentGrid.Padding = new Thickness(0);
                PreviewBorder.CornerRadius = new CornerRadius(0);
                PreviewBorder.Margin = new Thickness(0);
                MaximizeButtonIcon.Glyph = "";
                ToolTipService.SetToolTip(MaximizeButton, "还原");
            }
            else
            {
                TopBarGrid.Visibility = Visibility.Visible;
                LeftPanelColumn.Width = new GridLength(
                    _isLeftPanelCollapsed ? LeftPanelCollapsedWidth : LeftPanelExpandedWidth);
                PanelSpacerColumn.Width = new GridLength(8);
                TimelinePanel.Visibility = Visibility.Visible;
                InfoPanel.Visibility = Visibility.Visible;
                MainContentGrid.Padding = new Thickness(8, 0, 8, 6);
                PreviewBorder.CornerRadius = new CornerRadius(10);
                PreviewBorder.Margin = new Thickness(0, 0, 0, 4);
                MaximizeButtonIcon.Glyph = "";
                ToolTipService.SetToolTip(MaximizeButton, "最大化预览");
            }
        }

        private void KeyPhotoPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= KeyPhotoPage_Loaded;

            // 时间轴 ItemsSource 已通过 XAML x:Bind 绑定到 ViewModel.TimelineFrames，
            // 不再需要手动创建 dummy 数据

            LivePhotoBox.Helpers.ComboBoxHelper.AutoFitWidth(SortComboBox);
            LivePhotoBox.Helpers.ComboBoxHelper.AutoFitWidth(FilterComboBox);

            DispatcherQueue.TryEnqueue(() => ForceScrollBarsAlwaysThick());
        }

        /// <summary>
        /// ViewModel 通知时间轴选中 key photo 帧并居中滚动。
        /// 仅在程序化选中时触发（用户手动点击不滚动）。
        ///
        /// 核心问题：ViewModel 设置 SelectedTimelineFrame 时，ListView 的 Items
        /// 刚被刷新，容器尚未完成布局，ScrollViewer 的 ExtentWidth 仍为 0。
        /// 之前的双 DispatcherQueue 模式在首帧 ViewportWidth≤0 时直接放弃，导致
        /// 每次加载都静默跳过滚动。
        ///
        /// 改进：带重试次数的延迟滚动，最多尝试 5 次（每次约一帧），确保布局完成后
        /// 能正确居中。同时显式设置 ListView.SelectedItem 绕过 x:Bind TwoWay 时序问题。
        /// </summary>
        private void OnRequestScrollToFrame(TimelineFrame frame)
        {
            // 取消上一次在途的 scroll（快速切换文件时的竞态）
            _scrollCts?.Cancel();
            _scrollCts?.Dispose();
            _scrollCts = new CancellationTokenSource();
            var ct = _scrollCts.Token;

            // 显式设置 ListView 选中项（绕过 x:Bind TwoWay 时序问题）
            // 此时已在 UI 线程，直接同步设置即可
            TimelineListView.SelectedItem = frame;

            // 延迟滚动：每 120ms 重试一次，最多 5 次，直到 ScrollViewer 布局完成
            const int maxRetries = 5;
            const int delayMs = 120;
            ScheduleScrollRetry(frame, ct, maxRetries, delayMs);
        }

        private void ScheduleScrollRetry(TimelineFrame frame, CancellationToken ct,
            int remainingRetries, int delayMs)
        {
            if (ct.IsCancellationRequested || remainingRetries <= 0) return;

            DispatcherQueue.TryEnqueue(() =>
            {
                if (ct.IsCancellationRequested) return;

                try
                {
                    var sv = FindVisualChild<ScrollViewer>(TimelineListView);
                    if (sv == null || sv.ViewportWidth <= 0 || sv.ExtentWidth <= 0)
                    {
                        // 布局未就绪，延迟后重试
                        _ = RetryAfterDelay(frame, ct, remainingRetries - 1, delayMs);
                        return;
                    }

                    int index = ViewModel.TimelineFrames.IndexOf(frame);
                    if (index < 0) return;

                    const double itemStep = 56;
                    double totalWidth = ViewModel.TimelineFrames.Count * itemStep;
                    double maxOffset = totalWidth - sv.ViewportWidth;
                    if (maxOffset <= 0) return;

                    double targetOffset = index * itemStep + 28.0 - (sv.ViewportWidth / 2.0);
                    targetOffset = Math.Max(0, Math.Min(targetOffset, maxOffset));

                    LogService.Debug(
                        $"Timeline scroll: idx={index}, total={ViewModel.TimelineFrames.Count}, " +
                        $"vw={sv.ViewportWidth:F0}, extent={sv.ExtentWidth:F0}, " +
                        $"offset={targetOffset:F0}, max={maxOffset:F0}",
                        LogSource.UI);

                    sv.ChangeView(targetOffset, null, null);

                    // ChangeView 后容器需要约一帧才能被 realized，
                    // 延迟后找到容器并显式刷新选中框视觉
                    _ = RefreshSelectionVisualAfterScroll(frame, ct, 3);
                }
                catch (Exception ex)
                {
                    LogService.Debug($"Timeline scroll failed: {ex.Message}", LogSource.UI);
                }
            });
        }

        /// <summary>
        /// 滚动后延迟刷新选中视觉。ChangeView 后 WinUI ListView 虚拟化
        /// 需要约一帧来创建容器。每 50ms 通过 DispatcherQueue 检查一次，
        /// 找到容器后立即刷新选中框。
        /// </summary>
        private async Task RefreshSelectionVisualAfterScroll(
            TimelineFrame frame, CancellationToken ct, int remaining)
        {
            for (int i = 0; i < remaining; i++)
            {
                try { await Task.Delay(50, ct); }
                catch (TaskCanceledException) { return; }
                if (ct.IsCancellationRequested) return;

                // 必须在 UI 线程上访问 ContainerFromItem
                bool found = false;
                DispatcherQueue.TryEnqueue(() =>
                {
                    try
                    {
                        if (TimelineListView.ContainerFromItem(frame) is ListViewItem container)
                        {
                            var card = FindVisualChild<Border>(container);
                            if (card != null)
                            {
                                UpdateTimelineCardVisual(card, isSelected: true,
                                    hovered: false, pressed: false);
                                found = true;
                            }
                        }
                    }
                    catch { }
                });

                // 给 DispatcherQueue 回调一点时间执行
                try { await Task.Delay(20, ct); }
                catch (TaskCanceledException) { return; }

                if (found) break;
            }
        }

        private async Task RetryAfterDelay(TimelineFrame frame, CancellationToken ct,
            int remainingRetries, int delayMs)
        {
            try
            {
                await Task.Delay(delayMs, ct);
                ScheduleScrollRetry(frame, ct, remainingRetries, delayMs);
            }
            catch (TaskCanceledException) { }
        }

        // ════════════════════════════════════════════════════════════
        //  时间轴滚轮
        // ════════════════════════════════════════════════════════════

        // ════════════════════════════════════════════════════════════
        //  时间轴：ListView 加载 → 找到内部 ScrollViewer → 挂滚轮
        // ════════════════════════════════════════════════════════════

        private void TimelineListView_Loaded(object sender, RoutedEventArgs e)
        {
            var sv = FindVisualChild<ScrollViewer>(TimelineListView);
            if (sv != null)
                sv.PointerWheelChanged += TimelineScrollViewer_PointerWheelChanged;
        }

        private void TimelineScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset - delta);
                e.Handled = true;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  时间轴卡片交互（悬停 / 按下 / 选中）
        //  与左侧文件列表使用完全相同的强调色画刷 — 只改颜色不改尺寸
        // ════════════════════════════════════════════════════════════

        private bool IsTimelineCardSelected(Border card)
        {
            return TimelineListView.SelectedItem != null
                && card.DataContext == TimelineListView.SelectedItem;
        }

        private void UpdateTimelineCardVisual(Border card, bool isSelected, bool hovered, bool pressed)
        {
            card.BorderThickness = new Thickness(2);
            card.BorderBrush = isSelected ? _selectedBorder : _transparent;

            if (isSelected)
            {
                // 选中态只显强调色边框，不填底色（缩略图位置留给真实图片）
                card.Background = _transparent;
            }
            else
            {
                if (pressed)       card.Background = _pressedBg;
                else if (hovered)  card.Background = _hoverBg;
                else               card.Background = _transparent;
            }
        }

        // ── 时间轴容器生命周期（与左侧文件列表相同的 ContainerContentChanging 模式）──

        private void OnTimelineContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                if (args.ItemContainer is ListViewItem container)
                {
                    var card = FindVisualChild<Border>(container);
                    if (card != null)
                    {
                        card.PointerEntered -= TimelineCard_PointerEntered;
                        card.PointerExited -= TimelineCard_PointerExited;
                        card.PointerPressed -= TimelineCard_PointerPressed;
                        card.PointerReleased -= TimelineCard_PointerReleased;
                        if (_hoveredTimelineCard == card) _hoveredTimelineCard = null;
                        if (_pressedTimelineCard == card) _pressedTimelineCard = null;
                    }
                }
                return;
            }

            if (args.ItemContainer is ListViewItem lvi)
            {
                lvi.Loaded += OnTimelineContainerLoaded_WireCardEvents;
            }
        }

        private void OnTimelineContainerLoaded_WireCardEvents(object sender, RoutedEventArgs e)
        {
            if (sender is ListViewItem container)
            {
                container.Loaded -= OnTimelineContainerLoaded_WireCardEvents;
                var card = FindVisualChild<Border>(container);
                if (card != null)
                {
                    card.PointerEntered += TimelineCard_PointerEntered;
                    card.PointerExited += TimelineCard_PointerExited;
                    card.PointerPressed += TimelineCard_PointerPressed;
                    card.PointerReleased += TimelineCard_PointerReleased;

                    if (IsTimelineCardSelected(card))
                        UpdateTimelineCardVisual(card, isSelected: true, hovered: false, pressed: false);
                }
            }
        }

        private void TimelineCard_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card)
            {
                _hoveredTimelineCard = card;
                UpdateTimelineCardVisual(card, IsTimelineCardSelected(card),
                    hovered: true, pressed: _pressedTimelineCard == card);
            }
        }

        private void TimelineCard_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card)
            {
                if (_hoveredTimelineCard == card) _hoveredTimelineCard = null;
                UpdateTimelineCardVisual(card, IsTimelineCardSelected(card),
                    hovered: false, pressed: false);
            }
        }

        private void TimelineCard_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card)
            {
                _pressedTimelineCard = card;
                UpdateTimelineCardVisual(card, IsTimelineCardSelected(card),
                    hovered: _hoveredTimelineCard == card, pressed: true);
            }
        }

        private void TimelineCard_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card)
            {
                if (_pressedTimelineCard == card) _pressedTimelineCard = null;
                UpdateTimelineCardVisual(card, IsTimelineCardSelected(card),
                    hovered: _hoveredTimelineCard == card, pressed: false);
            }
        }

        private void TimelineListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            foreach (var item in e.RemovedItems)
                RefreshTimelineCardVisual(item, isSelected: false);
            foreach (var item in e.AddedItems)
                RefreshTimelineCardVisual(item, isSelected: true);
        }

        private void RefreshTimelineCardVisual(object? item, bool isSelected)
        {
            if (item == null) return;
            if (TimelineListView.ContainerFromItem(item) is ListViewItem container)
            {
                var card = FindVisualChild<Border>(container);
                if (card != null)
                    UpdateTimelineCardVisual(card, isSelected,
                        hovered: _hoveredTimelineCard == card,
                        pressed: _pressedTimelineCard == card);
            }
        }

        // ════════════════════════════════════════════════════════════
        //  文件夹浏览 & 路径输入
        // ════════════════════════════════════════════════════════════

        /// <summary>用户点击了浏览按钮 → 抑制本次 LostFocus 扫描</summary>
        private bool _suppressLostFocusScan;

        /// <summary>浏览按钮按下时设标记（早于 LostFocus 触发），防止 LostFocus 误扫描旧路径</summary>
        private void BrowseFolder_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _suppressLostFocusScan = true;
        }

        /// <summary>浏览按钮：弹出文件夹选择器，选中后填充路径并触发扫描</summary>
        private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null)
            {
                ViewModel.CurrentDirectory = folder.Path;
                // 浏览按钮选择的路径直接触发扫描（不依赖 LostFocus）
                ViewModel.TriggerScan();
            }
            // 重置标记
            _suppressLostFocusScan = false;
        }

        /// <summary>点击照片信息行 → 文件资源管理器中定位照片</summary>
        private void LocatePhotoFile_Click(object sender, RoutedEventArgs e)
        {
            var path = ViewModel.SelectedFilePath;
            if (!string.IsNullOrEmpty(path))
            {
                try { FilePickerService.RevealInExplorer(path); }
                catch (Exception ex) { LogService.Debug($"KeyPhoto reveal photo failed: {ex.Message}", LogSource.UI); }
            }
        }

        /// <summary>点击视频信息行 → 文件资源管理器中定位视频。
        /// 单文件实况照片（JPEG 内嵌 / HEIC 视频轨）直接定位照片本身，
        /// 双文件实况照片定位配对的视频文件。</summary>
        private void LocateVideoFile_Click(object sender, RoutedEventArgs e)
        {
            var photoPath = ViewModel.SelectedFilePath;
            if (string.IsNullOrEmpty(photoPath)) return;

            // 查找选中项，判断实况照片类型
            var item = ViewModel.FileItems.FirstOrDefault(f =>
                string.Equals(f.FilePath, photoPath, StringComparison.OrdinalIgnoreCase));

            // 单文件实况照片：视频嵌入在照片内 → 直接定位照片
            if (item?.LivePhotoType == LivePhotoBox.Models.LivePhotoType.SingleFileJpeg
                || item?.LivePhotoType == LivePhotoBox.Models.LivePhotoType.SingleFileHeic)
            {
                try { FilePickerService.RevealInExplorer(photoPath); }
                catch (Exception ex) { LogService.Debug($"KeyPhoto reveal video (single-file) failed: {ex.Message}", LogSource.UI); }
                return;
            }

            // 双文件实况照片：定位配对视频
            if (item?.LivePhotoType == LivePhotoBox.Models.LivePhotoType.DualFile
                && !string.IsNullOrEmpty(item.PairedVideoPath))
            {
                try { FilePickerService.RevealInExplorer(item.PairedVideoPath); }
                catch (Exception ex) { LogService.Debug($"KeyPhoto reveal video (paired) failed: {ex.Message}", LogSource.UI); }
                return;
            }

            // 回退：按同名查找视频
            var dir = Path.GetDirectoryName(photoPath);
            var baseName = Path.GetFileNameWithoutExtension(photoPath);
            if (string.IsNullOrEmpty(dir)) return;

            foreach (var ext in new[] { ".mov", ".mp4" })
            {
                var videoPath = System.IO.Path.Combine(dir, baseName + ext);
                if (System.IO.File.Exists(videoPath))
                {
                    try { FilePickerService.RevealInExplorer(videoPath); }
                    catch (Exception ex) { LogService.Debug($"KeyPhoto reveal video (fallback) failed: {ex.Message}", LogSource.UI); }
                    return;
                }
            }
        }

        /// <summary>路径输入框失去焦点时触发扫描（手动输入路径后点击别处的场景）</summary>
        private void CurrentDirTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // 用户点击了浏览按钮 → 跳过（由 BrowseFolder_Click 负责触发）
            if (_suppressLostFocusScan)
            {
                _suppressLostFocusScan = false;
                return;
            }
            ViewModel.TriggerScan();
        }

        // ════════════════════════════════════════════════════════════
        //  滚动条常驻
        // ════════════════════════════════════════════════════════════

        private void ForceScrollBarsAlwaysThick()
        {
            // 确保 ListView 纵向滚动条始终可见（厚/薄外观已通过自定义模板固定）
            var listViewSv = FindVisualChild<ScrollViewer>(FileItemListView);
            if (listViewSv != null)
                listViewSv.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;

            // Timeline 横向滚动条也保持可见
            SetAllScrollBarsIndicatorMode(this);
        }

        private static void SetAllScrollBarsIndicatorMode(DependencyObject parent)
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is ScrollBar bar)
                {
                    // 不再修改 IndicatorMode —— 粗滚动条外观由自定义模板保证
                    bar.Loaded += (s, _) =>
                    {
                        var sb = (ScrollBar)s;
                        if (sb.Orientation == Orientation.Vertical)
                            sb.IndicatorMode = ScrollingIndicatorMode.MouseIndicator;
                    };
                }
                SetAllScrollBarsIndicatorMode(child);
            }
        }

        private static T? FindVisualChild<T>(DependencyObject parent) where T : DependencyObject
        {
            int count = VisualTreeHelper.GetChildrenCount(parent);
            for (int i = 0; i < count; i++)
            {
                var child = VisualTreeHelper.GetChild(parent, i);
                if (child is T match) return match;
                var result = FindVisualChild<T>(child);
                if (result != null) return result;
            }
            return null;
        }

        // ════════════════════════════════════════════════════════════
        //  自定义卡片交互（悬停 / 按下 / 选中）
        //
        //  核心修复：BorderThickness 始终 2px（透明↔强调色），杜绝 0↔2 切换导致的内容位移。
        //  ListView 使用裸 ContentPresenter 模板 + SelectionMode=Single。
        // ════════════════════════════════════════════════════════════

        /// <summary>卡片对应的数据项是否为当前选中项</summary>
        private bool IsCardSelected(Border card)
        {
            return FileItemListView.SelectedItem != null && card.DataContext == FileItemListView.SelectedItem;
        }

        /// <summary>统一更新卡片的背景与边框。
        /// 边框始终 2px——只改颜色，彻底消除布局偏移。</summary>
        private void UpdateCardVisual(Border card, bool isSelected, bool hovered, bool pressed)
        {
            card.BorderThickness = new Thickness(2);
            card.BorderBrush = isSelected ? _selectedBorder : _transparent;

            if (isSelected)
            {
                if (pressed)
                    card.Background = _selectedPressedBg;
                else if (hovered)
                    card.Background = _selectedHoverBg;
                else
                    card.Background = _selectedBg;
            }
            else
            {
                if (pressed)
                    card.Background = _pressedBg;
                else if (hovered)
                    card.Background = _hoverBg;
                else
                    card.Background = _transparent;
            }
        }

        // ── 卡片 Loaded：虚拟化时恢复选中态 ──

        private void CardRoot_Loaded(object sender, RoutedEventArgs e)
        {
            if (sender is Border card)
            {
                // Loaded 可能触发多次（虚拟化回收），每次都检查
                if (IsCardSelected(card))
                    UpdateCardVisual(card, isSelected: true, hovered: false, pressed: false);
            }
        }

        // ── 指针事件 ──

        private void CardRoot_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card)
            {
                _hoveredCard = card;
                UpdateCardVisual(card, IsCardSelected(card), hovered: true, pressed: _pressedCard == card);
            }
        }

        private void CardRoot_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card)
            {
                if (_hoveredCard == card) _hoveredCard = null;
                UpdateCardVisual(card, IsCardSelected(card), hovered: false, pressed: false);
            }
        }

        private void CardRoot_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card)
            {
                _pressedCard = card;
                UpdateCardVisual(card, IsCardSelected(card), hovered: _hoveredCard == card, pressed: true);
            }
        }

        private void CardRoot_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (sender is Border card)
            {
                if (_pressedCard == card) _pressedCard = null;
                UpdateCardVisual(card, IsCardSelected(card), hovered: _hoveredCard == card, pressed: false);
            }
        }

        // ── 选中变更（ListView 内置选择驱动，我们只管视觉同步） ──

        private void FileItemListView_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            foreach (var item in e.RemovedItems)
                RefreshSingleCardVisual(item as KeyPhotoFileItem, isSelected: false);
            foreach (var item in e.AddedItems)
                RefreshSingleCardVisual(item as KeyPhotoFileItem, isSelected: true);

            if (FileItemListView.SelectedItem is KeyPhotoFileItem selected)
                ViewModel.SelectFile(selected.FilePath);
            else
                ViewModel.SelectFile(null);
        }

        /// <summary>通过数据项找到对应容器中的 Border 并刷新视觉</summary>
        private void RefreshSingleCardVisual(KeyPhotoFileItem? item, bool isSelected)
        {
            if (item == null) return;
            if (FileItemListView.ContainerFromItem(item) is ListViewItem container)
            {
                var card = FindVisualChild<Border>(container);
                if (card != null)
                    UpdateCardVisual(card, isSelected,
                        hovered: _hoveredCard == card, pressed: _pressedCard == card);
            }
        }

        // ════════════════════════════════════════════════════════════
        //  容器生命周期（虚拟化回收 + 事件挂接）
        // ════════════════════════════════════════════════════════════

        private void OnContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue)
            {
                if (args.ItemContainer is ListViewItem container)
                {
                    var card = FindVisualChild<Border>(container);
                    if (card != null)
                    {
                        card.PointerEntered -= CardRoot_PointerEntered;
                        card.PointerExited -= CardRoot_PointerExited;
                        card.PointerPressed -= CardRoot_PointerPressed;
                        card.PointerReleased -= CardRoot_PointerReleased;
                        if (_hoveredCard == card) _hoveredCard = null;
                        if (_pressedCard == card) _pressedCard = null;
                    }
                }
                return;
            }

            if (args.ItemContainer is ListViewItem lvi)
            {
                lvi.Loaded += OnContainerLoaded_WireCardEvents;
            }
        }

        private void OnContainerLoaded_WireCardEvents(object sender, RoutedEventArgs e)
        {
            if (sender is ListViewItem container)
            {
                container.Loaded -= OnContainerLoaded_WireCardEvents;
                var card = FindVisualChild<Border>(container);
                if (card != null)
                {
                    card.PointerEntered += CardRoot_PointerEntered;
                    card.PointerExited += CardRoot_PointerExited;
                    card.PointerPressed += CardRoot_PointerPressed;
                    card.PointerReleased += CardRoot_PointerReleased;

                    if (IsCardSelected(card))
                        UpdateCardVisual(card, isSelected: true, hovered: false, pressed: false);

                    // 同步折叠态文字可见性（新加载的容器）
                    if (_isLeftPanelCollapsed)
                        SetTextPanelVisible(card, Visibility.Collapsed);
                }
            }
        }

        /// <summary>折叠时隐藏文字 StackPanel + 归零*列宽度 + 缩紧 Border</summary>
        private static void SetTextPanelVisible(Border card, Visibility vis)
        {
            if (card.Child is Grid grid && grid.Children.Count > 1
                && grid.Children[1] is StackPanel textPanel)
            {
                textPanel.Visibility = vis;
                // 折叠时把文字列宽度归零 + 列间距清零，展开时恢复
                if (grid.ColumnDefinitions.Count > 1)
                    grid.ColumnDefinitions[1].Width = vis == Visibility.Collapsed
                        ? new GridLength(0) : new GridLength(1, GridUnitType.Star);
                grid.ColumnSpacing = vis == Visibility.Collapsed ? 0 : 8;
                // 折叠时 Border 紧贴内容，选中框只围缩略图；展开时恢复拉伸
                card.HorizontalAlignment = vis == Visibility.Collapsed
                    ? HorizontalAlignment.Left : HorizontalAlignment.Stretch;
            }
        }
    }
}
