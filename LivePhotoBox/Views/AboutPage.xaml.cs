using LivePhotoBox.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Reflection;
using Windows.ApplicationModel;

namespace LivePhotoBox.Views
{
    public sealed partial class AboutPage : Page
    {
        private static readonly Uri DeveloperUri = new("https://github.com/LengxiQwQ");
        private static readonly Uri RepositoryUri = new("https://github.com/LengxiQwQ/live-photo-box");
        private static readonly Uri LicenseUri = new("https://github.com/LengxiQwQ/live-photo-box/blob/master/LICENSE");

        public AboutPage()
        {
            this.InitializeComponent();
            VersionTextBlock.Text = ResourceService.Format("AboutPage_Version_Format", GetAppVersion());
        }

        private async void DeveloperLinkButton_Click(object sender, RoutedEventArgs e) => await FilePickerService.OpenUriAsync(DeveloperUri);
        private async void RepositoryLinkButton_Click(object sender, RoutedEventArgs e) => await FilePickerService.OpenUriAsync(RepositoryUri);
        private async void LicenseLinkButton_Click(object sender, RoutedEventArgs e) => await FilePickerService.OpenUriAsync(LicenseUri);
        private async void IssueLinkButton_Click(object sender, RoutedEventArgs e) => await FilePickerService.OpenUriAsync(FeedbackService.GetIssuesUri());

        private async void ExifTool_Click(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://exiftool.org/"));
        private async void JpegTran_Click(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.ijg.org/"));

        private static string GetAppVersion()
        {
            try { return Package.Current.Id.Version.Major + "." + Package.Current.Id.Version.Minor + "." + Package.Current.Id.Version.Build + "." + Package.Current.Id.Version.Revision; }
            catch { return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "1.0.0.0"; }
        }
    }
}