using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace LivePhotoBox.Converters
{
    // true（诊断报错）→ 红色，false → 正常文本色
    public sealed class BoolToDiagnosisErrorBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush ErrorBrush = new(ColorHelper.FromArgb(255, 239, 68, 68));

        // 将布尔值转换为诊断报错的前景色。true 返回红色错误画刷，false 返回正常文本色。
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isError && isError)
                return ErrorBrush;
            return Application.Current.Resources["TextFillColorPrimaryBrush"];
        }

        // 不支持反向转换。
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
