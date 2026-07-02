/*
 * LightboxPreview.xaml.cs
 *
 * 全屏预览控件（Lightbox）。继承 UserControl，提供沉浸式图片/视频浏览：
 *   - 图片交叉淡入淡出翻页 + 缩放手势（ScrollViewer ZoomMode）
 *   - 双播放器槽位实现视频无缝切换（TCS 事件驱动，无忙等轮询）
 *   - 视频播放控制栏（暂停/进度/时间/音量，3 秒无操作自动隐藏）
 *   - 底部缩略图导航条（虚拟化按需加载）
 *   - LIVE 按钮脉冲动画 + Acrylic 玻璃底板
 *   - 关闭按钮悬浮缩放动画
 *   - 实况照片播放（单次播放，播完自动恢复照片）
 *   - Acrylic 半透明磨砂背景
 *
 * 对应 ViewModel：无（由调用方传入文件列表）
 *
 * 生命周期：
 *   - ShowAsync(items, startIndex) → 打开预览，构建缩略图
 *   - 键盘/鼠标/缩略图条导航 → 翻页 → 交叉淡入淡出
 *   - Close() → 关闭并清理资源（含临时视频文件）
 */

using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Threading;
using System.Threading.Tasks;
using Windows.Foundation;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage;
using Windows.System;

namespace LivePhotoBox.Controls
{
    public sealed partial class LightboxPreview : UserControl
    {
        // ── 静态资源 ──────────────────────────────────

        private static readonly ImagePreviewService _previewService = new(
            maxCacheSize: 40, decodePixelWidth: 1920, preloadForward: 6, preloadBackward: 2);

        // ── 字段 ──────────────────────────────────────

        private IReadOnlyList<string> _paths = Array.Empty<string>();
        private IReadOnlyList<LightboxItem> _items = Array.Empty<LightboxItem>();
        private int _currentIndex = -1;
        private int _lastDirection = 1;
        private bool _isNavigating;
        private int _activeVideoSlot = -1;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _transportUpdateTimer;
        private KeyEventHandler? _pageKeyDownHandler;
        private bool _isLiveVideoPlaying;
        private string? _extractedVideoPath;
        private bool _isUserSeeking;
        private bool _isThumbnailNavigating;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _transportAutoHideTimer;

        // ── 属性 ──────────────────────────────────────

        public bool IsOpen => LightboxOverlay.Visibility == Visibility.Visible;

        /// <summary>缩略图导航条数据源（x:Bind 绑定）。</summary>
        public ObservableCollection<ThumbnailStripItem> ThumbnailItems { get; } = new();

        // ── 字段（续）─────────────────────────────────

        private Brush? _liveButtonDefaultBg; // 保存 XAML 原始 AcrylicBrush

        // ── 构造函数 ──────────────────────────────────

        public LightboxPreview()
        {
            InitializeComponent();
            _pageKeyDownHandler = new KeyEventHandler(OnKeyDown);
            AddHandler(UIElement.KeyDownEvent, _pageKeyDownHandler, true);
            _liveButtonDefaultBg = LivePhotoButton.Background; // 保存原始 Acrylic
        }

        // ── 公开 API ──────────────────────────────────

        /// <summary>向后兼容重载：从文件路径列表打开灯箱。</summary>
        public async Task ShowAsync(IReadOnlyList<string> paths, int startIndex)
        {
            var items = await LightboxItemSource.FromPathsAsync(paths);
            await ShowAsync(items, startIndex);
        }

        /// <summary>从 LightboxItem 列表打开灯箱，构建缩略图条。</summary>
        public async Task ShowAsync(IReadOnlyList<LightboxItem> items, int startIndex)
        {
            if (items == null || items.Count == 0) return;
            if (startIndex < 0 || startIndex >= items.Count) return;
            _items = items;
            _paths = items.Select(i => i.ImagePath).ToList();

            // 构建缩略图条数据（不加载图片，延迟到滚动可见时）
            ThumbnailItems.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                ThumbnailItems.Add(new ThumbnailStripItem
                {
                    ImagePath = items[i].ImagePath,
                    Index = i
                });
            }

            // 先显示灯箱外壳（spinner），再异步加载内容 — 点击即开，无迟滞
            LightboxOverlay.Visibility = Visibility.Visible;
            LightboxSpinner.Visibility = Visibility.Visible;

            await ShowItemAsync(startIndex, 1);
        }

        /// <summary>关闭灯箱，清理所有资源。</summary>
        public void Close()
        {
            StopLiveVideo();
            StopTransportTimer();
            StopTransportAutoHide();
            HideAllVideos();
            LightboxImage.Source = null;
            LightboxSpinner.Visibility = Visibility.Collapsed;
            LightboxOverlay.Visibility = Visibility.Collapsed;
            _currentIndex = -1;
            LivePhotoButton.Visibility = Visibility.Collapsed;
            LivePulseSb.Stop();
            ThumbnailItems.Clear();

            if (_extractedVideoPath != null)
            {
                try { File.Delete(_extractedVideoPath); } catch { }
                _extractedVideoPath = null;
            }
        }

        // ── 视频槽位 ──────────────────────────────────

        private MediaPlayerElement ActiveVideo =>
            _activeVideoSlot == 0 ? LightboxVideo0 :
            _activeVideoSlot == 1 ? LightboxVideo1 : null!;

        private MediaPlayerElement InactiveVideo =>
            _activeVideoSlot == 0 ? LightboxVideo1 :
            _activeVideoSlot == 1 ? LightboxVideo0 : LightboxVideo0;

        private void HideAllVideos()
        {
            LightboxVideo0.MediaPlayer.Pause();
            LightboxVideo1.MediaPlayer.Pause();
            LightboxVideo0.Visibility = Visibility.Collapsed;
            LightboxVideo1.Visibility = Visibility.Collapsed;
            _activeVideoSlot = -1;
            VideoTransportBar.Visibility = Visibility.Collapsed;
            StopTransportAutoHide();
        }

        // ── 核心导航 ──────────────────────────────────

        private async Task ShowItemAsync(int index, int direction)
        {
            _currentIndex = index;
            _lastDirection = direction;
            string path = _paths[index];

            UpdateLiveButton(index);

            if (IsVideoFile(path))
            {
                await ShowVideoAsync(path);
                _previewService.PreloadNeighbors(_paths, index, direction);
            }
            else
            {
                await ShowImageAsync(path);
                _previewService.PreloadNeighbors(_paths, index, direction);
            }

            LightboxCounter.Text = $"{index + 1} / {_paths.Count}";
            ScrollThumbnailIntoView(index);
        }

        // ── 图片显示（旧内容保持直到新图就绪，避免空档闪烁）─

        private async Task ShowImageAsync(string path)
        {
            StopTransportTimer();
            StopTransportAutoHide();

            // 保持当前内容可见，先加载新图
            var sw = System.Diagnostics.Stopwatch.StartNew();
            LightboxSpinner.Visibility = Visibility.Visible;
            var newImage = await _previewService.LoadCurrentAsync(path);
            long elapsed = sw.ElapsedMilliseconds;

            // 新图就绪了，现在一次性切换：隐藏旧内容 → 显示新图
            LightboxImage.Opacity = 0.0;           // 透明换源，防闪烁
            LightboxImage.Source = newImage;
            LightboxImage.Visibility = Visibility.Visible;
            HideAllVideos();                        // 此时才隐藏视频（新图已就绪）
            LightboxSpinner.Visibility = Visibility.Collapsed;

            if (elapsed < 80)
            {
                // 缓存命中 → 瞬间恢复
                LightboxImage.Opacity = 1.0;
            }
            else
            {
                // 慢加载 → 100ms 快速淡入
                ImageFadeInSb.Children[0].Duration = TimeSpan.FromMilliseconds(100);
                await RunStoryboardAsync(ImageFadeInSb);
            }
        }

        // ── 视频显示（TCS 事件驱动，无忙等轮询）───────

        private async Task ShowVideoAsync(string path)
        {
            StopTransportTimer();
            StopTransportAutoHide();

            var nextPlayer = InactiveVideo;
            int nextSlot = _activeVideoSlot == 0 ? 1 : 0;
            nextPlayer.MediaPlayer.IsLoopingEnabled = true;
            nextPlayer.MediaPlayer.IsMuted = false;
            nextPlayer.MediaPlayer.Volume = 1.0;

            LightboxSpinner.Visibility = Visibility.Visible;
            var source = MediaSource.CreateFromUri(new Uri(path));
            bool opened = await WaitForMediaOpenedAsync(nextPlayer, source);
            LightboxSpinner.Visibility = Visibility.Collapsed;
            if (!opened) return;

            if (_activeVideoSlot >= 0)
            {
                ActiveVideo.MediaPlayer.Pause();
                ActiveVideo.Visibility = Visibility.Collapsed;
            }
            nextPlayer.Visibility = Visibility.Visible;
            _activeVideoSlot = nextSlot;
            LightboxImage.Visibility = Visibility.Collapsed;

            ShowVideoTransport();
            StartTransportTimer();
        }

        // ── 视频加载器（TCS 替代忙等轮询）─────────────

        private static async Task<bool> WaitForMediaOpenedAsync(MediaPlayerElement player,
            MediaSource source, int timeoutMs = 5000)
        {
            var tcs = new TaskCompletionSource<bool>();
            TypedEventHandler<MediaPlayer, object> onOpened = (s, a) => tcs.TrySetResult(true);
            TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> onFailed = (s, a) => tcs.TrySetResult(false);

            player.MediaPlayer.MediaOpened += onOpened;
            player.MediaPlayer.MediaFailed += onFailed;
            try
            {
                player.Source = source;
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs));
                return completed == tcs.Task && tcs.Task.Result;
            }
            finally
            {
                player.MediaPlayer.MediaOpened -= onOpened;
                player.MediaPlayer.MediaFailed -= onFailed;
            }
        }

        // ── 播控栏 ────────────────────────────────────

        private void ShowVideoTransport()
        {
            VideoTransportBar.Visibility = Visibility.Visible;
            UpdateTransportUI();
            StartTransportAutoHide();
        }

        private void StartTransportAutoHide()
        {
            StopTransportAutoHide();
            _transportAutoHideTimer = DispatcherQueue.CreateTimer();
            _transportAutoHideTimer.Interval = TimeSpan.FromSeconds(3);
            _transportAutoHideTimer.Tick += TransportAutoHide_Tick;
            _transportAutoHideTimer.Start();
        }

        private void StopTransportAutoHide()
        {
            if (_transportAutoHideTimer != null)
            {
                _transportAutoHideTimer.Stop();
                _transportAutoHideTimer.Tick -= TransportAutoHide_Tick;
                _transportAutoHideTimer = null;
            }
        }

        private void TransportAutoHide_Tick(Microsoft.UI.Dispatching.DispatcherQueueTimer sender, object args)
        {
            if (_isUserSeeking) return;
            VideoTransportBar.Visibility = Visibility.Collapsed;
            StopTransportAutoHide();
        }

        private void TransportPlayPause_Click(object sender, RoutedEventArgs e)
        {
            if (_activeVideoSlot < 0) return;
            var player = ActiveVideo.MediaPlayer;
            if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                player.Pause();
            else
                player.Play();
            UpdateTransportPlayPauseIcon();
            StartTransportAutoHide();
        }

        // ── 自定义进度条交互（纯 Border，无 Slider 视觉状态）──

        private void TransportSeekBar_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_activeVideoSlot < 0) return;
            _isUserSeeking = true;
            ThumbTransform.ScaleX = 1.5;
            ThumbTransform.ScaleY = 1.5;
            SeekToPointer(e);
            TransportSeekBar.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void TransportSeekBar_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isUserSeeking) return;
            SeekToPointer(e);
        }

        private void TransportSeekBar_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            _isUserSeeking = false;
            ThumbTransform.ScaleX = 1.0;
            ThumbTransform.ScaleY = 1.0;
            TransportSeekBar.ReleasePointerCapture(e.Pointer);
            StartTransportAutoHide();
        }

        private void SeekToPointer(PointerRoutedEventArgs e)
        {
            var pt = e.GetCurrentPoint(TransportSeekBar);
            double ratio = Math.Clamp(pt.Position.X / Math.Max(1, TransportSeekBar.ActualWidth), 0, 1);
            var session = ActiveVideo.MediaPlayer.PlaybackSession;
            if (session == null || session.NaturalDuration.TotalSeconds <= 0) return;
            var newPos = TimeSpan.FromSeconds(ratio * session.NaturalDuration.TotalSeconds);
            ActiveVideo.MediaPlayer.PlaybackSession.Position = newPos;
            TransportCurrentTime.Text = FormatTime(newPos);
            TransportSeekFill.Width = ratio * TransportSeekBar.ActualWidth;
            PositionSeekThumb(ratio);
        }

        private void TransportVolume_Click(object sender, RoutedEventArgs e)
        {
            if (_activeVideoSlot < 0) return;
            var player = ActiveVideo.MediaPlayer;
            player.IsMuted = !player.IsMuted;
            UpdateTransportVolumeIcon(player.IsMuted);
            StartTransportAutoHide();
        }

        private void UpdateTransportUI()
        {
            if (_activeVideoSlot < 0) return;
            if (_isUserSeeking) return;

            var session = ActiveVideo.MediaPlayer.PlaybackSession;
            if (session == null) return;
            var dur = session.NaturalDuration;
            if (dur.TotalSeconds <= 0) return;

            var pos = session.Position;
            double ratio = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1);

            TransportSeekFill.Width = ratio * TransportSeekBar.ActualWidth;
            PositionSeekThumb(ratio);
            TransportCurrentTime.Text = FormatTime(pos);
            TransportTotalTime.Text = FormatTime(dur);
            UpdateTransportPlayPauseIcon();
        }

        private void PositionSeekThumb(double ratio)
        {
            double barW = TransportSeekBar.ActualWidth;
            if (barW <= 0) return;
            double left = Math.Clamp(ratio * barW - 7, -7, barW - 7); // 7 = thumb half-width
            TransportSeekThumb.Margin = new Thickness(left, 0, 0, 0);
        }

        private void UpdateTransportPlayPauseIcon()
        {
            if (_activeVideoSlot < 0) return;
            bool playing = ActiveVideo.MediaPlayer.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
            TransportPlayPauseIcon.Glyph = playing ? "" : "";
        }

        private void UpdateTransportVolumeIcon(bool isMuted)
        {
            TransportVolumeIcon.Glyph = isMuted ? "" : "";
        }

        // ── 播控栏进度定时器 ──────────────────────────

        private void StartTransportTimer()
        {
            StopTransportTimer();
            _transportUpdateTimer = DispatcherQueue.CreateTimer();
            _transportUpdateTimer.Interval = TimeSpan.FromMilliseconds(33); // ~30 FPS
            _transportUpdateTimer.Tick += (s, e) => UpdateTransportUI();
            _transportUpdateTimer.Start();
        }

        private void StopTransportTimer()
        {
            if (_transportUpdateTimer != null)
            {
                _transportUpdateTimer.Stop();
                _transportUpdateTimer = null;
            }
        }

        // ── 进度条圆点 hover 动画 ─────────────────────

        private void TransportSeekThumb_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            ThumbTransform.ScaleX = 1.5;
            ThumbTransform.ScaleY = 1.5;
        }

        private void TransportSeekThumb_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            ThumbTransform.ScaleX = 1.0;
            ThumbTransform.ScaleY = 1.0;
        }

        // ── 指针移动 → 显示播控栏 ─────────────────────

        private void LightboxOverlay_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (_activeVideoSlot < 0) return;
            if (VideoTransportBar.Visibility != Visibility.Visible)
            {
                VideoTransportBar.Visibility = Visibility.Visible;
                StartTransportAutoHide();
            }
            else
            {
                StartTransportAutoHide();
            }
        }

        // ── LIVE 按钮 ──────────────────────────────────

        /// <summary>强制重置 LIVE 按钮到正常状态（背景 + 缩放）。</summary>
        private void ResetLiveButtonState()
        {
            LivePhotoButton.Background = _liveButtonDefaultBg!;
            var visual = ElementCompositionPreview.GetElementVisual(LivePhotoButton);
            visual.CenterPoint = new Vector3((float)(LivePhotoButton.ActualWidth / 2.0),
                                              (float)(LivePhotoButton.ActualHeight / 2.0), 1f);
            var spring = visual.Compositor.CreateSpringVector3Animation();
            spring.DampingRatio = 0.55f;
            spring.Period = TimeSpan.FromMilliseconds(50);
            spring.FinalValue = new Vector3(1.0f);
            visual.StartAnimation("Scale", spring);
        }

        private void UpdateLiveButton(int index)
        {
            if (index < 0 || index >= _items.Count)
            {
                LivePhotoButton.Visibility = Visibility.Collapsed;
                LivePulseSb.Stop();
                return;
            }
            var item = _items[index];
            if (item.IsLivePhoto)
            {
                LivePhotoButton.Visibility = Visibility.Visible;
                LivePulseSb.Begin();
            }
            else
            {
                LivePhotoButton.Visibility = Visibility.Collapsed;
                LivePulseSb.Stop();
            }
        }

        private async void LivePhotoButton_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_isLiveVideoPlaying) return;
            if (_currentIndex < 0 || _currentIndex >= _items.Count) return;

            var item = _items[_currentIndex];
            if (!item.IsLivePhoto) return;

            string? videoSource = item.VideoPath;
            if (videoSource == null && item.AppendedVideoLength > 0)
            {
                LightboxSpinner.Visibility = Visibility.Visible;
                LivePhotoButton.Visibility = Visibility.Collapsed;
                LivePulseSb.Stop();
                try
                {
                    videoSource = await ExtractAppendedVideoAsync(item.ImagePath, item.AppendedVideoLength);
                    if (videoSource != null)
                        _extractedVideoPath = videoSource;
                }
                catch { videoSource = null; }
                LightboxSpinner.Visibility = Visibility.Collapsed;
            }

            if (videoSource == null || !File.Exists(videoSource))
            {
                UpdateLiveButton(_currentIndex);
                return;
            }

            await PlayLiveVideoAsync(videoSource);
        }

        private static async Task<string?> ExtractAppendedVideoAsync(string filePath, long videoLength)
        {
            await Task.Yield();
            string tempPath = Path.Combine(Path.GetTempPath(), $"lpb_live_{Guid.NewGuid():N}.mp4");
            using var fs = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read);
            fs.Seek(fs.Length - videoLength, SeekOrigin.Begin);
            using var outFs = new FileStream(tempPath, FileMode.Create);
            byte[] buffer = new byte[81920];
            long remaining = videoLength;
            while (remaining > 0)
            {
                int read = fs.Read(buffer, 0, (int)Math.Min(buffer.Length, remaining));
                if (read == 0) break;
                outFs.Write(buffer, 0, read);
                remaining -= read;
            }
            return tempPath;
        }

        private async Task PlayLiveVideoAsync(string videoPath)
        {
            StopLiveVideo();
            _isLiveVideoPlaying = true;
            ResetLiveButtonState(); // 防止 hover 状态"冻"住
            LivePhotoButton.Visibility = Visibility.Collapsed;
            LivePulseSb.Stop();
            StopTransportTimer();
            HideAllVideos();

            var player = InactiveVideo;
            int slot = _activeVideoSlot == 0 ? 1 : 0;
            player.MediaPlayer.IsLoopingEnabled = false;
            player.MediaPlayer.IsMuted = false;
            player.MediaPlayer.Volume = 1.0;

            void OnEnded(MediaPlayer sender, object args)
            {
                sender.MediaEnded -= OnEnded;
                _ = DispatcherQueue.TryEnqueue(RestorePhotoAfterLiveVideo);
            }
            player.MediaPlayer.MediaEnded += OnEnded;

            var source = MediaSource.CreateFromUri(new Uri(videoPath));
            bool opened = await WaitForMediaOpenedAsync(player, source);
            if (!opened || !_isLiveVideoPlaying) return;

            if (_activeVideoSlot >= 0)
            {
                ActiveVideo.MediaPlayer.Pause();
                ActiveVideo.Visibility = Visibility.Collapsed;
            }
            player.Visibility = Visibility.Visible;
            _activeVideoSlot = slot;
            LightboxImage.Visibility = Visibility.Collapsed;

            ShowVideoTransport();
            StartTransportTimer();
        }

        private void StopLiveVideo()
        {
            if (!_isLiveVideoPlaying) return;
            _isLiveVideoPlaying = false;
            HideAllVideos();
            RestorePhotoAfterLiveVideo();
        }

        private void RestorePhotoAfterLiveVideo()
        {
            _isLiveVideoPlaying = false;
            StopTransportTimer();
            StopTransportAutoHide();
            HideAllVideos();
            LightboxImage.Visibility = Visibility.Visible;
            UpdateLiveButton(_currentIndex);
        }

        // ── 缩略图导航条 ──────────────────────────────

        private void ThumbnailStrip_SelectionChanged(object sender, SelectionChangedEventArgs e)
        {
            if (_isThumbnailNavigating) return;
            if (ThumbnailStrip.SelectedItem is not ThumbnailStripItem item) return;
            if (item.Index == _currentIndex) return;

            _isThumbnailNavigating = true;
            try
            {
                StopLiveVideo();
                int direction = item.Index > _currentIndex ? 1 : -1;
                _ = ShowItemAsync(item.Index, direction);
            }
            finally
            {
                _isThumbnailNavigating = false;
            }
        }

        private void ThumbnailStrip_ContainerContentChanging(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.InRecycleQueue) return;
            if (args.Item is not ThumbnailStripItem item) return;

            if (args.Phase == 0)
            {
                // 占位：清除旧图
                if (args.ItemContainer.ContentTemplateRoot is Border border)
                {
                    var img = border.FindName("ThumbImage") as Image;
                    if (img != null) img.Source = null;
                }
                args.RegisterUpdateCallback(1, LoadThumbnailCallback);
                args.Handled = true;
            }
        }

        private async void LoadThumbnailCallback(ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            if (args.Item is not ThumbnailStripItem item) return;

            if (item.Thumbnail == null)
            {
                try
                {
                    var bitmap = new BitmapImage { DecodePixelWidth = 120 };
                    var file = await StorageFile.GetFileFromPathAsync(item.ImagePath);
                    using var stream = await file.OpenReadAsync();
                    await bitmap.SetSourceAsync(stream);
                    item.Thumbnail = bitmap;
                }
                catch { return; }
            }

            if (args.ItemContainer.ContentTemplateRoot is Border border)
            {
                var img = border.FindName("ThumbImage") as Image;
                if (img != null) img.Source = item.Thumbnail;
            }
        }

        private void ScrollThumbnailIntoView(int index)
        {
            if (index < 0 || index >= ThumbnailItems.Count) return;
            try
            {
                _isThumbnailNavigating = true;
                ThumbnailStrip.SelectedItem = ThumbnailItems[index];
                ThumbnailStrip.ScrollIntoView(ThumbnailItems[index]);
            }
            finally
            {
                _isThumbnailNavigating = false;
            }
        }

        // ── LIVE 按钮弹簧动画 + 颜色变浅 ───────────────

        private void LivePhotoButton_PointerEntered(object sender, PointerRoutedEventArgs e)
        {
            var visual = ElementCompositionPreview.GetElementVisual(LivePhotoButton);
            visual.CenterPoint = new Vector3((float)(LivePhotoButton.ActualWidth / 2.0),
                                              (float)(LivePhotoButton.ActualHeight / 2.0), 1f);
            var compositor = visual.Compositor;
            var spring = compositor.CreateSpringVector3Animation();
            spring.DampingRatio = 0.55f;
            spring.Period = TimeSpan.FromMilliseconds(50);
            spring.FinalValue = new Vector3(1.08f);
            visual.StartAnimation("Scale", spring);

            // 换浅色 Acrylic 磨砂底板
            LivePhotoButton.Background = new AcrylicBrush
            {
                TintColor = Windows.UI.Color.FromArgb(0xFF, 0x88, 0x88, 0x88),
                TintOpacity = 0.75,
                FallbackColor = Windows.UI.Color.FromArgb(0x88, 0x55, 0x55, 0x55)
            };
        }

        private void LivePhotoButton_PointerExited(object sender, PointerRoutedEventArgs e)
        {
            var visual = ElementCompositionPreview.GetElementVisual(LivePhotoButton);
            visual.CenterPoint = new Vector3((float)(LivePhotoButton.ActualWidth / 2.0),
                                              (float)(LivePhotoButton.ActualHeight / 2.0), 1f);
            var compositor = visual.Compositor;
            var spring = compositor.CreateSpringVector3Animation();
            spring.DampingRatio = 0.55f;
            spring.Period = TimeSpan.FromMilliseconds(50);
            spring.FinalValue = new Vector3(1.0f);
            visual.StartAnimation("Scale", spring);

            // 恢复 XAML 原始 Acrylic 磨砂底板
            LivePhotoButton.Background = _liveButtonDefaultBg!;
        }

        // ── 导航 ──────────────────────────────────────

        private async void Navigate(int direction)
        {
            if (_isNavigating) return;
            _isNavigating = true;
            try
            {
                int newIdx = _currentIndex + direction;
                if (newIdx < 0 || newIdx >= _paths.Count) return;
                StopLiveVideo();
                await ShowItemAsync(newIdx, direction);
            }
            catch (Exception ex)
            {
                LogService.Debug($"LightboxPreview navigate failed: {ex.Message}", LogSource.UI);
            }
            finally
            {
                _isNavigating = false;
            }
        }

        private static bool IsVideoFile(string path) =>
            path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase);

        // ── 辅助 ──────────────────────────────────────

        private static string FormatTime(TimeSpan t) =>
            t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes}:{t.Seconds:D2}";

        private static Task RunStoryboardAsync(Storyboard sb)
        {
            var tcs = new TaskCompletionSource<bool>();
            void OnDone(object? s, object e) { sb.Completed -= OnDone; tcs.TrySetResult(true); }
            sb.Completed += OnDone;
            sb.Begin();
            return tcs.Task;
        }

        // ── 事件处理 ──────────────────────────────────

        private void LightboxBackdrop_Tapped(object sender, TappedRoutedEventArgs e) => Close();

        private void LightboxOverlay_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
            Navigate(delta < 0 ? 1 : -1);
            e.Handled = true;
        }

        private void OnKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (!IsOpen) return;
            switch (e.Key)
            {
                case VirtualKey.Left:
                case VirtualKey.GamepadDPadLeft:
                    Navigate(-1); e.Handled = true; break;
                case VirtualKey.Right:
                case VirtualKey.GamepadDPadRight:
                    Navigate(1); e.Handled = true; break;
                case VirtualKey.Space:
                    if (_activeVideoSlot >= 0)
                    {
                        var player = ActiveVideo.MediaPlayer;
                        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                            player.Pause();
                        else
                            player.Play();
                        UpdateTransportPlayPauseIcon();
                        ShowVideoTransport();
                    }
                    e.Handled = true; break;
                case VirtualKey.Escape:
                    Close(); e.Handled = true; break;
            }
        }
    }
}
