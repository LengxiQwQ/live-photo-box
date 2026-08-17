using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using System;

namespace LivePhotoBox.Converters
{
    // BackdropIndex (0=Mica, 1=MicaAlt, 2=Acrylic, 3=None) → Visibility。
    // 索引 2 (Acrylic) 与 3 (Acrylic 薄透) 返回 Visible，其余返回 Collapsed。
    public sealed class BackdropToAcrylicVisibilityConverter : IValueConverter
    {
        // 将背景索引转换为 Acrylic 模式的可见性。
        // 索引 2 (Acrylic) 和 3 (Acrylic 薄透) 返回 Visible，其余返回 Collapsed。
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is int index && index is 2 or 3) // 2=Acrylic, 3=Acrylic 薄透
                return Visibility.Visible;
            return Visibility.Collapsed;
        }

        // 不支持反向转换。
        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
