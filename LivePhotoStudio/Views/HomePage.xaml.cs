using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media.Imaging;
using System;

namespace LivePhotoStudio.Views
{
    public sealed partial class HomePage : Page
    {
        public HomePage()
        {
            this.InitializeComponent();
            this.Loaded += HomePage_Loaded;
        }

        private void HomePage_Loaded(object sender, Microsoft.UI.Xaml.RoutedEventArgs e)
        {
            // Load or cache the banner image
            if (App.CachedBannerImage == null)
            {
                App.CachedBannerImage = new BitmapImage(new Uri("ms-appx:///Assets/BannerImage.jpg"));
            }

            BannerImage.Source = App.CachedBannerImage;
        }
    }
}