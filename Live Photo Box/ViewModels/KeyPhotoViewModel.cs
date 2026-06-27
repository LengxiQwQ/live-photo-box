// <copyright file="KeyPhotoViewModel.cs" company="Live Photo Box">
// Copyright (c) Live Photo Box. All rights reserved.
// </copyright>

using CommunityToolkit.Mvvm.ComponentModel;

namespace LivePhotoBox.ViewModels
{
    // 关键帧提取页面的 ViewModel，对应 KeyPhotoPage。
    // 目前功能较为简单，作为占位 ViewModel，后续扩展可在此添加。
    public partial class KeyPhotoViewModel : ViewModelBase
    {
        // <inheritdoc/>
        public override string? PageStatusTag => null;
    }
}
