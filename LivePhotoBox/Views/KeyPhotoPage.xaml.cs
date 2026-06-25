/*
 * KeyPhotoPage.xaml.cs
 *
 * 关键帧提取页面的代码后置。
 * 提供从实况照片视频中提取关键帧（Key Photo）的功能。
 *
 * 对应 ViewModel：KeyPhotoViewModel
 *
 * 生命周期：
 *   - 构造函数中完成组件初始化
 *   - 所有业务逻辑由 ViewModel 驱动
 */

using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LivePhotoBox.Views
{
    public sealed partial class KeyPhotoPage : Page
    {
        // 关联的 KeyPhotoViewModel
        public KeyPhotoViewModel ViewModel => AppViewModel.Instance.KeyPhoto;

        // 构造函数：初始化组件
        public KeyPhotoPage()
        {
            InitializeComponent();
        }
    }
}
