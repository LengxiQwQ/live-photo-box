// <copyright file="HomeViewModel.cs" company="Live Photo Box">
// Copyright (c) Live Photo Box. All rights reserved.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Services;
using System;

namespace LivePhotoBox.ViewModels
{
    // 首页的 ViewModel，对应 HomePage。
    // 负责功能入口的导航跳转（使用教程、各功能引导等）。
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
            RequestNavigateToPage?.Invoke(this, $"Home_{feature}");
        }
    }
}
