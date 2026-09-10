using LivePhotoBox.Models;
using Microsoft.UI.Xaml.Media;
using System;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;

namespace LivePhotoBox.ViewModels
{
    /// <summary>
    /// Presentation-only state for the master-based EditPage visual refactor.
    /// This adapter intentionally does not mutate media/protocol/native data.
    /// </summary>
    public partial class EditViewModel
    {
        private readonly ObservableCollection<EditTimelinePresentationItem> _videoTimelineFrames = new();
        private bool _editPresentationInitialized;
        private bool _presentationRefreshQueued;
        private TimelineFrame? _presentationCurrentCoverFrame;
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
                return $"第 {displayIndex} / {VideoFrameCount} 帧 · {SelectedFrameTimestamp.TotalSeconds:0.00} s / {VideoDuration.TotalSeconds:0.00} s";
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
            BuildAssociationText(_presentationCurrentCoverFrame ?? FindCurrentCoverAsset());

        public string OriginalCoverAssociationText =>
            BuildAssociationText(FindOriginalCoverAsset());

        public string PlaybackRangeStartText => $"{_playbackRangeStart.TotalSeconds:0.00} s";
        public string PlaybackRangeEndText => $"{_playbackRangeEnd.TotalSeconds:0.00} s";
        public string PlaybackRangeSummaryText =>
            $"{Math.Max(0, (_playbackRangeEnd - _playbackRangeStart).TotalSeconds):0.00} s · {VideoFrameCount} 帧";

        /// <summary>
        /// True only while the user has made a presentation-only cover choice that has not
        /// been handed to any media writer.
        /// </summary>
        public bool IsReencodeWarningVisible => _presentationCurrentCoverFrame != null;

        public void SetTimelineDisplayMode(EditTimelineDisplayMode mode) => TimelineDisplayMode = mode;

        /// <summary>
        /// UI/session-only cover choice. No writer, encoder, protocol or media-core call occurs here.
        /// </summary>
        public void SetCurrentCoverPresentation(TimelineFrame? frame)
        {
            EnsureEditPresentationInitialized();
            if (frame == null || frame.IsStillPhoto || frame.IsOriginalPhoto) return;

            _presentationCurrentCoverFrame = frame;
            RefreshCoverMarkers();
            RaiseCoverPresentationProperties();
            OnPropertyChanged(nameof(IsReencodeWarningVisible));
        }

        /// <summary>Presentation-only reset. No media trimming is performed.</summary>
        public void ResetPlaybackRangePresentation()
        {
            EnsureEditPresentationInitialized();
            _playbackRangeStart = TimeSpan.Zero;
            _playbackRangeEnd = VideoDuration;
            RaisePlaybackRangeProperties();
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
                QueuePresentationRefresh();
                OnPropertyChanged(nameof(IsReencodeWarningVisible));
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

        private void RefreshCoverMarkers()
        {
            foreach (var item in _videoTimelineFrames)
            {
                item.IsCurrentCoverMarker = false;
                item.IsOriginalCoverMarker = false;
            }

            MarkClosestFrame(_presentationCurrentCoverFrame ?? FindCurrentCoverAsset(), isCurrent: true);
            MarkClosestFrame(FindOriginalCoverAsset(), isCurrent: false);
        }

        private void MarkClosestFrame(TimelineFrame? coverFrame, bool isCurrent)
        {
            if (coverFrame == null || _videoTimelineFrames.Count == 0) return;

            var closest = _videoTimelineFrames
                .OrderBy(item => Math.Abs((item.Frame.Timestamp - coverFrame.Timestamp).Ticks))
                .FirstOrDefault();
            if (closest == null) return;

            if (isCurrent)
                closest.IsCurrentCoverMarker = true;
            else
                closest.IsOriginalCoverMarker = true;
        }

        private TimelineFrame? FindCurrentCoverAsset() =>
            TimelineFrames.FirstOrDefault(frame => frame.IsStillPhoto && !frame.IsOriginalPhoto);

        private TimelineFrame? FindOriginalCoverAsset() =>
            TimelineFrames.FirstOrDefault(frame => frame.IsOriginalPhoto);

        private string BuildAssociationText(TimelineFrame? coverFrame)
        {
            EnsureEditPresentationInitialized();
            if (coverFrame == null || _videoTimelineFrames.Count == 0)
                return "关联位置：未记录";

            var closest = _videoTimelineFrames
                .Select((item, index) => new
                {
                    item,
                    index,
                    delta = Math.Abs((item.Frame.Timestamp - coverFrame.Timestamp).Ticks)
                })
                .OrderBy(x => x.delta)
                .First();

            return $"关联位置：{coverFrame.Timestamp.TotalSeconds:0.00} s · 约第 {closest.index + 1} 帧";
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
