using Microsoft.UI.Xaml.Data;
using System;

namespace LivePhotoBox.Converters
{
    /// <summary>
    /// 0.0–1.0 → "50%" 格式化，供 Slider 的 ThumbToolTipValueConverter 使用。
    /// </summary>
    public sealed class DoubleToPercentConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double d)
                return $"{d * 100:0}%";
            return value?.ToString() ?? string.Empty;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
