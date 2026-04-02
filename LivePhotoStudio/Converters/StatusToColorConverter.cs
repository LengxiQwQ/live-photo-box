using LivePhotoStudio.Models;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using System;

namespace LivePhotoStudio.Converters
{
    public sealed class StatusToColorConverter : IValueConverter
    {
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            if (value is ProcessStatus status)
            {
                return status switch
                {
                    ProcessStatus.Processing => new SolidColorBrush(ColorHelper.FromArgb(255, 245, 158, 11)),
                    ProcessStatus.Success => new SolidColorBrush(ColorHelper.FromArgb(255, 16, 185, 129)),
                    ProcessStatus.Failed => new SolidColorBrush(ColorHelper.FromArgb(255, 239, 68, 68)),
                    _ => GetDefaultColorBrush()
                };
            }

            return GetDefaultColorBrush();
        }

        public object ConvertBack(object value, Type targetType, object parameter, string language)
        {
            throw new NotImplementedException();
        }

        private static SolidColorBrush GetDefaultColorBrush()
        {
            return Application.Current.RequestedTheme == ApplicationTheme.Light
                ? new SolidColorBrush(ColorHelper.FromArgb(255, 102, 102, 102))
                : new SolidColorBrush(ColorHelper.FromArgb(255, 224, 224, 224));
        }
    }
}
