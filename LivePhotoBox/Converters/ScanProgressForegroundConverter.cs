using System;
using LivePhotoBox.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;

namespace LivePhotoBox.Converters
{
    public class ProgressBarForegroundConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is ProgressBarState state)
            {
                return state switch
                {
                    // 扫描阶段及默认状态（偏灰色/黑色）
                    ProgressBarState.Scanning => new SolidColorBrush(Colors.DarkGray),
                    ProgressBarState.Idle => new SolidColorBrush(Colors.DarkGray),

                    // 处理阶段（绿色）
                    ProgressBarState.Processing => new SolidColorBrush(ColorHelper.FromArgb(255, 16, 185, 129)),

                    // 暂停中 / 已暂停（黄色）
                    ProgressBarState.Pausing => new SolidColorBrush(ColorHelper.FromArgb(255, 245, 158, 11)),
                    ProgressBarState.Paused => new SolidColorBrush(ColorHelper.FromArgb(255, 245, 158, 11)),

                    // 取消/停止阶段（红色）
                    ProgressBarState.Cancelled => new SolidColorBrush(ColorHelper.FromArgb(255, 239, 68, 68)),

                    // 完成阶段（绿色）
                    ProgressBarState.Success => new SolidColorBrush(ColorHelper.FromArgb(255, 16, 185, 129)),

                    _ => new SolidColorBrush(ColorHelper.FromArgb(255, 16, 185, 129))
                };
            }
            return new SolidColorBrush(Colors.DarkGray);
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }

    public class ProgressBarIndeterminateConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is ProgressBarState state && state == ProgressBarState.Scanning;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }
    }
}