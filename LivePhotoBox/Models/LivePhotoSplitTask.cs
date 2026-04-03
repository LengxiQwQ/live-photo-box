using CommunityToolkit.Mvvm.ComponentModel;

namespace LivePhotoBox.Models
{
    public partial class LivePhotoSplitTask : ObservableObject
    {
        [ObservableProperty] private int _index;
        [ObservableProperty] private string _sourceFileName = string.Empty;
        [ObservableProperty] private string _sourcePath = string.Empty;
        [ObservableProperty] private string _fileSize = string.Empty;
        [ObservableProperty] private string _progressText = "0%";
        [ObservableProperty] private ProcessStatus _status = ProcessStatus.Pending;
        [ObservableProperty] private string _details = string.Empty;
    }
}
