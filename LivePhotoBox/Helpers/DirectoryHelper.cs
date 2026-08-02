// <summary>
// File: DirectoryHelper.cs
// 提供统一的目录路径验证，用于"打开文件夹"按钮的 CanExecute 判断，
// 避免各页面重复编写 !IsNullOrWhiteSpace + Directory.Exists 模式。
// </summary>

using System.IO;

namespace LivePhotoBox.Helpers
{
    // 目录路径验证工具类。
    public static class DirectoryHelper
    {
        // 检查路径是否非空且目录存在（供 RelayCommand CanExecute 使用）。
        // path: 要检查的目录路径。
        // è¿å: 路径非空且目录存在时返回 true。
        public static bool CanOpenFolder(string? path) =>
            !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
    }
}
