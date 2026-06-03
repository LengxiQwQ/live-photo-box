using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LivePhotoBox.Views
{
    public sealed partial class KeyPhotoPage : Page
    {
        public KeyPhotoViewModel ViewModel => AppViewModel.Instance.KeyPhoto;

        public KeyPhotoPage()
        {
            InitializeComponent();
        }
    }
}
