using CommunityToolkit.Mvvm.ComponentModel;

namespace LivePhotoBox.ViewModels
{
    /// <summary>
    /// PhotoClassifyPage 目前为预留占位页面。
    /// 后续将通过照片元数据实现自动扫描分类，首批适配 Apple 设备，逐步覆盖安卓厂商。
    /// </summary>
    public partial class PhotoClassifyViewModel : ViewModelBase
    {
        public override string? PageStatusTag => null;
    }
}
