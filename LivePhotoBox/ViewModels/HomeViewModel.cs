/*
 * HomeViewModel.cs
 *
 * HomePage（首页）的视图模型，负责功能入口的导航跳转。
 *
 *   - 为首页功能提供教程区域导航
 *   - 通过 RequestNavigateToPage 事件请求页面跳转
 */

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Models;
using LivePhotoBox.Services;
using System;
using LogSource = LivePhotoBox.Models.LogSource;

namespace LivePhotoBox.ViewModels
{
    public partial class HomeViewModel : ViewModelBase
    {
        // <inheritdoc/>
        public override string? PageStatusTag => null;

        // 请求导航到指定页面的事件。
        public event EventHandler<string>? RequestNavigateToPage;

        // 导航到首页上指定功能的教程区域。
        [RelayCommand]
        private void GoToTutorial(string feature)
        {
            LogService.Info($"GoToTutorial: {feature}", LogSource.UI);
            RequestNavigateToPage?.Invoke(this, $"Home_{feature}");
        }
    }
}
