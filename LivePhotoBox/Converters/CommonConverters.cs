using Microsoft.UI.Xaml;
using System;

namespace LivePhotoBox.Converters
{
    /// <summary>
    /// bool → Visibility: true = Visible, false = Collapsed
    /// </summary>
    public sealed class VisibilityConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b) return b ? Visibility.Visible : Visibility.Collapsed;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is Visibility v) return v == Visibility.Visible;
            return false;
        }
    }

    /// <summary>
    /// bool → Visibility: true = Collapsed, false = Visible
    /// </summary>
    public sealed class InverseVisibilityConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b) return b ? Visibility.Collapsed : Visibility.Visible;
            return Visibility.Visible;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is Visibility v) return v != Visibility.Visible;
            return false;
        }
    }

    /// <summary>
    /// string → Visibility: non-null/non-empty = Visible, null/empty = Collapsed
    /// </summary>
    public sealed class StringNotEmptyConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is string s) return string.IsNullOrEmpty(s) ? Visibility.Collapsed : Visibility.Visible;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    /// <summary>
    /// bool → bool (inversion)
    /// </summary>
    public sealed class InverseBoolConverter : Microsoft.UI.Xaml.Data.IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b) return !b;
            return false;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            if (value is bool b) return !b;
            return false;
        }
    }
}
