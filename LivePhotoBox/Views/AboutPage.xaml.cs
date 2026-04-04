using LivePhotoBox.Services;
using Microsoft.UI.Xaml.Controls;
using System;
using System.Reflection;
using Windows.ApplicationModel;

namespace LivePhotoBox.Views
{
    public sealed partial class AboutPage : Page
    {
        public AboutPage()
        {
            this.InitializeComponent();
            VersionTextBlock.Text = ResourceService.Format("AboutPage_Version_Format", GetAppVersion());
        }

        private static string GetAppVersion()
        {
            try
            {
                PackageVersion version = Package.Current.Id.Version;
                return $"{version.Major}.{version.Minor}.{version.Build}.{version.Revision}";
            }
            catch (InvalidOperationException)
            {
                return Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "0.0.0.0";
            }
        }
    }
}