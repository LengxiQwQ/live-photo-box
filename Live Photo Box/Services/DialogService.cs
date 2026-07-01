/*
 * DialogService.cs
 *
 * 全局对话框服务。统一封装 ContentDialog 的创建和显示，
 * 所有弹窗使用 ContentDialog 原生按钮（渲染在底部灰色按钮栏中，
 * 保持与系统一致的视觉效果）。
 *
 * WinUI ContentDialog 按钮默认布局为 Primary(左·强调) Close(右·普通)。
 * 虽然这不是 macOS/iOS 的"主按钮在右"风格，但修改 ContentDialog
 * 模板会引入更多问题（标题对齐、单按钮位置等），且 Windows 用户
 * 已习惯当前布局。本服务选择保持原生行为，避免 Hack。
 *
 *   - ShowSingleAsync    单按钮（CloseButton，右侧）
 *   - ShowDualAsync      双按钮（Primary 左，Close 右）
 *   - ShowCustomAsync    完全自定义
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Threading.Tasks;

namespace LivePhotoBox.Services
{
    /// <summary>
    /// 全局对话框服务。提供 ContentDialog 的便捷封装。
    /// </summary>
    public static class DialogService
    {
        /// <summary>
        /// 将 content 参数统一为 UIElement。
        /// </summary>
        private static UIElement WrapContent(object content)
        {
            if (content is string text)
                return new TextBlock { Text = text, FontSize = 14, TextWrapping = TextWrapping.Wrap };
            return (UIElement)content;
        }

        /// <summary>
        /// 单按钮对话框。按钮在右侧（CloseButton）。
        /// </summary>
        public static async Task ShowSingleAsync(
            XamlRoot xamlRoot,
            string title,
            object content,
            string buttonText,
            ElementTheme? theme = null)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = WrapContent(content),
                CloseButtonText = buttonText,
                DefaultButton = ContentDialogButton.Close,
                XamlRoot = xamlRoot,
                RequestedTheme = theme ?? App.CurrentTheme
            };
            await dialog.ShowAsync();
        }

        /// <summary>
        /// 双按钮对话框：Primary（强调色）在左，Close（普通）在右。
        /// 返回 true 表示用户点击了 Primary 按钮。
        /// </summary>
        public static async Task<bool> ShowDualAsync(
            XamlRoot xamlRoot,
            string? title,
            object content,
            string primaryText,
            string closeText,
            ElementTheme? theme = null)
        {
            var dialog = new ContentDialog
            {
                Title = title,
                Content = WrapContent(content),
                PrimaryButtonText = primaryText,
                CloseButtonText = closeText,
                DefaultButton = ContentDialogButton.Primary,
                XamlRoot = xamlRoot,
                RequestedTheme = theme ?? App.CurrentTheme
            };
            return await dialog.ShowAsync() == ContentDialogResult.Primary;
        }

        /// <summary>
        /// 完全自定义对话框：调用方传入已配置好按钮的 ContentDialog，
        /// 本方法仅设置 Content 并显示。
        /// </summary>
        public static async Task<ContentDialogResult> ShowCustomAsync(
            ContentDialog dialog,
            object content)
        {
            dialog.Content = WrapContent(content);
            return await dialog.ShowAsync();
        }
    }
}
