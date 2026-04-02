using LivePhotoStudio.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LivePhotoStudio.Views
{
    public sealed partial class SettingsPage : Page
    {
        public AppViewModel ViewModel => AppViewModel.Instance;

        public SettingsPage()
        {
            this.InitializeComponent();
        }
    }
}