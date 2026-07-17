/*
 * PhotoViewer.xaml.cs
 *
 * 独立的高性能图片查看器 UserControl，用于 KeyPhoto 页面大图预览。
 *
 * 架构（参考 Windows 相册）：
 *   Viewport Grid 处理所有 Pointer 手势（滚轮缩放、拖拽平移、双击切换）。
 *   Overlay Canvas 仅承载 HUD 元素（默认 IsHitTestVisible=False，事件穿透）。
 *
 * 缩放方案：
 *   Image.Stretch="Uniform" 让框架完成基础适配 → 图片像素填满 Viewport 且保持宽高比。
 *   CompositeTransform（Scale + Translate）叠加在 Uniform 渲染之上实现缩放和平移。
 *   Uniform 会将内容居中，我们手动计算内容区域的偏移量用于坐标映射。
 *
 * 关键概念：
 *   - 内容区 (contentW × contentH)：Image 内部 Uniform 缩放后的实际画面区域
 *   - 内容偏移 (contentLeft, contentTop)：内容区在 Image 元素中的位置（居中产生的留白）
 *   - CompositeTransform 对整个 Image 元素生效，含留白部分
 *   - _currentScale=1 即 Fit 状态；_pixelScale = nw / contentW 即 100% 像素映射
 *
 * 交互：
 *   - 滚轮：以光标位置为中心缩放（clamp [1.0=Fit, MaxZoom]）
 *   - 拖拽：仅在放大状态下平移
 *   - 双击：Fit ↔ 100%（以双击点为中心）
 *   - 右上角按钮缩放：以 Viewport 中心为锚点
 *   - 图片源变更：自动 ResetToFit()
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using Windows.Foundation;

namespace LivePhotoBox.Controls
{
    public sealed partial class PhotoViewer : UserControl
    {
        // ══════════════════════════════════════════════════════════════
        //  Dependency Properties
        // ══════════════════════════════════════════════════════════════

        public static readonly DependencyProperty ImageSourceProperty =
            DependencyProperty.Register(
                nameof(ImageSource),
                typeof(ImageSource),
                typeof(PhotoViewer),
                new PropertyMetadata(null, OnImageSourceChanged));

        public ImageSource? ImageSource
        {
            get => (ImageSource?)GetValue(ImageSourceProperty);
            set => SetValue(ImageSourceProperty, value);
        }

        public static readonly DependencyProperty IsLoadingProperty =
            DependencyProperty.Register(
                nameof(IsLoading),
                typeof(bool),
                typeof(PhotoViewer),
                new PropertyMetadata(false, OnIsLoadingChanged));

        public bool IsLoading
        {
            get => (bool)GetValue(IsLoadingProperty);
            set => SetValue(IsLoadingProperty, value);
        }

        /// <summary>最大缩放比例（绝对缩放值，默认 10.0 = 1000% of fit）</summary>
        public static readonly DependencyProperty MaxZoomProperty =
            DependencyProperty.Register(
                nameof(MaxZoom),
                typeof(double),
                typeof(PhotoViewer),
                new PropertyMetadata(10.0));

        public double MaxZoom
        {
            get => (double)GetValue(MaxZoomProperty);
            set => SetValue(MaxZoomProperty, value);
        }

        // ══════════════════════════════════════════════════════════════
        //  双缓冲层管理
        // ══════════════════════════════════════════════════════════════

        private enum ActiveLayer { A, B }
        private ActiveLayer _activeLayer = ActiveLayer.A;
        /// <summary>是否有待执行的图层切换（新图已分配但尚未就绪）</summary>
        private bool _pendingSwap;

        private Image ActiveImage => _activeLayer == ActiveLayer.A ? ImageLayerA : ImageLayerB;
        private Image InactiveImage => _activeLayer == ActiveLayer.A ? ImageLayerB : ImageLayerA;
        private CompositeTransform ActiveTransform => _activeLayer == ActiveLayer.A ? ImageTransformA : ImageTransformB;
        private CompositeTransform InactiveTransform => _activeLayer == ActiveLayer.A ? ImageTransformB : ImageTransformA;

        // ══════════════════════════════════════════════════════════════
        //  内部状态
        // ══════════════════════════════════════════════════════════════

        private double _currentScale = 1.0;    // 当前缩放值（1.0 = Fit）
        private double _pixelScale = 1.0;       // 100% 像素映射所需缩放值
        private double _naturalWidth;           // 图片原始像素宽
        private double _naturalHeight;          // 图片原始像素高

        // 拖拽状态
        private bool _isDragging;
        private Point _dragStartPoint;           // Viewport 坐标
        private double _dragStartTranslateX;
        private double _dragStartTranslateY;

        // ══════════════════════════════════════════════════════════════
        //  公开属性
        // ══════════════════════════════════════════════════════════════

        /// <summary>当前缩放比例（相对于 Fit，1.0=Fit，2.0=2x Fit）</summary>
        public double CurrentScale => _currentScale;

        /// <summary>缩放比例发生变化时触发（滚轮/双击/按钮/SizeChanged）</summary>
        public event Action<double>? ScaleChanged;

        // ══════════════════════════════════════════════════════════════
        //  构造函数
        // ══════════════════════════════════════════════════════════════

        public PhotoViewer()
        {
            InitializeComponent();
            SizeChanged += (s, e) => CenterLoadingRing();
        }

        // ══════════════════════════════════════════════════════════════
        //  Dependency Property 变更回调
        // ══════════════════════════════════════════════════════════════

        private static void OnImageSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var viewer = (PhotoViewer)d;

            // null → 忽略，保持当前图像可见（消除 Source=null 导致的闪白）
            if (e.NewValue == null)
                return;

            if (e.NewValue is BitmapImage bmp)
            {
                // BitmapImage：可能已解码（同步 SetSource）或待解码（SetSourceAsync）
                viewer._pendingSwap = true;
                viewer.InactiveImage.Source = bmp;

                if (bmp.PixelWidth > 0 && bmp.PixelHeight > 0)
                {
                    // 已解码完成 → 立即切换
                    viewer.OnNewImageReady(bmp.PixelWidth, bmp.PixelHeight);
                }
                else
                {
                    // 等待异步解码完成
                    bmp.ImageOpened += viewer.OnBitmapImageReadyForSwap;
                }
            }
            else if (e.NewValue is ImageSource src)
            {
                // SoftwareBitmapSource 等：已在赋值前完成 SetBitmapAsync，可直接切换
                viewer._pendingSwap = true;
                viewer.InactiveImage.Source = src;
                // SoftwareBitmapSource 不通过 ImageOpened 通知就绪，
                // 但其构造函数保证 SetBitmapAsync 完成后再赋值，直接切换。
                viewer.OnNewImageReady(0, 0);
            }
        }

        /// <summary>BitmapImage 异步解码完成 → 执行图层切换</summary>
        private void OnBitmapImageReadyForSwap(object sender, RoutedEventArgs e)
        {
            if (sender is BitmapImage bmp)
            {
                bmp.ImageOpened -= OnBitmapImageReadyForSwap;
                if (_pendingSwap)
                    OnNewImageReady(bmp.PixelWidth, bmp.PixelHeight);
            }
        }

        /// <summary>
        /// 新图像已就绪 → 复制变换状态到非活跃层，Opacity 交替切换，
        /// 然后清空旧层 Source 释放内存。
        /// </summary>
        private void OnNewImageReady(double natW, double natH)
        {
            _pendingSwap = false;

            // 同步变换状态到新层
            InactiveTransform.ScaleX = ActiveTransform.ScaleX;
            InactiveTransform.ScaleY = ActiveTransform.ScaleY;
            InactiveTransform.TranslateX = ActiveTransform.TranslateX;
            InactiveTransform.TranslateY = ActiveTransform.TranslateY;

            // 交叉淡入淡出：旧层 → 透明，新层 → 不透明
            ActiveImage.Opacity = 0;
            InactiveImage.Opacity = 1;

            // 释放旧层图片内存
            ActiveImage.Source = null;

            // 翻转活跃层标记
            _activeLayer = _activeLayer == ActiveLayer.A ? ActiveLayer.B : ActiveLayer.A;

            // 更新自然尺寸用于后续缩放计算
            if (natW > 0 && natH > 0)
            {
                _naturalWidth = natW;
                _naturalHeight = natH;
            }

            // 新图像就绪 → 保持当前缩放，仅依新尺寸重夹持平移防白边
            UpdatePixelScale();
            var (tx, ty) = ClampTranslation(_currentScale,
                ActiveTransform.TranslateX, ActiveTransform.TranslateY);
            ApplyTransform(_currentScale, tx, ty);
        }

        private static void OnIsLoadingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var viewer = (PhotoViewer)d;
            viewer.LoadingRing.Visibility = (bool)e.NewValue
                ? Visibility.Visible : Visibility.Collapsed;
            viewer.CenterLoadingRing();
        }

        // ══════════════════════════════════════════════════════════════
        //  公开方法
        // ══════════════════════════════════════════════════════════════

        /// <summary>强制清空双缓冲层图片（用于切换到非图片文件时立即清除预览）</summary>
        public void ClearImage()
        {
            _pendingSwap = false;
            ImageLayerA.Source = null;
            ImageLayerB.Source = null;
            ImageLayerA.Opacity = 1;
            ImageLayerB.Opacity = 1;
            _activeLayer = ActiveLayer.A;
            _naturalWidth = 0;
            _naturalHeight = 0;
            ResetToFit();
        }

        /// <summary>重置缩放使图片自适应容器大小</summary>
        public void ResetToFit()
        {
            UpdatePixelScale();
            // Scale=1, Translate=0 即 Uniform Fit 状态
            ApplyTransform(1.0, 0, 0);
        }

        /// <summary>步进放大（×1.75，Viewport 中心为锚点）</summary>
        public void ZoomIn()
        {
            PerformZoom(_currentScale * 1.75, GetViewportCenter());
        }

        /// <summary>步进缩小（÷1.75，下限 1.0=Fit）</summary>
        public void ZoomOut()
        {
            PerformZoom(_currentScale / 1.75, GetViewportCenter());
        }

        /// <summary>Fit ↔ 100% 切换（Viewport 中心为锚点）</summary>
        public void ToggleFitVsPixel()
        {
            if (_naturalWidth <= 0 || _naturalHeight <= 0) return;

            UpdatePixelScale();
            bool isAtFit = Math.Abs(_currentScale - 1.0) < 0.001;
            PerformZoom(isAtFit ? _pixelScale : 1.0, GetViewportCenter());
        }

        /// <summary>直接设置缩放比例（Viewport 中心为锚点），供外部同步缩放状态</summary>
        public void SetScale(double scale)
        {
            PerformZoom(scale, GetViewportCenter());
        }

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
                px = max > min ? (ActiveTransform.TranslateX - min) / (max - min) : 0.5;
            }
            if (sh > vh + 0.01)
            {
                double min = vh - sh - ct;
                double max = -ct;
                py = max > min ? (ActiveTransform.TranslateY - min) / (max - min) : 0.5;
            }
            return (s, px, py);
        }

        /// <summary>应用完整缩放+平移状态（与 GetZoomPanState 配对）</summary>
        public void ApplyZoomPanState(double scale, double panX, double panY)
        {
            if (_naturalWidth <= 0 || _naturalHeight <= 0) return;
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

        public void SetOverlayContent(UIElement element)
        {
            ClearOverlay();
            OverlayCanvas.Children.Add(element);
        }

        public void ClearOverlay()
        {
            OverlayCanvas.Children.Clear();
            if (!OverlayCanvas.Children.Contains(LoadingRing))
                OverlayCanvas.Children.Add(LoadingRing);
        }

        public void SetVideoSource(Windows.Media.Core.MediaSource? source)
        {
            if (source == null)
            {
                VideoPlayer.Visibility = Visibility.Collapsed;
                VideoPlayer.Source = null;
                ActiveImage.Visibility = Visibility.Visible;
            }
            else
            {
                ImageLayerA.Visibility = Visibility.Collapsed;
                ImageLayerB.Visibility = Visibility.Collapsed;
                VideoPlayer.Source = source;
                VideoPlayer.Visibility = Visibility.Visible;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  布局计算
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 计算 Uniform 内容的布局参数。
        ///
        /// Stretch="Uniform" 下 Image 元素填满整个 ViewportGrid，
        /// 但实际画面内容在 Image 内部做 Uniform 缩放后居中。
        /// 返回内容区的尺寸和偏移量。
        /// </summary>
        private (double left, double top, double width, double height) GetContentLayout()
        {
            double vw = ViewportGrid.ActualWidth;
            double vh = ViewportGrid.ActualHeight;

            if (vw <= 0 || vh <= 0 || _naturalWidth <= 0 || _naturalHeight <= 0)
                return (0, 0, vw, vh);

            double aspect = _naturalWidth / _naturalHeight;
            double viewAspect = vw / vh;

            double cw, ch;
            if (viewAspect >= aspect)
            {
                // Viewport 比图片宽 → 高度约束
                ch = vh;
                cw = vh * aspect;
            }
            else
            {
                // Viewport 比图片高 → 宽度约束
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
            if (cw > 0 && _naturalWidth > 0)
                _pixelScale = _naturalWidth / cw;
            else
                _pixelScale = 1.0;
        }

        // ══════════════════════════════════════════════════════════════
        //  缩放核心
        // ══════════════════════════════════════════════════════════════

        /// <summary>
        /// 执行缩放操作，以 anchor 点（Viewport 坐标）为中心。
        ///
        /// 坐标系统说明：
        ///   Image 元素 = ViewportGrid 大小（Stretch=Uniform 填满）。
        ///   Uniform 将原始图片缩放后放在 Image 内部的内容区 (contentW×contentH)，
        ///   内容区在 Image 内居中偏移 (contentLeft, contentTop)。
        ///   CompositeTransform 对整个 Image 元素生效。
        ///
        /// 核心公式：
        ///   内容区坐标 → Viewport 坐标（经 CompositeTransform）：
        ///     viewX = contentLeft + contentAreaX × Scale + TranslateX
        ///   逆推：Viewport 点 (ax, ay) 对应的内容区坐标：
        ///     contentAreaX = ((ax - contentLeft) - TranslateX) / Scale
        ///   缩放后保持同一内容区点不动：
        ///     newTx = (ax - contentLeft) - contentAreaX × newScale
        /// </summary>
        private void PerformZoom(double targetScale, Point? anchor)
        {
            // Clamp：最小 1.0（Fit），最大 MaxZoom
            double newScale = Math.Clamp(targetScale, 1.0, MaxZoom);
            if (Math.Abs(newScale - _currentScale) < 0.0001) return;

            var (contentLeft, contentTop, _, _) = GetContentLayout();

            Point pt = anchor ?? GetViewportCenter();
            double ax = pt.X;
            double ay = pt.Y;

            // 锚点相对于内容区左上角的坐标
            double relX = ax - contentLeft;
            double relY = ay - contentTop;

            // 锚点在内容区中的坐标（逆 CompositeTransform）
            double cx = (relX - ActiveTransform.TranslateX) / _currentScale;
            double cy = (relY - ActiveTransform.TranslateY) / _currentScale;

            // 新的平移量：保持同一内容区坐标在锚点位置
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
            // 边界夹持：杜绝拖出白边
            var (clampedTx, clampedTy) = ClampTranslation(scale, tx, ty);
            ActiveTransform.ScaleX = scale;
            ActiveTransform.ScaleY = scale;
            ActiveTransform.TranslateX = clampedTx;
            ActiveTransform.TranslateY = clampedTy;

            // 通知外部百分比显示更新
            if (Math.Abs(scale - oldScale) > 0.0001)
                ScaleChanged?.Invoke(scale);
        }

        /// <summary>
        /// 边界夹持——参考 Windows 相册 / ImageGlass 行为：
        ///   图像内容必须始终铺满 Viewport，不允许出现白边。
        ///
        ///   若内容在某方向上小于 Viewport → 居中（禁止拖拽）。
        ///   若内容在某方向上大于 Viewport → 限制平移范围使边缘不会内缩到 Viewport 内部。
        /// </summary>
        private (double tx, double ty) ClampTranslation(double scale, double tx, double ty)
        {
            var (contentLeft, contentTop, contentW, contentH) = GetContentLayout();
            double vw = ViewportGrid.ActualWidth;
            double vh = ViewportGrid.ActualHeight;

            double scaledW = contentW * scale;
            double scaledH = contentH * scale;

            // ── 水平 ──
            double clampedTx;
            if (scaledW <= vw)
            {
                // 内容比 Viewport 窄 → 居中，禁止左右拖拽
                clampedTx = (vw - scaledW) / 2.0 - contentLeft;
            }
            else
            {
                // 内容比 Viewport 宽 → Clamp，杜绝白边
                double minTx = vw - scaledW - contentLeft;  // 右边缘贴 Viewport 右边
                double maxTx = -contentLeft;                 // 左边缘贴 Viewport 左边
                clampedTx = Math.Clamp(tx, minTx, maxTx);
            }

            // ── 垂直 ──
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

        /// <summary>延迟两帧后 ResetToFit（等待 ViewportGrid 完成布局）</summary>
        private void ScheduleResetToFit()
        {
            // 用 DispatcherQueue 串行两次，等 ViewportGrid 完成 Measure/Arrange
            bool enqueued = DispatcherQueue.TryEnqueue(() =>
            {
                DispatcherQueue.TryEnqueue(() =>
                {
                    UpdatePixelScale();
                    ResetToFit();
                });
            });
        }

        // ══════════════════════════════════════════════════════════════
        //  Viewport 事件处理
        // ══════════════════════════════════════════════════════════════

        private void Viewport_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            int delta = e.GetCurrentPoint(ViewportGrid).Properties.MouseWheelDelta;
            double zoomFactor = 1.0 + (delta / 120.0) * 0.14;
            var cursorPos = e.GetCurrentPoint(ViewportGrid).Position;
            PerformZoom(_currentScale * zoomFactor, cursorPos);
            e.Handled = true;
        }

        private void Viewport_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            // 仅放大状态下可拖拽
            if (_currentScale <= 1.001) return;

            _isDragging = true;
            _dragStartPoint = e.GetCurrentPoint(ViewportGrid).Position;
            _dragStartTranslateX = ActiveTransform.TranslateX;
            _dragStartTranslateY = ActiveTransform.TranslateY;

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
            if (_naturalWidth <= 0 || _naturalHeight <= 0) return;

            UpdatePixelScale();
            bool isAtFit = Math.Abs(_currentScale - 1.0) < 0.001;
            var pos = e.GetPosition(ViewportGrid);
            PerformZoom(isAtFit ? _pixelScale : 1.0, pos);
            e.Handled = true;
        }

        private void Viewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdatePixelScale();
            // SizeChanged 后 Uniform 自动重新居中，ResetToFit 重置状态
            ResetToFit();
        }

        // ══════════════════════════════════════════════════════════════
        //  辅助
        // ══════════════════════════════════════════════════════════════

        private void CenterLoadingRing()
        {
            double w = RootGrid.ActualWidth;
            double h = RootGrid.ActualHeight;
            if (w > 0 && h > 0)
            {
                Canvas.SetLeft(LoadingRing, (w - LoadingRing.Width) / 2);
                Canvas.SetTop(LoadingRing, (h - LoadingRing.Height) / 2);
            }
        }
    }
}
