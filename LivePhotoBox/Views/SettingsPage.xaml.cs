using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LivePhotoBox.Views
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