using Microsoft.UI.Xaml.Controls;
using LivePhotoStudio.ViewModels;

namespace LivePhotoStudio.Views
{
    public sealed partial class SettingsPage : Page
    {
        // 绑定全局共享状态，确保这里的修改能同步到所有页面
        public SharedViewModel ViewModel => SharedViewModel.Instance;

        public SettingsPage()
        {
            this.InitializeComponent();
        }
    }
}