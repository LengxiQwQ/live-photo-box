using Microsoft.UI.Xaml;
using System;

namespace LivePhotoBox.Converters
{
    // bool → Visibility: true = Visible, false = Collapsed
    public sealed class VisibilityConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        // bool → Visibility: true = Visible, false = Collapsed。
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b) return b ? Visibility.Visible : Visibility.Collapsed;
            return Visibility.Collapsed;
        }

        // Visibility → bool: Visible = true, Collapsed = false。
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is Visibility v) return v == Visibility.Visible;
            return false;
        }
    }

    // bool → Visibility: true = Collapsed, false = Visible
    public sealed class InverseVisibilityConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        // bool → Visibility: true = Collapsed, false = Visible（与 VisibilityConverter 相反）。
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b) return b ? Visibility.Collapsed : Visibility.Visible;
            return Visibility.Visible;
        }

        // Visibility → bool: Collapsed = true, Visible = false。
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is Visibility v) return v != Visibility.Visible;
            return false;
        }
    }

    // string → Visibility: non-null/non-empty = Visible, null/empty = Collapsed
    public sealed class StringNotEmptyConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        // 判断字符串是否非空。非 null 且非空字符串返回 Visible，否则返回 Collapsed。
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string s) return string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
            return Visibility.Collapsed;
        }

        // 不支持反向转换。
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    // bool → bool (inversion)
    public sealed class InverseBoolConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        // 对布尔值取反。true → false, false → true。
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b) return !b;
            return false;
        }

        // 反向转换，同样对布尔值取反。
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b) return !b;
            return false;
        }
    }
}
