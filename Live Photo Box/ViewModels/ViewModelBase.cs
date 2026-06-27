using CommunityToolkit.Mvvm.ComponentModel;

namespace LivePhotoBox.ViewModels
{
    // 所有 ViewModel 的抽象基类。
    // 继承自 CommunityToolkit.Mvvm 的 ObservableObject，
    // 提供 PageStatusTag（导航栏状态标签）和 Status（状态文本）的基础定义。
    public abstract partial class ViewModelBase : ObservableObject
    {
        // 页面导航栏状态标签。返回 null 表示不在导航栏显示状态。
        public virtual string? PageStatusTag => null;

        // 当前状态文本（由子类维护，默认返回空字符串）。
        public virtual string Status => string.Empty;
    }
}
