/*
 * SnapPanel.cs
 *
 * 轻量 IScrollSnapPointsInfo 实现，为 ItemsRepeater 时间轴提供等距吸附点。
 *
 * 用法：
 *   <ScrollViewer HorizontalSnapPointsType="MandatorySingle"
 *                 HorizontalSnapPointsAlignment="Center">
 *       <controls:SnapPanel RegularSnapStep="60" SnapOffset="28">
 *           <ItemsRepeater ... />
 *       </controls:SnapPanel>
 *   </ScrollViewer>
 *
 * ScrollViewer 检测到直接子元素实现了 IScrollSnapPointsInfo 后，
 * 会自动使用此处提供的吸附点进行强制吸附。
 */

using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using System;
using System.Collections.Generic;

namespace LivePhotoBox.Controls
{
    public sealed class SnapPanel : Grid, IScrollSnapPointsInfo
    {
        // ══════════════════════════════════════════════════════════════
        //  Dependency Properties
        // ══════════════════════════════════════════════════════════════

        /// <summary>等距吸附步长（像素）。默认 60（56px 卡片 + 4px 间距）。</summary>
        public static readonly DependencyProperty RegularSnapStepProperty =
            DependencyProperty.Register(
                nameof(RegularSnapStep),
                typeof(double),
                typeof(SnapPanel),
                new PropertyMetadata(60.0, OnSnapPropertyChanged));

        public double RegularSnapStep
        {
            get => (double)GetValue(RegularSnapStepProperty);
            set => SetValue(RegularSnapStepProperty, value);
        }

        /// <summary>首个吸附点偏移量（像素）。默认 28（半个卡片宽 56/2）。</summary>
        public static readonly DependencyProperty SnapOffsetProperty =
            DependencyProperty.Register(
                nameof(SnapOffset),
                typeof(double),
                typeof(SnapPanel),
                new PropertyMetadata(28.0, OnSnapPropertyChanged));

        public double SnapOffset
        {
            get => (double)GetValue(SnapOffsetProperty);
            set => SetValue(SnapOffsetProperty, value);
        }

        private static void OnSnapPropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is SnapPanel panel)
                panel.HorizontalSnapPointsChanged?.Invoke(panel, EventArgs.Empty);
        }

        // ══════════════════════════════════════════════════════════════
        //  IScrollSnapPointsInfo
        // ══════════════════════════════════════════════════════════════

        public bool AreHorizontalSnapPointsRegular => true;
        public bool AreVerticalSnapPointsRegular => false;

        public event EventHandler<object>? HorizontalSnapPointsChanged;
#pragma warning disable CS0067
        public event EventHandler<object>? VerticalSnapPointsChanged;
#pragma warning restore CS0067

        public IReadOnlyList<float> GetIrregularSnapPoints(
            Orientation orientation, SnapPointsAlignment alignment)
        {
            return Array.Empty<float>();
        }

        public float GetRegularSnapPoints(
            Orientation orientation, SnapPointsAlignment alignment, out float offset)
        {
            if (orientation == Orientation.Horizontal)
            {
                offset = (float)SnapOffset;
                return (float)RegularSnapStep;
            }
            offset = 0;
            return 0;
        }
    }
}
