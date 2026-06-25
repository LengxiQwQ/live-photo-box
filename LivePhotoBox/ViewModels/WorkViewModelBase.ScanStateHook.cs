namespace LivePhotoBox.ViewModels
{
    // WorkViewModelBase 的分部类：处理 IsScanning 属性变化时的扫描状态钩子。
    // 负责在扫描开始/结束时更新进度条状态、按钮样式，并通知子类。
    public abstract partial class WorkViewModelBase
    {
        // IsScanning 属性变更时：更新 ProgressBarState 和 ScanButtonStyle，并调用子类钩子。
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

        // 扫描状态变更时的子类钩子（子类在此更新按钮文本等）。
        protected virtual void OnScanStateChanged(bool isScanning)
        {
        }
    }
}
