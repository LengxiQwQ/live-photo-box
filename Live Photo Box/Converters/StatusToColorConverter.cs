using LivePhotoBox.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace LivePhotoBox.Converters
{
    // 根据 ProcessStatus 枚举值返回对应的状态颜色画刷。
    // Processing → 橙；Success → 绿；Failed/Cancelled → 红；其他状态跟随系统主题自动适配。
    public sealed class StatusToColorConverter : IValueConverter
    {
        // 将 ProcessStatus 转换为表示处理状态的 SolidColorBrush。
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is ProcessStatus status)
            {
                return status switch
                {
                    ProcessStatus.Processing => new SolidColorBrush(ColorHelper.FromArgb(255, 245, 158, 11)),
                    ProcessStatus.Success => new SolidColorBrush(ColorHelper.FromArgb(255, 16, 185, 129)),
                    ProcessStatus.Failed => new SolidColorBrush(ColorHelper.FromArgb(255, 239, 68, 68)),
                    ProcessStatus.Cancelled => new SolidColorBrush(ColorHelper.FromArgb(255, 239, 68, 68)),
                    _ => GetDefaultColorBrush()
                };
            }

            return GetDefaultColorBrush();
        }

        // 不支持反向转换。
        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }

        // 获取默认状态颜色画刷，根据系统主题（亮色/暗色）自动选择灰色调。
        private static SolidColorBrush GetDefaultColorBrush()
        {
            return Application.Current.RequestedTheme == ApplicationTheme.Light
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 102, 102, 102))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 224, 224, 224));
        }
    }
}
