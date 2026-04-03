using LivePhotoBox.ViewModels;
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
    }
}
