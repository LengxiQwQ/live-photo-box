using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;

namespace LivePhotoBox.Views
{
    /// <summary>UI/presentation shell only; current-master media/protocol/native services remain authoritative.</summary>
    public sealed partial class EditPage : Page
    {
        private const double TimelineItemWidth = 72.0;
        private const double TimelineItemSpacing = 6.0;
        private const double TimelineItemStep = TimelineItemWidth + TimelineItemSpacing;

        public EditViewModel ViewModel => AppViewModel.Instance.Edit;

        private bool _isPreviewMaximized;
        private bool _isClassicScrollInternal;
        private bool _isPlaybackSourcePending;
        private string? _previewTempVideoPath;
        private double _sharedZoomScale = 1.0;
        private double _sharedPanX = 0.5;
        private double _sharedPanY = 0.5;

        public EditPage()
        {
            InitializeComponent();
            ViewModel.RequestScrollToFrame += OnRequestScrollToFrame;
            ViewModel.PreviewClearRequested += OnPreviewClearRequested;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            PhotoViewer.ScaleChanged += PhotoViewer_ScaleChanged;
            PureMediaViewer.ScaleChanged += PureMediaViewer_ScaleChanged;
            PureMediaViewer.VideoOpened += PureMediaViewer_VideoOpened;
            Loaded += EditPage_Loaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            Bindings.Update();
            UpdateEmptyPreviewState();
            ScrollSelectedFrameIntoView(true);
            UpdateOverviewBar();
        }

        private void EditPage_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateEmptyPreviewState();
            UpdateClassicPadding();
            UpdateZoomPercentDisplay();
            ApplyMuteState();
            UpdateOverviewBar();
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(EditViewModel.SelectedFilePath))
            {
                StopPlaybackPresentation();
                CleanupPreviewTempVideo();
                UpdateEmptyPreviewState();
            }
            else if (e.PropertyName == nameof(EditViewModel.SelectedTimelineFrame))
            {
                StopPlaybackPresentation();
                UpdateOverviewBar();
            }
            else if (e.PropertyName == nameof(EditViewModel.IsMuted))
            {
                ApplyMuteState();
            }
        }

        private bool IsVideoActive() =>
            PureMediaViewer.Visibility == Visibility.Visible && PureMediaViewer.Opacity > 0.99;

        private void ZoomInButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsVideoActive()) PureMediaViewer.ZoomIn(); else PhotoViewer.ZoomIn();
            UpdateZoomPercentDisplay();
        }

        private void ZoomOutButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsVideoActive()) PureMediaViewer.ZoomOut(); else PhotoViewer.ZoomOut();
            UpdateZoomPercentDisplay();
        }

        private void ZoomPercentButton_Click(object sender, RoutedEventArgs e)
        {
            if (IsVideoActive()) PureMediaViewer.ToggleFitVsPixel(); else PhotoViewer.ToggleFitVsPixel();
            UpdateZoomPercentDisplay();
        }

        private void PhotoViewer_ScaleChanged(double scale)
        {
            _sharedZoomScale = scale;
            if (!IsVideoActive()) UpdateZoomPercentDisplay();
        }

        private void PureMediaViewer_ScaleChanged(double scale)
        {
            _sharedZoomScale = scale;
            if (IsVideoActive()) UpdateZoomPercentDisplay();
        }

        private void UpdateZoomPercentDisplay()
        {
            var scale = IsVideoActive() ? PureMediaViewer.CurrentScale : PhotoViewer.CurrentScale;
            ZoomPercentText.Text = $"{Math.Round(scale * 100):0}%";
        }

        private void MaximizeButton_Click(object sender, RoutedEventArgs e)
        {
            _isPreviewMaximized = !_isPreviewMaximized;
            if (_isPreviewMaximized)
            {
                HeaderBar.Visibility = Visibility.Collapsed;
                EditToolsBorder.Visibility = Visibility.Collapsed;
                TimelinePanel.Visibility = Visibility.Collapsed;
                ToolsColumn.MinWidth = 0;
                ToolsColumn.Width = new GridLength(0);
                EditorTopGrid.ColumnSpacing = 0;
                PageRoot.Padding = new Thickness(0);
                PageRoot.RowSpacing = 0;
                PreviewBorder.CornerRadius = new CornerRadius(0);
                MaximizeButtonIcon.Glyph = "\uE73F";
                ToolTipService.SetToolTip(MaximizeButton, "还原预览");
            }
            else
            {
                HeaderBar.Visibility = Visibility.Visible;
                EditToolsBorder.Visibility = Visibility.Visible;
                TimelinePanel.Visibility = Visibility.Visible;
                ToolsColumn.MinWidth = 300;
                ToolsColumn.Width = new GridLength(340);
                EditorTopGrid.ColumnSpacing = 12;
                PageRoot.Padding = new Thickness(16, 12, 16, 14);
                PageRoot.RowSpacing = 12;
                PreviewBorder.CornerRadius = new CornerRadius(8);
                MaximizeButtonIcon.Glyph = "\uE740";
                ToolTipService.SetToolTip(MaximizeButton, "最大化预览");
                UpdateClassicPadding();
                UpdateOverviewBar();
            }
        }

        private void UpdateEmptyPreviewState() =>
            PreviewEmptyState.Visibility = ViewModel.HasSelectedFile ? Visibility.Collapsed : Visibility.Visible;

        private async void PlayPauseButton_Click(object sender, RoutedEventArgs e)
        {
            var player = PureMediaViewer.Player;
            if (IsVideoActive() && player != null)
            {
                var playing = player.PlaybackSession.PlaybackState == MediaPlaybackState.Playing;
                if (playing) player.Pause(); else player.Play();
                SetPlaybackIcon(!playing);
                return;
            }
            await StartPlaybackPresentationAsync();
        }

        // Adapted from current master: paired source -> cache -> Native-backed extractor fallback.
        private async Task StartPlaybackPresentationAsync()
        {
            if (_isPlaybackSourcePending) return;
            _isPlaybackSourcePending = true;
            try
            {
                var videoPath = await ResolveVideoPathAsync();
                if (videoPath == null) return;

                PureMediaViewer.AutoCloseOnEnd = true;
                PureMediaViewer.ShowCloseButton = false;
                PureMediaViewer.ShowTransportControls = false;
                PureMediaViewer.ZoomEnabled = true;
                PureMediaViewer.VideoSource = MediaSource.CreateFromUri(new Uri(videoPath));

                var photoState = PhotoViewer.GetZoomPanState();
                _sharedZoomScale = photoState.scale;
                _sharedPanX = photoState.panX;
                _sharedPanY = photoState.panY;

                var ready = new TaskCompletionSource<bool>();
                Action onOpened = null!;
                onOpened = () =>
                {
                    PureMediaViewer.VideoOpened -= onOpened;
                    PhotoViewer.Opacity = 0;
                    PureMediaViewer.ShowDirect();
                    PureMediaViewer.ApplyZoomPanState(_sharedZoomScale, _sharedPanX, _sharedPanY);
                    ready.TrySetResult(true);
                };
                PureMediaViewer.VideoOpened += onOpened;

                await Task.WhenAny(ready.Task, Task.Delay(3000));
                if (PureMediaViewer.Visibility != Visibility.Visible)
                {
                    PhotoViewer.Opacity = 1;
                    SetPlaybackIcon(false);
                    return;
                }
                ApplyMuteState();
                SetPlaybackIcon(true);
            }
            catch
            {
                StopPlaybackPresentation();
            }
            finally
            {
                _isPlaybackSourcePending = false;
            }
        }

        private async Task<string?> ResolveVideoPathAsync()
        {
            CleanupPreviewTempVideo();
            var selectedPath = ViewModel.SelectedFilePath;
            if (string.IsNullOrEmpty(selectedPath)) return null;

            var item = ViewModel.FileItems.FirstOrDefault(f =>
                string.Equals(f.FilePath, selectedPath, StringComparison.OrdinalIgnoreCase));
            if (item == null) return null;

            if (item.LivePhotoType == LivePhotoType.DualFile &&
                !string.IsNullOrEmpty(item.PairedVideoPath) && File.Exists(item.PairedVideoPath))
                return item.PairedVideoPath;

            var cachedVideo = ViewModel.CachedTempVideoPath;
            if (!string.IsNullOrEmpty(cachedVideo) && File.Exists(cachedVideo)) return cachedVideo;

            if (item.LivePhotoType is LivePhotoType.SingleFileJpeg or LivePhotoType.SingleFileHeic && File.Exists(item.FilePath))
            {
                var nativeVideo = await LivePhotoVideoExtractor.ExtractVideoAutoAsync(
                    item.FilePath, item.AppendedVideoLength, CancellationToken.None);
                if (!string.IsNullOrEmpty(nativeVideo) && File.Exists(nativeVideo))
                {
                    _previewTempVideoPath = nativeVideo;
                    return nativeVideo;
                }
            }

            return ViewModel.IsSelectedFileVideo && File.Exists(selectedPath) ? selectedPath : null;
        }

        private void CleanupPreviewTempVideo()
        {
            if (_previewTempVideoPath == null) return;
            try { if (File.Exists(_previewTempVideoPath)) File.Delete(_previewTempVideoPath); } catch { }
            _previewTempVideoPath = null;
        }

        private void PureMediaViewer_VideoOpened()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ApplyMuteState();
                SetPlaybackIcon(true);
                UpdateZoomPercentDisplay();
            });
        }

        private void PureMediaViewer_CloseRequested(object sender, EventArgs e) => StopPlaybackPresentation();

        private void OnPreviewClearRequested()
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                StopPlaybackPresentation();
                PhotoViewer.ClearImage();
            });
        }

        private void StopPlaybackPresentation()
        {
            if (IsVideoActive())
            {
                try
                {
                    var videoState = PureMediaViewer.GetZoomPanState();
                    _sharedZoomScale = videoState.scale;
                    _sharedPanX = videoState.panX;
                    _sharedPanY = videoState.panY;
                    PureMediaViewer.Player?.Pause();
                }
                catch { }
            }
            PureMediaViewer.Visibility = Visibility.Collapsed;
            PhotoViewer.Opacity = 1;
            PhotoViewer.ApplyZoomPanState(_sharedZoomScale, _sharedPanX, _sharedPanY);
            SetPlaybackIcon(false);
            UpdateZoomPercentDisplay();
        }

        private void MuteButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.IsMuted = !ViewModel.IsMuted;
            ApplyMuteState();
        }

        private void ApplyMuteState()
        {
            PureMediaViewer.IsMuted = ViewModel.IsMuted;
            MuteIcon.Glyph = ViewModel.IsMuted ? "\uE995" : "\uE767";
        }

        private void SetPlaybackIcon(bool playing) => PlaybackIcon.Glyph = playing ? "\uE769" : "\uE768";

        private void UseCurrentFrameAsCover_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SetCurrentCoverPresentation(ViewModel.SelectedTimelineFrame);
            UpdateOverviewBar();
        }

        private void ResetPlaybackRange_Click(object sender, RoutedEventArgs e) =>
            ViewModel.ResetPlaybackRangePresentation();

        private void FilmstripModeButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SetTimelineDisplayMode(EditTimelineDisplayMode.Filmstrip);
            FilmstripModeButton.IsChecked = true;
            ClassicModeButton.IsChecked = false;
            Bindings.Update();
            ScrollSelectedFrameIntoView(true);
            UpdateOverviewBar();
        }

        private void ClassicModeButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.SetTimelineDisplayMode(EditTimelineDisplayMode.Classic);
            ClassicModeButton.IsChecked = true;
            FilmstripModeButton.IsChecked = false;
            Bindings.Update();
            UpdateClassicPadding();
            ScrollSelectedFrameIntoView(true);
            UpdateOverviewBar();
        }

        private void TimelineThumbnail_Tapped(object sender, TappedRoutedEventArgs e)
        {
            if (sender is not FrameworkElement element || element.DataContext is not EditTimelinePresentationItem item) return;
            ViewModel.SelectTimelineFrameInteractively(item.Frame);
            if (ViewModel.IsClassicPresentationMode) ScrollClassicToFrame(item.Frame, false);
            UpdateOverviewBar();
        }

        private void PreviousFrameButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.VideoFrameCount == 0) return;
            var index = ViewModel.SelectedVideoFrameIndex;
            if (index <= 0) index = 1;
            ViewModel.SelectTimelineFrameProgrammatically(
                ViewModel.VideoTimelineFrames[Math.Max(0, index - 1)].Frame);
        }

        private void NextFrameButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.VideoFrameCount == 0) return;
            var index = ViewModel.SelectedVideoFrameIndex;
            var targetIndex = index < 0 ? 0 : Math.Min(ViewModel.VideoFrameCount - 1, index + 1);
            ViewModel.SelectTimelineFrameProgrammatically(ViewModel.VideoTimelineFrames[targetIndex].Frame);
        }

        private void OnRequestScrollToFrame(TimelineFrame frame)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                if (ViewModel.IsClassicPresentationMode) ScrollClassicToFrame(frame, false);
                else ScrollFilmstripToFrame(frame, false);
                UpdateOverviewBar();
            });
        }

        private void ScrollSelectedFrameIntoView(bool disableAnimation)
        {
            var frame = ViewModel.SelectedTimelineFrame;
            if (frame == null) return;
            if (ViewModel.IsClassicPresentationMode) ScrollClassicToFrame(frame, disableAnimation);
            else ScrollFilmstripToFrame(frame, disableAnimation);
        }

        private int IndexOfVideoFrame(TimelineFrame frame)
        {
            for (var i = 0; i < ViewModel.VideoTimelineFrames.Count; i++)
                if (ReferenceEquals(ViewModel.VideoTimelineFrames[i].Frame, frame)) return i;
            return -1;
        }

        private void ScrollFilmstripToFrame(TimelineFrame frame, bool disableAnimation)
        {
            var index = IndexOfVideoFrame(frame);
            if (index < 0) return;
            var target = Math.Max(0, index * TimelineItemStep - FilmstripScrollViewer.ViewportWidth * 0.35);
            FilmstripScrollViewer.ChangeView(target, null, null, disableAnimation);
        }

        private void ScrollClassicToFrame(TimelineFrame frame, bool disableAnimation)
        {
            var index = IndexOfVideoFrame(frame);
            if (index < 0) return;
            _isClassicScrollInternal = true;
            ClassicTimelineScrollViewer.ChangeView(index * TimelineItemStep, null, null, disableAnimation);
            _isClassicScrollInternal = false;
        }

        private void ClassicTimelineScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateClassicPadding();
            ScrollSelectedFrameIntoView(true);
            UpdateOverviewBar();
        }

        private void UpdateClassicPadding()
        {
            var half = Math.Max(0, ClassicTimelineScrollViewer.ViewportWidth / 2 - TimelineItemWidth / 2);
            ClassicTimelinePaddingBorder.Padding = new Thickness(half, 0, half, 0);
        }

        private void ClassicTimelineScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            UpdateOverviewBar();
            if (e.IsIntermediate || _isClassicScrollInternal || ViewModel.VideoFrameCount == 0) return;

            var index = (int)Math.Round(ClassicTimelineScrollViewer.HorizontalOffset / TimelineItemStep);
            index = Math.Clamp(index, 0, ViewModel.VideoFrameCount - 1);
            ViewModel.SelectTimelineFrameInteractively(ViewModel.VideoTimelineFrames[index].Frame);

            _isClassicScrollInternal = true;
            ClassicTimelineScrollViewer.ChangeView(index * TimelineItemStep, null, null, true);
            _isClassicScrollInternal = false;
        }

        private void TimelineScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e) => UpdateOverviewBar();
        private void TimelineViewport_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateOverviewBar();
        private void TimelineOverviewTrack_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateOverviewBar();

        private void UpdateOverviewBar()
        {
            if (TimelineOverviewTrack.ActualWidth <= 0) return;
            var viewer = ViewModel.IsClassicPresentationMode ? ClassicTimelineScrollViewer : FilmstripScrollViewer;
            var trackWidth = TimelineOverviewTrack.ActualWidth;
            var contentWidth = Math.Max(trackWidth, ViewModel.VideoFrameCount * TimelineItemStep);
            var viewportRatio = Math.Min(1, Math.Max(1, viewer.ViewportWidth) / contentWidth);
            TimelineOverviewViewport.Width = Math.Min(trackWidth, Math.Max(24, trackWidth * viewportRatio));

            var viewportTravel = Math.Max(0, trackWidth - TimelineOverviewViewport.Width);
            var viewportLeft = viewportTravel * Math.Clamp(viewer.HorizontalOffset / Math.Max(1, viewer.ScrollableWidth), 0, 1);
            Canvas.SetLeft(TimelineOverviewViewport, viewportLeft);

            var selected = ViewModel.SelectedVideoFrameIndex;
            var positionRatio = ViewModel.VideoFrameCount <= 1 || selected < 0
                ? 0 : (double)selected / (ViewModel.VideoFrameCount - 1);
            Canvas.SetLeft(TimelineOverviewPosition,
                Math.Clamp(positionRatio * (trackWidth - TimelineOverviewPosition.Width), 0, trackWidth));
        }
    }
}
