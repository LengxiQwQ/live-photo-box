/*
 * LightboxPreview.xaml.cs
 *
 * 全屏预览控件（Lightbox）。继承 UserControl，提供沉浸式图片/视频浏览：
 *   - 支持图片和 .mp4/.mov 视频的沉浸式预览
 *   - 双播放器槽位实现视频无缝切换
 *   - 键盘方向键 / 鼠标滚轮翻页
 *   - 视频进度条和时间显示
 *   - 照片预加载（ImagePreviewService）
 *
 * 对应 ViewModel：无（由调用方传入文件列表）
 *
 * 生命周期：
 *   - ShowAsync(paths, startIndex) → 打开预览
 *   - 键盘/鼠标导航 → 翻页 → 自动切换图片/视频加载策略
 *   - Close() → 关闭并清理资源
 */

using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.System;

namespace LivePhotoBox.Controls
{
    public sealed partial class LightboxPreview : UserControl
    {
        // 共享的图片预览服务，支持缓存、解码尺寸限制和预加载
        private static readonly ImagePreviewService _previewService = new(maxCacheSize: 40, decodePixelWidth: 1920, preloadForward: 6, preloadBackward: 2);

        private IReadOnlyList<string> _paths = Array.Empty<string>();
        private IReadOnlyList<LightboxItem> _items = Array.Empty<LightboxItem>();
        private int _currentIndex = -1;
        private int _lastDirection = 1;
        private bool _isNavigating;
        private int _activeVideoSlot = -1;
        private bool _videoReady;
        private CancellationTokenSource? _videoProgressCts;
        private KeyEventHandler? _pageKeyDownHandler;
        private bool _isLiveVideoPlaying;  // 是否正在播放实况视频（非循环，播完自动恢复）
        private string? _extractedVideoPath; // 单文件模式提取的临时视频，Close 时清理

        // 当前是否处于打开状态
        public bool IsOpen => LightboxOverlay.Visibility == Visibility.Visible;

        // 构造函数：初始化控件并注册全局键盘事件
        public LightboxPreview()
        {
            InitializeComponent();
            _pageKeyDownHandler = new KeyEventHandler(OnKeyDown);
            AddHandler(UIElement.KeyDownEvent, _pageKeyDownHandler, true);
        }

        // 以全屏模式打开文件列表，从指定索引开始显示（向后兼容重载）。
        // paths: 文件路径列表
        // startIndex: 起始显示索引
        public async Task ShowAsync(IReadOnlyList<string> paths, int startIndex)
        {
            var items = await LightboxItemSource.FromPathsAsync(paths);
            await ShowAsync(items, startIndex);
        }

        // 以全屏模式打开条目列表，从指定索引开始显示。
        // items: LightboxItem 列表（含 Live Photo 视频源信息）
        // startIndex: 起始显示索引
        public async Task ShowAsync(IReadOnlyList<LightboxItem> items, int startIndex)
        {
            if (items == null || items.Count == 0) return;
            if (startIndex < 0 || startIndex >= items.Count) return;
            _items = items;
            _paths = items.Select(i => i.ImagePath).ToList();
            await ShowItemAsync(startIndex, 1);
            LightboxOverlay.Visibility = Visibility.Visible;
            LightboxCloseButton.Focus(FocusState.Programmatic);
        }

        // 关闭预览，清理所有媒体资源（含临时提取的实况视频文件）。
        public void Close()
        {
            StopLiveVideo();
            StopVideoTimer();
            HideAllVideos();
            LightboxImage.Source = null;
            LightboxSpinner.Visibility = Visibility.Collapsed;
            LightboxOverlay.Visibility = Visibility.Collapsed;
            _currentIndex = -1;
            LivePhotoButton.Visibility = Visibility.Collapsed;

            // 清理单文件实况提取的临时视频
            if (_extractedVideoPath != null)
            {
                try { File.Delete(_extractedVideoPath); } catch { }
                _extractedVideoPath = null;
            }
        }

        // 获取当前活动的视频播放器
        private MediaPlayerElement ActiveVideo =>
            _activeVideoSlot == 0 ? LightboxVideo0 :
            _activeVideoSlot == 1 ? LightboxVideo1 : null!;

        // 获取当前非活动的视频播放器（用于后台预加载）
        private MediaPlayerElement InactiveVideo =>
            _activeVideoSlot == 0 ? LightboxVideo1 :
            _activeVideoSlot == 1 ? LightboxVideo0 : LightboxVideo0;

        // 暂停并隐藏两个视频播放器
        private void HideAllVideos()
        {
            LightboxVideo0.MediaPlayer.Pause();
            LightboxVideo1.MediaPlayer.Pause();
            LightboxVideo0.Visibility = Visibility.Collapsed;
            LightboxVideo1.Visibility = Visibility.Collapsed;
            _activeVideoSlot = -1;
        }

        // 显示指定索引的文件。根据文件类型（图片/视频）采用不同的加载策略：
        // - 视频：在隐藏播放器中预加载首帧，再切换显示
        // - 图片：通过 ImagePreviewService 异步解码并显示
        // 显示指定索引的文件。根据文件类型（图片/视频）采用不同的加载策略
        private async Task ShowItemAsync(int index, int direction)
        {
            _currentIndex = index;
            _lastDirection = direction;
            string path = _paths[index];

            // ✅ 修复点：刚进方法就立刻刷新 LIVE 按钮状态，不要等图片转圈加载完！
            UpdateLiveButton(index);

            if (IsVideoFile(path))
            {
                StopVideoTimer();

                // 在隐藏的播放器里加载 → 等首帧 → 停旧播 → 切换显示
                var nextPlayer = InactiveVideo;
                int nextSlot = _activeVideoSlot == 0 ? 1 : 0;
                nextPlayer.MediaPlayer.IsLoopingEnabled = true;
                nextPlayer.MediaPlayer.IsMuted = false;
                nextPlayer.MediaPlayer.Volume = 1.0;
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

        // 根据当前索引更新 LIVE 按钮的可见性
        private void UpdateLiveButton(int index)
        {
            if (index < 0 || index >= _items.Count)
            {
                LivePhotoButton.Visibility = Visibility.Collapsed;
                return;
            }
            var item = _items[index];
            LivePhotoButton.Visibility = item.IsLivePhoto ? Visibility.Visible : Visibility.Collapsed;
        }

        // 视频首次打开完成时的回调，标记就绪状态
        private void OnVideoOpened(Windows.Media.Playback.MediaPlayer sender, object args)
        {
            _videoReady = true;
        }

        // 按指定方向翻页（±1），带防重入锁。翻页时停止当前实况视频。
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

        // 根据文件扩展名判断是否为视频文件
        private static bool IsVideoFile(string path) =>
            path.EndsWith(".mp4", StringComparison.OrdinalIgnoreCase) ||
            path.EndsWith(".mov", StringComparison.OrdinalIgnoreCase);

        // 启动视频进度更新定时器（每 200ms 更新 UI）
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

        // 停止视频进度更新定时器
        private void StopVideoTimer()
        {
            _videoProgressCts?.Cancel();
            _videoProgressCts?.Dispose();
            _videoProgressCts = null;
            VideoProgressFill.Width = 0;
        }

        // 更新视频进度条宽度和时间标签
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

        // 格式化时间跨度，超过 1 小时显示 HH:MM:SS，否则显示 MM:SS
        private static string FormatTime(TimeSpan t) =>
            t.TotalHours >= 1
                ? $"{(int)t.TotalHours}:{t.Minutes:D2}:{t.Seconds:D2}"
                : $"{t.Minutes}:{t.Seconds:D2}";

        // ── Live Photo 播放 ──────────────────────────────

        // LIVE 按钮点击：提取视频（如需要）→ 播放 → 监听 MediaEnded → 恢复照片。
        private async void LivePhotoButton_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (_isLiveVideoPlaying) return;
            if (_currentIndex < 0 || _currentIndex >= _items.Count) return;

            var item = _items[_currentIndex];
            if (!item.IsLivePhoto) return;

            string? videoSource = item.VideoPath;
            if (videoSource == null && item.AppendedVideoLength > 0)
            {
                // 模式 B：从 JPEG 尾部提取视频段到临时文件
                LightboxSpinner.Visibility = Visibility.Visible;
                LivePhotoButton.Visibility = Visibility.Collapsed;
                try
                {
                    videoSource = await ExtractAppendedVideoAsync(item.ImagePath, item.AppendedVideoLength);
                    if (videoSource != null)
                        _extractedVideoPath = videoSource;
                }
                catch
                {
                    videoSource = null;
                }
                LightboxSpinner.Visibility = Visibility.Collapsed;
            }

            if (videoSource == null || !File.Exists(videoSource))
            {
                LivePhotoButton.Visibility = Visibility.Visible;
                return;
            }

            await PlayLiveVideoAsync(videoSource);
        }

        // 从 JPEG 文件尾部提取追加的视频段到临时文件。
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

        // 播放实况视频（单次播放不循环），播完后自动恢复照片显示。
        private async Task PlayLiveVideoAsync(string videoPath)
        {
            StopLiveVideo();
            _isLiveVideoPlaying = true;
            LivePhotoButton.Visibility = Visibility.Collapsed;
            StopVideoTimer();
            HideAllVideos();

            var player = InactiveVideo;
            int slot = _activeVideoSlot == 0 ? 1 : 0;
            player.MediaPlayer.IsLoopingEnabled = false;
            player.MediaPlayer.IsMuted = false;
            player.MediaPlayer.Volume = 1.0;

            // 注册一次性 MediaEnded 回调，播完恢复照片
            void OnEnded(MediaPlayer sender, object args)
            {
                sender.MediaEnded -= OnEnded;
                _ = DispatcherQueue.TryEnqueue(RestorePhotoAfterLiveVideo);
            }
            player.MediaPlayer.MediaEnded += OnEnded;

            try
            {
                _videoReady = false;
                player.MediaPlayer.MediaOpened += OnVideoOpened;
                player.Source = MediaSource.CreateFromUri(new Uri(videoPath));
                for (int i = 0; i < 100 && !_videoReady; i++)
                    await Task.Delay(30);
            }
            catch { }
            finally
            {
                player.MediaPlayer.MediaOpened -= OnVideoOpened;
            }

            if (!_isLiveVideoPlaying) return; // 加载期间被 StopLiveVideo 中断

            if (_activeVideoSlot >= 0)
            {
                ActiveVideo.MediaPlayer.Pause();
                ActiveVideo.Visibility = Visibility.Collapsed;
            }
            player.Visibility = Visibility.Visible;
            _activeVideoSlot = slot;
            LightboxImage.Visibility = Visibility.Collapsed;
            VideoProgressBar.Visibility = Visibility.Visible;
            VideoTimeLabel.Visibility = Visibility.Visible;
            StartVideoTimer();
        }

        // 停止当前实况视频并恢复照片显示。
        private void StopLiveVideo()
        {
            if (!_isLiveVideoPlaying) return;
            _isLiveVideoPlaying = false;
            HideAllVideos();
            RestorePhotoAfterLiveVideo();
        }

        // 恢复照片层（隐藏视频控件、显示图片、恢复 LIVE 按钮）。
        private void RestorePhotoAfterLiveVideo()
        {
            _isLiveVideoPlaying = false;
            StopVideoTimer();
            HideAllVideos();
            LightboxImage.Visibility = Visibility.Visible;
            VideoProgressBar.Visibility = Visibility.Collapsed;
            VideoTimeLabel.Visibility = Visibility.Collapsed;
            UpdateLiveButton(_currentIndex);
        }

        // 点击背景关闭预览
        private void LightboxBackdrop_Tapped(object sender, TappedRoutedEventArgs e) => Close();

        // 点击关闭按钮关闭预览
        private void LightboxCloseButton_Click(object sender, RoutedEventArgs e) => Close();

        // 鼠标滚轮翻页：上滚后退，下滚前进
        private void LightboxOverlay_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            var delta = e.GetCurrentPoint(null).Properties.MouseWheelDelta;
            Navigate(delta < 0 ? 1 : -1);
            e.Handled = true;
        }

        // 键盘导航：左右方向键翻页，Esc 关闭
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
