using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using LivePhotoBox.Services;
using System;

namespace LivePhotoBox.ViewModels
{
    public partial class HomeViewModel : ViewModelBase
    {
        public override string? PageStatusTag => null;

        public event EventHandler<string>? RequestNavigateToPage;

        [RelayCommand]
        private void GoToTutorial(string feature)
        {
            RequestNavigateToPage?.Invoke(this, $"Home_{feature}");
        }
    }
}
