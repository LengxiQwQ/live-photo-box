/*
 * PhotoClassifyPage.xaml.cs
 *
 * 照片分类页面的代码后置。
 * 提供对实况照片进行分类/归档管理的功能。
 *
 * 对应 ViewModel：PhotoClassifyViewModel
 *
 * 生命周期：
 *   - 构造函数中完成组件初始化
 *   - 所有业务逻辑由 ViewModel 驱动
 */

using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LivePhotoBox.Views
{
    public sealed partial class PhotoClassifyPage : Page
    {
        // 关联的 PhotoClassifyViewModel
        public PhotoClassifyViewModel ViewModel => AppViewModel.Instance.PhotoClassify;

        // 构造函数：初始化组件
        public PhotoClassifyPage()
        {
            InitializeComponent();
        }
    }
}
