using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System.ComponentModel;

namespace LivePhotoBox.Models
{
    /// <summary>
    /// View adapter for one real video frame in EditPage.
    /// Cover/still assets are intentionally excluded from this type's source collection.
    /// </summary>
    public sealed class EditTimelinePresentationItem : ObservableObject
    {
        private bool _isCurrentCoverMarker;
        private bool _isOriginalCoverMarker;

        public EditTimelinePresentationItem(TimelineFrame frame, int displayNumber)
        {
            Frame = frame;
            DisplayNumber = displayNumber;
            Frame.PropertyChanged += Frame_PropertyChanged;
        }

        public TimelineFrame Frame { get; }
        public int DisplayNumber { get; }
        public ImageSource? Thumbnail => Frame.Thumbnail;
        public Visibility ThumbnailPlaceholderVisibility => Frame.ThumbnailPlaceholderVisibility;
        public bool IsSelected => Frame.IsSelected;

        public bool IsCurrentCoverMarker
        {
            get => _isCurrentCoverMarker;
            set => SetProperty(ref _isCurrentCoverMarker, value);
        }

        public bool IsOriginalCoverMarker
        {
            get => _isOriginalCoverMarker;
            set => SetProperty(ref _isOriginalCoverMarker, value);
        }

        public void Detach() => Frame.PropertyChanged -= Frame_PropertyChanged;

        private void Frame_PropertyChanged(object? sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(TimelineFrame.Thumbnail))
            {
                OnPropertyChanged(nameof(Thumbnail));
                OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
            }
            else if (e.PropertyName == nameof(TimelineFrame.IsSelected))
            {
                OnPropertyChanged(nameof(IsSelected));
            }
        }
    }

    public enum EditTimelineDisplayMode
    {
        Filmstrip,
        Classic
    }
}
