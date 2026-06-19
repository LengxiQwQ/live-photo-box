using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Specialized;
using System.Reflection;

namespace LivePhotoBox.Helpers
{
    /// <summary>
    /// 自动将 ComboBox 的宽度设置为所有下拉选项中最宽文本的宽度 + 留白余量。
    /// 解决了 WinUI 3 ComboBox 只按选中项定宽、不按最宽项定宽的问题。
    /// </summary>
    public static class ComboBoxHelper
    {
        private const double ChromeWidth = 64; // 默认 padding(11*2) + 边框(2) + 下拉箭头(~32) + 安全余量

        /// <summary>
        /// 同步测量 ComboBox 的所有选项文本宽度，并设置固定 Width。
        /// 适用于直接 XAML 声明的 ComboBoxItem，或 ItemsSource 已填充的数据绑定 ComboBox。
        /// </summary>
        public static void AutoFitWidth(ComboBox comboBox)
        {
            if (comboBox == null || comboBox.Items.Count == 0) return;

            double fontSize = comboBox.FontSize > 0 && !double.IsNaN(comboBox.FontSize)
                ? comboBox.FontSize
                : 14.0;

            double maxTextWidth = 0;

            foreach (var item in comboBox.Items)
            {
                string? text = GetItemDisplayText(comboBox, item);
                if (string.IsNullOrEmpty(text)) continue;

                var measureBlock = new TextBlock
                {
                    Text = text,
                    FontSize = fontSize,
                    TextWrapping = TextWrapping.NoWrap
                };

                measureBlock.Measure(new Windows.Foundation.Size(
                    double.PositiveInfinity, double.PositiveInfinity));

                maxTextWidth = Math.Max(maxTextWidth, measureBlock.DesiredSize.Width);
            }

            if (maxTextWidth > 0)
            {
                comboBox.Width = maxTextWidth + ChromeWidth;
            }
        }

        /// <summary>
        /// 对于异步加载 ItemsSource 的 ComboBox（如硬件列表），
        /// 在 Loaded 时若数据尚未就绪，则订阅集合变更事件，
        /// 待数据到达后自动测量宽度并设值（仅执行一次）。
        /// </summary>
        public static void AutoFitWidthAsync(ComboBox comboBox, INotifyCollectionChanged? sourceCollection)
        {
            if (comboBox == null) return;

            // 如果数据已就绪，直接测量
            if (comboBox.Items.Count > 0)
            {
                AutoFitWidth(comboBox);
                return;
            }

            // 数据未就绪，订阅变更事件
            if (sourceCollection == null) return;

            NotifyCollectionChangedEventHandler? handler = null;
            handler = (_, _) =>
            {
                // 等待数据到达后测量一次，立即取消订阅
                if (comboBox.Items.Count > 0)
                {
                    AutoFitWidth(comboBox);
                    sourceCollection.CollectionChanged -= handler;
                }
            };

            sourceCollection.CollectionChanged += handler;
        }

        /// <summary>
        /// 从 ComboBoxItem 或数据对象中提取显示的文本。
        /// 支持直接 ComboBoxItem.Content 和 ItemsSource + DisplayMemberPath 两种模式。
        /// </summary>
        private static string? GetItemDisplayText(ComboBox comboBox, object item)
        {
            if (item is ComboBoxItem cbi)
            {
                return cbi.Content as string;
            }

            // 数据绑定模式：通过 DisplayMemberPath 反射获取显示的属性值
            if (!string.IsNullOrEmpty(comboBox.DisplayMemberPath))
            {
                var prop = item.GetType().GetRuntimeProperty(comboBox.DisplayMemberPath);
                return prop?.GetValue(item)?.ToString();
            }

            return item.ToString();
        }
    }
}
