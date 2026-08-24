/*
 * AdaptiveFileName.cs
 *
 * 宽度自适应的文件名省略（TextBlock 附加属性）：
 *  - 可用宽度足够时显示完整文件名；
 *  - 宽度不足时中间省略：保留扩展名 + 扩展名前几个字符，
 *    无扩展名（如双文件实况的 "(JPG+MOV)" 组合显示）时保留末尾几个字符；
 *  - 不依赖 TextTrimming（那是末尾省略号），由附加属性按实际渲染宽度二分计算。
 *
 * 用法：
 *   <TextBlock controls:AdaptiveFileName.FileName="{x:Bind FullDisplayFileName}"
 *              FontSize="12" FontWeight="Bold"/>
 */

using System;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace LivePhotoBox.Controls
{
    /// <summary>
    /// 宽度自适应、中间省略号的文件名附加属性。
    /// 宽度够 → 完整显示；不够 → "前缀…后缀"（保留扩展名与扩展名前几个字符）。
    /// </summary>
    public static class AdaptiveFileName
    {
        /// <summary>扩展名前保留的字符数（与 FileNameFormatter.Truncate 保持一致）</summary>
        private const int KeepTailChars = 4;

        /// <summary>无扩展名时保留的末尾字符数（覆盖 "(JPG+MOV)" 等组合后缀）</summary>
        private const int FallbackTailChars = 9;

        /// <summary>宽度变化小于该值时忽略，避免窗口拖拽时频繁重算</summary>
        private const double WidthChangeThreshold = 1.0;

        /// <summary>每个 TextBlock 的内部状态（记录上次应用的宽度）</summary>
        private sealed class TrimState
        {
            public double LastWidth = double.NaN;

            /// <summary>脱离视觉树的测量控件，避免手动 Measure 改变真实 TextBlock 的布局状态。</summary>
            public TextBlock Probe { get; } = new()
            {
                TextTrimming = TextTrimming.None,
                TextWrapping = TextWrapping.NoWrap,
                MaxLines = 1
            };

            /// <summary>当前监听的宽度来源（父元素优先；父元素不可用时退回 TextBlock 自身）</summary>
            public FrameworkElement? WidthSource;

            /// <summary>挂在宽度来源上的回调，便于换父元素时退订旧来源</summary>
            public SizeChangedEventHandler? WidthSourceHandler;
        }

        /// <summary>完整显示文本（通常是完整文件名）</summary>
        public static readonly DependencyProperty FileNameProperty = DependencyProperty.RegisterAttached(
            "FileName",
            typeof(string),
            typeof(AdaptiveFileName),
            new PropertyMetadata(null, OnFileNameChanged));

        public static string? GetFileName(DependencyObject obj) => (string?)obj.GetValue(FileNameProperty);

        public static void SetFileName(DependencyObject obj, string? value) => obj.SetValue(FileNameProperty, value);

        /// <summary>
        /// 在宿主完成启动、最大化或还原布局后，刷新视觉树中已经实例化的文件名。
        /// 只遍历虚拟化列表当前可见的元素，不会创建未实现的容器。
        /// </summary>
        public static void RefreshDescendants(DependencyObject root)
        {
            if (root is TextBlock textBlock
                && textBlock.GetValue(StateProperty) is TrimState state)
            {
                state.LastWidth = double.NaN;
                Refresh(textBlock, state);
            }

            int childCount = VisualTreeHelper.GetChildrenCount(root);
            for (int i = 0; i < childCount; i++)
                RefreshDescendants(VisualTreeHelper.GetChild(root, i));
        }

        private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
            "TrimState",
            typeof(TrimState),
            typeof(AdaptiveFileName),
            new PropertyMetadata(null));

        private static void OnFileNameChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is not TextBlock textBlock) return;

            var state = (TrimState?)textBlock.GetValue(StateProperty);
            if (state is null)
            {
                state = new TrimState();
                textBlock.SetValue(StateProperty, state);
                // 末尾省略由本附加属性自己处理，关闭系统级 TextTrimming
                textBlock.TextTrimming = TextTrimming.None;
                textBlock.TextWrapping = TextWrapping.NoWrap;
                textBlock.MaxLines = 1;
                textBlock.Loaded += OnTextBlockLoaded;
                textBlock.Unloaded += OnTextBlockUnloaded;
            }

            Refresh(textBlock, state);
        }

        /// <summary>
        /// 元素连接到视觉树后同步宽度来源（虚拟化回收复用时 Loaded 会重新触发）。
        /// </summary>
        private static void OnTextBlockLoaded(object sender, RoutedEventArgs e)
        {
            if (sender is TextBlock textBlock)
            {
                if (textBlock.GetValue(StateProperty) is TrimState state)
                {
                    SyncWidthSource(textBlock, state);
                    Refresh(textBlock, state);
                }
            }
        }

        /// <summary>虚拟化容器离开视觉树时解除父级宽度监听，防止回收项保留旧布局引用。</summary>
        private static void OnTextBlockUnloaded(object sender, RoutedEventArgs e)
        {
            if (sender is not TextBlock textBlock
                || textBlock.GetValue(StateProperty) is not TrimState state)
                return;

            DetachWidthSource(state);
            state.LastWidth = double.NaN;
        }

        /// <summary>
        /// 选定并订阅宽度来源：优先父元素（其宽度由列宽决定，与文字长度无关，稳定）；
        /// 父元素不可用时退回 TextBlock 自身。换来源时先退订旧的。
        /// </summary>
        private static void SyncWidthSource(TextBlock textBlock, TrimState state)
        {
            FrameworkElement? target = textBlock.Parent as FrameworkElement;
            if (target is null) target = textBlock;
            if (ReferenceEquals(target, state.WidthSource)) return;

            DetachWidthSource(state);

            state.WidthSource = target;
            state.WidthSourceHandler = (_, _) =>
            {
                double available = GetAvailableWidth(textBlock, state);
                bool refresh = double.IsNaN(state.LastWidth)
                    || Math.Abs(available - state.LastWidth) > WidthChangeThreshold;
                if (refresh)
                    Refresh(textBlock, state);
            };
            target.SizeChanged += state.WidthSourceHandler;
        }

        private static void DetachWidthSource(TrimState state)
        {
            if (state.WidthSource != null && state.WidthSourceHandler != null)
                state.WidthSource.SizeChanged -= state.WidthSourceHandler;

            state.WidthSource = null;
            state.WidthSourceHandler = null;
        }

        /// <summary>
        /// 可用宽度 = 宽度来源（父元素，布局驱动）的实际宽度 - TextBlock 在其中的左偏移。
        /// 不能直接取 TextBlock.ActualWidth：在 Grid 星号列被无限宽度测量时，
        /// TextBlock 的宽度会跟随文字内容（实测父 Grid 906px 时 TextBlock 仍只有 60px），
        /// 导致拉宽后可用宽度不变、文件名永远缩着。左偏移即图标/间距等兄弟元素占用的空间。
        /// </summary>
        private static double GetAvailableWidth(TextBlock textBlock, TrimState state)
        {
            if (state.WidthSource is { ActualWidth: > 0 } source && !ReferenceEquals(source, textBlock))
            {
                try
                {
                    double leftOffset = textBlock.TransformToVisual(source)
                        .TransformPoint(new Point(0, 0)).X;
                    if (leftOffset >= 0 && double.IsFinite(leftOffset))
                    {
                        return Math.Max(0, source.ActualWidth - leftOffset);
                    }
                }
                catch
                {
                    // 视觉树尚未连接时退回父元素整宽
                }
                return source.ActualWidth;
            }
            if (textBlock.ActualWidth > 0) return textBlock.ActualWidth;
            return 0;
        }

        private static void Refresh(TextBlock textBlock, TrimState state)
        {
            SyncWidthSource(textBlock, state);
            try
            {
                string full = GetFileName(textBlock) ?? string.Empty;
                double available = GetAvailableWidth(textBlock, state);

                if (available <= 0)
                {
                    ApplyText(textBlock, string.Empty);
                    state.LastWidth = 0;
                    return;
                }

                if (full.Length == 0)
                {
                    ApplyText(textBlock, string.Empty);
                    state.LastWidth = available;
                    return;
                }

                SyncProbeStyle(textBlock, state.Probe);
                string display = FitToWidth(state.Probe, full, available);
                ApplyText(textBlock, display);
                state.LastWidth = available;
            }
            catch
            {
                state.LastWidth = GetAvailableWidth(textBlock, state);
                ApplyText(
                    textBlock,
                    GetFileName(textBlock) ?? string.Empty);
            }
        }

        private static readonly Size ProbeConstraint = new(double.PositiveInfinity, double.PositiveInfinity);

        private static void ApplyText(TextBlock textBlock, string text)
        {
            if (!string.Equals(textBlock.Text, text, StringComparison.Ordinal))
                textBlock.Text = text;

            // TextBlock 已由父级星号列约束。保留旧 MaxWidth 会在启动/最大化的过渡布局中
            // 形成一个较窄的居中元素，表现为文件名突然远离左侧图标。
            textBlock.ClearValue(FrameworkElement.MaxWidthProperty);
        }

        private static void SyncProbeStyle(TextBlock source, TextBlock probe)
        {
            probe.FontFamily = source.FontFamily;
            probe.FontSize = source.FontSize;
            probe.FontStyle = source.FontStyle;
            probe.FontWeight = source.FontWeight;
            probe.FontStretch = source.FontStretch;
            probe.CharacterSpacing = source.CharacterSpacing;
        }

        private static double MeasureWidth(TextBlock probe, string text)
        {
            probe.Text = text;
            probe.Measure(ProbeConstraint);
            return probe.DesiredSize.Width;
        }

        /// <summary>
        /// 生成能放入指定宽度的最长显示文本。极窄时逐步舍弃前缀与尾部，
        /// 最终可退化为单个省略号或空文本，保证文件名列能够持续收缩。
        /// </summary>
        private static string FitToWidth(TextBlock probe, string full, double available)
        {
            if (MeasureWidth(probe, full) <= available)
                return full;

            const string ellipsis = "…";
            if (MeasureWidth(probe, ellipsis) > available)
                return string.Empty;

            string tail = BuildTail(full);
            string minimum = ellipsis + tail;

            // 连完整尾部都放不下时，保留能容纳的最长末尾片段。
            if (MeasureWidth(probe, minimum) > available)
            {
                int lo = 0;
                int hi = tail.Length;
                string best = ellipsis;
                while (lo <= hi)
                {
                    int count = (lo + hi) / 2;
                    string candidate = count == 0 ? ellipsis : ellipsis + tail[^count..];
                    if (MeasureWidth(probe, candidate) <= available)
                    {
                        best = candidate;
                        lo = count + 1;
                    }
                    else
                    {
                        hi = count - 1;
                    }
                }
                return best;
            }

            // 尾部稳定后，二分查找能容纳的最长前缀。
            int maxPrefix = Math.Max(0, full.Length - tail.Length);
            int prefixLo = 0;
            int prefixHi = maxPrefix;
            string result = minimum;
            while (prefixLo <= prefixHi)
            {
                int count = (prefixLo + prefixHi) / 2;
                string candidate = full[..count] + ellipsis + tail;
                if (MeasureWidth(probe, candidate) <= available)
                {
                    result = candidate;
                    prefixLo = count + 1;
                }
                else
                {
                    prefixHi = count - 1;
                }
            }
            return result;
        }

        /// <summary>
        /// 计算保留的尾部：
        /// 有扩展名 → 扩展名 + 扩展名前 keepTail 个字符；
        /// 无扩展名（如 "IMG_123 (JPG+MOV)"）→ 保留末尾 fallbackTail 个字符。
        /// </summary>
        private static string BuildTail(string text)
        {
            string ext = System.IO.Path.GetExtension(text);
            if (ext.Length > 0)
            {
                string nameWithoutExt = System.IO.Path.GetFileNameWithoutExtension(text);
                int keep = Math.Min(KeepTailChars, nameWithoutExt.Length);
                return nameWithoutExt[^keep..] + ext;
            }

            int tailLen = Math.Min(FallbackTailChars, text.Length);
            return text[^tailLen..];
        }
    }
}
