/*
 * PureMediaViewer.xaml.cs
 *
 * 可复用的纯净视频播放器 UserControl。
 * 硬切直跳，无过渡动画。
 *
 * 缩放/平移（参照 PhotoViewer 模式）：
 *   VideoPlayer 挂 CompositeTransform（GPU 加速），
 *   ViewportGrid 接收滚轮/拖拽/双击手势。
 *   Stretch="Uniform" 做基础适配，CompositeTransform 叠加缩放和平移。
 *
 * 依赖属性：
 *   VideoSource      — 视频源（MediaSource）
 *   AutoCloseOnEnd   — 播放完毕是否自动关闭
 *   ShowCloseButton  — 是否显示右上角关闭按钮
 *   MaxZoom          — 最大缩放倍数（默认 10.0）
 *
 * 事件：
 *   CloseRequested   — 视频已关闭，外部可恢复底层控件
 *   ScaleChanged     — 缩放比例变化（外部更新百分比显示）
 */

using LivePhotoBox.Services;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using System;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace LivePhotoBox.Controls
{
    public sealed partial class PureMediaViewer : UserControl
    {
        // ══════════════════════════════════════════════════════════════
        //  依赖属性
        // ══════════════════════════════════════════════════════════════

        public static readonly DependencyProperty VideoSourceProperty =
            DependencyProperty.Register(
                nameof(VideoSource), typeof(MediaSource), typeof(PureMediaViewer),
                new PropertyMetadata(null, OnVideoSourceChanged));

        public MediaSource? VideoSource
        {
            get => (MediaSource?)GetValue(VideoSourceProperty);
            set => SetValue(VideoSourceProperty, value);
        }

        /// <summary>播放完毕是否自动关闭。纯视频设为 false 停在最后一帧</summary>
        public static readonly DependencyProperty AutoCloseOnEndProperty =
            DependencyProperty.Register(
                nameof(AutoCloseOnEnd), typeof(bool), typeof(PureMediaViewer),
                new PropertyMetadata(true));

        public bool AutoCloseOnEnd
        {
            get => (bool)GetValue(AutoCloseOnEndProperty);
            set => SetValue(AutoCloseOnEndProperty, value);
        }

        /// <summary>是否显示右上角关闭按钮。纯视频设为 false</summary>
        public static readonly DependencyProperty ShowCloseButtonProperty =
            DependencyProperty.Register(
                nameof(ShowCloseButton), typeof(bool), typeof(PureMediaViewer),
                new PropertyMetadata(true, OnShowCloseButtonChanged));

        public bool ShowCloseButton
        {
            get => (bool)GetValue(ShowCloseButtonProperty);
            set => SetValue(ShowCloseButtonProperty, value);
        }

        /// <summary>是否显示传输控件（进度条/播放按钮等）。实况照片播放时隐藏，普通视频保留</summary>
        public static readonly DependencyProperty ShowTransportControlsProperty =
            DependencyProperty.Register(
                nameof(ShowTransportControls), typeof(bool), typeof(PureMediaViewer),
                new PropertyMetadata(true, OnShowTransportControlsChanged));

        public bool ShowTransportControls
        {
            get => (bool)GetValue(ShowTransportControlsProperty);
            set => SetValue(ShowTransportControlsProperty, value);
        }

        /// <summary>最大缩放比例（绝对缩放值，默认 10.0 = 1000% of fit）</summary>
        public static readonly DependencyProperty MaxZoomProperty =
            DependencyProperty.Register(
                nameof(MaxZoom), typeof(double), typeof(PureMediaViewer),
                new PropertyMetadata(10.0));

        public double MaxZoom
        {
            get => (double)GetValue(MaxZoomProperty);
            set => SetValue(MaxZoomProperty, value);
        }

        /// <summary>
        /// 是否启用缩放/平移手势。
        /// 实况照片播放时设为 true（自由缩放），普通视频播放时设为 false。
        /// </summary>
        public bool ZoomEnabled { get; set; } = true;

        private static void OnShowTransportControlsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var viewer = (PureMediaViewer)d;
            bool show = (bool)e.NewValue;
            // TransportBar 在构造阶段可能尚未初始化 → 空守卫
            if (viewer.TransportBar == null) return;
            viewer.TransportBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
            if (show) viewer.StartTransportTimer();
            else viewer.StopTransportTimer();
        }

        private static void OnVideoSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var viewer = (PureMediaViewer)d;
            if (e.NewValue is MediaSource source)
            {
                var player = viewer.VideoPlayer.MediaPlayer;
                if (player != null)
                    player.Source = source;
                else
                    viewer._pendingSource = source;
            }
        }

        private static void OnShowCloseButtonChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var viewer = (PureMediaViewer)d;
            viewer.CloseButton.Visibility = (bool)e.NewValue ? Visibility.Visible : Visibility.Collapsed;
        }

        // ══════════════════════════════════════════════════════════════
        //  事件
        // ══════════════════════════════════════════════════════════════

        /// <summary>视频已关闭，外部可恢复底层控件</summary>
        public event EventHandler? CloseRequested;

        /// <summary>缩放比例发生变化时触发（滚轮/双击/按钮/SizeChanged）</summary>
        public event Action<double>? ScaleChanged;

        /// <summary>视频第一帧已就绪（MediaOpened + 布局完成），外部可同步隐藏底层照片</summary>
        public event Action? VideoOpened;

        /// <summary>获取或设置底层 MediaPlayer 的静音状态</summary>
        public bool IsMuted
        {
            get => VideoPlayer.MediaPlayer?.IsMuted ?? false;
            set
            {
                if (VideoPlayer.MediaPlayer != null)
                    VideoPlayer.MediaPlayer.IsMuted = value;
            }
        }

        /// <summary>获取底层 MediaPlayer 实例，供外部订阅 MediaOpened 等事件以获取视频信息</summary>
        public MediaPlayer? Player => VideoPlayer.MediaPlayer;

        // ══════════════════════════════════════════════════════════════
        //  内部状态 — 缩放/平移
        // ══════════════════════════════════════════════════════════════

        private double _currentScale = 1.0;    // 当前缩放值（1.0 = Fit）
        private double _pixelScale = 1.0;       // 100% 像素映射所需缩放值
        private uint _naturalVideoWidth;        // 视频原始像素宽
        private uint _naturalVideoHeight;       // 视频原始像素高

        // 拖拽状态
        private bool _isDragging;
        private Point _dragStartPoint;           // Viewport 坐标
        private double _dragStartTranslateX;
        private double _dragStartTranslateY;

        // ══════════════════════════════════════════════════════════════
        //  公开属性 — 缩放
        // ══════════════════════════════════════════════════════════════

        /// <summary>当前缩放比例（相对于 Fit，1.0=Fit，2.0=2x Fit）</summary>
        public double CurrentScale => _currentScale;

        // ══════════════════════════════════════════════════════════════
        //  内部状态 — 播放器
        // ══════════════════════════════════════════════════════════════

        private bool _isClosing;
        private readonly DispatcherQueue _dispatcherQueue;
        private MediaSource? _pendingSource;

        // ══════════════════════════════════════════════════════════════
        //  构造
        // ══════════════════════════════════════════════════════════════

        public PureMediaViewer()
        {
            this.InitializeComponent();
            _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

            // 用 AddHandler + handledEventsToo 确保 SwapChainPanel 拦截后仍能收到滚轮事件
            ViewportGrid.AddHandler(
                UIElement.PointerWheelChangedEvent,
                new PointerEventHandler(Viewport_PointerWheelChanged),
                handledEventsToo: true);

            VideoPlayer.Loaded += OnVideoPlayerLoaded;
        }

        private void OnVideoPlayerLoaded(object sender, RoutedEventArgs e)
        {
            VideoPlayer.Loaded -= OnVideoPlayerLoaded;

            var player = VideoPlayer.MediaPlayer;
            if (player == null) return;

            player.AudioCategory = MediaPlayerAudioCategory.Movie;
            player.IsLoopingEnabled = false;
            player.MediaEnded += OnMediaEnded;
            player.MediaFailed += OnMediaFailed;
            player.MediaOpened += OnMediaOpened;

            // Loaded 时同步传输栏初始可见性（DP 回调可能在构造时因 null 跳过）
            if (TransportBar != null)
            {
                TransportBar.Visibility = ShowTransportControls
                    ? Visibility.Visible : Visibility.Collapsed;
                if (ShowTransportControls) StartTransportTimer();
            }

            if (_pendingSource != null)
            {
                player.Source = _pendingSource;
                _pendingSource = null;
            }
        }

        /// <summary>
        /// MediaOpened 时获取视频尺寸 + 按宽高比缩放到 ViewportGrid，
        /// 消除 swap chain 黑边；同时初始化像素比供缩放计算。
        /// </summary>
        private void OnMediaOpened(MediaPlayer sender, object args)
        {
            uint vw = sender.PlaybackSession.NaturalVideoWidth;
            uint vh = sender.PlaybackSession.NaturalVideoHeight;
            _naturalVideoWidth = vw;
            _naturalVideoHeight = vh;

            _ = _dispatcherQueue.TryEnqueue(() =>
            {
                FitPlayerToVideoAspect();
                UpdatePixelScale();
                ResetToFit();

                // 外部请求的待定缩放（图片 → 视频缩放同步）
                if (PendingZoomScale > 1.001)
                {
                    PerformZoom(PendingZoomScale, GetViewportCenter());
                    PendingZoomScale = 0;
                }

                // 通知外部：视频第一帧就绪，可同步隐藏底层照片
                VideoOpened?.Invoke();
            });
        }

        /// <summary>
        /// 按视频宽高比缩放 MediaPlayerElement 使其填满 ViewportGrid 不产生黑边。
        /// Uniform-fit：撑满一边，另一边留空（SwapChain 无 letterbox → 无黑底）。
        /// </summary>
        /// <summary>
        /// 按视频宽高比缩放 MediaPlayerElement 使其填满 ViewportGrid 不产生黑边。
        /// 仅在 Stretch.Uniform 模式下生效；其他模式由 Stretch 属性自行控制填充方式。
        /// </summary>
        private void FitPlayerToVideoAspect()
        {
            if (VideoPlayer.Stretch != Stretch.Uniform) return;

            double containerW = ViewportGrid.ActualWidth;
            double containerH = ViewportGrid.ActualHeight;
            if (containerW <= 0 || containerH <= 0) return;
            if (_naturalVideoWidth <= 0 || _naturalVideoHeight <= 0) return;

            double videoAspect = (double)_naturalVideoWidth / _naturalVideoHeight;
            double containerAspect = containerW / containerH;

            double targetW, targetH;
            if (videoAspect > containerAspect)
            {
                // 视频更宽 → 撑满宽度
                targetW = containerW;
                targetH = containerW / videoAspect;
            }
            else
            {
                // 视频更高 → 撑满高度
                targetH = containerH;
                targetW = containerH * videoAspect;
            }

            VideoPlayer.Width = targetW;
            VideoPlayer.Height = targetH;
        }

        // ══════════════════════════════════════════════════════════════
        //  公共 API — 播放控制
        // ══════════════════════════════════════════════════════════════

        /// <summary>立刻显示并播放视频（先透明加载，第一帧就绪后变不透明）</summary>
        public async void Play()
        {
            _isClosing = false;

            // 清除上一次播放残留的显式尺寸（等 MediaOpened 时重新按宽高比适配）
            VideoPlayer.ClearValue(WidthProperty);
            VideoPlayer.ClearValue(HeightProperty);

            // 先可见但透明：用户看到的还是底层的 PhotoViewer
            this.Visibility = Visibility.Visible;
            this.Opacity = 0;
            RootGrid.Opacity = 1.0;

            // 从头播放
            var player = VideoPlayer.MediaPlayer;
            if (player != null)
            {
                player.PlaybackSession.Position = TimeSpan.Zero;
                player.Play();
            }

            // 等第一帧渲染完成
            await Task.Delay(80);

            if (!_isClosing)
            {
                // 变不透明 — 此时 swap chain 已有视频第一帧，不会闪白
                this.Opacity = 1.0;
            }
        }

        /// <summary>
        /// 直接显示出视频（无透明度动画）。
        /// 调用前应确保 MediaOpened 已触发（第一帧已在 swap chain 中），
        /// 由外部在同一帧同时隐藏底层照片，实现原子切换。
        /// </summary>
        public void ShowDirect()
        {
            _isClosing = false;
            // 不重置 Width/Height — FitPlayerToVideoAspect 已在 OnMediaOpened 设好宽高比
            this.Visibility = Visibility.Visible;
            this.Opacity = 1.0;
            RootGrid.Opacity = 1.0;

            var player = VideoPlayer.MediaPlayer;
            if (player != null)
            {
                player.PlaybackSession.Position = TimeSpan.Zero;
                player.Play();
            }
        }

        /// <summary>立刻关闭并触发 CloseRequested，所有状态归零</summary>
        public void Close()
        {
            if (_isClosing) return;
            _isClosing = true;

            // 重置传输栏状态，确保下次打开时 DP 回调能触发定时器重启
            ShowTransportControls = false;
            StopTransportTimer();

            // 重置传输栏 UI 状态
            TimeText.Text = "00:00";
            DurationText.Text = "00:00";
            SeekSlider.Value = 0;
            PlayPauseIcon.Glyph = "";  // 播放图标
            SeekTimeBubble.Visibility = Visibility.Collapsed;
            _lastUserSeekTime = DateTime.MinValue;

            if (VideoPlayer.MediaPlayer != null)
            {
                var player = VideoPlayer.MediaPlayer;
                player.Pause();
                player.PlaybackSession.Position = TimeSpan.Zero;
                player.Source = null;
            }
            RootGrid.Opacity = 0;
            this.Visibility = Visibility.Collapsed;

            _isClosing = false;

            // 先通知外部（让它读取当前缩放值同步回照片）
            CloseRequested?.Invoke(this, EventArgs.Empty);

            // 静默重置缩放状态，不触发 ScaleChanged（避免覆盖 _sharedZoomScale）
            VideoTransform.ScaleX = 1;
            VideoTransform.ScaleY = 1;
            VideoTransform.TranslateX = 0;
            VideoTransform.TranslateY = 0;
            _currentScale = 1.0;
            VideoPlayer.ClearValue(WidthProperty);
            VideoPlayer.ClearValue(HeightProperty);
        }

        // ══════════════════════════════════════════════════════════════
        //  公共 API — 缩放控制
        // ══════════════════════════════════════════════════════════════

        /// <summary>重置缩放使视频自适应容器大小</summary>
        public void ResetToFit()
        {
            ApplyTransform(1.0, 0, 0);
        }

        /// <summary>步进放大（×1.75，Viewport 中心为锚点）</summary>
        public void ZoomIn()
        {
            if (!ZoomEnabled) return;
            PerformZoom(_currentScale * 1.75, GetViewportCenter());
        }

        /// <summary>步进缩小（÷1.75，下限 1.0=Fit）</summary>
        public void ZoomOut()
        {
            if (!ZoomEnabled) return;
            PerformZoom(_currentScale / 1.75, GetViewportCenter());
        }

        /// <summary>Fit ↔ 100% 切换（Viewport 中心为锚点）</summary>
        public void ToggleFitVsPixel()
        {
            if (!ZoomEnabled) return;
            if (_naturalVideoWidth <= 0 || _naturalVideoHeight <= 0) return;

            UpdatePixelScale();
            bool isAtFit = Math.Abs(_currentScale - 1.0) < 0.001;
            PerformZoom(isAtFit ? _pixelScale : 1.0, GetViewportCenter());
        }

        /// <summary>直接设置缩放比例（Viewport 中心为锚点），供外部同步缩放状态</summary>
        public void SetScale(double scale)
        {
            if (!ZoomEnabled) return;
            PerformZoom(scale, GetViewportCenter());
        }

        /// <summary>
        /// 待 MediaOpened 后应用的缩放值（0=不操作）。
        /// 外部在 Play() 前设置，OnMediaOpened 完成后自动应用并清零。
        /// </summary>
        public double PendingZoomScale { get; set; }

        /// <summary>读取当前缩放+平移状态（缩放值 + 水平/垂直位置比例 0~1）</summary>
        public (double scale, double panX, double panY) GetZoomPanState()
        {
            var (cl, ct, cw, ch) = GetContentLayout();
            double vw = ViewportGrid.ActualWidth;
            double vh = ViewportGrid.ActualHeight;
            double s = _currentScale;
            double sw = cw * s;
            double sh = ch * s;

            double px = 0.5, py = 0.5;
            if (sw > vw + 0.01)
            {
                double min = vw - sw - cl;
                double max = -cl;
                px = max > min ? (VideoTransform.TranslateX - min) / (max - min) : 0.5;
            }
            if (sh > vh + 0.01)
            {
                double min = vh - sh - ct;
                double max = -ct;
                py = max > min ? (VideoTransform.TranslateY - min) / (max - min) : 0.5;
            }
            return (s, px, py);
        }

        /// <summary>应用完整缩放+平移状态（与 GetZoomPanState 配对）</summary>
        public void ApplyZoomPanState(double scale, double panX, double panY)
        {
            if (!ZoomEnabled) return;
            if (_naturalVideoWidth <= 0 || _naturalVideoHeight <= 0) return;
            scale = Math.Clamp(scale, 1.0, MaxZoom);

            var (cl, ct, cw, ch) = GetContentLayout();
            double vw = ViewportGrid.ActualWidth;
            double vh = ViewportGrid.ActualHeight;
            double sw = cw * scale;
            double sh = ch * scale;

            double tx, ty;
            if (sw > vw + 0.01)
            {
                double min = vw - sw - cl;
                double max = -cl;
                tx = min + panX * (max - min);
            }
            else { tx = (vw - sw) / 2.0 - cl; }

            if (sh > vh + 0.01)
            {
                double min = vh - sh - ct;
                double max = -ct;
                ty = min + panY * (max - min);
            }
            else { ty = (vh - sh) / 2.0 - ct; }

            ApplyTransform(scale, tx, ty);
        }

        // ══════════════════════════════════════════════════════════════
        //  布局计算
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 计算 Uniform 内容在 ViewportGrid 中的布局参数。
        /// 返回内容区的偏移量和尺寸。
        /// </summary>
        private (double left, double top, double width, double height) GetContentLayout()
        {
            double vw = ViewportGrid.ActualWidth;
            double vh = ViewportGrid.ActualHeight;

            if (vw <= 0 || vh <= 0 || _naturalVideoWidth <= 0 || _naturalVideoHeight <= 0)
                return (0, 0, vw, vh);

            double aspect = (double)_naturalVideoWidth / _naturalVideoHeight;
            double viewAspect = vw / vh;

            double cw, ch;
            if (viewAspect >= aspect)
            {
                ch = vh;
                cw = vh * aspect;
            }
            else
            {
                cw = vw;
                ch = vw / aspect;
            }

            double left = (vw - cw) / 2.0;
            double top = (vh - ch) / 2.0;

            return (left, top, cw, ch);
        }

        /// <summary>更新 100% 像素映射所需缩放值</summary>
        private void UpdatePixelScale()
        {
            var (_, _, cw, _) = GetContentLayout();
            if (cw > 0 && _naturalVideoWidth > 0)
                _pixelScale = _naturalVideoWidth / cw;
            else
                _pixelScale = 1.0;
        }

        // ══════════════════════════════════════════════════════════════
        //  缩放核心
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 执行缩放操作，以 anchor 点（Viewport 坐标）为中心。
        /// 公式与 PhotoViewer 一致。
        /// </summary>
        private void PerformZoom(double targetScale, Point? anchor)
        {
            double newScale = Math.Clamp(targetScale, 1.0, MaxZoom);
            if (Math.Abs(newScale - _currentScale) < 0.0001) return;

            var (contentLeft, contentTop, _, _) = GetContentLayout();

            Point pt = anchor ?? GetViewportCenter();
            double ax = pt.X;
            double ay = pt.Y;

            double relX = ax - contentLeft;
            double relY = ay - contentTop;

            double cx = (relX - VideoTransform.TranslateX) / _currentScale;
            double cy = (relY - VideoTransform.TranslateY) / _currentScale;

            double newTx = relX - cx * newScale;
            double newTy = relY - cy * newScale;

            ApplyTransform(newScale, newTx, newTy);
        }

        private Point GetViewportCenter()
        {
            return new Point(
                ViewportGrid.ActualWidth / 2.0,
                ViewportGrid.ActualHeight / 2.0);
        }

        private void ApplyTransform(double scale, double tx, double ty)
        {
            double oldScale = _currentScale;
            _currentScale = scale;
            var (clampedTx, clampedTy) = ClampTranslation(scale, tx, ty);
            VideoTransform.ScaleX = scale;
            VideoTransform.ScaleY = scale;
            VideoTransform.TranslateX = clampedTx;
            VideoTransform.TranslateY = clampedTy;

            if (Math.Abs(scale - oldScale) > 0.0001)
                ScaleChanged?.Invoke(scale);
        }

        /// <summary>
        /// 边界夹持——视频内容不能出现白边。
        /// 内容小于 Viewport 时居中；大于时限制平移范围。
        /// </summary>
        private (double tx, double ty) ClampTranslation(double scale, double tx, double ty)
        {
            var (contentLeft, contentTop, contentW, contentH) = GetContentLayout();
            double vw = ViewportGrid.ActualWidth;
            double vh = ViewportGrid.ActualHeight;

            double scaledW = contentW * scale;
            double scaledH = contentH * scale;

            double clampedTx;
            if (scaledW <= vw)
            {
                clampedTx = (vw - scaledW) / 2.0 - contentLeft;
            }
            else
            {
                double minTx = vw - scaledW - contentLeft;
                double maxTx = -contentLeft;
                clampedTx = Math.Clamp(tx, minTx, maxTx);
            }

            double clampedTy;
            if (scaledH <= vh)
            {
                clampedTy = (vh - scaledH) / 2.0 - contentTop;
            }
            else
            {
                double minTy = vh - scaledH - contentTop;
                double maxTy = -contentTop;
                clampedTy = Math.Clamp(ty, minTy, maxTy);
            }

            return (clampedTx, clampedTy);
        }

        // ══════════════════════════════════════════════════════════════
        //  Viewport 事件处理
        // ══════════════════════════════════════════════════════════════

        private void Viewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (!ZoomEnabled) return;

            int delta = e.GetCurrentPoint(ViewportGrid).Properties.MouseWheelDelta;
            double zoomFactor = 1.0 + (delta / 120.0) * 0.14;
            var cursorPos = e.GetCurrentPoint(ViewportGrid).Position;
            PerformZoom(_currentScale * zoomFactor, cursorPos);
            e.Handled = true;
        }

        private void Viewport_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (!ZoomEnabled || _currentScale <= 1.001) return;

            _isDragging = true;
            _dragStartPoint = e.GetCurrentPoint(ViewportGrid).Position;
            _dragStartTranslateX = VideoTransform.TranslateX;
            _dragStartTranslateY = VideoTransform.TranslateY;

            ViewportGrid.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void Viewport_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;

            var pos = e.GetCurrentPoint(ViewportGrid).Position;
            ApplyTransform(
                _currentScale,
                _dragStartTranslateX + (pos.X - _dragStartPoint.X),
                _dragStartTranslateY + (pos.Y - _dragStartPoint.Y));

            e.Handled = true;
        }

        private void Viewport_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isDragging) return;
            _isDragging = false;
            ViewportGrid.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        private void Viewport_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
        {
            if (!ZoomEnabled) return;
            if (_naturalVideoWidth <= 0 || _naturalVideoHeight <= 0) return;

            UpdatePixelScale();
            bool isAtFit = Math.Abs(_currentScale - 1.0) < 0.001;
            var pos = e.GetPosition(ViewportGrid);
            PerformZoom(isAtFit ? _pixelScale : 1.0, pos);
            e.Handled = true;
        }

        // ══════════════════════════════════════════════════════════════
        //  布局事件
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// RootGrid 尺寸变化 → 更新裁剪区域 + 重新计算布局。
        /// 裁剪防止缩放后的视频溢出到 CloseButton 区域。
        /// </summary>
        private void RootGrid_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            // 用裁剪限制视频视觉区域不超出播放器边界
            RootGrid.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, e.NewSize.Width, e.NewSize.Height)
            };

            // 容器大小变化 → 重新按宽高比适配播放器尺寸
            FitPlayerToVideoAspect();

            // 重新计算像素比并夹持平移
            UpdatePixelScale();
            if (_currentScale > 1.0)
            {
                var (clampedTx, clampedTy) = ClampTranslation(
                    _currentScale, VideoTransform.TranslateX, VideoTransform.TranslateY);
                ApplyTransform(_currentScale, clampedTx, clampedTy);
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  事件处理 — 播放器生命周期
        // ══════════════════════════════════════════════════════════════

        private void OnMediaEnded(MediaPlayer sender, object args)
        {
            _ = _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                if (AutoCloseOnEnd) Close();
            });
        }

        private void OnMediaFailed(MediaPlayer sender, MediaPlayerFailedEventArgs args)
        {
            System.Diagnostics.Debug.WriteLine(
                $"[PureMediaViewer] MediaFailed: {args.Error} - {args.ErrorMessage}");
            _ = _dispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                Close();
            });
        }

        // ══════════════════════════════════════════════════════════════
        //  传输栏事件
        // ══════════════════════════════════════════════════════════════

        private void PlayPauseBtn_Click(object sender, RoutedEventArgs e)
        {
            var player = VideoPlayer.MediaPlayer;
            if (player == null) return;
            if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                player.Pause();
            else
                player.Play();
        }

        /// <summary>
        /// 上次用户操作进度条的时间（防定时器在拖拽期间覆盖滑块）。
        /// </summary>
        private DateTime _lastUserSeekTime = DateTime.MinValue;

        /// <summary>
        /// 拖拽/点击进度条 → 跳转 + 气泡时间，定时器刷新忽略（距离 < 0.25s）。
        /// </summary>
        private void SeekSlider_ValueChanged(object sender, RangeBaseValueChangedEventArgs e)
        {
            var player = VideoPlayer.MediaPlayer;
            if (player == null) return;

            double expected = player.PlaybackSession.Position.TotalSeconds;
            double newVal = e.NewValue;
            if (Math.Abs(newVal - expected) < 0.25) return;  // 定时器刷新 → 忽略

            var duration = player.PlaybackSession.NaturalDuration;
            if (duration.TotalSeconds <= 0) return;

            double secs = Math.Clamp(newVal, 0, duration.TotalSeconds);
            player.PlaybackSession.Position = TimeSpan.FromSeconds(secs);
            TimeText.Text = FormatTime(secs);

            // 浮动气泡 + 标记时间防定时器干扰
            _lastUserSeekTime = DateTime.Now;
            SeekTimeBubbleText.Text = FormatTime(secs);
            SeekTimeBubble.Visibility = Visibility.Visible;
            StartBubbleHideTimer();
        }

        private DispatcherQueueTimer? _bubbleHideTimer;
        private void StartBubbleHideTimer()
        {
            _bubbleHideTimer?.Stop();
            _bubbleHideTimer = _dispatcherQueue.CreateTimer();
            _bubbleHideTimer.Interval = TimeSpan.FromMilliseconds(800);
            _bubbleHideTimer.IsRepeating = false;
            _bubbleHideTimer.Tick += (s, e) =>
            {
                SeekTimeBubble.Visibility = Visibility.Collapsed;
                _bubbleHideTimer = null;
            };
            _bubbleHideTimer.Start();
        }

        private void VolumeBtn_Click(object sender, RoutedEventArgs e)
        {
            IsMuted = !IsMuted;
            VolumeIcon.Glyph = IsMuted ? "" : "";
        }

        private static readonly string[] StretchResKeys =
        {
            "PureMediaViewer_Stretch_Uniform",
            "PureMediaViewer_Stretch_UniformToFill",
            "PureMediaViewer_Stretch_Fill",
            "PureMediaViewer_Stretch_None",
        };
        private static readonly Stretch[] StretchModes = { Stretch.Uniform, Stretch.UniformToFill, Stretch.Fill, Stretch.None };
        private int _stretchIdx;
        private void StretchBtn_Click(object sender, RoutedEventArgs e)
        {
            _stretchIdx = (_stretchIdx + 1) % StretchModes.Length;
            var mode = StretchModes[_stretchIdx];
            VideoPlayer.Stretch = mode;

            if (mode == Stretch.Uniform)
            {
                VideoPlayer.HorizontalAlignment = HorizontalAlignment.Center;
                VideoPlayer.VerticalAlignment = VerticalAlignment.Center;
                FitPlayerToVideoAspect();
            }
            else
            {
                VideoPlayer.ClearValue(WidthProperty);
                VideoPlayer.ClearValue(HeightProperty);
                VideoPlayer.HorizontalAlignment = HorizontalAlignment.Stretch;
                VideoPlayer.VerticalAlignment = VerticalAlignment.Stretch;
            }

            // 居中气泡显示当前模式名，复用 SeekTimeBubble 样式
            var label = ResourceService.GetString(StretchResKeys[_stretchIdx]);
            SeekTimeBubbleText.Text = label;
            SeekTimeBubble.Visibility = Visibility.Visible;
            StartBubbleHideTimer();
        }

        /// <summary>定时刷新进度条/时间/播放按钮状态</summary>
        private DispatcherQueueTimer? _transportTimer;
        private void StartTransportTimer()
        {
            if (_transportTimer != null) return;
            _transportTimer = _dispatcherQueue.CreateTimer();
            _transportTimer.Interval = TimeSpan.FromMilliseconds(250);
            _transportTimer.Tick += (s, e) => UpdateTransportUI();
            _transportTimer.Start();
        }
        private void StopTransportTimer()
        {
            _transportTimer?.Stop();
            _transportTimer = null;
        }

        private void UpdateTransportUI()
        {
            var player = VideoPlayer.MediaPlayer;
            if (player == null) return;
            var session = player.PlaybackSession;
            var dur = session.NaturalDuration;
            if (dur.TotalSeconds <= 0) return;

            double total = dur.TotalSeconds;
            double pos = session.Position.TotalSeconds;

            // 更新图标
            bool playing = session.PlaybackState == MediaPlaybackState.Playing;
            PlayPauseIcon.Glyph = playing ? "" : "";

            // 更新进度条（用户操作后 300ms 内不覆盖，防回跳）
            SeekSlider.Maximum = total;
            if ((DateTime.Now - _lastUserSeekTime).TotalMilliseconds > 300)
                SeekSlider.Value = Math.Clamp(pos, 0, total);

            // 更新时间文本
            TimeText.Text = FormatTime(pos);
            DurationText.Text = FormatTime(total);
        }

        private static string FormatTime(double seconds)
        {
            var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
            return t.Hours > 0
                ? $"{t.Hours}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes:D2}:{t.Seconds:D2}";
        }

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
