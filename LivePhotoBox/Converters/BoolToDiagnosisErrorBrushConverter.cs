using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace LivePhotoBox.Converters
{
    /// <summary>
    /// true（诊断报错）→ 红色，false → 正常文本色
    /// </summary>
    public sealed class BoolToDiagnosisErrorBrushConverter : IValueConverter
    {
        private static readonly SolidColorBrush ErrorBrush = new(ColorHelper.FromArgb(255, 239, 68, 68));

        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is bool isError && isError)
                return ErrorBrush;
            return Application.Current.Resources["TextFillColorPrimaryBrush"];
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}
