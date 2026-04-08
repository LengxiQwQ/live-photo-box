using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LivePhotoBox.Views
{
    public sealed partial class RepairPage : Page
    {
        public AppViewModel ViewModel => AppViewModel.Instance;

        public RepairPage()
        {
            this.InitializeComponent();
        }
    }
}