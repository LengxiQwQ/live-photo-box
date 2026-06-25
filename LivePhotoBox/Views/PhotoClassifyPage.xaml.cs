using LivePhotoBox.ViewModels;
using Microsoft.UI.Xaml.Controls;

namespace LivePhotoBox.Views
{
    public sealed partial class PhotoClassifyPage : Page
    {
        public PhotoClassifyViewModel ViewModel => AppViewModel.Instance.PhotoClassify;

        public PhotoClassifyPage()
        {
            InitializeComponent();
        }
    }
}
