using System;
using LivePhotoBox.Models;
using Microsoft.UI;
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

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
