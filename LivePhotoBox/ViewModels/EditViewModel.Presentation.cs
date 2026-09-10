using LivePhotoBox.Models;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;

namespace LivePhotoBox.ViewModels
{
    /// <summary>
    /// View-facing state for the master-based EditPage visual refactor.
    /// Media/protocol/native services on current master remain authoritative.
    /// </summary>
    public partial class EditViewModel
    {
        private readonly ObservableCollection<EditTimelinePresentationItem> _videoTimelineFrames = new();
        private bool _editPresentationInitialized;
        private bool _presentationRefreshQueued;
        private TimelineFrame? _presentationCurrentCoverFrame;
        private TimelineFrame? _presentationCurrentCoverAssociationFrame;
        private TimelineFrame? _presentationOriginalCoverAssociationFrame;
        private EditTimelineDisplayMode _timelineDisplayMode = EditTimelineDisplayMode.Filmstrip;
        private TimeSpan _playbackRangeStart = TimeSpan.Zero;
        private TimeSpan _playbackRangeEnd = TimeSpan.Zero;

        public ObservableCollection<EditTimelinePresentationItem> VideoTimelineFrames
        {
            get
            {
                EnsureEditPresentationInitialized();
                return _videoTimelineFrames;
            }
        }

        public EditTimelineDisplayMode TimelineDisplayMode
        {
            get => _timelineDisplayMode;
            set
            {
                if (_timelineDisplayMode == value) return;
                _timelineDisplayMode = value;
                OnPropertyChanged(nameof(TimelineDisplayMode));
                OnPropertyChanged(nameof(IsFilmstripPresentationMode));
                OnPropertyChanged(nameof(IsClassicPresentationMode));
            }
        }

        public bool IsFilmstripPresentationMode => TimelineDisplayMode == EditTimelineDisplayMode.Filmstrip;
        public bool IsClassicPresentationMode => TimelineDisplayMode == EditTimelineDisplayMode.Classic;

        public int VideoFrameCount
        {
            get
            {
                EnsureEditPresentationInitialized();
                return _videoTimelineFrames.Count;
            }
        }

        public int SelectedVideoFrameIndex
        {
            get
            {
                EnsureEditPresentationInitialized();
                if (SelectedTimelineFrame == null) return -1;
                for (var i = 0; i < _videoTimelineFrames.Count; i++)
                {
                    if (ReferenceEquals(_videoTimelineFrames[i].Frame, SelectedTimelineFrame))
                        return i;
                }
                return -1;
            }
        }

        public TimeSpan SelectedFrameTimestamp =>
            SelectedTimelineFrame != null && !SelectedTimelineFrame.IsStillPhoto && !SelectedTimelineFrame.IsOriginalPhoto
                ? SelectedTimelineFrame.Timestamp
                : TimeSpan.Zero;

        public TimeSpan VideoDuration
        {
            get
            {
                EnsureEditPresentationInitialized();
                if (_videoTimelineFrames.Count == 0) return TimeSpan.Zero;
                if (_videoTimelineFrames.Count == 1) return _videoTimelineFrames[0].Frame.Timestamp;

                var last = _videoTimelineFrames[^1].Frame.Timestamp;
                var previous = _videoTimelineFrames[^2].Frame.Timestamp;
                var step = last - previous;
                return step > TimeSpan.Zero ? last + step : last;
            }
        }

        public string TimelinePositionText
        {
            get
            {
                var selected = SelectedVideoFrameIndex;
                var displayIndex = selected >= 0 ? selected + 1 : 0;
                return ResourceService.Format(
                    "EditPage_Visual_TimelinePosition",
                    displayIndex,
                    VideoFrameCount,
                    SelectedFrameTimestamp.TotalSeconds.ToString("0.00"),
                    VideoDuration.TotalSeconds.ToString("0.00"));
            }
        }

        public ImageSource? CurrentCoverThumbnail
        {
            get
            {
                EnsureEditPresentationInitialized();
                if (_presentationCurrentCoverFrame?.Thumbnail != null)
                    return _presentationCurrentCoverFrame.Thumbnail;

                var currentStill = FindCurrentCoverAsset();
                return currentStill?.Thumbnail ?? SelectedFileThumbnail;
            }
        }

        public ImageSource? OriginalCoverThumbnail
        {
            get
            {
                EnsureEditPresentationInitialized();
                return FindOriginalCoverAsset()?.Thumbnail;
            }
        }

        public string CurrentCoverAssociationText =>
            BuildAssociationText(_presentationCurrentCoverAssociationFrame);

        public string OriginalCoverAssociationText =>
            BuildAssociationText(_presentationOriginalCoverAssociationFrame);

        public string PlaybackRangeStartText =>
            ResourceService.Format("EditPage_Visual_Seconds", _playbackRangeStart.TotalSeconds.ToString("0.00"));

        public string PlaybackRangeEndText =>
            ResourceService.Format("EditPage_Visual_Seconds", _playbackRangeEnd.TotalSeconds.ToString("0.00"));

        public string PlaybackRangeSummaryText =>
            ResourceService.Format(
                "EditPage_Visual_PlaybackRangeSummary",
                Math.Max(0, (_playbackRangeEnd - _playbackRangeStart).TotalSeconds).ToString("0.00"),
                VideoFrameCount);

        /// <summary>
        /// No operation in this UI phase mutates the motion video, so nothing here can require
        /// re-encoding. This remains false until a future backend-backed edit explicitly owns it.
        /// </summary>
        public bool IsReencodeWarningVisible => false;

        public void SetTimelineDisplayMode(EditTimelineDisplayMode mode) => TimelineDisplayMode = mode;

        /// <summary>
        /// Session cover choice. The selected real video frame is reliable user-provided
        /// association evidence, so the marker may point to that exact frame. No writer/encoder runs.
        /// </summary>
        public void SetCurrentCoverPresentation(TimelineFrame? frame)
        {
            EnsureEditPresentationInitialized();
            if (frame == null || frame.IsStillPhoto || frame.IsOriginalPhoto) return;
            if (!_videoTimelineFrames.Any(item => ReferenceEquals(item.Frame, frame))) return;

            _presentationCurrentCoverFrame = frame;
            _presentationCurrentCoverAssociationFrame = frame;
            RefreshCoverMarkers();
            RaiseCoverPresentationProperties();
        }

        /// <summary>View-state reset only. No media trimming is performed.</summary>
        public void ResetPlaybackRangePresentation()
        {
            EnsureEditPresentationInitialized();
            _playbackRangeStart = TimeSpan.Zero;
            _playbackRangeEnd = VideoDuration;
            RaisePlaybackRangeProperties();
        }

        /// <summary>
        /// Minimal single-file entry for the new EditPage. It deliberately delegates discovery,
        /// pairing and protocol inspection to current master's existing directory scan pipeline.
        /// </summary>
        public async Task<bool> OpenSingleFileForEditAsync(string filePath)
        {
            if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
                return false;
            if (!IsSupportedImageExtension(Path.GetExtension(filePath)))
                return false;

            var directory = Path.GetDirectoryName(filePath);
            if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory))
                return false;

            CurrentDirectory = directory;
            await ScanDirectoryAsync(directory).ConfigureAwait(false);

            // ApplySortAndFilter enqueues its collection update before this callback. Enqueueing
            // selection afterwards preserves that ordering without duplicating scanner logic.
            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher == null) return false;

            var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            if (!dispatcher.TryEnqueue(() =>
            {
                var item = FileItems.FirstOrDefault(candidate =>
                    string.Equals(candidate.FilePath, filePath, StringComparison.OrdinalIgnoreCase));
                if (item == null)
                {
                    completion.TrySetResult(false);
                    return;
                }

                SelectFile(item.FilePath);
                completion.TrySetResult(true);
            }))
            {
                return false;
            }
            return await completion.Task.ConfigureAwait(false);
        }

        private void EnsureEditPresentationInitialized()
        {
            if (_editPresentationInitialized) return;
            _editPresentationInitialized = true;
            TimelineFrames.CollectionChanged += TimelineFrames_PresentationCollectionChanged;
            PropertyChanged += EditViewModel_PresentationPropertyChanged;
            RefreshTimelinePresentation();
        }

        private void TimelineFrames_PresentationCollectionChanged(object? sender, NotifyCollectionChangedEventArgs e)
        {
            QueuePresentationRefresh();
        }

        private void EditViewModel_PresentationPropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(SelectedTimelineFrame))
            {
                OnPropertyChanged(nameof(SelectedVideoFrameIndex));
                OnPropertyChanged(nameof(SelectedFrameTimestamp));
                OnPropertyChanged(nameof(TimelinePositionText));
            }
            else if (e.PropertyName == nameof(SelectedFilePath))
            {
                _presentationCurrentCoverFrame = null;
                _presentationCurrentCoverAssociationFrame = null;
                _presentationOriginalCoverAssociationFrame = null;
                QueuePresentationRefresh();
            }
            else if (e.PropertyName == nameof(SelectedFileThumbnail))
            {
                RaiseCoverPresentationProperties();
            }
        }

        private void QueuePresentationRefresh()
        {
            if (_presentationRefreshQueued) return;
            _presentationRefreshQueued = true;

            var dispatcher = App.MainWindow?.DispatcherQueue;
            if (dispatcher == null)
            {
                _presentationRefreshQueued = false;
                RefreshTimelinePresentation();
                return;
            }

            dispatcher.TryEnqueue(() =>
            {
                _presentationRefreshQueued = false;
                RefreshTimelinePresentation();
            });
        }

        private void RefreshTimelinePresentation()
        {
            foreach (var item in _videoTimelineFrames)
                item.Detach();
            _videoTimelineFrames.Clear();

            var videoFrames = TimelineFrames
                .Where(frame => !frame.IsStillPhoto && !frame.IsOriginalPhoto)
                .OrderBy(frame => frame.Timestamp)
                .ThenBy(frame => frame.FrameIndex)
                .ToList();

            for (var i = 0; i < videoFrames.Count; i++)
                _videoTimelineFrames.Add(new EditTimelinePresentationItem(videoFrames[i], i + 1));

            // Associations are evidence-only. If an exact associated frame no longer exists,
            // discard the association instead of remapping it by Timestamp.
            if (!ContainsVideoFrame(_presentationCurrentCoverAssociationFrame))
                _presentationCurrentCoverAssociationFrame = null;
            if (!ContainsVideoFrame(_presentationOriginalCoverAssociationFrame))
                _presentationOriginalCoverAssociationFrame = null;
            if (!ContainsVideoFrame(_presentationCurrentCoverFrame))
                _presentationCurrentCoverFrame = null;

            _playbackRangeStart = TimeSpan.Zero;
            _playbackRangeEnd = VideoDuration;
            RefreshCoverMarkers();

            OnPropertyChanged(nameof(VideoFrameCount));
            OnPropertyChanged(nameof(SelectedVideoFrameIndex));
            OnPropertyChanged(nameof(SelectedFrameTimestamp));
            OnPropertyChanged(nameof(VideoDuration));
            OnPropertyChanged(nameof(TimelinePositionText));
            RaiseCoverPresentationProperties();
            RaisePlaybackRangeProperties();
        }

        private bool ContainsVideoFrame(TimelineFrame? frame) =>
            frame != null && _videoTimelineFrames.Any(item => ReferenceEquals(item.Frame, frame));

        private void RefreshCoverMarkers()
        {
            foreach (var item in _videoTimelineFrames)
            {
                item.IsCurrentCoverMarker = ReferenceEquals(item.Frame, _presentationCurrentCoverAssociationFrame);
                item.IsOriginalCoverMarker = ReferenceEquals(item.Frame, _presentationOriginalCoverAssociationFrame);
            }
        }

        private TimelineFrame? FindCurrentCoverAsset() =>
            TimelineFrames.FirstOrDefault(frame => frame.IsStillPhoto && !frame.IsOriginalPhoto);

        private TimelineFrame? FindOriginalCoverAsset() =>
            TimelineFrames.FirstOrDefault(frame => frame.IsOriginalPhoto);

        private string BuildAssociationText(TimelineFrame? associationFrame)
        {
            EnsureEditPresentationInitialized();
            if (associationFrame == null)
                return ResourceService.GetString("EditPage_Visual_CoverAssociationUnknown");

            var index = -1;
            for (var i = 0; i < _videoTimelineFrames.Count; i++)
            {
                if (ReferenceEquals(_videoTimelineFrames[i].Frame, associationFrame))
                {
                    index = i;
                    break;
                }
            }

            if (index < 0)
                return ResourceService.GetString("EditPage_Visual_CoverAssociationUnknown");

            return ResourceService.Format(
                "EditPage_Visual_CoverAssociationKnown",
                associationFrame.Timestamp.TotalSeconds.ToString("0.00"),
                index + 1);
        }

        private void RaiseCoverPresentationProperties()
        {
            OnPropertyChanged(nameof(CurrentCoverThumbnail));
            OnPropertyChanged(nameof(OriginalCoverThumbnail));
            OnPropertyChanged(nameof(CurrentCoverAssociationText));
            OnPropertyChanged(nameof(OriginalCoverAssociationText));
        }

        private void RaisePlaybackRangeProperties()
        {
            OnPropertyChanged(nameof(PlaybackRangeStartText));
            OnPropertyChanged(nameof(PlaybackRangeEndText));
            OnPropertyChanged(nameof(PlaybackRangeSummaryText));
        }
    }
}
