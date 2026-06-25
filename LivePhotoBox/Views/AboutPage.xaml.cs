/*
 * AboutPage.xaml.cs
 *
 * 关于页面的代码后置。
 * 负责对外链接的点击跳转和版本号的展示。
 *
 * 生命周期：
 *   - 构造函数中初始化版本号（通过 GetAppVersion()）
 *   - 各链接按钮统一通过 FilePickerService / Launcher 打开 Uri
 */

using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
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

        public AboutViewModel ViewModel => AppViewModel.Instance.About;

        public AboutPage()
        {
            InitializeComponent();
            VersionTextBlock.Text = ResourceService.Format("AboutPage_Version_Format", App.AppVersion);
        }

        private async void DeveloperLinkButton_Click(object sender, RoutedEventArgs e) => await FilePickerService.OpenUriAsync(DeveloperUri);
        private async void RepositoryLinkButton_Click(object sender, RoutedEventArgs e) => await FilePickerService.OpenUriAsync(RepositoryUri);
        private async void LicenseLinkButton_Click(object sender, RoutedEventArgs e) => await FilePickerService.OpenUriAsync(LicenseUri);
        private async void IssueLinkButton_Click(object sender, RoutedEventArgs e) => await FilePickerService.OpenUriAsync(FeedbackService.GetIssuesUri());

        private async void ExifTool_Click(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://exiftool.org/"));
        private async void JHead_Click(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.sentex.ca/~mwandel/jhead/"));
        private async void JpegTran_Click(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.ijg.org/"));
        private async void FFmpeg_Click(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://ffmpeg.org/"));
        private async void ImageMagick_Click(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://imagemagick.org/"));

    }
}
