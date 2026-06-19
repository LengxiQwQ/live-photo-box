using CommunityToolkit.Mvvm.ComponentModel;
using LivePhotoBox.Helpers;
using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using System;
using System.IO;
using System.Threading.Tasks;

namespace LivePhotoBox.Models
{
    public partial class SplitTask : ObservableObject
    {
        #region Observable Properties

        [ObservableProperty] private int _index;
        [ObservableProperty] private string _sourceFileName = string.Empty;
        [ObservableProperty] private string _sourcePath = string.Empty;
        [ObservableProperty] private string _fileSize = string.Empty;
        [ObservableProperty] private string _progressText = "0%";
        [ObservableProperty] private ProcessStatus _status = ProcessStatus.Pending;
        [ObservableProperty] private string _details = string.Empty;

        public bool HasErrorDetails => Status == ProcessStatus.Failed && !string.IsNullOrWhiteSpace(Details);

        #endregion

        #region Thumbnail

        private bool _isLoadingThumbnail;
        private ImageSource? _thumbnail;

        public ImageSource? Thumbnail
        {
            get => ThumbnailService.TryGetOrLoad(ref _thumbnail, ref _isLoadingThumbnail, SourcePath, value => Thumbnail = value);
            set
            {
                if (SetProperty(ref _thumbnail, value))
                {
                    OnPropertyChanged(nameof(ThumbnailPlaceholderVisibility));
                }
            }
        }

        public Visibility ThumbnailPlaceholderVisibility => ThumbnailService.GetPlaceholderVisibility(_thumbnail);

        partial void OnSourcePathChanged(string value)
        {
            _isLoadingThumbnail = false;
            Thumbnail = null;
        }

        partial void OnStatusChanged(ProcessStatus value)
        {
            OnPropertyChanged(nameof(DisplayStatus));
            OnPropertyChanged(nameof(HasErrorDetails));
        }

        partial void OnDetailsChanged(string value)
        {
            OnPropertyChanged(nameof(DisplayStatus));
            OnPropertyChanged(nameof(HasErrorDetails));
        }

        #endregion

        #region Computed Properties

        public string DisplaySourceFileName => FileNameFormatter.Truncate(SourceFileName);

        public string DisplayStatus
        {
            get
            {
                if (!string.IsNullOrWhiteSpace(Details))
                    return Details;

                return Status switch
                {
                    ProcessStatus.Pending => ResourceService.GetString("SplitPage_Task_Pending"),
                    ProcessStatus.Processing => ResourceService.GetString("SplitPage_Task_Processing"),
                    ProcessStatus.Success => ResourceService.GetString("SplitPage_Task_Success"),
                    ProcessStatus.Failed => ResourceService.GetString("Task_Failed"),
                    _ => Status.ToString()
                };
            }
        }

        #endregion
    }
}
