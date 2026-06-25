using System;
using LivePhotoBox.Models;
using Microsoft.UI.Xaml.Data;

namespace LivePhotoBox.Converters
{
    // 根据 ProgressBarState 判断进度条是否应显示为不确定模式（Indeterminate）。
    // 仅 Scanning 状态返回 true，触发无限循环动画。
    public class ProgressBarIndeterminateConverter : IValueConverter
    {
        // 判断当前状态是否为 Scanning，若是则返回 true 以启用 Indeterminate 进度动画。
        public object Convert(object value, Type targetType, object parameter, string language)
        {
            return value is ProgressBarState state && state == ProgressBarState.Scanning;
        }

        // 不支持反向转换。
        public object ConvertBack(object value, Type targetType, object parameter, string language)
            => throw new NotImplementedException();
    }
}
