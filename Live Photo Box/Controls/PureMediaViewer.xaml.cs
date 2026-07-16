/*
 * PureMediaViewer.xaml.cs
 *
 * 可复用的纯净视频播放器 UserControl。
 * 硬切直跳，无过渡动画。
 *
 * 依赖属性：
 *   VideoSource      — 视频源（MediaSource）
 *   AutoCloseOnEnd   — 播放完毕是否自动关闭
 *   ShowCloseButton  — 是否显示右上角关闭按钮
 *
 * 事件：
 *   CloseRequested   — 视频已关闭，外部可恢复底层控件
 */

using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;
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

        private static void OnShowTransportControlsChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var viewer = (PureMediaViewer)d;
            viewer.VideoPlayer.AreTransportControlsEnabled = (bool)e.NewValue;
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

        // ══════════════════════════════════════════════════════════════
        //  内部状态
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

            if (_pendingSource != null)
            {
                player.Source = _pendingSource;
                _pendingSource = null;
            }
        }

        // ══════════════════════════════════════════════════════════════
        //  公共 API — 硬直切，无动画
        // ══════════════════════════════════════════════════════════════

        /// <summary>立刻显示并播放视频（先透明加载，第一帧就绪后变不透明）</summary>
        public async void Play()
        {
            _isClosing = false;

            // 先可见但透明：用户看到的还是底层的 PhotoViewer
            this.Visibility = Visibility.Visible;
            this.Opacity = 0;
            RootGrid.Opacity = 1.0;

            // 开始播放（视频开始解码，swap chain 初始化）
            VideoPlayer.MediaPlayer?.Play();

            // 等第一帧渲染完成
            await Task.Delay(80);

            if (!_isClosing)
            {
                // 变不透明 — 此时 swap chain 已有视频第一帧，不会闪白
                this.Opacity = 1.0;
            }
        }

        /// <summary>立刻关闭并触发 CloseRequested</summary>
        public void Close()
        {
            if (_isClosing) return;
            _isClosing = true;

            if (VideoPlayer.MediaPlayer != null)
            {
                VideoPlayer.MediaPlayer.Pause();
                VideoPlayer.MediaPlayer.Source = null;
            }
            RootGrid.Opacity = 0;
            this.Visibility = Visibility.Collapsed;

            _isClosing = false;
            CloseRequested?.Invoke(this, EventArgs.Empty);
        }

        // ══════════════════════════════════════════════════════════════
        //  事件处理
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

        private void CloseButton_Click(object sender, RoutedEventArgs e)
        {
            Close();
        }
    }
}
