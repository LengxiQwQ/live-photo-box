/*
 * EditPage.xaml.cs
 *
 * 实况照片封面更换页面的代码后置。
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
using Windows.Media.Core;
using Windows.Storage;

namespace LivePhotoBox.Views
{
    public sealed partial class EditPage : Page
    {
        public EditViewModel ViewModel => AppViewModel.Instance.Edit;

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

        /// <summary>上次成功触发扫描的目录路径（路径未变时跳过 LostFocus 重复扫描）</summary>
        private string? _lastScannedPath;

        // ── 视频预览状态 ──
        /// <summary>SingleFileJpeg 提取的临时视频路径（用于播放后清理）</summary>
        private string? _previewTempVideoPath;
        /// <summary>正在切换预览模式（禁止 CloseRequested 重复恢复 UI）</summary>
        private bool _isApplyingPreviewMode;

        // ── 拖拽类型缓存（DragEnter 异步检测 → DragOver 同步读取）──
        /// <summary>左侧面板：当前拖入的 StorageItems 是否全是文件夹</summary>
        private bool _isLeftDropAllFolders;
        /// <summary>右侧面板：当前拖入的 StorageItems 是否包含媒体文件</summary>
        private bool _isRightDropHasFiles;

        // ── 预览最大化状态 ──
        private bool _isPreviewMaximized;

        // ── 缩放+平移状态同步（图片 ↔ 实况视频）──
        private double _sharedZoomScale = 1.0;
        private double _sharedPanX = 0.5;
        private double _sharedPanY = 0.5;

        // ════════════════════════════════════════════════════════════
        //  底部选项卡单选状态（带记忆 + 非实况自动切"文件基础信息"）
        //
        //  四个选项卡（单选）对应的面板可见性：
        //    "combined"     → 时间轴 + 文件基础信息（组合查看）
        //    "frames"       → 仅时间轴
        //    "basicInfo"    → 仅文件基础信息
        //    "detailProps"  → 仅更改文件属性（占位）
        //
        //  记忆规则：用户手动切换选项卡时保存到 AppSettings；
        //           非实况照片自动切到"文件基础信息"，不保存此次自动切换。
        //           默认值为"combined"（组合查看）。
        // ════════════════════════════════════════════════════════════

        private const string InfoTabSettingKey = "KeyPhoto_LastInfoTab";
        private const string DefaultInfoTab = "combined";

        /// <summary>防止自动切换选项卡时触发保存记忆</summary>
        private bool _isAutoSwitchingTab;

        /// <summary>
        /// InfoTabs 加载时恢复上次记忆的选项卡（默认"combined"）。
        /// </summary>
        private void InfoTabs_Loaded(object sender, RoutedEventArgs e)
        {
            var savedTag = AppSettingsService.GetValue(InfoTabSettingKey, DefaultInfoTab);
            var item = FindSegmentedItem(savedTag) ?? FindSegmentedItem(DefaultInfoTab);
            InfoTabs.SelectedItem = item;
        }

        /// <summary>
        /// Segmented 单选 SelectionChanged 事件处理：
        ///   1. 根据当前选项卡 Tag 决定各面板可见性
        ///   2. 非自动切换时保存用户选择到 AppSettings（记忆功能）
        /// </summary>
        private void InfoTabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            var selectedTag = (InfoTabs.SelectedItem as CommunityToolkit.WinUI.Controls.SegmentedItem)?.Tag as string;

            // "combined" → 组合查看：时间轴 + 基础信息都显示
            // "frames" / "basicInfo" / "detailProps" → 各自单独显示
            ViewModel.IsFramesPanelVisible = selectedTag == "combined" || selectedTag == "frames";
            ViewModel.IsBasicInfoPanelVisible = selectedTag == "combined" || selectedTag == "basicInfo";
            ViewModel.IsDetailPropsPanelVisible = selectedTag == "detailProps";

            // 记忆用户手动选择的选项卡（自动切换不记忆）
            if (!_isAutoSwitchingTab && selectedTag != null)
            {
                AppSettingsService.SetValue(InfoTabSettingKey, selectedTag);
            }
        }

        /// <summary>
        /// 根据当前选中文件类型自动调整底部选项卡：
        ///   非实况照片 + 当前在"组合查看"或"实况照片帧" → 自动切到"文件基础信息"
        ///     （这两个选项卡是为实况照片时间轴设计的，普通文件不适用；
        ///      但"更改文件属性"例外——普通照片也可查看，保持不变）
        ///   实况照片 → 恢复用户上次记忆的选项卡
        /// </summary>
        private void ApplyInfoTabForSelectedFile()
        {
            var currentTag = (InfoTabs.SelectedItem as CommunityToolkit.WinUI.Controls.SegmentedItem)?.Tag as string;

            if (ViewModel.HasSelectedFile && !ViewModel.IsSelectedLivePhoto)
            {
                // 非实况照片：如果当前在"组合查看"或"实况照片帧"，切到"文件基础信息"
                // "文件基础信息"和"更改文件属性"保持不动
                if (currentTag == "combined" || currentTag == "frames")
                {
                    _isAutoSwitchingTab = true;
                    InfoTabs.SelectedItem = FindSegmentedItem("basicInfo");
                    _isAutoSwitchingTab = false;
                }
            }
            else if (ViewModel.IsSelectedLivePhoto)
            {
                // 实况照片：恢复记忆的选项卡
                var savedTag = AppSettingsService.GetValue(InfoTabSettingKey, DefaultInfoTab);
                var item = FindSegmentedItem(savedTag) ?? FindSegmentedItem(DefaultInfoTab);
                if (!ReferenceEquals(InfoTabs.SelectedItem, item))
                {
                    _isAutoSwitchingTab = true;
                    InfoTabs.SelectedItem = item;
                    _isAutoSwitchingTab = false;
                }
            }
        }

        private CommunityToolkit.WinUI.Controls.SegmentedItem? FindSegmentedItem(string tag)
        {
            return InfoTabs.Items
                .OfType<CommunityToolkit.WinUI.Controls.SegmentedItem>()
                .FirstOrDefault(i => (string)i.Tag == tag);
        }

        // ── 时间轴常量 ──
        /// <summary>帧步长：56px 卡片 + 0px 间距 = 56px（Spacing="0"）</summary>
        private const double FilmstripItemStep = 56.0;
        private const double FilmstripItemWidth = 56.0;

        // ── 经典模式（ListView）时间轴状态 ──
        private CancellationTokenSource? _scrollCts;
        private Border? _hoveredTimelineCard;
        private Border? _pressedTimelineCard;
        private bool _isClassicTimelineInitialized;

        // ── 胶片模式（ScrollViewer + SnapPanel 吸附）状态 ──
        private int _filmstripCurrentFrameIndex;
        private bool _isFilmstripTimelineInitialized;
        /// <summary>胶片模式滚轮事件委托引用（AddHandler/RemoveHandler 需要同一实例）</summary>
        private readonly PointerEventHandler _filmstripWheelHandler;

        // ── 精准滚轮累积目标 ──
        /// <summary>数学绝对目标偏移量（不受动画中途残缺值干扰），-1 表示未初始化</summary>
        private double _targetScrollOffset = -1;
        /// <summary>上次滚轮事件时间，用于 250ms 超时重校准</summary>
        private DateTime _lastWheelTime = DateTime.MinValue;
        /// <summary>帧合并锁：同一渲染帧内仅提交一次 ChangeView，防高频调用 0xc000027b 崩溃</summary>
        private bool _isScrollQueued;
        /// <summary>胶片模式滚动重试取消令牌（布局未就绪时延迟重试）</summary>
        private CancellationTokenSource? _filmstripScrollRetryCts;

        public EditPage()
        {
            InitializeComponent();

            // 拖拽事件（Unloaded 中 detach，OnNavigatedTo 中重新 attach）
            AttachDragEvents();

            RebuildAllBrushes();

            // 存储委托引用，确保 AddHandler / RemoveHandler 使用同一实例
            _filmstripWheelHandler = new PointerEventHandler(OnFilmstripPointerWheelChanged);

            // 系统换强调色时实时更新（页面缓存，无需 detach，跟随 app 生命周期）
            _uiSettings.ColorValuesChanged += OnSystemColorValuesChanged;

            // 时间轴照片帧自动滚动（ViewModel 事件，页面缓存期间持续有效）
            ViewModel.RequestScrollToFrame += OnRequestScrollToFrame;

            // 大图预览清空（实况→非实况切换）
            ViewModel.PreviewClearRequested += OnPreviewClearRequested;

            Loaded += KeyPhotoPage_Loaded;
            PhotoViewer.ScaleChanged += PhotoViewer_ScaleChanged;
            PureMediaViewer.ScaleChanged += s =>
            {
                _sharedZoomScale = s;
                UpdateZoomPercentDisplay();
            };
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
                UnifiedInfoPanel.Visibility = Visibility.Collapsed;
                MainContentGrid.Padding = new Thickness(0);
                PreviewBorder.CornerRadius = new CornerRadius(0);
                PreviewBorder.Margin = new Thickness(0);
                MaximizeButtonIcon.Glyph = "";
                ToolTipService.SetToolTip(MaximizeButton, "还原");
            }
            else
            {
                TopBarGrid.Visibility = Visibility.Visible;
                LeftPanelColumn.Width = new GridLength(
                    _isLeftPanelCollapsed ? LeftPanelCollapsedWidth : LeftPanelExpandedWidth);
                PanelSpacerColumn.Width = new GridLength(8);
                UnifiedInfoPanel.Visibility = Visibility.Visible;
                MainContentGrid.Padding = new Thickness(8, 0, 8, 6);
                PreviewBorder.CornerRadius = ViewModel.IsSelectedFileVideo
                    ? new CornerRadius(0) : new CornerRadius(10);
                PreviewBorder.Margin = new Thickness(0, 0, 0, 4);
                MaximizeButtonIcon.Glyph = "";
                ToolTipService.SetToolTip(MaximizeButton, "最大化预览");
            }
        }

        // ════════════════════════════════════════════════════════════
        // ════════════════════════════════════════════════════════════
        //  实况照片按钮悬停动画：仅图标放大（文字不动）
        // ════════════════════════════════════════════════════════════

        private void LivePhotoBadgeButton_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            // 图标顺时针旋转一整圈
            var rotateAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = LivePhotoIconTransform.Rotation,
                To = LivePhotoIconTransform.Rotation + 360.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(400)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(rotateAnimation, LivePhotoIconTransform);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(rotateAnimation, "Rotation");

            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            storyboard.Children.Add(rotateAnimation);
            storyboard.Begin();
        }

        private void LivePhotoBadgeButton_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            // 图标逆时针转一圈归位
            var rotateAnimation = new Microsoft.UI.Xaml.Media.Animation.DoubleAnimation
            {
                From = LivePhotoIconTransform.Rotation,
                To = LivePhotoIconTransform.Rotation - 360.0,
                Duration = new Duration(TimeSpan.FromMilliseconds(400)),
                EasingFunction = new Microsoft.UI.Xaml.Media.Animation.CubicEase { EasingMode = Microsoft.UI.Xaml.Media.Animation.EasingMode.EaseOut }
            };
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTarget(rotateAnimation, LivePhotoIconTransform);
            Microsoft.UI.Xaml.Media.Animation.Storyboard.SetTargetProperty(rotateAnimation, "Rotation");

            var storyboard = new Microsoft.UI.Xaml.Media.Animation.Storyboard();
            storyboard.Children.Add(rotateAnimation);
            storyboard.Begin();
        }

        // ════════════════════════════════════════════════════════════
        //  实况照片就地视频预览（PureMediaViewer 叠加层交互）
        // ════════════════════════════════════════════════════════════

        // ════════════════════════════════════════════════════════════
        //  实况照片就地视频预览（PureMediaViewer 叠加层交互）
        //  硬直切，无过渡动画
        // ════════════════════════════════════════════════════════════

        /// <summary>
        /// 点击 LIVE 按钮 → 等待 MediaOpened → VideoOpened 事件中同一帧藏照片+亮视频。
        /// </summary>
        private async void LivePhotoBadgeButton_Click(object sender, RoutedEventArgs e)
        {
            if (PureMediaViewer.Visibility == Visibility.Visible) return;

            var videoPath = await ResolveVideoPathAsync();
            if (videoPath == null) return;

            try
            {
                var storageFile = await StorageFile.GetFileFromPathAsync(videoPath);
                var mediaSource = MediaSource.CreateFromStorageFile(storageFile);

                PureMediaViewer.AutoCloseOnEnd = true;
                PureMediaViewer.ShowCloseButton = true;
                PureMediaViewer.ShowTransportControls = false;
                PureMediaViewer.ZoomEnabled = true;

                PureMediaViewer.VideoSource = mediaSource;

                // 从照片读取完整缩放+平移状态
                var photoState = PhotoViewer.GetZoomPanState();
                _sharedZoomScale = photoState.scale;
                _sharedPanX = photoState.panX;
                _sharedPanY = photoState.panY;

                // ════════════════════════════════════════════════════════
                // 原子切换：等视频第一帧就绪后，同一帧内藏照片 + 亮视频 + 同步缩放平移
                // ════════════════════════════════════════════════════════
                var tcs = new TaskCompletionSource<bool>();
                Action onOpened = null!;
                onOpened = () =>
                {
                    PureMediaViewer.VideoOpened -= onOpened;
                    PhotoViewer.Opacity = 0;
                    PureMediaViewer.ShowDirect();
                    // 应用完整缩放+平移状态（ShowDirect 后布局已就绪）
                    PureMediaViewer.ApplyZoomPanState(_sharedZoomScale, _sharedPanX, _sharedPanY);
                    tcs.TrySetResult(true);
                };
                PureMediaViewer.VideoOpened += onOpened;

                // 等待视频就绪（含 3 秒超时）
                await Task.WhenAny(tcs.Task, Task.Delay(3000));

                // 超时或加载失败 → 视频未成功显示，恢复状态
                if (PureMediaViewer.Visibility != Visibility.Visible)
                {
                    SyncLivePhotoBadgeVisibility();
                    ZoomControlsPanel.ClearValue(StackPanel.VisibilityProperty);
                    return;
                }

                // 隐藏浮在视频上方的控件——保留 ZoomControlsPanel 供缩放
                LivePhotoBadgeButton.Visibility = Visibility.Collapsed;
                MuteButton.Visibility = Visibility.Collapsed;

                // 播放前应用静音状态（ShowDirect 已启动播放）
                ApplyMuteState();
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[EditPage] 视频播放失败: {ex.Message}");
                PhotoViewer.Opacity = 1;
                SyncLivePhotoBadgeVisibility();
                ZoomControlsPanel.ClearValue(StackPanel.VisibilityProperty);
            }
        }

        /// <summary>
        /// PureMediaViewer 关闭回调。
        /// 图片层始终位于视频层下方未被隐藏，只需恢复浮动控件。
        /// </summary>
        private void PureMediaViewer_CloseRequested(object? sender, EventArgs e)
        {
            if (_isApplyingPreviewMode) return;

            // 从视频读取完整缩放+平移状态
            var videoState = PureMediaViewer.GetZoomPanState();
            _sharedZoomScale = videoState.scale;
            _sharedPanX = videoState.panX;
            _sharedPanY = videoState.panY;

            // 恢复照片层可见
            PhotoViewer.Opacity = 1;

            // 将完整状态同步回照片
            PhotoViewer.ApplyZoomPanState(_sharedZoomScale, _sharedPanX, _sharedPanY);

            SyncLivePhotoBadgeVisibility();
            ZoomControlsPanel.ClearValue(StackPanel.VisibilityProperty);
        }

        /// <summary>
        /// 根据 ViewModel.IsSelectedLivePhoto 显式同步 LIVE + 静音按钮可见性。
        /// 用于替代 ClearValue，因为 x:Bind 使用直接属性赋值而非 SetBinding，
        /// ClearValue 后按钮会回退到默认 Visible 状态，x:Bind 不会自动重新应用。
        /// </summary>
        private void SyncLivePhotoBadgeVisibility()
        {
            var visibility = ViewModel.IsSelectedLivePhoto
                ? Visibility.Visible
                : Visibility.Collapsed;
            LivePhotoBadgeButton.Visibility = visibility;
            MuteButton.Visibility = visibility;
        }

        /// <summary>
        /// 将 ViewModel.IsMuted 应用到 PureMediaViewer 的 MediaPlayer。
        /// 在切换静音、开始播放时调用，确保 UI 状态与播放器同步。
        /// </summary>
        private void ApplyMuteState()
        {
            PureMediaViewer.IsMuted = ViewModel.IsMuted;
        }

        /// <summary>
        /// 点击静音按钮 → 切换静音状态并应用到当前视频播放器。
        /// </summary>
        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsMuted = !ViewModel.IsMuted;
            ApplyMuteState();
        }

        /// <summary>
        /// 获取当前选中实况照片的视频路径。
        /// DualFile → 直接取 PairedVideoPath；
        /// SingleFileJpeg → 从 JPEG 尾部提取嵌入式 MP4 到临时文件。
        /// 返回 null 表示无法获取有效视频。
        /// </summary>
        private async Task<string?> ResolveVideoPathAsync()
        {
            // 先清理上一次的临时文件
            CleanupPreviewTempVideo();

            var selectedPath = ViewModel.SelectedFilePath;
            if (string.IsNullOrEmpty(selectedPath)) return null;

            var item = ViewModel.FileItems
                .FirstOrDefault(f => f.FilePath == selectedPath);
            if (item == null) return null;

            // DualFile：直接使用配对视频路径
            if (item.LivePhotoType == LivePhotoType.DualFile
                && !string.IsNullOrEmpty(item.PairedVideoPath)
                && File.Exists(item.PairedVideoPath))
            {
                return item.PairedVideoPath;
            }

            // SingleFileJpeg：从 JPEG 文件尾部提取嵌入式视频
            if (item.LivePhotoType == LivePhotoType.SingleFileJpeg
                && item.AppendedVideoLength > 0
                && File.Exists(item.FilePath))
            {
                try
                {
                    var tempPath = Path.Combine(
                        Path.GetTempPath(),
                        $"lpb_preview_{Guid.NewGuid():N}.mp4");

                    var imagePath = item.FilePath;
                    var videoLength = item.AppendedVideoLength;

                    await Task.Run(() =>
                    {
                        using var src = new FileStream(imagePath, FileMode.Open,
                            FileAccess.Read, FileShare.Read);
                        src.Seek(-videoLength, SeekOrigin.End);

                        using var dst = new FileStream(tempPath, FileMode.Create,
                            FileAccess.Write, FileShare.None);
                        src.CopyTo(dst);
                    });

                    if (File.Exists(tempPath))
                    {
                        _previewTempVideoPath = tempPath;
                        return tempPath;
                    }
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine(
                        $"[EditPage] 嵌入式视频提取失败: {ex.Message}");
                }
            }

            return null;
        }

        /// <summary>删除上一次提取的临时视频文件，释放磁盘空间</summary>
        private void CleanupPreviewTempVideo()
        {
            if (_previewTempVideoPath == null) return;
            try
            {
                if (File.Exists(_previewTempVideoPath))
                    File.Delete(_previewTempVideoPath);
            }
            catch (Exception ex)
            {
                System.Diagnostics.Debug.WriteLine(
                    $"[EditPage] 临时视频清理失败: {ex.Message}");
            }
            finally
            {
                _previewTempVideoPath = null;
            }
        }

        // ════════════════════════════════════════════════════════════
        //  缩放按钮（视频活跃时路由到 PureMediaViewer，否则到 PhotoViewer）
        // ════════════════════════════════════════════════════════════

        /// <summary>视频层是否正在活跃显示</summary>
        private bool IsVideoActive() =>
            PureMediaViewer.Visibility == Visibility.Visible && PureMediaViewer.Opacity > 0.99;

        private void ZoomInButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsVideoActive())
                PureMediaViewer.ZoomIn();
            else
                PhotoViewer.ZoomIn();
            UpdateZoomPercentDisplay();
        }

        private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsVideoActive())
                PureMediaViewer.ZoomOut();
            else
                PhotoViewer.ZoomOut();
            UpdateZoomPercentDisplay();
        }

        private void ZoomPercentButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsVideoActive())
                PureMediaViewer.ToggleFitVsPixel();
            else
                PhotoViewer.ToggleFitVsPixel();
            UpdateZoomPercentDisplay();
        }

        /// <summary>同步缩放百分比显示（相对于 Fit 的整数百分比）</summary>
        private void UpdateZoomPercentDisplay()
        {
            int percent;
            if (IsVideoActive())
                percent = (int)Math.Round(PureMediaViewer.CurrentScale * 100);
            else
                percent = (int)Math.Round(PhotoViewer.CurrentScale * 100);
            ZoomPercentText.Text = $"{percent}%";
        }

        private void PhotoViewer_ScaleChanged(double newScale)
        {
            _sharedZoomScale = newScale;
            if (!IsVideoActive())
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
            if (_isClassicTimelineInitialized) return;
            _isClassicTimelineInitialized = true;

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

        private void ClassicScrollToFrame(TimelineFrame frame,
            int maxRetries = 5, int delayMs = 120)
        {
            _scrollCts?.Cancel();
            _scrollCts?.Dispose();
            _scrollCts = new CancellationTokenSource();
            var ct = _scrollCts.Token;

            TimelineListView.SelectedItem = frame;

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
        //  胶片模式 — 统一滚动管线
        //
        //  所有滚动（滚轮 / ←→ 按钮 / ViewModel 通知）最终都经过
        //  ScrollTimelineBy → ChangeView → ViewChanged 这条管线，
        //  ViewChanged 统一负责同步帧索引 + 触发大图双缓冲更新。
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// 胶片模式初始化：选中框画刷 + 滚轮劫持。
        /// </summary>
        private void InitializeFilmstripTimeline()
        {
            if (_isFilmstripTimelineInitialized) return;
            _isFilmstripTimelineInitialized = true;

            UpdateFilmstripSelectionHighlight();
            FilmstripScrollViewer.AddHandler(
                UIElement.PointerWheelChangedEvent,
                _filmstripWheelHandler,
                handledEventsToo: true);
        }

        // ── 统一滚动核心（DispatcherQueue 原生版）──

        /// <summary>
        /// 绝对目标累加式精确滚动，支持高精度浮点步数。
        /// 所有输入源（滚轮 / 按钮 / 代码）最终汇入此方法。
        ///
        /// DispatcherQueue.TryEnqueue 合并同一渲染帧内的高频滚轮事件：
        ///   _targetScrollOffset 纯数学累加不丢失任何位移，
        ///   仅首事件入队 ChangeView(disableAnimation:false)，
        ///   依赖 WinUI 3 原生物理引擎飞向目标 —— 滚轮偏快、按钮优雅，各走各的原生曲线。
        /// </summary>
        private void ScrollTimelineBy(double steps)
        {
            if (ViewModel.TimelineFrames.Count == 0) return;

            double itemWidth = 56.0;

            // 停顿超 250ms 或代码跳转到任意位置后 → 对齐网格重校准基准
            if ((DateTime.Now - _lastWheelTime).TotalMilliseconds > 250 || _targetScrollOffset < 0)
            {
                _targetScrollOffset = Math.Round(
                    FilmstripScrollViewer.HorizontalOffset / itemWidth) * itemWidth;
            }

            _lastWheelTime = DateTime.Now;

            // 在数学绝对目标上累加，绝不丢失任何一次滚轮位移
            _targetScrollOffset += steps * itemWidth;

            // 边界钳制
            if (_targetScrollOffset < 0)
                _targetScrollOffset = 0;
            if (_targetScrollOffset > FilmstripScrollViewer.ScrollableWidth)
                _targetScrollOffset = FilmstripScrollViewer.ScrollableWidth;

            // 帧合并：同一渲染帧内仅首事件入队 ChangeView
            if (!_isScrollQueued)
            {
                _isScrollQueued = true;
                DispatcherQueue.TryEnqueue(
                    Microsoft.UI.Dispatching.DispatcherQueuePriority.Normal, () =>
                {
                    _isScrollQueued = false;
                    FilmstripScrollViewer.ChangeView(
                        _targetScrollOffset, null, null, disableAnimation: false);
                });
            }
        }

        // ── 滚轮事件 → 统一管线（高精度浮点步数）──

        private void OnFilmstripPointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (!ViewModel.IsFilmstripTimelineMode) return;

            double delta = e.GetCurrentPoint(FilmstripScrollViewer).Properties.MouseWheelDelta;
            // 向上滚 delta>0 → 内容左移（steps<0）
            double steps = -(delta / 120.0);
            ScrollTimelineBy(steps);
            e.Handled = true;
        }

        // ── ← → 按钮 Click → 统一管线 ──

        private void FilmstripPrevButton_Click(object sender, RoutedEventArgs e)
        {
            ScrollTimelineBy(-1);
        }

        private void FilmstripNextButton_Click(object sender, RoutedEventArgs e)
        {
            ScrollTimelineBy(1);
        }

        // ── ScrollViewer 生命周期 ──

        private void FilmstripScrollViewer_Loaded(object sender, RoutedEventArgs e)
        {
            if (!ViewModel.IsFilmstripTimelineMode) return;

            UpdateFilmstripEdgePadding();

            // 同步 ViewModel 选中帧并定位
            if (ViewModel.CurrentKeyFrame != null)
            {
                int idx = ViewModel.TimelineFrames.IndexOf(ViewModel.CurrentKeyFrame);
                if (idx >= 0) _filmstripCurrentFrameIndex = idx;
            }
            FilmstripScrollToFrameIndex(_filmstripCurrentFrameIndex);
        }

        private void FilmstripScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            if (!ViewModel.IsFilmstripTimelineMode) return;
            UpdateFilmstripEdgePadding();

            // Padding 变更 → 布局偏移 → 等布局刷新完成后瞬间归位（无动画）
            DispatcherQueue.TryEnqueue(() =>
            {
                if (_targetScrollOffset >= 0)
                {
                    FilmstripScrollViewer.ChangeView(
                        _targetScrollOffset, null, null, disableAnimation: true);
                }
            });
        }

        private void UpdateFilmstripEdgePadding()
        {
            double vw = FilmstripScrollViewer.ViewportWidth;
            if (vw <= 0) return;

            double padding = (vw / 2.0) - (FilmstripItemWidth / 2.0);
            if (padding < 0) padding = 0;

            FilmstripPaddingBorder.Padding = new Thickness(padding, 0, padding, 0);
        }

        // ── ViewChanged：统一帧选中 + 大图双缓冲触发 ──

        /// <summary>
        /// 滚动结束 → 反算最近帧索引，同步 ViewModel 选中帧。
        /// 无论是滚轮、按钮还是 ViewModel 通知的滚动，最终都在这里
        /// 触发 SelectTimelineFrameInteractively → UpdatePreviewForTimelineFrameAsync
        /// → PhotoViewer 双缓冲大图更新。
        /// </summary>
        private void FilmstripScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (ViewModel.TimelineFrames.Count == 0) return;

            double offset = FilmstripScrollViewer.HorizontalOffset;
            int nearestIndex = (int)Math.Round(offset / FilmstripItemStep);
            nearestIndex = Math.Clamp(nearestIndex, 0, ViewModel.TimelineFrames.Count - 1);

            if (nearestIndex != _filmstripCurrentFrameIndex)
            {
                _filmstripCurrentFrameIndex = nearestIndex;
                // 统一走交互式选中 → 自动触发大图双缓冲更新
                ViewModel.SelectTimelineFrameInteractively(
                    ViewModel.TimelineFrames[nearestIndex]);
            }
        }

        // ── ViewModel 通知跳转（SelectFile 后自动定位到封面帧）──

        /// <summary>
        /// ViewModel 在文件加载完成后通过 RequestScrollToFrame 事件
        /// 通知 View 定位到封面帧。同样走 ChangeView → ViewChanged 管线。
        /// </summary>
        private void FilmstripScrollToFrameIndex(int index, bool disableAnimation = true)
        {
            if (ViewModel.TimelineFrames.Count == 0) return;

            index = Math.Clamp(index, 0, ViewModel.TimelineFrames.Count - 1);
            _filmstripCurrentFrameIndex = index;

            double targetOffset = index * FilmstripItemStep;
            _targetScrollOffset = targetOffset;

            // ── 布局未就绪？重试 ──
            // 根因：Clear() → Add() 后 ItemsRepeater 不会同步完成布局。
            // ScrollableWidth 可能为 0（首次），也可能残留旧值（Clear 未处理完），
            // 导致 ChangeView 静默失败 → 封面不居中。
            //
            // 策略：先立即尝试一次（无动画），若布局已就绪则一次到位；
            //       若未就绪（ScrollableWidth == 0 或小于目标偏移），延迟重试。
            if (FilmstripScrollViewer.ScrollableWidth > 0
                && targetOffset <= FilmstripScrollViewer.ScrollableWidth)
            {
                // 布局已就绪，直接到位
                FilmstripScrollViewer.ChangeView(targetOffset, null, null, disableAnimation: disableAnimation);
                return;
            }

            // 布局未就绪 → 取消旧重试，启动新重试
            _filmstripScrollRetryCts?.Cancel();
            _filmstripScrollRetryCts?.Dispose();
            _filmstripScrollRetryCts = new CancellationTokenSource();
            _ = FilmstripScrollToFrameRetryAsync(index, disableAnimation, _filmstripScrollRetryCts.Token);
        }

        /// <summary>
        /// 胶片模式滚动重试：每 50ms 检查一次 ScrollViewer 是否已完成布局，
        /// 最多重试 10 次（共 500ms）。布局就绪后立即执行 ChangeView。
        /// </summary>
        private async Task FilmstripScrollToFrameRetryAsync(int index, bool disableAnimation, CancellationToken ct, int maxRetries = 10)
        {
            for (int i = 0; i < maxRetries; i++)
            {
                try { await Task.Delay(50, ct); }
                catch (TaskCanceledException) { return; }

                double targetOffset = index * FilmstripItemStep;
                if (FilmstripScrollViewer.ScrollableWidth > 0
                    && targetOffset <= FilmstripScrollViewer.ScrollableWidth)
                {
                    _targetScrollOffset = targetOffset;
                    FilmstripScrollViewer.ChangeView(targetOffset, null, null, disableAnimation: disableAnimation);
                    return;
                }
            }

            // 最终兜底：即使布局可能还没好，也强制执行一次
            double fallbackOffset = index * FilmstripItemStep;
            _targetScrollOffset = fallbackOffset;
            if (FilmstripScrollViewer.ScrollableWidth > 0)
                fallbackOffset = Math.Min(fallbackOffset, FilmstripScrollViewer.ScrollableWidth);
            FilmstripScrollViewer.ChangeView(fallbackOffset, null, null, disableAnimation: disableAnimation);
        }

        private void UpdateFilmstripSelectionHighlight()
        {
            if (FilmstripSelectionHighlight == null) return;
            FilmstripSelectionHighlight.BorderBrush = _selectedBorder;
            FilmstripSelectionHighlight.Background = _selectedBg;
        }

        // ═════════════════════════════════════════════════════════════════
        //  ViewModel → View 滚动通知（双模式派发）
        // ═════════════════════════════════════════════════════════════════

        /// <summary>
        /// ViewModel 通知时间轴选中 cover 帧并居中滚动。
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
                    FilmstripScrollToFrameIndex(index, disableAnimation: false);
            }
        }

        /// <summary>实况→非实况切换时，强制清空 PhotoViewer 双缓冲层</summary>
        private void OnPreviewClearRequested()
        {
            PhotoViewer.ClearImage();
        }

        /// <summary>
        /// 导航回 EditPage 时调用。如果用户在设置页切换了模式，
        /// 此时页面已在前台，正式触发 Visibility 切换 + 强刷绑定 + 恢复滚动。
        ///
        /// 为什么不在 NotifyTimelineModeChanged 中切 Visibility：
        /// WinUI 3 在后台（缓存）页面上切换 Visibility 会导致 x:Bind 绑定断裂，
        /// ListView SelectedItem 双向绑定失效 → 点击缩略图封面不更新、滚动条不响应。
        /// </summary>
        protected override void OnNavigatedTo(Microsoft.UI.Xaml.Navigation.NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);

            if (!ViewModel.NeedsModeSwitchFixup) return;
            ViewModel.NeedsModeSwitchFixup = false;

            // 1. 暂存当前选中帧进度，防止丢失
            var currentFrame = ViewModel.SelectedTimelineFrame;

            // 2. 页面已在前台，正式通知 XAML 切换 Visibility
            ViewModel.TriggerModeVisibilityUpdate();

            DispatcherQueue.TryEnqueue(async () =>
            {
                // 3. 给 WinUI 3 渲染新布局一点时间（生成容器）
                await Task.Delay(100);

                // 4. 重新初始化对应模式（防重复挂接守卫保持原样）
                if (ViewModel.IsFilmstripTimelineMode)
                {
                    InitializeFilmstripTimeline();
                    UpdateFilmstripEdgePadding();
                    UpdateFilmstripSelectionHighlight();
                }
                else if (ViewModel.IsClassicTimelineMode)
                {
                    InitializeClassicTimeline();
                    ForceScrollBarsAlwaysThick();
                }

                // 5. 核心修复：强刷双向绑定，解决"点击缩略图封面不更新"
                if (currentFrame != null)
                {
                    ViewModel.SelectedTimelineFrame = null;
                    ViewModel.SelectedTimelineFrame = currentFrame;

                    // 6. 无缝恢复滚动条位置
                    if (ViewModel.IsFilmstripTimelineMode)
                    {
                        int idx = ViewModel.TimelineFrames.IndexOf(currentFrame);
                        if (idx >= 0)
                            FilmstripScrollToFrameIndex(idx); // disableAnimation: true（瞬间复位）
                    }
                    else if (ViewModel.IsClassicTimelineMode)
                    {
                        ClassicScrollToFrame(currentFrame, maxRetries: 30, delayMs: 200);
                    }
                }
            });
        }

        // ════════════════════════════════════════════════════════════
        //  文件夹浏览 & 路径输入
        // ════════════════════════════════════════════════════════════

        /// <summary>用户点击了浏览按钮 → 抑制本次 LostFocus 扫描</summary>
        private bool _suppressLostFocusScan;

        /// <summary>刷新按钮：重新扫描当前目录</summary>
        /// <summary>
        /// 目录有内容 → 刷新重新扫描；目录为空 → 清空全部内容（等价"大叉号"）。
        /// CurrentDirectory 变化时同步更新图标。
        /// </summary>
        private void RefreshOrClearDir_Click(object sender, RoutedEventArgs e)
        {
            var path = ViewModel.CurrentDirectory;
            if (!string.IsNullOrEmpty(path) && Directory.Exists(path))
            {
                _lastScannedPath = path;
                ViewModel.TriggerScan();
            }
            else
            {
                // 目录为空或路径不存在 → 清空内容（输入的无效路径也一起清掉）
                ViewModel.ClearAll();
                _lastScannedPath = null;
                UpdateRefreshButtonIcon();
            }
        }

        /// <summary>输入框文字变化时实时更新按钮图标（目录有效→↻，无效/空→✕）。</summary>
        private void CurrentDirTextBox_TextChanged(object sender, TextChangedEventArgs e)
        {
            UpdateRefreshButtonIcon();
        }

        /// <summary>当前路径是否有效 → ↻ 刷新；路径为空或不合法 → ✕ 清空。</summary>
        private void UpdateRefreshButtonIcon()
        {
            var path = ViewModel.CurrentDirectory;
            var isValid = !string.IsNullOrEmpty(path) && Directory.Exists(path);
            RefreshDirIcon.Glyph = isValid ? "" : ""; // ↻ vs ✕
            ToolTipService.SetToolTip(RefreshDirBtn,
                ResourceService.GetString(isValid
                    ? "KeyPhotoPage_RefreshDirTooltip"
                    : "KeyPhotoPage_ClearDirTooltip"));
        }

        /// <summary>浏览按钮按下时设标记（早于 LostFocus 触发），防止 LostFocus 误扫描旧路径</summary>
        private void BrowseFolder_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _suppressLostFocusScan = true;
        }

        /// <summary>浏览按钮：弹出文件夹选择器，选中后填充路径并触发扫描</summary>
        private async void BrowseFolder_Click(object sender, RoutedEventArgs e)
        {
            try
            {
                var folder = await FilePickerService.PickFolderAsync();
                if (folder != null)
                {
                    ViewModel.CurrentDirectory = folder.Path;
                    UpdateRefreshButtonIcon();
                    // 浏览按钮选择的路径直接触发扫描（不依赖 LostFocus）
                    _lastScannedPath = folder.Path;
                    ViewModel.TriggerScan();
                }
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"BrowseFolder CRASH: {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Error, ex);
            }
            finally
            {
                // 重置标记（异常时也要重置，防止后续 LostFocus 被永久抑制）
                _suppressLostFocusScan = false;
            }
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



        /// <summary>路径输入框失去焦点时触发扫描（手动输入路径后点击别处的场景）。
        /// 路径与上次扫描相同时跳过，避免无变化时的重复扫描。</summary>
        private void CurrentDirTextBox_LostFocus(object sender, RoutedEventArgs e)
        {
            // 用户点击了浏览按钮 → 跳过（由 BrowseFolder_Click 负责触发）
            if (_suppressLostFocusScan)
            {
                _suppressLostFocusScan = false;
                return;
            }

            // 路径未变 → 跳过，避免重复扫描
            var currentPath = ViewModel.CurrentDirectory;
            if (string.Equals(currentPath, _lastScannedPath, StringComparison.OrdinalIgnoreCase))
                return;

            _lastScannedPath = currentPath;
            UpdateRefreshButtonIcon();
            ViewModel.TriggerScan();
        }

        // ════════════════════════════════════════════════════════════
        //  拖拽事件挂接/解除（构造 + OnNavigatedTo → attach，Unloaded → detach）
        // ════════════════════════════════════════════════════════════

        private void AttachDragEvents()
        {
            LeftPanelBorder.DragEnter += LeftPanel_DragEnter;
            LeftPanelBorder.DragOver += LeftPanel_DragOver;
            LeftPanelBorder.DragLeave += LeftPanel_DragLeave;
            LeftPanelBorder.Drop += LeftPanel_Drop;
            RightPanelBorder.DragEnter += RightPanel_DragEnter;
            RightPanelBorder.DragOver += RightPanel_DragOver;
            RightPanelBorder.DragLeave += RightPanel_DragLeave;
            RightPanelBorder.Drop += RightPanel_Drop;
        }

        private void DetachDragEvents()
        {
            LeftPanelBorder.DragEnter -= LeftPanel_DragEnter;
            LeftPanelBorder.DragOver -= LeftPanel_DragOver;
            LeftPanelBorder.DragLeave -= LeftPanel_DragLeave;
            LeftPanelBorder.Drop -= LeftPanel_Drop;
            RightPanelBorder.DragEnter -= RightPanel_DragEnter;
            RightPanelBorder.DragOver -= RightPanel_DragOver;
            RightPanelBorder.DragLeave -= RightPanel_DragLeave;
            RightPanelBorder.Drop -= RightPanel_Drop;
        }

        // ════════════════════════════════════════════════════════════
        //  拖拽文件夹到左侧面板（Drag & Drop）— 只接受文件夹
        // ════════════════════════════════════════════════════════════

        /// <summary>拖入时异步检测内容是否全是文件夹，缓存结果</summary>
        private async void LeftPanel_DragEnter(object sender, DragEventArgs e)
        {
            _isLeftDropAllFolders = false;
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var deferral = e.GetDeferral();
                try
                {
                    var items = await e.DataView.GetStorageItemsAsync();
                    _isLeftDropAllFolders = items.Count > 0
                        && items.All(i => i is StorageFolder);
                }
                catch { _isLeftDropAllFolders = false; }
                finally { deferral.Complete(); }
            }
        }

        private void LeftPanel_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;

            if (_isLeftDropAllFolders)
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.DragUIOverride.IsGlyphVisible = true;
                e.DragUIOverride.IsCaptionVisible = false;
                DragOverlay.Visibility = Visibility.Visible;
                LeftEmptyHint.Visibility = Visibility.Collapsed; // 遮罩显示时隐藏底层空提示
            }

            e.Handled = true;
        }

        private void LeftPanel_DragLeave(object sender, DragEventArgs e)
        {
            DragOverlay.Visibility = Visibility.Collapsed;
            LeftEmptyHint.ClearValue(UIElement.VisibilityProperty); // 恢复 x:Bind
            _isLeftDropAllFolders = false;
            e.Handled = true;
        }

        /// <summary>
        /// 拖拽释放：提取文件夹路径 → 设置 ViewModel → 触发扫描。
        /// 优先取拖入的文件夹，若拖入的是文件则取其父目录。
        /// </summary>
        private async void LeftPanel_Drop(object sender, DragEventArgs e)
        {
            try
            {
                DragOverlay.Visibility = Visibility.Collapsed;
                LeftEmptyHint.ClearValue(UIElement.VisibilityProperty);

                if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                    return;

                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count == 0) return;

                // 优先取文件夹，否则取第一个文件的父目录
                string? targetPath = null;
                foreach (var item in items)
                {
                    if (item is StorageFolder folder)
                    {
                        targetPath = folder.Path;
                        break;
                    }
                }

                if (targetPath == null && items[0] is StorageFile file)
                    targetPath = Path.GetDirectoryName(file.Path);

                if (string.IsNullOrEmpty(targetPath) || !Directory.Exists(targetPath))
                    return;

                ViewModel.CurrentDirectory = targetPath;
                _lastScannedPath = targetPath;
                ViewModel.TriggerScan();

                e.Handled = true;
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"Drop[Left] CRASH: {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Error, ex);
            }
        }

        // ════════════════════════════════════════════════════════════
        //  拖拽单文件到右侧面板 → 自动检测并加载（Drag & Drop）— 只接受文件
        // ════════════════════════════════════════════════════════════

        /// <summary>拖入时异步检测是否包含媒体文件，缓存结果</summary>
        private async void RightPanel_DragEnter(object sender, DragEventArgs e)
        {
            _isRightDropHasFiles = false;
            if (e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
            {
                var deferral = e.GetDeferral();
                try
                {
                    var items = await e.DataView.GetStorageItemsAsync();
                    _isRightDropHasFiles = items.Count > 0
                        && items.All(i => i is StorageFile)
                        && items.Cast<StorageFile>().Any(
                            f => IsSupportedMediaFile(Path.GetExtension(f.Path)));
                }
                catch { _isRightDropHasFiles = false; }
                finally { deferral.Complete(); }
            }
        }

        private void RightPanel_DragOver(object sender, DragEventArgs e)
        {
            e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.None;

            if (_isRightDropHasFiles)
            {
                e.AcceptedOperation = Windows.ApplicationModel.DataTransfer.DataPackageOperation.Copy;
                e.DragUIOverride.IsGlyphVisible = true;
                e.DragUIOverride.IsCaptionVisible = false;
                RightDragOverlay.Visibility = Visibility.Visible;
                RightEmptyHint.Visibility = Visibility.Collapsed;
                TimelineEmptyHint.Visibility = Visibility.Collapsed;
                DetailPropsPlaceholder.Visibility = Visibility.Collapsed;
            }

            e.Handled = true;
        }

        private void RightPanel_DragLeave(object sender, DragEventArgs e)
        {
            RightDragOverlay.Visibility = Visibility.Collapsed;
            RightEmptyHint.ClearValue(UIElement.VisibilityProperty);
            TimelineEmptyHint.ClearValue(UIElement.VisibilityProperty);
            DetailPropsPlaceholder.ClearValue(UIElement.VisibilityProperty);
            _isRightDropHasFiles = false;
            e.Handled = true;
        }

        private async void RightPanel_Drop(object sender, DragEventArgs e)
        {
            try
            {
                RightDragOverlay.Visibility = Visibility.Collapsed;
                RightEmptyHint.ClearValue(UIElement.VisibilityProperty);
                TimelineEmptyHint.ClearValue(UIElement.VisibilityProperty);
                DetailPropsPlaceholder.ClearValue(UIElement.VisibilityProperty);

                if (!e.DataView.Contains(Windows.ApplicationModel.DataTransfer.StandardDataFormats.StorageItems))
                    return;

                var items = await e.DataView.GetStorageItemsAsync();
                if (items.Count == 0) return;

                // 收集所有拖入的媒体文件路径
                var filePaths = new List<string>();
                foreach (var item in items)
                {
                    if (item is StorageFile file)
                    {
                        var ext = Path.GetExtension(file.Path);
                        if (IsSupportedMediaFile(ext))
                            filePaths.Add(file.Path);
                    }
                }

                if (filePaths.Count == 0) return;

                LogService.FileOp(
                    $"Drop[Right] Received {filePaths.Count} file(s): " +
                    string.Join(", ", filePaths.Select(p => Path.GetFileName(p))),
                    LogLevel.Info);

                // 交给 ViewModel：自动检测配对、去重、加入列表
                var firstNewPath = await ViewModel.LoadDroppedFilesAsync(filePaths);

                // 通过 ListView 选中触发 SelectionChanged → SelectFile，
                // 而不是让 ViewModel 直接调 SelectFile，避免选中态不同步 + 重复加载。
                if (firstNewPath != null)
                {
                    var item = ViewModel.FileItems.FirstOrDefault(f =>
                        string.Equals(f.FilePath, firstNewPath, StringComparison.OrdinalIgnoreCase));
                    if (item != null)
                        FileItemListView.SelectedItem = item;
                }

                e.Handled = true;
            }
            catch (Exception ex)
            {
                LogService.FileOp(
                    $"Drop[Right] CRASH: {ex.GetType().Name}: {ex.Message}",
                    LogLevel.Error, ex);
            }
        }

        /// <summary>判断扩展名是否为支持的图片/视频格式（大小写不敏感）</summary>
        private static bool IsSupportedMediaFile(string ext)
        {
            var lower = ext.ToLowerInvariant();
            return lower is ".heic" or ".heif" or ".jpg" or ".jpeg" or ".png"
                or ".bmp" or ".gif" or ".tiff" or ".tif" or ".webp"
                or ".mov" or ".mp4";
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
                // Loaded 在虚拟化回收复用时重新触发。必须无条件刷新视觉状态：
                // 旧项可能是选中态（蓝色边框），新项不是的话要清除，否则出现"多个选中"假象。
                bool isSelected = IsCardSelected(card);
                UpdateCardVisual(card, isSelected, hovered: false, pressed: false);
            }
        }

        /// <summary>
        /// DataContext 变更时强制刷新卡片视觉（虚拟化回收复用的关键补丁）。
        /// Loaded 触发时 DataContext 可能还指向旧项 → IsCardSelected 误判。
        /// DataContextChanged 一定在数据绑定完成后触发 → 判断准确。
        /// </summary>
        private void CardRoot_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
        {
            if (sender is Border card)
            {
                bool isSelected = IsCardSelected(card);
                UpdateCardVisual(card, isSelected, hovered: false, pressed: false);
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
                RefreshSingleCardVisual(item as EditFileItem, isSelected: false);
            foreach (var item in e.AddedItems)
                RefreshSingleCardVisual(item as EditFileItem, isSelected: true);

            if (FileItemListView.SelectedItem is EditFileItem selected)
                ViewModel.SelectFile(selected.FilePath);
            else
                ViewModel.SelectFile(null);

            // 切换文件 → 重置共享缩放/平移 + 照片 Viewer 归位
            _sharedZoomScale = 1.0;
            _sharedPanX = 0.5;
            _sharedPanY = 0.5;
            PhotoViewer.ResetToFit();

            // 非实况照片自动切"文件基础信息"；实况照片恢复记忆选项卡
            ApplyInfoTabForSelectedFile();

            // 根据新选中的文件类型决定显示模式
            _ = ApplyPreviewModeAsync();
        }

        /// <summary>
        /// 根据当前选中文件类型切换预览模式：
        /// - 纯视频文件 → 自动隐藏 PhotoViewer，显示 PureMediaViewer 播放
        /// - 实况照片 → 显示 PhotoViewer + LIVE 播放按钮
        /// - 普通图片 → 显示 PhotoViewer
        /// - 无选中 → 关闭视频层
        /// </summary>
        private async Task ApplyPreviewModeAsync()
        {
            _isApplyingPreviewMode = true;

            try
            {
                if (PureMediaViewer.Visibility == Visibility.Visible)
                    PureMediaViewer.Close();

                if (ViewModel.IsSelectedFileVideo)
                {
                    var videoPath = ViewModel.SelectedFilePath;
                    if (!string.IsNullOrEmpty(videoPath) && File.Exists(videoPath))
                    {
                        try
                        {
                            var storageFile = await StorageFile.GetFileFromPathAsync(videoPath);
                            var mediaSource = MediaSource.CreateFromStorageFile(storageFile);

                            PureMediaViewer.AutoCloseOnEnd = false;
                            PureMediaViewer.ShowCloseButton = false;
                            PureMediaViewer.ShowTransportControls = true;
                            PureMediaViewer.ZoomEnabled = false;

                            // 视频模式 → 预览面板直角
                            PreviewBorder.CornerRadius = new CornerRadius(0);

                            // 先透明加载（用户仍看到底层控件）
                            PureMediaViewer.VideoSource = mediaSource;
                            PureMediaViewer.Play();

                            // 等第一帧就绪
                            await Task.Delay(100);

                            // 隐藏浮动控件（图片层始终可见，被视频覆盖）
                            LivePhotoBadgeButton.Visibility = Visibility.Collapsed;
                            MuteButton.Visibility = Visibility.Collapsed;
                            ZoomControlsPanel.Visibility = Visibility.Collapsed;

                            // 普通视频不受实况照片静音影响，始终非静音
                            //（用户通过内置传输栏音量按钮自行控制）
                            PureMediaViewer.IsMuted = false;
                            return;
                        }
                        catch (Exception ex)
                        {
                            System.Diagnostics.Debug.WriteLine(
                                $"[EditPage] 视频自动播放失败: {ex.Message}");
                        }
                    }
                }

                // 非视频文件：恢复图片预览模式 + 圆角
                PreviewBorder.CornerRadius = new CornerRadius(10);
                PhotoViewer.Visibility = Visibility.Visible;
                PhotoViewer.Opacity = 1;
                SyncLivePhotoBadgeVisibility();
                ZoomControlsPanel.ClearValue(StackPanel.VisibilityProperty);
            }
            finally
            {
                _isApplyingPreviewMode = false;
            }
        }

        /// <summary>通过数据项找到对应容器中的 Border 并刷新视觉</summary>
        private void RefreshSingleCardVisual(EditFileItem? item, bool isSelected)
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
