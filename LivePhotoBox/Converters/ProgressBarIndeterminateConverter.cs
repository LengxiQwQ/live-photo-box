using System;
using LivePhotoBox.Models;
using Microsoft.UI.Xaml.Data;

namespace LivePhotoBox.Converters
{
    public class ProgressBarIndeterminateConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is ProgressBarState state && state == ProgressBarState.Scanning;
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
