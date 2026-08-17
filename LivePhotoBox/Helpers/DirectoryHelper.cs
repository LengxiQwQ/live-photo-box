/*
 * DirectoryHelper.cs
 *
 * 目录路径验证工具类。提供统一的目录存在性检查（CanOpenFolder），
 * 供"打开文件夹"按钮的 CanExecute 判断复用，
 * 避免各页面重复编写 !IsNullOrWhiteSpace + Directory.Exists 模式。
 */

using System.IO;

namespace LivePhotoBox.Helpers
{
    // 目录路径验证工具类。
    public static class DirectoryHelper
    {
        // 检查路径是否非空且目录存在，供 RelayCommand CanExecute 判断目录是否可打开。
        public static bool CanOpenFolder(string? path) =>
            !string.IsNullOrWhiteSpace(path) && Directory.Exists(path);
    }
}
