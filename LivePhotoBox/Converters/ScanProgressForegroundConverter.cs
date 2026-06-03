using System;
using LivePhotoBox.Models;
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
                    ProgressBarState.Scanning   => new SolidColorBrush(Microsoft.UI.Colors.Black),
                    ProgressBarState.Processing => (SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources["CyberCyanBrush"],
                    ProgressBarState.Paused     => new SolidColorBrush(Microsoft.UI.Colors.Goldenrod),
                    ProgressBarState.Cancelled  => new SolidColorBrush(Microsoft.UI.Colors.Crimson),
                    _                          => (SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources["CyberCyanBrush"]
                };
            }
            return (SolidColorBrush)Microsoft.UI.Xaml.Application.Current.Resources["CyberCyanBrush"];
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
