/*
 * HistoryPage.xaml.cs
 *
 * 操作历史页面的代码后置。
 * 显示用户对实况照片的历史操作记录，支持选择文件夹查看历史。
 *
 * 对应 ViewModel：HistoryViewModel
 *
 * 生命周期：
 *   - 构造函数中完成组件初始化
 *   - SelectFolder_Click 事件触发文件夹选择，更新 ViewModel
 */

using LivePhotoBox.Models;
using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml.Controls;
using System;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using LogSource = LivePhotoBox.Models.LogSource;

namespace LivePhotoBox.Views
{
    public sealed partial class HistoryPage : Page, INotifyPropertyChanged
    {
        public event PropertyChangedEventHandler? PropertyChanged;

        private void NotifyPropertyChanged([CallerMemberName] string propertyName = "")
        {
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
        }

        // 关联的 HistoryViewModel
        public HistoryViewModel ViewModel => AppViewModel.Instance.History;

        // 构造函数：初始化组件
        public HistoryPage()
        {
            InitializeComponent();
        }

        // 选择文件夹按钮点击：弹出文件夹选择器并将路径写入 ViewModel
        private async void SelectFolder_Click(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder == null)
            {
                LogService.Info("SelectFolder: user cancelled", LogSource.UI);
                return;
            }

            LogService.Info($"SelectFolder: {folder.Path}", LogSource.UI);
            ViewModel.SelectedFolder = folder.Path;
            FolderPathText.Text = folder.Path;
        }
    }
}
