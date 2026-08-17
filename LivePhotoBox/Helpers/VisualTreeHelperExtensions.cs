/*
 * VisualTreeHelperExtensions.cs
 *
 * VisualTreeHelper 扩展方法集合。提供深度优先的可视化树后代查找功能，
 * 按类型递归查找指定类型的子控件（如查找 ListView 内部的 ScrollViewer）。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LivePhotoBox.Helpers
{
    // VisualTreeHelper 扩展方法集合。
    // 提供深度优先的可视化树后代查找功能，用于在 XAML 可视化树中
    // 按类型递归查找指定类型的子控件（如查找 ListView 内部的 ScrollViewer）。
    public static class VisualTreeHelperExtensions
    {
        // 在可视化树中深度优先查找指定类型的后代元素。
        // T: 要查找的后代元素类型，须为 DependencyObject。
        // root: 搜索的根元素。
        // 返回: 第一个匹配的 T 类型后代元素；若未找到则返回 null。
        public static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject
        {
            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
            {
                DependencyObject child = VisualTreeHelper.GetChild(root, i);
                if (child is T match) return match;
                T? nested = FindDescendant<T>(child);
                if (nested is not null) return nested;
            }
            return null;
        }
    }
}
