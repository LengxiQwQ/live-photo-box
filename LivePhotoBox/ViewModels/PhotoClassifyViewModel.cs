/*
 * PhotoClassifyViewModel.cs
 *
 * PhotoClassifyPage（照片分类页面）的视图模型，当前为预留占位页。
 *
 *   - 规划通过照片元数据实现自动扫描分类
 *   - 首批适配 Apple 设备，逐步覆盖安卓厂商
 */

using CommunityToolkit.Mvvm.ComponentModel;

namespace LivePhotoBox.ViewModels
{
    public partial class PhotoClassifyViewModel : ViewModelBase
    {
        // 该页面不在导航栏显示状态标签，返回 null。
        public override string? PageStatusTag => null;
    }
}
