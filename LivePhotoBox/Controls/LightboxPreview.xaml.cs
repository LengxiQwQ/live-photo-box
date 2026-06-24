using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.System;

namespace LivePhotoBox.Controls
{
    public sealed partial class LightboxPreview : UserControl
    {
        private static readonly ImagePreviewService _previewService = new(maxCacheSize: 40, decodePixelWidth: 1920, preloadForward: 6, preloadBackward: 2);

        private IReadOnlyList<string> _paths = Array.Empty<string>();
        private int _currentIndex = -1;
        private int _lastDirection = 1;
        private bool _isNavigating;
        private int _activeVideoSlot = -1;
        private bool _videoReady;
        private CancellationTokenSource? _videoProgressCts;
        private KeyEventHandler? _pageKeyDownHandler;

        public bool IsOpen => LightboxOverlay.Visibility == Visibility.Visible;

        public LightboxPreview()
        {
            InitializeComponent();
            _pageKeyDownHandler = new KeyEventHandler(OnKeyDown);
            AddHandler(UIElement.KeyDownEvent, _pageKeyDownHandler, true);
        }

        public async Task ShowAsync(IReadOnlyList<string> paths, int startIndex)
        {
            if (paths == null || paths.Count == 0) return;
            if (startIndex < 0 || startIndex >= paths.Count) return;
            _paths = paths;
            await ShowItemAsync(startIndex, 1);
            LightboxOverlay.Visibility = Visibility.Visible;
            LightboxCloseButton.Focus(FocusState.Programmatic);
        }

        public void Close()
        {
            StopVideoTimer();
            HideAllVideos();
            LightboxImage.Source = null;
            LightboxSpinner.Visibility = Visibility.Collapsed;
            LightboxOverlay.Visibility = Visibility.Collapsed;
            _currentIndex = -1;
        }

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
        }

        private async Task ShowItemAsync(int index, int direction)
        {
            _currentIndex = index;
            _lastDirection = direction;
            string path = _paths[index];

            if (IsVideoFile(path))
            {
                StopVideoTimer();

                // 在隐藏的播放器里加载 → 等首帧 → 停旧播 → 切换显示
                var nextPlayer = InactiveVideo;
                int nextSlot = _activeVideoSlot == 0 ? 1 : 0;
                nextPlayer.MediaPlayer.IsLoopingEnabled = true;
                nextPlayer.MediaPlayer.MediaOpened += OnVideoOpened;
                try
                {
                    _videoReady = false;
                    nextPlayer.Source = MediaSource.CreateFromUri(new Uri(path));
                    for (int i = 0; i < 100 && !_videoReady; i++)
                        await Task.Delay(30);
                }
                catch { }
                finally
                {
                    nextPlayer.MediaPlayer.MediaOpened -= OnVideoOpened;
                }

                if (_activeVideoSlot >= 0)
                {
                    ActiveVideo.MediaPlayer.Pause();
                    ActiveVideo.Visibility = Visibility.Collapsed;
                }
                nextPlayer.Visibility = Visibility.Visible;
                _activeVideoSlot = nextSlot;

                LightboxImage.Visibility = Visibility.Collapsed;
                VideoProgressBar.Visibility = Visibility.Visible;
                VideoTimeLabel.Visibility = Visibility.Visible;
                StartVideoTimer();
                _previewService.PreloadNeighbors(_paths, index, direction);
            }
            else
            {
                StopVideoTimer();
                HideAllVideos();
                VideoProgressBar.Visibility = Visibility.Collapsed;
                VideoTimeLabel.Visibility = Visibility.Collapsed;
                LightboxSpinner.Visibility = Visibility.Visible;
                var newImage = await _previewService.LoadCurrentAsync(path);
                LightboxSpinner.Visibility = Visibility.Collapsed;

                LightboxImage.Visibility = Visibility.Visible;
                LightboxImage.Source = newImage;
                _previewService.PreloadNeighbors(_paths, index, direction);
            }

            LightboxCounter.Text = $"{index + 1} / {_paths.Count}";
        }

        private void OnVideoOpened(Windows.Media.Playback.MediaPlayer sender, object args)
        {
            _videoReady = true;
        }

        private async void Navigate(int direction)
        {
            if (_isNavigating) return;
            _isNavigating = true;
            try
            {
                int newIdx = _currentIndex + direction;
                if (newIdx < 0 || newIdx >= _paths.Count) return;
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

        private void StartVideoTimer()
        {
            StopVideoTimer();
            _videoProgressCts = new CancellationTokenSource();
            var token = _videoProgressCts.Token;
            _ = Task.Run(async () =>
            {
                while (!token.IsCancellationRequested)
                {
                    await Task.Delay(200, token);
                    if (token.IsCancellationRequested) break;
                    _ = this.DispatcherQueue.TryEnqueue(() => UpdateVideoProgress());
                }
            }, token);
        }

        private void StopVideoTimer()
        {
            _videoProgressCts?.Cancel();
            _videoProgressCts?.Dispose();
            _videoProgressCts = null;
            VideoProgressFill.Width = 0;
        }

        private void UpdateVideoProgress()
        {
            try
            {
                if (_activeVideoSlot < 0) return;
                var session = ActiveVideo.MediaPlayer.PlaybackSession;
                if (session == null) return;

                var pos = session.Position;
                var dur = session.NaturalDuration;
                if (dur.TotalSeconds <= 0) return;

                double ratio = Math.Clamp(pos.TotalSeconds / dur.TotalSeconds, 0, 1);
                VideoProgressFill.Width = VideoProgressBar.ActualWidth * ratio;
                VideoTimeLabel.Text = $"{FormatTime(pos)} / {FormatTime(dur)}";
            }
            catch { }
        }

        private static string FormatTime(TimeSpan t) =>
            t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes}:{t.Seconds:D2}";

        private void LightboxBackdrop_Tapped(object sender, TappedRoutedEventArgs e) => Close();
        private void LightboxCloseButton_Click(object sender, RoutedEventArgs e) => Close();

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
                case VirtualKey.Escape:
                    Close(); e.Handled = true; break;
            }
        }
    }
}
