namespace LivePhotoBox.ViewModels
{
    public abstract partial class WorkViewModelBase
    {
        partial void OnIsScanningChanged(bool value)
        {
            OnPropertyChanged(nameof(ScanButtonStyle));
            ProgressBarState = value ? Models.ProgressBarState.Scanning : Models.ProgressBarState.Idle;
            OnScanStateChanged(value);
        }

        protected virtual void OnScanStateChanged(bool isScanning)
        {
        }
    }
}
