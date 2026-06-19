using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace LivePhotoBox.Helpers
{
    public static class VisualTreeHelperExtensions
    {
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
