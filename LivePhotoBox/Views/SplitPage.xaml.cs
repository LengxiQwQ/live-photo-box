using LivePhotoBox.Services;
using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace LivePhotoBox.Views
{
    public sealed partial class SplitPage : Page
    {
        public AppViewModel ViewModel => AppViewModel.Instance;

        public SplitPage()
        {
            InitializeComponent();
        }

        private async void BrowseInput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null)
            {
                ViewModel.SplitInputDirectory = folder.Path;
            }
        }

        private async void BrowseOutput_Click(object sender, RoutedEventArgs e)
        {
            var folder = await FilePickerService.PickFolderAsync();
            if (folder != null)
            {
                ViewModel.SplitOutputDirectory = folder.Path;
            }
        }
    }
}
