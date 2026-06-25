using System;
using LivePhotoBox.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace LivePhotoBox.Converters
{
    // 根据 ProgressBarState 枚举值返回对应的前景色画刷，用于指示进度条当前状态。
    // Scanning/Idle → 灰；Processing/Success → 绿；Pausing/Paused → 黄；Cancelled → 红。
    public class ProgressBarForegroundConverter : IValueConverter
    {
        // 将 ProgressBarState 状态值转换为对应的 SolidColorBrush 前景色。
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is ProgressBarState state)
            {
                return state switch
                {
                    ProgressBarState.Scanning => new SolidColorBrush(Colors.DarkGray),
                    ProgressBarState.Idle => new SolidColorBrush(Colors.DarkGray),
                    ProgressBarState.Processing => new SolidColorBrush(ColorHelper.FromArgb(255, 16, 185, 129)),
                    ProgressBarState.Pausing => new SolidColorBrush(ColorHelper.FromArgb(255, 245, 158, 11)),
                    ProgressBarState.Paused => new SolidColorBrush(ColorHelper.FromArgb(255, 245, 158, 11)),
                    ProgressBarState.Cancelled => new SolidColorBrush(ColorHelper.FromArgb(255, 239, 68, 68)),
                    ProgressBarState.Success => new SolidColorBrush(ColorHelper.FromArgb(255, 16, 185, 129)),
                    _ => new SolidColorBrush(ColorHelper.FromArgb(255, 16, 185, 129))
                };
            }
            return new SolidColorBrush(Colors.DarkGray);
        }

        // 不支持反向转换。
        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
