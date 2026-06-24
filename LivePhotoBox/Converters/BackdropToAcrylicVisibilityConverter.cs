using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace LivePhotoBox.Converters
{
    /// <summary>
    /// BackdropIndex (0=Mica, 1=MicaAlt, 2=Acrylic, 3=None) → Visibility。
    /// 仅当 2 (Acrylic) 时返回 Visible。
    /// </summary>
    public sealed class BackdropToAcrylicVisibilityConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int index && index is 2 or 3) // 2=Acrylic, 3=Acrylic 薄透
                return Visibility.Visible;
            return Visibility.Collapsed;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
