using CommunityToolkit.Mvvm.ComponentModel;

namespace LivePhotoBox.ViewModels
{
    // PhotoClassifyPage 的 ViewModel（预留占位页）。
    // 当前页面为占位状态，后续规划通过照片元数据实现自动扫描分类，
    // 首批适配 Apple 设备，逐步覆盖安卓厂商。
    public partial class PhotoClassifyViewModel : ViewModelBase
    {
        // 该页面不在导航栏显示状态标签，返回 null。
        public override string? PageStatusTag => null;
    }
}
