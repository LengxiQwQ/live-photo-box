/*
 * AboutPage.xaml.cs
 *
 * 关于页面的代码后置。
 * 负责对外链接的点击跳转和版本号的展示。
 *
 * 对应 ViewModel：AboutViewModel
 *
 * 生命周期：
 *   - 构造函数中初始化版本号（通过 App.AppVersion）
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
        // 开发者主页链接
        private static readonly Uri DeveloperUri = new("https://github.com/LengxiQwQ");

        // 项目仓库链接
        private static readonly Uri RepositoryUri = new("https://github.com/LengxiQwQ/live-photo-box");

        // 项目许可证链接
        private static readonly Uri LicenseUri = new("https://github.com/LengxiQwQ/live-photo-box/blob/master/LICENSE");

        // 关联的 AboutViewModel
        public AboutViewModel ViewModel => AppViewModel.Instance.About;

        // 构造函数：初始化组件并显示应用版本号
        public AboutPage()
        {
            InitializeComponent();
            VersionTextBlock.Text = ResourceService.Format("AboutPage_Version_Format", App.AppVersion);
        }

        // 打开开发者 GitHub 链接
        private async void DeveloperLinkButton_Click(object sender, RoutedEventArgs e) => await FilePickerService.OpenUriAsync(DeveloperUri);

        // 打开项目仓库链接
        private async void RepositoryLinkButton_Click(object sender, RoutedEventArgs e) => await FilePickerService.OpenUriAsync(RepositoryUri);

        // 打开项目许可证链接
        private async void LicenseLinkButton_Click(object sender, RoutedEventArgs e) => await FilePickerService.OpenUriAsync(LicenseUri);

        // 打开反馈/问题页面链接
        private async void IssueLinkButton_Click(object sender, RoutedEventArgs e) => await FilePickerService.OpenUriAsync(FeedbackService.GetIssuesUri());

        // 打开 ExifTool 官网
        private async void ExifTool_Click(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://exiftool.org/"));

        // 打开 JPEGTran 工具官网
        private async void JpegTran_Click(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://www.ijg.org/"));

        // 打开 FFmpeg 官网
        private async void FFmpeg_Click(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://ffmpeg.org/"));

        // 打开 ImageMagick 官网
        private async void ImageMagick_Click(object sender, RoutedEventArgs e) => await Windows.System.Launcher.LaunchUriAsync(new Uri("https://imagemagick.org/"));

    }
}
