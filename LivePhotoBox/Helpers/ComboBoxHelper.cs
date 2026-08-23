/*
 * ComboBoxHelper.cs
 *
 * 为 WinUI 3 ComboBox 提供自适应宽度功能。
 * 自动测量下拉列表中最宽选项的文本宽度，并将 ComboBox 的 Width 设为该宽度 + 留白余量，
 * 解决 ComboBox 默认只按选中项定宽、不按最宽项定宽的问题。
 */

using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml;
using System;
using System.Collections.Generic;
using System.Collections.Specialized;
using System.Reflection;

namespace LivePhotoBox.Helpers
{
    // 自动将 ComboBox 的宽度设置为所有下拉选项中最宽文本的宽度 + 留白余量。
    // 解决了 WinUI 3 ComboBox 只按选中项定宽、不按最宽项定宽的问题。
    public static class ComboBoxHelper
    {
        private const double ChromeWidth = 57; // padding(11*2) + 边框(2) + 下拉箭头(~32) + 余量(1)

        // 将一组 ComboBox 的宽度对齐到其中最长（最大测量宽度）的那个：
        // 先逐个测量各 ComboBox 最宽选项，取所有测量宽度中的最大值，再统一设置为该宽度，
        // 使同一组下拉框左右平齐，视觉上更整齐。
        public static void AlignWidths(params ComboBox?[] comboBoxes)
        {
            var combos = new List<ComboBox>(comboBoxes.Length);
            double maxWidth = 0;

            foreach (var comboBox in comboBoxes)
            {
                if (comboBox == null) continue;

                combos.Add(comboBox);

                // 先按各自最宽选项测量，得到该下拉内容需要的宽度（含箭头等 chrome）
                AutoFitWidth(comboBox);
                double measured = comboBox.Width;
                if (measured > 0)
                    maxWidth = Math.Max(maxWidth, measured);
            }

            // 把同组所有下拉统一为最宽测量值（若任一下拉还没数据则为 0，跳过）
            foreach (var comboBox in combos)
            {
                if (maxWidth > 0 && comboBox.Width < maxWidth)
                    comboBox.Width = maxWidth;
            }
        }

        // 同步测量 ComboBox 的所有选项文本宽度，并设置固定 Width。
        // 适用于直接 XAML 声明的 ComboBoxItem，或 ItemsSource 已填充的数据绑定 ComboBox。
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

        // 对于异步加载 ItemsSource 的 ComboBox（如硬件列表），
        // 在 Loaded 时若数据尚未就绪，则订阅集合变更事件，
        // 待数据到达后自动测量宽度并设值（仅执行一次）。
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

        // 从 ComboBoxItem 或数据对象中提取显示的文本。
        // 支持直接 ComboBoxItem.Content 和 ItemsSource + DisplayMemberPath 两种模式。
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
