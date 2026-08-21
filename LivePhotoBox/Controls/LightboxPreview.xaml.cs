/*
 * LightboxPreview.xaml.cs
 *
 * 全屏预览控件（Lightbox）。继承 UserControl，提供沉浸式图片/视频浏览：
 * - 图片交叉淡入淡出翻页 + 缩放手势（ScrollViewer ZoomMode）
 * - 双播放器槽位实现视频无缝切换（TCS 事件驱动，无忙等轮询）
 * - 视频播放控制栏（暂停/进度/时间/音量，3 秒无操作自动隐藏）
 * - 底部缩略图导航条（虚拟化按需加载）
 * - LIVE 按钮脉冲动画 + Acrylic 玻璃底板
 * - 关闭按钮悬浮缩放动画
 * - 实况照片播放（单次播放，播完自动恢复照片）
 * - Acrylic 半透明磨砂背景
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

        // 提取结果缓存：文件路径 → 临时视频文件路径。同一张照片第二次点 LIVE 直接命中，零重新提取。
        private static readonly System.Collections.Concurrent.ConcurrentDictionary<string, string> _extractedVideoCache = new();

        // ── 字段 ──────────────────────────────────────

        private IReadOnlyList<string> _paths = Array.Empty<string>();
        private IReadOnlyList<LightboxItem> _items = Array.Empty<LightboxItem>();
        private int _currentIndex = -1;
        private int _lastDirection = 1;
        private int _activeVideoSlot = -1;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _transportUpdateTimer;
        private KeyEventHandler? _pageKeyDownHandler;
        private bool _isLiveVideoPlaying;
        private string? _extractedVideoPath;
        private bool _isUserSeeking;
        private bool _isThumbnailNavigating;
        private Microsoft.UI.Dispatching.DispatcherQueueTimer? _transportAutoHideTimer;
        private DateTime _lastSeekTime = DateTime.MinValue;
        private FrameworkElement? _currentVisual;
        private int _transitionId = 0;
        private CancellationTokenSource? _loadCts;

        public bool IsOpen => LightboxOverlay.Visibility == Visibility.Visible;
        public ObservableCollection<ThumbnailStripItem> ThumbnailItems { get; } = new();

        private Brush? _liveButtonDefaultBg;

        // ── 构造函数 ──────────────────────────────────

        public LightboxPreview()
        {
            InitializeComponent();

            // 🔴 修复 1：允许灯箱自身获取焦点，否则全局按键无法捕获
            this.IsTabStop = true;

            _pageKeyDownHandler = new KeyEventHandler(OnKeyDown);
            AddHandler(UIElement.KeyDownEvent, _pageKeyDownHandler, true);
            _liveButtonDefaultBg = LivePhotoButton.Background;

            // 🔴 修复 2：XAML 中可能只绑定了 Tapped。我们在这里强行补上 Click 事件。
            // 这样一来，当焦点在实况按钮上时，按空格键就会正确触发播放了！
            LivePhotoButton.Tapped += (s, e) => { _ = TriggerLivePhotoPlaybackAsync(); };
        }

        // ── 公开 API ──────────────────────────────────

        public async Task ShowAsync(IReadOnlyList<string> paths, int startIndex)
        {
            var items = await LightboxItemSource.FromPathsAsync(paths);
            await ShowAsync(items, startIndex);
        }

        public async Task ShowAsync(IReadOnlyList<LightboxItem> items, int startIndex)
        {
            if (items == null || items.Count == 0) return;
            if (startIndex < 0 || startIndex >= items.Count) return;
            _items = items;
            _paths = items.Select(i => i.ImagePath).ToList();

            ThumbnailItems.Clear();
            for (int i = 0; i < items.Count; i++)
            {
                ThumbnailItems.Add(new ThumbnailStripItem
                {
                    ImagePath = items[i].ImagePath,
                    Index = i
                });
            }

            LightboxOverlay.Visibility = Visibility.Visible;
            LightboxSpinner.Visibility = Visibility.Visible;

            // 🔴 修复 3：打开灯箱的瞬间，强行把键盘焦点从主页面抢夺过来！
            this.Focus(FocusState.Programmatic);

            await ShowItemAsync(startIndex, 1);
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
            _loadCts?.Cancel();
            _loadCts = new CancellationTokenSource();
            var token = _loadCts.Token;

            _currentIndex = index;
            _lastDirection = direction;
            string path = _paths[index];

            UpdateLiveButton(index);
            LightboxCounter.Text = $"{index + 1} / {_paths.Count}";
            ScrollThumbnailIntoView(index);

            if (IsVideoFile(path))
            {
                await ShowVideoAsync(path, token);
                _previewService.PreloadNeighbors(_paths, index, direction);
            }
            else
            {
                await ShowImageAsync(path, token);
                _previewService.PreloadNeighbors(_paths, index, direction);
            }
        }

        private async Task ShowImageAsync(string path, CancellationToken token)
        {
            StopTransportTimer();
            StopTransportAutoHide();
            VideoTransportBar.Visibility = Visibility.Collapsed;

            LightboxSpinner.Visibility = Visibility.Visible;
            var newImage = await _previewService.LoadCurrentAsync(path, token);

            if (token.IsCancellationRequested) return;

            LightboxSpinner.Visibility = Visibility.Collapsed;
            LightboxImage.Opacity = 1.0;
            LightboxImage.Source = newImage;

            _activeVideoSlot = -1;
            await TransitionToVisualAsync(LightboxImage);

            // 图片已显示。此后台探测单文件实况（读 XMP 头 + 尾部 LIVE_ 标记），
            // 检测到实况则置 IsLivePhoto=true 显示 LIVE 按钮；普通照片永不显示。
            // 探测不阻塞图片显示，按钮延迟出现（用户已确认可接受）。
            if (_currentIndex >= 0 && _currentIndex < _items.Count)
            {
                var item = _items[_currentIndex];
                if (!item.IsLivePhoto && item.VideoPath == null)
                {
                    _ = DetectLivePhotoInBackgroundAsync(item);
                }
            }
        }

        /// <summary>
        /// 后台探测单文件实况照片：复用 LightboxItemSource.DetectSingleFileVideo
        /// （读 XMP 头 64KB + 尾部 LIVE_ 标记），探测到 videoLen&gt;0 就更新条目并显示 LIVE 按钮。
        /// </summary>
        private async Task DetectLivePhotoInBackgroundAsync(LightboxItem item)
        {
            try
            {
                await Task.Run(() =>
                {
                    LightboxItemSource.DetectSingleFileVideo(item.ImagePath, out long videoLen);
                    if (videoLen > 0)
                    {
                        item.AppendedVideoLength = videoLen;
                        item.IsLivePhoto = true;
                    }
                });

                // 回到 UI 线程更新按钮
                DispatcherQueue.TryEnqueue(() =>
                {
                    if (_currentIndex < 0 || _currentIndex >= _items.Count) return;
                    if (_items[_currentIndex] != item) return;   // 已切换条目
                    if (item.IsLivePhoto)
                    {
                        LivePhotoButton.Visibility = Visibility.Visible;
                        LivePulseSb.Begin();
                    }
                });
            }
            catch { /* 探测失败静默——普通照片 */ }
        }

        private async Task ShowVideoAsync(string path, CancellationToken token)
        {
            StopTransportTimer();
            StopTransportAutoHide();

            var nextPlayer = InactiveVideo;
            int nextSlot = _activeVideoSlot == 0 ? 1 : 0;
            nextPlayer.MediaPlayer.IsLoopingEnabled = true;
            nextPlayer.MediaPlayer.IsMuted = false;
            nextPlayer.MediaPlayer.Volume = 1.0;

            LightboxSpinner.Visibility = Visibility.Visible;

            MediaSource source;
            try
            {
                var file = await StorageFile.GetFileFromPathAsync(path);
                source = MediaSource.CreateFromStorageFile(file);
            }
            catch
            {
                source = MediaSource.CreateFromUri(new Uri(path));
            }

            bool opened = await WaitForMediaOpenedAsync(nextPlayer, source, token);

            if (token.IsCancellationRequested) return;

            if (!opened)
            {
                LightboxSpinner.Visibility = Visibility.Collapsed;
                return;
            }

            LightboxSpinner.Visibility = Visibility.Collapsed;
            _activeVideoSlot = nextSlot;

            await TransitionToVisualAsync(nextPlayer);

            ShowVideoTransport();
            StartTransportTimer();
        }

        private async Task PlayLiveVideoAsync(string videoPath)
        {
            _isLiveVideoPlaying = true;
            ResetLiveButtonState();
            LivePhotoButton.Visibility = Visibility.Collapsed;
            LivePulseSb.Stop();
            StopTransportTimer();

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
            bool opened = await WaitForMediaOpenedAsync(player, source, CancellationToken.None);
            if (!opened || !_isLiveVideoPlaying) return;

            _activeVideoSlot = slot;
            await TransitionToVisualAsync(player);

            ShowVideoTransport();
            StartTransportTimer();
        }

        private void RestorePhotoAfterLiveVideo()
        {
            _isLiveVideoPlaying = false;
            StopTransportTimer();
            StopTransportAutoHide();

            // 实况视频播完退回图片模式时，必须明确隐藏播控栏
            VideoTransportBar.Visibility = Visibility.Collapsed;

            _activeVideoSlot = -1;
            _ = TransitionToVisualAsync(LightboxImage);

            UpdateLiveButton(_currentIndex);
        }

        public void Close()
        {
            _isLiveVideoPlaying = false;
            System.Threading.Interlocked.Increment(ref _transitionId);
            _loadCts?.Cancel();

            StopTransportTimer();
            StopTransportAutoHide();
            HideAllVideos();

            LightboxImage.Source = null;
            LightboxImage.Visibility = Visibility.Collapsed;
            LightboxSpinner.Visibility = Visibility.Collapsed;
            LightboxOverlay.Visibility = Visibility.Collapsed;

            _currentIndex = -1;
            _currentVisual = null;
            _activeVideoSlot = -1;

            LivePhotoButton.Visibility = Visibility.Collapsed;
            LivePulseSb.Stop();
            ThumbnailItems.Clear();

            if (_extractedVideoPath != null)
            {
                try { File.Delete(_extractedVideoPath); } catch { }
                _extractedVideoPath = null;
            }

            // 关闭灯箱时清空提取缓存并删除全部临时视频文件
            foreach (var temp in _extractedVideoCache.Values)
            {
                try { File.Delete(temp); } catch { }
            }
            _extractedVideoCache.Clear();
        }

        private static async Task<bool> WaitForMediaOpenedAsync(MediaPlayerElement player,
            MediaSource source, CancellationToken token, int timeoutMs = 5000)
        {
            var tcs = new TaskCompletionSource<bool>();
            using var registration = token.Register(() => tcs.TrySetResult(false));

            TypedEventHandler<MediaPlayer, object> onOpened = (s, a) => tcs.TrySetResult(true);
            TypedEventHandler<MediaPlayer, MediaPlayerFailedEventArgs> onFailed = (s, a) => tcs.TrySetResult(false);

            player.MediaPlayer.MediaOpened += onOpened;
            player.MediaPlayer.MediaFailed += onFailed;
            try
            {
                player.Source = source;
                var completed = await Task.WhenAny(tcs.Task, Task.Delay(timeoutMs, token));
                return completed == tcs.Task && tcs.Task.Result && !token.IsCancellationRequested;
            }
            catch (TaskCanceledException)
            {
                return false;
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
            {
                TransportPlayPauseIcon.Glyph = "\uE768";
                player.Pause();
            }
            else
            {
                TransportPlayPauseIcon.Glyph = "\uE769";
                player.Play();
            }

            StartTransportAutoHide();
        }

        private void TransportSeekBar_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            if (_activeVideoSlot < 0) return;
            _isUserSeeking = true;
            ThumbTransform.ScaleX = 1.5;
            ThumbTransform.ScaleY = 1.5;

            SeekToPointer(e, isFinalCommit: true);
            TransportSeekBar.CapturePointer(e.Pointer);
            e.Handled = true;
        }

        private void TransportSeekBar_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isUserSeeking) return;
            SeekToPointer(e, isFinalCommit: false);
        }

        private void TransportSeekBar_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (_activeVideoSlot < 0) return;
            SeekToPointer(e, isFinalCommit: true);

            _isUserSeeking = false;
            ThumbTransform.ScaleX = 1.0;
            ThumbTransform.ScaleY = 1.0;
            TransportSeekBar.ReleasePointerCapture(e.Pointer);
            StartTransportAutoHide();
        }

        private void SeekToPointer(PointerRoutedEventArgs e, bool isFinalCommit = false)
        {
            if (_activeVideoSlot < 0 || ActiveVideo == null) return;

            var pt = e.GetCurrentPoint(TransportSeekBar);
            double ratio = Math.Clamp(pt.Position.X / Math.Max(1, TransportSeekBar.ActualWidth), 0, 1);
            var session = ActiveVideo.MediaPlayer?.PlaybackSession;
            if (session == null || session.NaturalDuration.TotalSeconds <= 0) return;

            var newPos = TimeSpan.FromSeconds(ratio * session.NaturalDuration.TotalSeconds);

            TransportCurrentTime.Text = FormatTime(newPos);
            TransportSeekFill.Width = ratio * TransportSeekBar.ActualWidth;
            PositionSeekThumb(ratio);

            if (isFinalCommit)
            {
                session.Position = newPos;
            }
            else
            {
                if ((DateTime.Now - _lastSeekTime).TotalMilliseconds > 100)
                {
                    session.Position = newPos;
                    _lastSeekTime = DateTime.Now;
                }
            }
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

            string newCurrentTime = FormatTime(pos);
            if (TransportCurrentTime.Text != newCurrentTime)
                TransportCurrentTime.Text = newCurrentTime;

            string newTotalTime = FormatTime(dur);
            if (TransportTotalTime.Text != newTotalTime)
                TransportTotalTime.Text = newTotalTime;

            UpdateTransportPlayPauseIcon();
        }

        private void PositionSeekThumb(double ratio)
        {
            double barW = TransportSeekBar.ActualWidth;
            if (barW <= 0) return;
            double left = Math.Clamp(ratio * barW - 7, -7, barW - 7);
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

        private void StartTransportTimer()
        {
            StopTransportTimer();
            _transportUpdateTimer = DispatcherQueue.CreateTimer();
            _transportUpdateTimer.Interval = TimeSpan.FromMilliseconds(33);
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

        // XAML 中如果继续保留 Tapped 事件也没关系，这层逻辑依然完美工作
        private void LivePhotoButton_Tapped(object sender, TappedRoutedEventArgs e)
        {
            _ = TriggerLivePhotoPlaybackAsync();
        }

        private async Task TriggerLivePhotoPlaybackAsync()
        {
            if (_isLiveVideoPlaying) return;
            if (_currentIndex < 0 || _currentIndex >= _items.Count) return;

            var item = _items[_currentIndex];
            if (!item.IsLivePhoto) return;

            string? videoSource = item.VideoPath;

            // 无配对视频路径 → 从单文件实况中提取嵌入视频。
            // 按方法探测顺序自动定位（LIVE_→mpvd→SEF→尾部），无需协议枚举。
            if (videoSource == null)
            {
                LightboxSpinner.Visibility = Visibility.Visible;
                LivePhotoButton.Visibility = Visibility.Collapsed;
                LivePulseSb.Stop();
                try
                {
                    // 先查缓存：同一文件已提取过则直接命中，零重新提取
                    if (!_extractedVideoCache.TryGetValue(item.ImagePath, out videoSource))
                    {
                        videoSource = await LivePhotoVideoExtractor.ExtractVideoAutoAsync(
                            item.ImagePath, item.AppendedVideoLength);

                        if (videoSource != null)
                        {
                            _extractedVideoCache[item.ImagePath] = videoSource;
                        }
                    }
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
            // 已废弃：改用 LivePhotoVideoExtractor.ExtractVideoAutoAsync（方法探测）。
            // 此方法保留仅用于编译兼容，实际调用已改为 ExtractVideoAutoAsync。
            await Task.Yield();
            return null;
        }

        private void StopLiveVideo()
        {
            if (!_isLiveVideoPlaying) return;
            _isLiveVideoPlaying = false;
            HideAllVideos();
            RestorePhotoAfterLiveVideo();
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

        // ── LIVE 按钮弹簧动画 ──────────────────────────

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

            LivePhotoButton.Background = _liveButtonDefaultBg!;
        }

        // ── 导航 ──────────────────────────────────────

        private async void Navigate(int direction)
        {
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
        }

        private static bool IsVideoFile(string path) =>
            path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase);

        private static string FormatTime(TimeSpan t) =>
            t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes}:{t.Seconds:D2}";

        // ── 事件处理 ──────────────────────────────────

        private void LightboxBackdrop_Tapped(object sender, TappedRoutedEventArgs e) => Close();

        private void LightboxOverlay_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
            Navigate(delta < 0 ? 1 : -1);
            e.Handled = true;
        }

        private async Task TransitionToVisualAsync(FrameworkElement newVisual)
        {
            int currentId = System.Threading.Interlocked.Increment(ref _transitionId);

            Canvas.SetZIndex(newVisual, 10);
            newVisual.Visibility = Visibility.Visible;

            if (_currentVisual == newVisual)
            {
                Canvas.SetZIndex(newVisual, 0);
                return;
            }

            await Task.Delay(80);

            if (currentId != _transitionId)
            {
                Canvas.SetZIndex(newVisual, 0);
                return;
            }

            if (_currentVisual != null && _currentVisual != newVisual)
            {
                _currentVisual.Visibility = Visibility.Collapsed;
                if (_currentVisual is MediaPlayerElement oldPlayer)
                {
                    oldPlayer.MediaPlayer.Pause();
                }
            }

            if (newVisual != LightboxImage) LightboxImage.Visibility = Visibility.Collapsed;
            if (newVisual != LightboxVideo0)
            {
                LightboxVideo0.Visibility = Visibility.Collapsed;
                LightboxVideo0.MediaPlayer.Pause();
            }
            if (newVisual != LightboxVideo1)
            {
                LightboxVideo1.Visibility = Visibility.Collapsed;
                LightboxVideo1.MediaPlayer.Pause();
            }

            Canvas.SetZIndex(newVisual, 0);
            _currentVisual = newVisual;
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
                    // 🔴 智能焦点判断机制，解决各种键盘冲突！
                    var focusedElement = FocusManager.GetFocusedElement(this.XamlRoot) as FrameworkElement;

                    // 如果焦点现在正巧停留在某个原生按钮上（比如你刚点过的“关闭”键或者“实况”键）
                    // 那么原生的空格操作会自动去触发这个按钮，我们此时绝对不能介入，否则会引发严重冲突！
                    if (focusedElement is ButtonBase)
                    {
                        return; // 默默退下，让原生的按钮逻辑去执行
                    }

                    // 如果焦点没在任何按钮上，我们就把空格键当做全局强行播放/暂停！
                    // 不用管底层的 ScrollViewer 有没有把按键事件吞掉（屏蔽掉 e.Handled 拦截逻辑）
                    if (_activeVideoSlot >= 0)
                    {
                        var player = ActiveVideo.MediaPlayer;
                        if (player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing)
                        {
                            TransportPlayPauseIcon.Glyph = "\uE768";
                            player.Pause();
                        }
                        else
                        {
                            TransportPlayPauseIcon.Glyph = "\uE769";
                            player.Play();
                        }
                        ShowVideoTransport();
                    }
                    else
                    {
                        _ = TriggerLivePhotoPlaybackAsync();
                    }

                    e.Handled = true; // 我们处理完了，阻止画面往下滚
                    break;
                case VirtualKey.Escape:
                    Close(); e.Handled = true; break;
            }
        }
    }
}