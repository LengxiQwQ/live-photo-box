using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System.Windows.Input;

namespace LivePhotoBox.Controls
{
    public sealed partial class ScanDirectoryButton : UserControl
    {
        public static readonly DependencyProperty CommandProperty =
            DependencyProperty.Register(
                nameof(Command),
                typeof(ICommand),
                typeof(ScanDirectoryButton),
                new PropertyMetadata(null));

        public static readonly DependencyProperty LabelProperty =
            DependencyProperty.Register(
                nameof(Label),
                typeof(string),
                typeof(ScanDirectoryButton),
                new PropertyMetadata(string.Empty));

        public static readonly DependencyProperty IsCancelAppearanceProperty =
            DependencyProperty.Register(
                nameof(IsCancelAppearance),
                typeof(bool),
                typeof(ScanDirectoryButton),
                new PropertyMetadata(false, OnAppearancePropertyChanged));

        public static readonly DependencyProperty IsButtonEnabledProperty =
            DependencyProperty.Register(
                nameof(IsButtonEnabled),
                typeof(bool),
                typeof(ScanDirectoryButton),
                new PropertyMetadata(true));

        private static Style? _defaultButtonStyle;
        private static Style? _cancelButtonStyle;

        public ScanDirectoryButton()
        {
            InitializeComponent();
            Loaded += (_, _) => UpdateAppearance();
        }

        public ICommand? Command
        {
            get => (ICommand?)GetValue(CommandProperty);
            set => SetValue(CommandProperty, value);
        }

        public string Label
        {
            get => (string)GetValue(LabelProperty);
            set => SetValue(LabelProperty, value);
        }

        public bool IsCancelAppearance
        {
            get => (bool)GetValue(IsCancelAppearanceProperty);
            set => SetValue(IsCancelAppearanceProperty, value);
        }

        public bool IsButtonEnabled
        {
            get => (bool)GetValue(IsButtonEnabledProperty);
            set => SetValue(IsButtonEnabledProperty, value);
        }

        private static void OnAppearancePropertyChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
        {
            if (d is ScanDirectoryButton button)
            {
                button.UpdateAppearance();
            }
        }

        private void UpdateAppearance()
        {
            EnsureStyles();

            if (IsCancelAppearance && _cancelButtonStyle != null)
            {
                ScanButton.Style = _cancelButtonStyle;
                return;
            }

            if (_defaultButtonStyle != null)
            {
                ScanButton.Style = _defaultButtonStyle;
            }

            ScanButton.ClearValue(BackgroundProperty);
            ScanButton.ClearValue(ForegroundProperty);
            ScanButton.ClearValue(BorderBrushProperty);
        }

        private static void EnsureStyles()
        {
            if (_defaultButtonStyle != null && _cancelButtonStyle != null)
            {
                return;
            }

            var resources = Application.Current.Resources;
            if (_defaultButtonStyle == null
                && resources.TryGetValue("DefaultButtonStyle", out var defaultStyle)
                && defaultStyle is Style defaultButtonStyle)
            {
                _defaultButtonStyle = defaultButtonStyle;
            }

            if (_cancelButtonStyle == null
                && resources.TryGetValue("ScanCancelButtonStyle", out var cancelStyle)
                && cancelStyle is Style cancelButtonStyle)
            {
                _cancelButtonStyle = cancelButtonStyle;
            }
        }
    }
}
