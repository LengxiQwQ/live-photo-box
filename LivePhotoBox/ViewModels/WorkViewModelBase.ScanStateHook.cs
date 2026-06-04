namespace LivePhotoBox.ViewModels
{
    public abstract partial class WorkViewModelBase
    {
        partial void OnIsScanningChanged(bool value)
        {
            OnPropertyChanged(nameof(ScanButtonStyle));
            if (value)
            {
                ProgressBarState = Models.ProgressBarState.Scanning;
            }
            else
            {
                // 如果是用户取消的扫描，保持 Cancelled 状态（红色），不覆盖
                if (!_scanCancelledByUser)
                {
                    ProgressBarState = Models.ProgressBarState.Idle;
                }
                else
                {
                    // 扫描取消：状态文字已在 catch 块中更新（"取消扫描"），现在应用红色
                    ApplyCancellationState();
                    _scanCancelledByUser = false;
                }
            }
            OnScanStateChanged(value);
        }

        protected virtual void OnScanStateChanged(bool isScanning)
        {
        }
    }
}
