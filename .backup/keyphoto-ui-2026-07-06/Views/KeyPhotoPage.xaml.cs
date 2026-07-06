/*
 * KeyPhotoPage.xaml.cs
 *
 * 实况照片主图更换页面的代码后置。
 * 处理 UI 事件（文件夹浏览选择器等），
 * 所有业务逻辑由 KeyPhotoViewModel 驱动。
 *
 * 对应 ViewModel：KeyPhotoViewModel
 *
 * 生命周期：
 *   - 构造函数中完成组件初始化
 *   - FolderPicker 等交互逻辑在此处理
 *   - ListView 的 ContainerContentChanging 用于按需加载缩略图（后续实现）
 */

using LivePhotoBox.Models;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml.Controls;
using System;
using Windows.Storage;
using Windows.Storage.Pickers;

namespace LivePhotoBox.Views
{
    public sealed partial class KeyPhotoPage : Page
    {
        // 关联的 KeyPhotoViewModel，通过 AppViewModel 单例获取。
        public KeyPhotoViewModel ViewModel => AppViewModel.Instance.KeyPhoto;

        // 构造函数：初始化组件。
        public KeyPhotoPage()
        {
            InitializeComponent();
        }

        /// <summary>
        /// 浏览选择输入目录。
        /// 使用 Windows FolderPicker 让用户选择包含实况照片的文件夹。
        /// </summary>
        private async void BrowseInput_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add("*");

            // 获取窗口句柄以关联 FolderPicker
            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                ViewModel.InputDirectory = folder.Path;
            }
        }

        /// <summary>
        /// 浏览选择输出目录。
        /// 使用 Windows FolderPicker 让用户选择转换结果保存的目标文件夹。
        /// </summary>
        private async void BrowseOutput_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var picker = new FolderPicker
            {
                SuggestedStartLocation = PickerLocationId.PicturesLibrary
            };
            picker.FileTypeFilter.Add("*");

            var hwnd = WinRT.Interop.WindowNative.GetWindowHandle(App.MainWindow);
            WinRT.Interop.InitializeWithWindow.Initialize(picker, hwnd);

            StorageFolder? folder = await picker.PickSingleFolderAsync();
            if (folder != null)
            {
                ViewModel.OutputDirectory = folder.Path;
            }
        }

        /// <summary>
        /// ListView 容器内容变化事件。
        /// 用于按需加载缩略图等耗时操作，提升大列表滚动性能。
        /// 当前为预留桩，后续实现缩略图异步加载。
        /// </summary>
        private void KeyPhotoTaskListView_ContainerContentChanging(
            ListViewBase sender, ContainerContentChangingEventArgs args)
        {
            // 后续实现：根据 args.Item 加载缩略图
            // 参考 MergePage.MergeTaskListView_ContainerContentChanging
            args.Handled = true;
        }
    }
}
