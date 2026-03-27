using CommunityToolkit.Mvvm.ComponentModel;

namespace LivePhotoStudio.Models
{
    public enum ProcessStatus
    {
        Pending,
        Processing,
        Success,
        Failed
    }

    public partial class LivePhotoTask : ObservableObject
    {
        private string _fileName = string.Empty;
        public string FileName { get => _fileName; set => SetProperty(ref _fileName, value); }

        private string _imagePath = string.Empty;
        public string ImagePath { get => _imagePath; set => SetProperty(ref _imagePath, value); }

        private string _videoPath = string.Empty;
        public string VideoPath { get => _videoPath; set => SetProperty(ref _videoPath, value); }

        private ProcessStatus _status = ProcessStatus.Pending;
        public ProcessStatus Status { get => _status; set => SetProperty(ref _status, value); }

        private double _progress = 0;
        public double Progress { get => _progress; set => SetProperty(ref _progress, value); }

        private string _details = "等待配对...";
        public string Details { get => _details; set => SetProperty(ref _details, value); }
    }
}