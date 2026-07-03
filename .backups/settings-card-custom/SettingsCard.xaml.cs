using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LivePhotoBox.Controls
{
    /// <summary>
    /// 通用设置卡片控件，封装"图标 + 标题&描述 + 操作控件"的三列布局。
    ///
    /// Header / Description 支持两种传值方式：
    /// 1. 字符串 — 自动套用默认 TextBlock 样式。
    /// 2. TextBlock（或任意 UIElement）— 直接渲染，常用于 x:Uid 多语言绑定。
    /// </summary>
    public sealed partial class SettingsCard : UserControl
    {
        public static readonly DependencyProperty IconProperty =
            DependencyProperty.Register(
                nameof(Icon),
                typeof(FrameworkElement),
                typeof(SettingsCard),
                new PropertyMetadata(null, OnIconChanged));

        public static readonly DependencyProperty HeaderProperty =
            DependencyProperty.Register(
                nameof(Header),
                typeof(object),
                typeof(SettingsCard),
                new PropertyMetadata(null, OnHeaderChanged));

        public static readonly DependencyProperty DescriptionProperty =
            DependencyProperty.Register(
                nameof(Description),
                typeof(object),
                typeof(SettingsCard),
                new PropertyMetadata(null, OnDescriptionChanged));

        public static readonly DependencyProperty ActionContentProperty =
            DependencyProperty.Register(
                nameof(ActionContent),
                typeof(object),
                typeof(SettingsCard),
                new PropertyMetadata(null, OnActionContentChanged));

        /// <summary>卡片图标（FontIcon / SymbolIcon 等）</summary>
        public FrameworkElement? Icon
        {
            get => (FrameworkElement?)GetValue(IconProperty);
            set => SetValue(IconProperty, value);
        }

        /// <summary>卡片标题（string 或 TextBlock）</summary>
        public object? Header
        {
            get => GetValue(HeaderProperty);
            set => SetValue(HeaderProperty, value);
        }

        /// <summary>卡片描述（string 或 TextBlock）</summary>
        public object? Description
        {
            get => GetValue(DescriptionProperty);
            set => SetValue(DescriptionProperty, value);
        }

        /// <summary>右侧操作控件（ComboBox / ToggleSwitch / Slider 等）</summary>
        public object? ActionContent
        {
            get => GetValue(ActionContentProperty);
            set => SetValue(ActionContentProperty, value);
        }

        public SettingsCard()
        {
            this.InitializeComponent();
        }

        private static void OnIconChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var card = (SettingsCard)d;
            card.IconPresenter.Content = e.NewValue;
        }

        private static void OnHeaderChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var card = (SettingsCard)d;
            if (e.NewValue is string s)
            {
                card.HeaderPresenter.Content = new TextBlock
                {
                    Text = s,
                    FontSize = 15
                };
            }
            else
            {
                card.HeaderPresenter.Content = e.NewValue;
            }
        }

        private static void OnDescriptionChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var card = (SettingsCard)d;
            if (e.NewValue is string s)
            {
                card.DescriptionPresenter.Content = new TextBlock
                {
                    Text = s,
                    FontSize = 12,
                    Foreground = (Microsoft.UI.Xaml.Media.Brush)
                        Application.Current.Resources["TextFillColorSecondaryBrush"],
                    TextWrapping = TextWrapping.Wrap
                };
            }
            else
            {
                card.DescriptionPresenter.Content = e.NewValue;
            }
        }

        private static void OnActionContentChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            var card = (SettingsCard)d;
            card.ActionContentPresenter.Content = e.NewValue;
        }
    }
}
