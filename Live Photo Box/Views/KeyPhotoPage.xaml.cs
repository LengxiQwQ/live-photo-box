/*
 * KeyPhotoPage.xaml.cs
 *
 * 实况照片主图更换页面的代码后置。
 * 处理 UI 事件 + 自定义卡片交互（悬停/选中/按下）+ 预览最大化/缩放。
 *
 * 时间轴支持双模式（在设置页切换）：
 *   经典模式（Classic）  — ListView（原有），卡片选中框跟随选中帧移动。
 *   胶片模式（Filmstrip） — ItemsRepeater + 固定选中框覆盖层，
 *                           选中框始终位于画面中心不动，缩略图从框下划过。
 *                           滚轮每次精确步进一帧，支持边缘 padding
 *                           确保所有帧都能到达画面中心。
 *
 * 左侧文件列表仍使用 ListView（裸 ContentPresenter 模板，消除
 * ListViewItem 内置的 PointerDownThemeAnimation）。
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

        // ── 时间轴常量 ──
        /// <summary>帧步长：56px 卡片 + 4px 间距 = 60px</summary>
        private const double FilmstripItemStep = 60.0;
        private const double FilmstripItemWidth = 56.0;

        // ── 经典模式（ListView）时间轴状态 ──
        private CancellationTokenSource? _scrollCts;
        private Border? _hoveredTimelineCard;
        private Border? _pressedTimelineCard;

        // ── 胶片模式（固定选中框）状态 ──
        private int _filmstripCurrentFrameIndex;
        private double _filmstripViewportWidth;

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
            PhotoViewer.ScaleChanged += PhotoViewer_ScaleChanged;
            FileItemListView.ContainerContentChanging += OnContainerContentChanging;
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

            // 同步胶片模式选中框画刷（与文件列表使用相同的强调色画刷）
            UpdateFilmstripSelectionHighlight();
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

        /// <summary>遍历所有可见文件列表卡片容器，刷新其视觉状态</summary>
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

        // ════════════════════════════════════════════════════════════
        //  缩放按钮（委托给 PhotoViewer）
        // ════════════════════════════════════════════════════════════

        private void ZoomInButton_Click(object sender, RoutedEventArgs e)
        {
            PhotoViewer.ZoomIn();
            UpdateZoomPercentDisplay();
        }

        private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
        {
            PhotoViewer.ZoomOut();
            UpdateZoomPercentDisplay();
        }

        private void ZoomPercentButton_Click(object sender, RoutedEventArgs e)
        {
            PhotoViewer.ToggleFitVsPixel();
            UpdateZoomPercentDisplay();
        }

        /// <summary>同步缩放百分比显示（相对于 Fit 的整数百分比）</summary>
        private void UpdateZoomPercentDisplay()
        {
            int percent = (int)Math.Round(PhotoViewer.CurrentScale * 100);
            ZoomPercentText.Text = $"{percent}%";
        }

        private void PhotoViewer_ScaleChanged(double newScale)
        {
            UpdateZoomPercentDisplay();
        }

        private void KeyPhotoPage_Loaded(object sender, RoutedEventArgs e)
        {
            Loaded -= KeyPhotoPage_Loaded;

            LivePhotoBox.Helpers.ComboBoxHelper.AutoFitWidth(SortComboBox);
            LivePhotoBox.Helpers.ComboBoxHelper.AutoFitWidth(FilterComboBox);

            DispatcherQueue.TryEnqueue(() => ForceScrollBarsAlwaysThick());

            // 根据模式初始化对应的时间轴
            if (ViewModel.IsClassicTimelineMode)
                InitializeClassicTimeline();
            else
                InitializeFilmstripTimeline();
        }

        // ═════════════════════════════════════════════════════════════════
        //  经典模式（ListView）时间轴
        // ═════════════════════════════════════════════════════════════════

        private void InitializeClassicTimeline()
        {
            // ListView 的 ContainerContentChanging 在 XAML 中已注册
            TimelineListView.ContainerContentChanging += OnTimelineContainerContentChanging;
        }

        private void TimelineListView_Loaded(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.IsClassicTimelineMode) return;
            var sv = FindVisualChild<ScrollViewer>(TimelineListView);
            if (sv != null)
                sv.PointerWheelChanged += TimelineListScrollViewer_PointerWheelChanged;
        }

        private void TimelineListScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (sender is ScrollViewer sv)
            {
                var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
                sv.ScrollToHorizontalOffset(sv.HorizontalOffset - delta);
                e.Handled = true;
            }
        }

        // ── 经典模式：ViewModel 通知滚动（带重试）──

        private void ClassicScrollToFrame(TimelineFrame frame)
        {
            _scrollCts?.Cancel();
            _scrollCts?.Dispose();
            _scrollCts = new CancellationTokenSource();
            var ct = _scrollCts.Token;

            TimelineListView.SelectedItem = frame;

            const int maxRetries = 5;
            const int delayMs = 120;
            ScheduleClassicScrollRetry(frame, ct, maxRetries, delayMs);
        }

        private void ScheduleClassicScrollRetry(TimelineFrame frame, CancellationToken ct,
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
                        _ = ClassicRetryAfterDelay(frame, ct, remainingRetries - 1, delayMs);
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

                    sv.ChangeView(targetOffset, null, null);
                    _ = ClassicRefreshSelectionAfterScroll(frame, ct, 3);
                }
                catch (Exception ex)
                {
                    LogService.Debug($"Classic timeline scroll failed: {ex.Message}", LogSource.UI);
                }
            });
        }

        private async Task ClassicRefreshSelectionAfterScroll(
            TimelineFrame frame, CancellationToken ct, int remaining)
        {
            for (int i = 0; i < remaining; i++)
            {
                try { await Task.Delay(50, ct); }
                catch (TaskCanceledException) { return; }
                if (ct.IsCancellationRequested) return;

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

                try { await Task.Delay(20, ct); }
                catch (TaskCanceledException) { return; }
                if (found) break;
            }
        }

        private async Task ClassicRetryAfterDelay(TimelineFrame frame, CancellationToken ct,
            int remainingRetries, int delayMs)
        {
            try
            {
                await Task.Delay(delayMs, ct);
                ScheduleClassicScrollRetry(frame, ct, remainingRetries, delayMs);
            }
            catch (TaskCanceledException) { }
        }

        // ── 经典模式：卡片视觉（选中框+悬停）──

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
                card.Background = _transparent;
            }
            else
            {
                if (pressed)       card.Background = _pressedBg;
                else if (hovered)  card.Background = _hoverBg;
                else               card.Background = _transparent;
            }
        }

        private void OnTimelineContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (!ViewModel.IsClassicTimelineMode) return;
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

        // ═════════════════════════════════════════════════════════════════
        //  胶片模式（固定选中框 + ItemsRepeater 逐帧步进）
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// 胶片模式初始化。
        /// 设置选中框画刷，计算边缘 padding 确保首末帧可到中心，初始化选中帧。
        /// </summary>
        private void InitializeFilmstripTimeline()
        {
            UpdateFilmstripSelectionHighlight();
        }

        /// <summary>
        /// 更新选中框画刷颜色（跟随系统强调色）。
        /// 选中框是固定覆盖层，强调色边框 + 半透明强调色背景。
        /// </summary>
        private void UpdateFilmstripSelectionHighlight()
        {
            if (FilmstripSelectionHighlight == null) return;
            FilmstripSelectionHighlight.BorderBrush = _selectedBorder;
            FilmstripSelectionHighlight.Background = _selectedBg;
        }

        /// <summary>
        /// Viewport 加载完成 → 初始化 Clip、定位到当前帧。
        /// </summary>
        private void FilmstripViewport_Loaded(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.IsFilmstripTimelineMode) return;

            _filmstripViewportWidth = FilmstripViewport.ActualWidth;
            UpdateFilmstripViewportClip();

            // 初始定位到首帧并在 ViewModel 中选中
            _filmstripCurrentFrameIndex = 0;
            UpdateFilmstripTransform();
            if (ViewModel.TimelineFrames.Count > 0)
                ViewModel.SelectTimelineFrameInteractively(ViewModel.TimelineFrames[0]);
        }

        /// <summary>
        /// Viewport 尺寸变化 → 重算 Clip 和 Transform。
        /// 模式切换（经典→胶片）时也会触发（Collapsed→Visible → SizeChanged），
        /// 此时从中同步 ViewModel 的选中帧索引。
        /// </summary>
        private void FilmstripViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!ViewModel.IsFilmstripTimelineMode) return;

            _filmstripViewportWidth = e.NewSize.Width;
            UpdateFilmstripViewportClip();

            // 同步 ViewModel 当前选中帧的索引
            if (ViewModel.CurrentKeyFrame != null)
            {
                int idx = ViewModel.TimelineFrames.IndexOf(ViewModel.CurrentKeyFrame);
                if (idx >= 0) _filmstripCurrentFrameIndex = idx;
            }

            UpdateFilmstripTransform();
        }

        /// <summary>
        /// 设置 Viewport 裁切区域，防止内容溢出可见范围。
        /// </summary>
        private void UpdateFilmstripViewportClip()
        {
            if (FilmstripViewport.ActualWidth <= 0 || FilmstripViewport.ActualHeight <= 0) return;
            var clip = new Microsoft.UI.Xaml.Media.RectangleGeometry();
            clip.Rect = new Windows.Foundation.Rect(0, 0,
                FilmstripViewport.ActualWidth, FilmstripViewport.ActualHeight);
            FilmstripViewport.Clip = clip;
        }

        /// <summary>
        /// 鼠标滚轮 — 每次步进一帧，绝对精确。
        /// 不依赖任何"滚动"机制，只通过索引计算目标偏移。
        /// </summary>
        private void FilmstripViewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (!ViewModel.IsFilmstripTimelineMode || ViewModel.TimelineFrames.Count == 0) return;

            var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
            int direction = delta > 0 ? -1 : 1;

            int count = ViewModel.TimelineFrames.Count;
            int targetIdx = Math.Clamp(_filmstripCurrentFrameIndex + direction, 0, count - 1);

            if (targetIdx != _filmstripCurrentFrameIndex)
            {
                _filmstripCurrentFrameIndex = targetIdx;
                UpdateFilmstripTransform();
                ViewModel.SelectTimelineFrameInteractively(ViewModel.TimelineFrames[targetIdx]);
            }

            e.Handled = true;
        }

        /// <summary>
        /// 核心：将指定索引的帧定位到画面中心。
        /// 完全通过 TranslateTransform 控制，无任何"滚动"层参与。
        /// </summary>
        private void FilmstripScrollToFrameIndex(int index, bool animate)
        {
            if (ViewModel.TimelineFrames.Count == 0) return;
            if (_filmstripViewportWidth <= 0) return;

            index = Math.Clamp(index, 0, ViewModel.TimelineFrames.Count - 1);
            _filmstripCurrentFrameIndex = index;

            UpdateFilmstripTransform();

            ViewModel.SelectTimelineFrameInteractively(ViewModel.TimelineFrames[index]);
        }

        /// <summary>
        /// 计算 TranslateTransform.X 使当前帧位于画面中心。
        /// 边缘空隙（padding）由 Transform 自身产生（正数 X = 内容右移），
        /// 不依赖任何 Border.Padding，无布局时机问题。
        ///
        /// 公式: Transform.X = viewportWidth/2 - (index * step + itemWidth/2)
        ///       = 视口中心 - 帧中心在 ItemsRepeater 中的坐标
        ///
        /// 帧0: Transform.X > 0（内容右移，左侧出现边缘空隙）
        /// 中间帧: Transform.X ≈ 0
        /// 末帧: Transform.X < 0（内容左移）
        /// </summary>
        private void UpdateFilmstripTransform()
        {
            if (_filmstripViewportWidth <= 0 || ViewModel.TimelineFrames.Count == 0) return;

            double totalContentWidth = ViewModel.TimelineFrames.Count * FilmstripItemStep - 4; // spacing
            if (totalContentWidth <= _filmstripViewportWidth)
            {
                // 内容宽度小于视口 → 整体居中即可
                FilmstripTransform.X = (_filmstripViewportWidth - totalContentWidth) / 2.0;
                return;
            }

            double frameCenter = _filmstripCurrentFrameIndex * FilmstripItemStep + FilmstripItemWidth / 2.0;
            double targetX = _filmstripViewportWidth / 2.0 - frameCenter;

            // clamp: 帧0（最大右移） … 末帧（最大左移）
            double maxRight = _filmstripViewportWidth / 2.0 - FilmstripItemWidth / 2.0;
            double maxLeft = _filmstripViewportWidth / 2.0
                - ((ViewModel.TimelineFrames.Count - 1) * FilmstripItemStep + FilmstripItemWidth / 2.0);

            FilmstripTransform.X = Math.Clamp(targetX, maxLeft, maxRight);
        }

        // ═════════════════════════════════════════════════════════════════
        //  ViewModel → View 滚动通知（双模式派发）
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// ViewModel 通知时间轴选中 key photo 帧并居中滚动。
        /// 根据当前模式分别派发到经典或胶片模式。
        /// </summary>
        private void OnRequestScrollToFrame(TimelineFrame frame)
        {
            if (ViewModel.IsClassicTimelineMode)
            {
                ClassicScrollToFrame(frame);
            }
            else if (ViewModel.IsFilmstripTimelineMode)
            {
                int index = ViewModel.TimelineFrames.IndexOf(frame);
                if (index >= 0)
                    FilmstripScrollToFrameIndex(index, animate: false);
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
            // 确保文件列表 ListView 纵向滚动条始终可见
            var listViewSv = FindVisualChild<ScrollViewer>(FileItemListView);
            if (listViewSv != null)
                listViewSv.VerticalScrollBarVisibility = ScrollBarVisibility.Visible;

            // 时间轴：经典模式需要常驻滚动条
            if (ViewModel.IsClassicTimelineMode)
            {
                var timelineSv = FindVisualChild<ScrollViewer>(TimelineListView);
                if (timelineSv != null)
                    timelineSv.HorizontalScrollBarVisibility = ScrollBarVisibility.Visible;
            }
            // 胶片模式无 ScrollViewer，无需操作
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
