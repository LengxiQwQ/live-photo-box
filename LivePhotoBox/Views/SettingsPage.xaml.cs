using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LivePhotoBox.Views
{
    public sealed partial class SettingsPage : Page
    {
        public AppViewModel ViewModel => AppViewModel.Instance;

        public Visibility TestToolsVisibility => _isTestToolsVisible ? Visibility.Visible : Visibility.Collapsed;

        public string TestToolsToggleButtonText => ResourceService.GetString(_isTestToolsVisible
            ? "SettingsPage_TestHide_Button_Text"
            : "SettingsPage_TestShow_Button_Text");

        private bool _isTestToolsVisible;

        public SettingsPage()
        {
            this.InitializeComponent();
        }

        private void ToggleTestToolsButton_Click(object sender, RoutedEventArgs e)
        {
            _isTestToolsVisible = !_isTestToolsVisible;
            Bindings.Update();
        }

        private void GenerateTestCrashLogButton_Click(object sender, RoutedEventArgs e)
        {
            ViewModel.GenerateTestCrashLogActionCommand.Execute(null);
        }

        private async void PreviewCrashDialogButton_Click(object sender, RoutedEventArgs e)
        {
            string? logPath = CrashLogService.GetLatestCrashLogPath();
            if (string.IsNullOrWhiteSpace(logPath))
            {
                ViewModel.GenerateTestCrashLogActionCommand.Execute(null);
                logPath = CrashLogService.GetLatestCrashLogPath();
            }

            if (string.IsNullOrWhiteSpace(logPath) || XamlRoot == null)
            {
                return;
            }

            CrashLogService.RecordBreadcrumb($"PreviewCrashDialog requested. File='{System.IO.Path.GetFileName(logPath)}'");
            await CrashLogService.ShowCrashDialogAsync(XamlRoot, logPath);
        }
    }
}