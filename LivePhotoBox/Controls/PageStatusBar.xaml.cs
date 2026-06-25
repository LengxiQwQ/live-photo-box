/*
 * PageStatusBar.xaml.cs
 *
 * 页面底部状态栏控件。用于显示当前页面的操作状态或提示信息。
 * 数据绑定到 AppViewModel，由各页面通过 SetCurrentStatusPage 更新。
 *
 * 对应 ViewModel：AppViewModel（单例）
 *
 * 生命周期：
 *   - 在 MainWindow 中作为全局控件实例化
 *   - 通过 Visibility 绑定控制显示/隐藏
 */

using LivePhotoBox.ViewModels;

namespace LivePhotoBox.Controls
{
    public sealed partial class PageStatusBar
    {
        // 全局 AppViewModel 单例
        public AppViewModel ViewModel => AppViewModel.Instance;

        // 构造函数：初始化组件
        public PageStatusBar()
        {
            InitializeComponent();
        }
    }
}
