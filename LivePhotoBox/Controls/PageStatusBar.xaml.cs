using LivePhotoBox.ViewModels;

namespace LivePhotoBox.Controls
{
    public sealed partial class PageStatusBar
    {
        public AppViewModel ViewModel => AppViewModel.Instance;

        public PageStatusBar()
        {
            InitializeComponent();
        }
    }
}
