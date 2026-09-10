using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Navigation;
using System;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.Core;
using Windows.Media.Playback;
using Windows.Storage.Pickers;

namespace LivePhotoBox.Views
{
    /// <summary>
    /// EditPage presentation shell on top of current master's media/protocol/native architecture.
    /// </summary>
    public sealed partial class EditPage : Page
    {
        private const double TimelineItemWidth = 72.0;
        private const double TimelineItemSpacing = 6.0;
        private const double TimelineItemStep = TimelineItemWidth + TimelineItemSpacing;

        public EditViewModel ViewModel => AppViewModel.Instance.Edit;

        private UIElement? _editNavigationInputHost;
        private bool _isPreviewMaximized;
        private bool _isPlaybackSourcePending;
        private string? _previewTempVideoPath;
        private double _sharedZoomScale = 1.0;
        private double _sharedPanX = 0.5;
        private double _sharedPanY = 0.5;

        // Filmstrip keeps free browsing, but coalesces high-frequency wheel input so repeated
        // ChangeView calls cannot overwhelm WinUI's scroll presenter.
        private readonly PointerEventHandler _filmstripWheelHandler;
        private double _filmstripTargetOffset = -1;
        private DateTime _lastFilmstripWheelTime = DateTime.MinValue;
        private bool _filmstripScrollQueued;
        private CancellationTokenSource? _filmstripScrollRetryCts;

        // Classic keeps the mature center/snap behavior, with the same target/retry protections.
        private readonly PointerEventHandler _classicWheelHandler;
        private double _classicTargetOffset = -1;
        private DateTime _lastClassicWheelTime = DateTime.MinValue;
        private bool _classicScrollQueued;
        private CancellationTokenSource? _classicScrollRetryCts;

        private bool _isOverviewDragging;

        public EditPage()
        {
            InitializeComponent();

            _filmstripWheelHandler = new PointerEventHandler(FilmstripScrollViewer_PointerWheelChanged);
            _classicWheelHandler = new PointerEventHandler(ClassicTimelineScrollViewer_PointerWheelChanged);
            FilmstripScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent, _filmstripWheelHandler, handledEventsToo: true);
            ClassicTimelineScrollViewer.AddHandler(UIElement.PointerWheelChangedEvent, _classicWheelHandler, handledEventsToo: true);

            ViewModel.RequestScrollToFrame += OnRequestScrollToFrame;
            ViewModel.PreviewClearRequested += OnPreviewClearRequested;
            ViewModel.PropertyChanged += ViewModel_PropertyChanged;
            PhotoViewer.ScaleChanged += PhotoViewer_ScaleChanged;
            PureMediaViewer.ScaleChanged += PureMediaViewer_ScaleChanged;
            PureMediaViewer.VideoOpened += PureMediaViewer_VideoOpened;
            Loaded += EditPage_Loaded;
            Unloaded += EditPage_Unloaded;
        }

        protected override void OnNavigatedTo(NavigationEventArgs e)
        {
            base.OnNavigatedTo(e);
            AttachEditNavigationInput();
            Bindings.Update();
            UpdateEmptyPreviewState();
            UpdateClassicPadding();
            ScrollSelectedFrameIntoView(true);
            UpdateOverviewBar();
        }

        protected override void OnNavigatedFrom(NavigationEventArgs e)
        {
            DetachEditNavigationInput();
            CancelTimelineScrollRetries();
            StopPlaybackPresentation();
            CleanupPreviewTempVideo();
            base.OnNavigatedFrom(e);
        }

        private void EditPage_Loaded(object sender, RoutedEventArgs e)
        {
            UpdateEmptyPreviewState();
            UpdateClassicPadding();
            UpdateZoomPercentDisplay();
            ApplyMuteState();
            UpdateOverviewBar();
        }

        private void EditPage_Unloaded(object sender, RoutedEventArgs e)
        {
            DetachEditNavigationInput();
            CancelTimelineScrollRetries();
            StopPlaybackPresentation();
            CleanupPreviewTempVideo();
        }

        private void CancelTimelineScrollRetries()
        {
            _filmstripScrollRetryCts?.Cancel();
            _filmstripScrollRetryCts?.Dispose();
            _filmstripScrollRetryCts = null;
            _classicScrollRetryCts?.Cancel();
            _classicScrollRetryCts?.Dispose();
            _classicScrollRetryCts = null;
        }

        private void ViewModel_PropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(EditViewModel.SelectedFilePath))
            {
                StopPlaybackPresentation();
                CleanupPreviewTempVideo();
                _sharedZoomScale = 1.0;
                _sharedPanX = 0.5;
                _sharedPanY = 0.5;
                PhotoViewer.ResetToFit();
                UpdateEmptyPreviewState();
                Bindings.Update();
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

        // =====================================================================
        // Minimal file entry. Discovery/pairing remains in current master's VM.
        // =====================================================================

        private async void OpenLivePhotoButton_Click(object sender, RoutedEventArgs e)
        {
            OpenLivePhotoButton.IsEnabled = false;
            try
            {
                var picker = new FileOpenPicker
                {
                    SuggestedStartLocation = PickerLocationId.PicturesLibrary,
                    ViewMode = PickerViewMode.Thumbnail
                };
                picker.FileTypeFilter.Add(".jpg");
                picker.FileTypeFilter.Add(".jpeg");
                picker.FileTypeFilter.Add(".heic");
                picker.FileTypeFilter.Add(".heif");

                var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
                WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);
                var file = await picker.PickSingleFileAsync();
                if (file == null) return;

                if (await ViewModel.OpenSingleFileForEditAsync(file.Path))
                {
                    Bindings.Update();
                    UpdateEmptyPreviewState();
                    UpdateClassicPadding();
                    ScrollSelectedFrameIntoView(true);
                    UpdateOverviewBar();
                }
            }
            catch (Exception ex)
            {
                LogService.Debug($"EditPage open-file failed: {ex.Message}", LogSource.UI);
            }
            finally
            {
                OpenLivePhotoButton.IsEnabled = true;
            }
        }

        // =====================================================================
        // Preview: preserve current-master zoom/playback state synchronization.
        // =====================================================================

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
                ToolTipService.SetToolTip(MaximizeButton, ResourceService.GetString("EditPage_RestorePreviewTooltip"));
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
                ToolTipService.SetToolTip(MaximizeButton, ResourceService.GetString("EditPage_MaximizePreviewTooltip"));
                UpdateClassicPadding();
                ScrollSelectedFrameIntoView(true);
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

                var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
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
                PureMediaViewer.VideoOpened -= onOpened;
                if (PureMediaViewer.Visibility != Visibility.Visible)
                {
                    PhotoViewer.Opacity = 1;
                    SetPlaybackIcon(false);
                    return;
                }

                ApplyMuteState();
                SetPlaybackIcon(true);
            }
            catch (Exception ex)
            {
                LogService.Debug($"EditPage playback failed: {ex.Message}", LogSource.UI);
                StopPlaybackPresentation();
            }
            finally
            {
                _isPlaybackSourcePending = false;
            }
        }

        // Current-master source resolution: paired source -> cache -> Native-backed extractor.
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

        private void PureMediaViewer_CloseRequested(object sender, EventArgs e)
        {
            StopPlaybackPresentation();
            CleanupPreviewTempVideo();
        }

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

        // =====================================================================
        // Mature root PreviewKeyDown behavior, stripped of old File Browser UI.
        // =====================================================================

        private void AttachEditNavigationInput()
        {
            if (_editNavigationInputHost != null || App.MainWindow?.Content is not UIElement host)
                return;
            host.PreviewKeyDown += EditNavigationHost_PreviewKeyDown;
            _editNavigationInputHost = host;
        }

        private void DetachEditNavigationInput()
        {
            if (_editNavigationInputHost == null) return;
            _editNavigationInputHost.PreviewKeyDown -= EditNavigationHost_PreviewKeyDown;
            _editNavigationInputHost = null;
        }

        private void EditNavigationHost_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
        {
            if (App.MainWindow is MainWindow { Lightbox.IsOpen: true }) return;

            const Windows.System.VirtualKey oemPlus = (Windows.System.VirtualKey)187;
            const Windows.System.VirtualKey oemMinus = (Windows.System.VirtualKey)189;
            var controlDown = IsModifierDown(Windows.System.VirtualKey.Control);
            var shiftDown = IsModifierDown(Windows.System.VirtualKey.Shift);
            var altDown = IsModifierDown(Windows.System.VirtualKey.Menu);
            var noModifiers = !controlDown && !shiftDown && !altDown;
            var onlyControl = controlDown && !shiftDown && !altDown;
            var controlShift = controlDown && shiftDown && !altDown;

            DependencyObject? focused = _editNavigationInputHost?.XamlRoot != null
                ? FocusManager.GetFocusedElement(_editNavigationInputHost.XamlRoot) as DependencyObject
                : null;
            if (ShouldPreserveEditShortcut(focused)) return;

            var isZoomIn = e.Key == Windows.System.VirtualKey.Add || e.Key == oemPlus;
            var isZoomOut = e.Key == Windows.System.VirtualKey.Subtract || (e.Key == oemMinus && !shiftDown);

            if ((noModifiers || (shiftDown && !controlDown && !altDown)) && isZoomIn && ViewModel.HasSelectedFile)
            {
                ZoomInButton_Click(this, e);
                e.Handled = true;
            }
            else if (noModifiers && isZoomOut && ViewModel.HasSelectedFile)
            {
                ZoomOutButton_Click(this, e);
                e.Handled = true;
            }
            else if (noModifiers && e.Key == Windows.System.VirtualKey.Number0 && ViewModel.HasSelectedFile)
            {
                if (IsVideoActive()) PureMediaViewer.ResetToFit(); else PhotoViewer.ResetToFit();
                UpdateZoomPercentDisplay();
                e.Handled = true;
            }
            else if (noModifiers && e.Key == Windows.System.VirtualKey.F11 && ViewModel.HasSelectedFile)
            {
                MaximizeButton_Click(MaximizeButton, e);
                e.Handled = true;
            }
            else if (noModifiers && e.Key == Windows.System.VirtualKey.Escape)
            {
                if (IsVideoActive())
                {
                    StopPlaybackPresentation();
                    e.Handled = true;
                }
                else if (_isPreviewMaximized)
                {
                    MaximizeButton_Click(MaximizeButton, e);
                    e.Handled = true;
                }
            }
            else if (noModifiers && e.Key == Windows.System.VirtualKey.Space && ViewModel.CanPlayLivePhoto)
            {
                PlayPauseButton_Click(PlayPauseButton, e);
                e.Handled = true;
            }
            else if (noModifiers && e.Key == Windows.System.VirtualKey.M && ViewModel.CanPlayLivePhoto)
            {
                MuteButton_Click(this, e);
                e.Handled = true;
            }
            else if (noModifiers && (e.Key is Windows.System.VirtualKey.Left or Windows.System.VirtualKey.Right
                or Windows.System.VirtualKey.Home or Windows.System.VirtualKey.End))
            {
                e.Handled = TryNavigateTimelineFrame(e.Key);
            }
            else if (onlyControl && e.Key == Windows.System.VirtualKey.O)
            {
                OpenLivePhotoButton_Click(OpenLivePhotoButton, e);
                e.Handled = true;
            }
            else if (onlyControl && e.Key == Windows.System.VirtualKey.S)
            {
                e.Handled = TryExecuteEditCommand(ViewModel.SaveCommand);
            }
            else if (controlShift && e.Key == Windows.System.VirtualKey.S)
            {
                e.Handled = TryExecuteEditCommand(ViewModel.SaveAsCommand);
            }
            else if (onlyControl && e.Key == Windows.System.VirtualKey.E)
            {
                e.Handled = TryExecuteEditCommand(ViewModel.ExportCurrentFrameCommand);
            }
            else if (controlShift && e.Key == Windows.System.VirtualKey.E)
            {
                e.Handled = TryExecuteEditCommand(ViewModel.ExportAllFramesCommand);
            }
        }

        private bool TryNavigateTimelineFrame(Windows.System.VirtualKey key)
        {
            if (ViewModel.VideoFrameCount == 0) return false;
            var current = ViewModel.SelectedVideoFrameIndex;
            var target = key switch
            {
                Windows.System.VirtualKey.Home => 0,
                Windows.System.VirtualKey.End => ViewModel.VideoFrameCount - 1,
                Windows.System.VirtualKey.Left when current >= 0 => Math.Max(0, current - 1),
                Windows.System.VirtualKey.Right when current >= 0 => Math.Min(ViewModel.VideoFrameCount - 1, current + 1),
                _ => 0
            };
            ViewModel.SelectTimelineFrameProgrammatically(ViewModel.VideoTimelineFrames[target].Frame);
            return true;
        }

        private static bool TryExecuteEditCommand(System.Windows.Input.ICommand command)
        {
            if (!command.CanExecute(null)) return false;
            command.Execute(null);
            return true;
        }

        private static bool IsModifierDown(Windows.System.VirtualKey key)
        {
            if (IsKeyDown(key)) return true;
            return key switch
            {
                Windows.System.VirtualKey.Control => IsKeyDown(Windows.System.VirtualKey.LeftControl) || IsKeyDown(Windows.System.VirtualKey.RightControl),
                Windows.System.VirtualKey.Shift => IsKeyDown(Windows.System.VirtualKey.LeftShift) || IsKeyDown(Windows.System.VirtualKey.RightShift),
                Windows.System.VirtualKey.Menu => IsKeyDown(Windows.System.VirtualKey.LeftMenu) || IsKeyDown(Windows.System.VirtualKey.RightMenu),
                _ => false
            };
        }

        private static bool IsKeyDown(Windows.System.VirtualKey key) =>
            (Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(key)
                & Windows.UI.Core.CoreVirtualKeyStates.Down) != 0;

        private static bool ShouldPreserveEditShortcut(DependencyObject? source)
        {
            for (DependencyObject? current = source; current != null; current = VisualTreeHelper.GetParent(current))
            {
                if (current is TextBox or RichEditBox or PasswordBox or NumberBox or ComboBox or Slider)
                    return true;
            }
            return false;
        }

        // =====================================================================
        // View-state tools; media mutation remains outside this phase.
        // =====================================================================

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
            if (ViewModel.IsClassicPresentationMode)
                ScrollClassicToFrame(item.Frame, false);
            UpdateOverviewBar();
        }

        private void PreviousFrameButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.VideoFrameCount == 0) return;
            var index = ViewModel.SelectedVideoFrameIndex;
            var target = index <= 0 ? 0 : index - 1;
            ViewModel.SelectTimelineFrameProgrammatically(ViewModel.VideoTimelineFrames[target].Frame);
        }

        private void NextFrameButton_Click(object sender, RoutedEventArgs e)
        {
            if (ViewModel.VideoFrameCount == 0) return;
            var index = ViewModel.SelectedVideoFrameIndex;
            var target = index < 0 ? 0 : Math.Min(ViewModel.VideoFrameCount - 1, index + 1);
            ViewModel.SelectTimelineFrameProgrammatically(ViewModel.VideoTimelineFrames[target].Frame);
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

        // =====================================================================
        // Filmstrip: free browsing + click selection, with coalesced wheel input.
        // =====================================================================

        private void FilmstripScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (!ViewModel.IsFilmstripPresentationMode || FilmstripScrollViewer.ScrollableWidth <= 0) return;
            var delta = e.GetCurrentPoint(FilmstripScrollViewer).Properties.MouseWheelDelta;
            QueueFilmstripScroll(-(delta / 120.0) * TimelineItemStep);
            e.Handled = true;
        }

        private void QueueFilmstripScroll(double deltaPixels)
        {
            if ((DateTime.Now - _lastFilmstripWheelTime).TotalMilliseconds > 250 || _filmstripTargetOffset < 0)
                _filmstripTargetOffset = FilmstripScrollViewer.HorizontalOffset;
            _lastFilmstripWheelTime = DateTime.Now;

            _filmstripTargetOffset = Math.Clamp(
                _filmstripTargetOffset + deltaPixels,
                0,
                Math.Max(0, FilmstripScrollViewer.ScrollableWidth));

            if (_filmstripScrollQueued) return;
            _filmstripScrollQueued = true;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                _filmstripScrollQueued = false;
                FilmstripScrollViewer.ChangeView(_filmstripTargetOffset, null, null, disableAnimation: false);
            });
        }

        private void ScrollFilmstripToFrame(TimelineFrame frame, bool disableAnimation)
        {
            var index = IndexOfVideoFrame(frame);
            if (index < 0) return;
            var itemLeft = index * TimelineItemStep;
            var itemRight = itemLeft + TimelineItemWidth;
            var viewportLeft = FilmstripScrollViewer.HorizontalOffset;
            var viewportRight = viewportLeft + FilmstripScrollViewer.ViewportWidth;
            var target = viewportLeft;
            if (itemLeft < viewportLeft) target = itemLeft;
            else if (itemRight > viewportRight) target = itemRight - FilmstripScrollViewer.ViewportWidth;
            target = Math.Clamp(target, 0, Math.Max(0, FilmstripScrollViewer.ScrollableWidth));

            _filmstripTargetOffset = target;
            if (FilmstripScrollViewer.ViewportWidth > 0 && target <= FilmstripScrollViewer.ScrollableWidth + 0.5)
            {
                FilmstripScrollViewer.ChangeView(target, null, null, disableAnimation);
                return;
            }
            StartFilmstripScrollRetry(index, disableAnimation);
        }

        private void StartFilmstripScrollRetry(int index, bool disableAnimation)
        {
            _filmstripScrollRetryCts?.Cancel();
            _filmstripScrollRetryCts?.Dispose();
            _filmstripScrollRetryCts = new CancellationTokenSource();
            _ = FilmstripScrollRetryAsync(index, disableAnimation, _filmstripScrollRetryCts.Token);
        }

        private async Task FilmstripScrollRetryAsync(int index, bool disableAnimation, CancellationToken token)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try { await Task.Delay(50, token); }
                catch (OperationCanceledException) { return; }
                if (index >= ViewModel.VideoFrameCount) return;

                var itemLeft = index * TimelineItemStep;
                var target = Math.Clamp(
                    itemLeft - Math.Max(0, FilmstripScrollViewer.ViewportWidth - TimelineItemWidth),
                    0,
                    Math.Max(0, FilmstripScrollViewer.ScrollableWidth));
                if (FilmstripScrollViewer.ViewportWidth > 0 && FilmstripScrollViewer.ExtentWidth > 0)
                {
                    _filmstripTargetOffset = target;
                    FilmstripScrollViewer.ChangeView(target, null, null, disableAnimation);
                    return;
                }
            }
        }

        private void TimelineScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            if (!e.IsIntermediate)
                _filmstripTargetOffset = FilmstripScrollViewer.HorizontalOffset;
            UpdateOverviewBar();
        }

        private void TimelineViewport_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            DispatcherQueue.TryEnqueue(() =>
            {
                ScrollSelectedFrameIntoView(true);
                UpdateOverviewBar();
            });
        }

        // =====================================================================
        // Classic: center padding + snap selection + protected wheel ChangeView.
        // =====================================================================

        private void ClassicTimelineScrollViewer_PointerWheelChanged(object sender, PointerRoutedEventArgs e)
        {
            if (!ViewModel.IsClassicPresentationMode || ViewModel.VideoFrameCount == 0) return;
            var delta = e.GetCurrentPoint(ClassicTimelineScrollViewer).Properties.MouseWheelDelta;
            QueueClassicScroll(-(delta / 120.0));
            e.Handled = true;
        }

        private void QueueClassicScroll(double frameSteps)
        {
            if ((DateTime.Now - _lastClassicWheelTime).TotalMilliseconds > 250 || _classicTargetOffset < 0)
            {
                _classicTargetOffset = Math.Round(
                    ClassicTimelineScrollViewer.HorizontalOffset / TimelineItemStep) * TimelineItemStep;
            }
            _lastClassicWheelTime = DateTime.Now;
            _classicTargetOffset = Math.Clamp(
                _classicTargetOffset + frameSteps * TimelineItemStep,
                0,
                Math.Max(0, ClassicTimelineScrollViewer.ScrollableWidth));

            if (_classicScrollQueued) return;
            _classicScrollQueued = true;
            DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Normal, () =>
            {
                _classicScrollQueued = false;
                ClassicTimelineScrollViewer.ChangeView(_classicTargetOffset, null, null, disableAnimation: false);
            });
        }

        private void ScrollClassicToFrame(TimelineFrame frame, bool disableAnimation)
        {
            var index = IndexOfVideoFrame(frame);
            if (index < 0) return;
            var target = index * TimelineItemStep;
            _classicTargetOffset = target;

            if (ClassicTimelineScrollViewer.ViewportWidth > 0 &&
                target <= ClassicTimelineScrollViewer.ScrollableWidth + 0.5)
            {
                ClassicTimelineScrollViewer.ChangeView(target, null, null, disableAnimation);
                return;
            }

            StartClassicScrollRetry(index, disableAnimation);
        }

        private void StartClassicScrollRetry(int index, bool disableAnimation)
        {
            _classicScrollRetryCts?.Cancel();
            _classicScrollRetryCts?.Dispose();
            _classicScrollRetryCts = new CancellationTokenSource();
            _ = ClassicScrollRetryAsync(index, disableAnimation, _classicScrollRetryCts.Token);
        }

        private async Task ClassicScrollRetryAsync(int index, bool disableAnimation, CancellationToken token)
        {
            for (var attempt = 0; attempt < 10; attempt++)
            {
                try { await Task.Delay(50, token); }
                catch (OperationCanceledException) { return; }
                if (index >= ViewModel.VideoFrameCount) return;

                var target = index * TimelineItemStep;
                if (ClassicTimelineScrollViewer.ViewportWidth > 0 &&
                    ClassicTimelineScrollViewer.ExtentWidth > 0 &&
                    target <= ClassicTimelineScrollViewer.ScrollableWidth + 0.5)
                {
                    _classicTargetOffset = target;
                    ClassicTimelineScrollViewer.ChangeView(target, null, null, disableAnimation);
                    return;
                }
            }
        }

        private void ClassicTimelineScrollViewer_SizeChanged(object sender, SizeChangedEventArgs e)
        {
            UpdateClassicPadding();
            DispatcherQueue.TryEnqueue(() =>
            {
                ScrollSelectedFrameIntoView(true);
                UpdateOverviewBar();
            });
        }

        private void UpdateClassicPadding()
        {
            var half = Math.Max(0, ClassicTimelineScrollViewer.ViewportWidth / 2 - TimelineItemWidth / 2);
            ClassicTimelinePaddingBorder.Padding = new Thickness(half, 0, half, 0);
        }

        private void ClassicTimelineScrollViewer_ViewChanged(object sender, ScrollViewerViewChangedEventArgs e)
        {
            UpdateOverviewBar();
            if (e.IsIntermediate || ViewModel.VideoFrameCount == 0) return;

            var index = (int)Math.Round(ClassicTimelineScrollViewer.HorizontalOffset / TimelineItemStep);
            index = Math.Clamp(index, 0, ViewModel.VideoFrameCount - 1);
            var snapOffset = index * TimelineItemStep;
            _classicTargetOffset = snapOffset;

            if (Math.Abs(ClassicTimelineScrollViewer.HorizontalOffset - snapOffset) > 0.5)
            {
                ClassicTimelineScrollViewer.ChangeView(snapOffset, null, null, disableAnimation: true);
                return;
            }

            var targetFrame = ViewModel.VideoTimelineFrames[index].Frame;
            if (!ReferenceEquals(ViewModel.SelectedTimelineFrame, targetFrame))
                ViewModel.SelectTimelineFrameInteractively(targetFrame);
        }

        // =====================================================================
        // Overview / Position Bar: sole global timeline position control.
        // =====================================================================

        private void TimelineOverviewTrack_SizeChanged(object sender, SizeChangedEventArgs e) => UpdateOverviewBar();

        private void TimelineOverviewTrack_PointerPressed(object sender, PointerRoutedEventArgs e)
        {
            _isOverviewDragging = true;
            TimelineOverviewTrack.CapturePointer(e.Pointer);
            NavigateOverviewTo(e.GetCurrentPoint(TimelineOverviewTrack).Position.X, final: false);
            e.Handled = true;
        }

        private void TimelineOverviewTrack_PointerMoved(object sender, PointerRoutedEventArgs e)
        {
            if (!_isOverviewDragging) return;
            NavigateOverviewTo(e.GetCurrentPoint(TimelineOverviewTrack).Position.X, final: false);
            e.Handled = true;
        }

        private void TimelineOverviewTrack_PointerReleased(object sender, PointerRoutedEventArgs e)
        {
            if (!_isOverviewDragging) return;
            NavigateOverviewTo(e.GetCurrentPoint(TimelineOverviewTrack).Position.X, final: true);
            _isOverviewDragging = false;
            TimelineOverviewTrack.ReleasePointerCapture(e.Pointer);
            e.Handled = true;
        }

        private void TimelineOverviewTrack_PointerCaptureLost(object sender, PointerRoutedEventArgs e)
        {
            _isOverviewDragging = false;
        }

        private void NavigateOverviewTo(double x, bool final)
        {
            if (TimelineOverviewTrack.ActualWidth <= 0) return;
            var viewer = ViewModel.IsClassicPresentationMode ? ClassicTimelineScrollViewer : FilmstripScrollViewer;
            var ratio = Math.Clamp(x / TimelineOverviewTrack.ActualWidth, 0, 1);
            var target = ratio * Math.Max(0, viewer.ScrollableWidth);

            if (ViewModel.IsClassicPresentationMode && final && ViewModel.VideoFrameCount > 0)
            {
                var index = (int)Math.Round(target / TimelineItemStep);
                index = Math.Clamp(index, 0, ViewModel.VideoFrameCount - 1);
                var frame = ViewModel.VideoTimelineFrames[index].Frame;
                ViewModel.SelectTimelineFrameProgrammatically(frame);
            }
            else
            {
                viewer.ChangeView(target, null, null, disableAnimation: true);
                if (ViewModel.IsClassicPresentationMode) _classicTargetOffset = target;
                else _filmstripTargetOffset = target;
            }
            UpdateOverviewBar();
        }

        private void UpdateOverviewBar()
        {
            if (TimelineOverviewTrack.ActualWidth <= 0) return;
            var viewer = ViewModel.IsClassicPresentationMode ? ClassicTimelineScrollViewer : FilmstripScrollViewer;
            var trackWidth = TimelineOverviewTrack.ActualWidth;
            var scrollable = Math.Max(0, viewer.ScrollableWidth);
            var extent = Math.Max(viewer.ViewportWidth, viewer.ExtentWidth);
            var viewportRatio = extent <= 0 ? 1 : Math.Clamp(viewer.ViewportWidth / extent, 0, 1);
            TimelineOverviewViewport.Width = Math.Min(trackWidth, Math.Max(24, trackWidth * viewportRatio));

            var viewportTravel = Math.Max(0, trackWidth - TimelineOverviewViewport.Width);
            var viewportLeft = scrollable <= 0 ? 0 : viewportTravel * Math.Clamp(viewer.HorizontalOffset / scrollable, 0, 1);
            Canvas.SetLeft(TimelineOverviewViewport, viewportLeft);

            var selected = ViewModel.SelectedVideoFrameIndex;
            var positionRatio = ViewModel.VideoFrameCount <= 1 || selected < 0
                ? 0 : (double)selected / (ViewModel.VideoFrameCount - 1);
            Canvas.SetLeft(TimelineOverviewPosition,
                Math.Clamp(positionRatio * Math.Max(0, trackWidth - TimelineOverviewPosition.Width), 0, trackWidth));
        }
    }
}
