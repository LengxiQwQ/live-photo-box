using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace LivePhotoBox.Views
{
    public sealed partial class HomePage : Page
    {
        public HomePage()
        {
            this.InitializeComponent();
            this.Loaded += HomePage_Loaded;
        }

        private void HomePage_Loaded(object sender, RoutedEventArgs e)
        {
            if (App.CachedBannerImage == null)
            {
                App.CachedBannerImage = new BitmapImage(new Uri("ms-appx:///Assets/BannerImage.jpg"));
            }

            if (this.FindName("BannerImage") is Image bannerImage)
            {
                bannerImage.Source = App.CachedBannerImage;
            }

            if (this.FindName("HeroTitleText") is TextBlock heroTitleText &&
                this.FindName("HeroTitleShadow") is TextBlock heroTitleShadow)
            {
                heroTitleShadow.Text = heroTitleText.Text;
            }
        }
    }
}