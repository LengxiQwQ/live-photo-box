using Microsoft.UI.Xaml.Data;
using System;

namespace LivePhotoBox.Converters
{
    // 0.0–1.0 → "50%" 格式化，供 Slider 的 ThumbToolTipValueConverter 使用。
    public sealed class DoubleToPercentConverter : IValueConverter
    {
        // 将 0.0–1.0 范围的 double 值转换为百分比格式（如 "50%"），供 Slider 的 ToolTip 显示使用。
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is double d)
                return $"{d * 100:0}%";
            return value?.ToString() ?? string.Empty;
        }

        // 不支持反向转换。
        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
