/*
 * TaskQueueColumnLayout.cs
 *
 * 合成/拆分任务队列的共享列宽模型。
 * 表头和每个虚拟化列表项绑定同一个实例，避免各自按可用宽度重复计算后错位。
 */

using Microsoft.UI.Xaml;
using System;

namespace LivePhotoBox.Controls
{
    /// <summary>
    /// 计算任务队列表头与虚拟化列表项的统一列宽。
    /// 合成页与拆分页共用带内容最小值的比例分配。
    /// </summary>
    public sealed class TaskQueueColumnLayout : DependencyObject
    {
        // 左右内边距 40 + 序号 44 + 缩略图 56 + 五个 8px 间距。
        private const double FixedLayoutWidth = 180;
        private const double FileWeight = 10;
        private const double SizeWeight = 1.8;
        private const double StatusWeight = 2.6;
        private const double ActionsWeight = 1.6;
        private const double FileMinimum = 18;

        private const double MinSizeWidth = 76;
        private const double MinStatusWidth = 92;

        public static readonly DependencyProperty SizeWidthProperty = DependencyProperty.Register(
            nameof(SizeWidth),
            typeof(GridLength),
            typeof(TaskQueueColumnLayout),
            new PropertyMetadata(new GridLength(MinSizeWidth)));

        public static readonly DependencyProperty StatusWidthProperty = DependencyProperty.Register(
            nameof(StatusWidth),
            typeof(GridLength),
            typeof(TaskQueueColumnLayout),
            new PropertyMetadata(new GridLength(MinStatusWidth)));

        public static readonly DependencyProperty FileWidthProperty = DependencyProperty.Register(
            nameof(FileWidth),
            typeof(GridLength),
            typeof(TaskQueueColumnLayout),
            new PropertyMetadata(new GridLength(FileMinimum)));

        public static readonly DependencyProperty ActionsWidthProperty = DependencyProperty.Register(
            nameof(ActionsWidth),
            typeof(GridLength),
            typeof(TaskQueueColumnLayout),
            new PropertyMetadata(new GridLength(72)));

        /// <summary>文件大小列当前宽度。</summary>
        public GridLength SizeWidth
        {
            get => (GridLength)GetValue(SizeWidthProperty);
            private set => SetValue(SizeWidthProperty, value);
        }

        /// <summary>任务状态列当前宽度。</summary>
        public GridLength StatusWidth
        {
            get => (GridLength)GetValue(StatusWidthProperty);
            private set => SetValue(StatusWidthProperty, value);
        }

        /// <summary>文件名列当前宽度。</summary>
        public GridLength FileWidth
        {
            get => (GridLength)GetValue(FileWidthProperty);
            private set => SetValue(FileWidthProperty, value);
        }

        /// <summary>操作列当前宽度。</summary>
        public GridLength ActionsWidth
        {
            get => (GridLength)GetValue(ActionsWidthProperty);
            private set => SetValue(ActionsWidthProperty, value);
        }

        /// <summary>当前内容所需的最小队列表面宽度（不含外层边框）。</summary>
        public double MinimumSurfaceWidth { get; private set; } = 440;

        /// <summary>
        /// 按权重分配文件名、大小、状态和操作列。
        /// 所有列在空间充足时同步按比例伸缩；某列触及内容最小宽度后退出分配，
        /// 剩余列继续按原权重分配，最终只由文件名列吸收继续缩小的压力。
        /// </summary>
        public void UpdateProportional(
            double surfaceWidth,
            double sizeMinimum,
            double statusMinimum,
            double actionsMinimum)
        {
            if (!double.IsFinite(surfaceWidth) || surfaceWidth <= 0)
                return;

            sizeMinimum = NormalizeMinimum(sizeMinimum, MinSizeWidth);
            statusMinimum = NormalizeMinimum(statusMinimum, MinStatusWidth);
            actionsMinimum = NormalizeMinimum(actionsMinimum, 72);

            double[] weights =
            [
                FileWeight,
                SizeWeight,
                StatusWeight,
                ActionsWeight
            ];
            double[] minimums =
            [
                FileMinimum,
                sizeMinimum,
                statusMinimum,
                actionsMinimum
            ];

            double elasticSpace = Math.Max(0, surfaceWidth - FixedLayoutWidth);
            double[] widths = AllocateWithMinimums(elasticSpace, weights, minimums);

            SetWidthIfChanged(FileWidthProperty, FileWidth, widths[0]);
            SetWidthIfChanged(SizeWidthProperty, SizeWidth, widths[1]);
            SetWidthIfChanged(StatusWidthProperty, StatusWidth, widths[2]);
            SetWidthIfChanged(ActionsWidthProperty, ActionsWidth, widths[3]);

            MinimumSurfaceWidth = FixedLayoutWidth
                + FileMinimum
                + sizeMinimum
                + statusMinimum
                + actionsMinimum;
        }

        private static double NormalizeMinimum(double value, double fallback) =>
            double.IsFinite(value) && value > 0 ? value : fallback;

        /// <summary>
        /// 带最小值的加权分配（water filling）：低于最小值的列先锁定，
        /// 余量再交给尚未锁定的列按权重继续分配。
        /// </summary>
        private static double[] AllocateWithMinimums(
            double available,
            double[] weights,
            double[] minimums)
        {
            var result = new double[weights.Length];
            var active = new bool[weights.Length];
            Array.Fill(active, true);

            // 即使宿主宽度暂时低于最小布局，也不压坏非文件名内容；文件名退化到 0。
            double protectedMinimum = 0;
            for (int i = 1; i < minimums.Length; i++)
                protectedMinimum += minimums[i];
            if (available <= protectedMinimum)
            {
                result[0] = Math.Max(0, available - protectedMinimum);
                for (int i = 1; i < result.Length; i++)
                    result[i] = minimums[i];
                return result;
            }

            double remaining = available;
            double remainingWeight = 0;
            foreach (double weight in weights)
                remainingWeight += weight;

            while (remainingWeight > 0)
            {
                int clampIndex = -1;
                for (int i = 0; i < weights.Length; i++)
                {
                    if (!active[i]) continue;
                    double proportional = remaining * weights[i] / remainingWeight;
                    if (proportional < minimums[i])
                    {
                        clampIndex = i;
                        break;
                    }
                }

                if (clampIndex < 0)
                {
                    for (int i = 0; i < weights.Length; i++)
                    {
                        if (active[i])
                            result[i] = remaining * weights[i] / remainingWeight;
                    }
                    break;
                }

                result[clampIndex] = minimums[clampIndex];
                active[clampIndex] = false;
                remaining -= minimums[clampIndex];
                remainingWeight -= weights[clampIndex];
            }

            return result;
        }

        private void SetWidthIfChanged(
            DependencyProperty property,
            GridLength current,
            double value)
        {
            if (Math.Abs(current.Value - value) > 0.1)
                SetValue(property, new GridLength(value));
        }
    }
}
