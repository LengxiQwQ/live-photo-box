using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using LivePhotoBox.Models;
using System;

namespace LivePhotoBox.Views
{
    public sealed partial class SettingsPage : Page
    {
        public SettingsViewModel ViewModel => AppViewModel.Instance.Settings;

        public AboutViewModel AboutViewModel => AppViewModel.Instance.About;

        public Visibility TestToolsVisibility => _isTestToolsVisible ? Visibility.Visible : Visibility.Collapsed;

        public string TestToolsToggleButtonText => ResourceService.GetString(_isTestToolsVisible
            ? "SettingsPage_TestHide_Button_Text"
            : "SettingsPage_TestShow_Button_Text");

        private bool _isTestToolsVisible;

        public SettingsPage()
        {
            InitializeComponent();
        }

        private void ToggleTestToolsButton_Click(object sender, RoutedEventArgs e)
        {
            _isTestToolsVisible = !_isTestToolsVisible;
            Bindings.Update();
        }

        private async void PreviewCrashDialogButton_Click(object sender, RoutedEventArgs e)
        {
            string? logPath = LogService.GetLatestLogPath();
            if (string.IsNullOrWhiteSpace(logPath) || XamlRoot == null) return;

            LogService.Info($"PreviewCrashDialog requested. File='{System.IO.Path.GetFileName(logPath)}'", LogSource.UI);
            await CrashHandler.ShowCrashDialogAsync(XamlRoot, logPath);
        }

        private async void RestoreDefaultSettings_Click(object sender, RoutedEventArgs e)
        {
            if (App.MainWindow?.Content?.XamlRoot == null) return;

            var dialog = new ContentDialog
            {
                Title = ResourceService.GetString("SettingsPage_Restore_Confirm_Title"),
                Content = new TextBlock
                {
                    Text = ResourceService.GetString("SettingsPage_Restore_Confirm_Message"),
                    FontSize = 14,
                    TextWrapping = TextWrapping.Wrap
                },
                PrimaryButtonText = ResourceService.GetString("Msg_Cancel"),
                SecondaryButtonText = ResourceService.GetString("Msg_Confirm"),
                DefaultButton = ContentDialogButton.Secondary,
                XamlRoot = App.MainWindow.Content.XamlRoot,
                RequestedTheme = App.CurrentTheme
            };

            var result = await dialog.ShowAsync();
            if (result == ContentDialogResult.Secondary)
            {
                ViewModel.RestoreDefaultSettingsCommand.Execute(null);
            }
        }
    }
}
